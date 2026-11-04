using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Assigns a WebChat conversation to a queue on the FIRST inbound message of a session,
/// mirroring the webhook inbound path (<see cref="WebhookInboundProcessor"/>). WebChat
/// conversations are pre-created (owner-less, in <see cref="ConversationState.Queued"/>) at
/// session open, and the inbound pipeline only persists messages — it never routes/assigns —
/// so <c>QueueDistributionWorker</c> (which skips owner-less conversations) never offers them
/// to an agent. This bridge closes that gap. It routes the session's first inbound message and any
/// message whose conversation still needs routing (<see cref="WebhookInboundProcessor.NeedsRouting"/>: the
/// session's conversation ended, so the pipeline created a fresh owner-less one, or reused one still queued
/// and owner-less); other messages are only published on the live stream (contact-history-opens-by-assignment, design D17).
/// </summary>
internal sealed partial class WebChatInboundRouter
{
    private readonly IInboundRouter _router;
    private readonly IConversationSwitchboard _switchboard;
    private readonly IConversationStore _conversationStore;
    private readonly IContactStore _contactStore;
    private readonly PlatformEventBus _eventBus;
    private readonly WebChatSessionManager _sessionManager;
    private readonly CustomerReturnAnnouncer _returnAnnouncer;
    private readonly ILogger<WebChatInboundRouter> _logger;

    public WebChatInboundRouter(
        IInboundRouter router,
        IConversationSwitchboard switchboard,
        IConversationStore conversationStore,
        IContactStore contactStore,
        PlatformEventBus eventBus,
        WebChatSessionManager sessionManager,
        CustomerReturnAnnouncer returnAnnouncer,
        ILogger<WebChatInboundRouter> logger)
    {
        _router = router;
        _switchboard = switchboard;
        _conversationStore = conversationStore;
        _contactStore = contactStore;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _returnAnnouncer = returnAnnouncer;
        _logger = logger;
    }

    /// <summary>
    /// Stores one visitor message through <paramref name="pipeline"/>. A failure after the delivery won a return close
    /// announces that close before the original failure propagates (audit round 4, F1; see
    /// <see cref="CustomerReturnAnnouncer.ProcessAsync"/>).
    /// </summary>
    public Task<PipelineResult> ProcessInboundAsync(
        IInboundMessagePipeline pipeline, InboundMessage inbound, TenantId tenantId, CancellationToken ct) =>
        _returnAnnouncer.ProcessAsync(pipeline, inbound, tenantId, ChannelType.WebChat, ct);

    // The visitor's message on the live stream, for the conversation's owner or open-offer agent as loaded just before
    // (design D17); a conversation that could not be loaded or read gives an empty audience (readers only), as on the webhook path.
    private void PublishInbound(TenantId tenantId, PipelineResult pipelineResult, Conversation? conversation) =>
        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, pipelineResult.ConversationId.Value,
            pipelineResult.MessageId.Value, "Inbound", ChannelType.WebChat.ToString())
        {
            Audience = ConversationOffer.AudienceOf(conversation),
        });

    /// <summary>
    /// Routes and assigns the conversation to a queue on the session's first inbound message, or when the conversation
    /// the message was stored in still needs routing (<see cref="WebhookInboundProcessor.NeedsRouting"/>: it is new, or
    /// <c>Queued</c> with no owner). Other calls keep the session on the visitor's conversation and publish the message.
    /// Every non-duplicate message is published as the webhook path publishes it, so the agent UI updates live.
    /// </summary>
    /// <remarks>
    /// conversations-close-after-inactivity (design D8): a return close the delivery won
    /// (<see cref="PipelineResult.ClosedOnReturnId"/>) is announced first, also on a duplicate result (the lost-insert
    /// branch). A duplicate result then routes and re-points nothing and does not consume the session's first-message
    /// mark: routing and the session belong to the insert winner, which may have reused the replacement conversation the
    /// close winner opened (<c>IsNewConversation: false</c> on a <c>Queued</c> owner-less row).
    /// </remarks>
    public async Task RouteFirstInboundAsync(
        string sessionId,
        TenantId tenantId,
        PipelineResult pipelineResult,
        MessageEnvelope? content,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipelineResult);

        if (pipelineResult.ClosedOnReturnId is { } closedOnReturn)
            await _returnAnnouncer.AnnounceAsync(tenantId, closedOnReturn).ConfigureAwait(false);

        if (pipelineResult.IsDuplicate)
            return;

        // The session follows the visitor: its next message, its typing indicators and later lookups use the contact and
        // the conversation this message was stored under. Both change when the contact was erased and replaced.
        if (await _sessionManager.GetSessionAsync(sessionId).ConfigureAwait(false) is { } session)
        {
            session.ContactId = pipelineResult.ContactId;
            session.ConversationId = pipelineResult.ConversationId;
        }

        // Route the session's first message, and any message whose conversation still needs routing: one the message
        // opened (the session's conversation ended), or one still queued and owner-less (a replacement another
        // delivery opened, or an earlier routing that found no queue) — the predicate the webhook path uses.
        var firstOfSession = _sessionManager.TryMarkRouted(sessionId);
        var conversation = await ReadForPublishAsync(tenantId, pipelineResult.ConversationId).ConfigureAwait(false);
        if (!firstOfSession
            && (conversation is null || !WebhookInboundProcessor.NeedsRouting(pipelineResult, conversation)))
        {
            // Every stored message is published, not only the one that routes (contact-history-opens-by-assignment,
            // design D17, spec audit F3): a later message of an Offered or Active conversation reaches its agent live.
            PublishInbound(tenantId, pipelineResult, conversation);
            return;
        }

        // The routing branch keeps its order: the state first, then the message, published once.
        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, pipelineResult.ConversationId.Value, "", "Queued"));
        PublishInbound(tenantId, pipelineResult, conversation);

        // Before the contact read: a conversation that could not be read routes nothing, so a contact read failing in the
        // same outage must not fail the request of a message already stored and published.
        if (conversation is null)
        {
            return;
        }

        var contact = await _contactStore.GetByIdAsync(tenantId, pipelineResult.ContactId, ct).ConfigureAwait(false);
        if (contact is null)
        {
            return;
        }

        var routingCtx = new RoutingContext(conversation, contact, ChannelType.WebChat, content, tenantId);
        try
        {
            var routeResult = await _router.RouteAsync(routingCtx, ct).ConfigureAwait(false);

            // C5 (implicit capture): stamp any reason/metadata the router resolved (e.g. the
            // ReasonHintMiddleware's "reasonPath") onto the conversation BEFORE assignment.
            // WebChat does NOT run the bot here, so there is no C6 FlowMetadata path in this bridge.
            // AssignToQueueAsync reloads + persists the row, so this metadata round-trips through it.
            if (routeResult.Metadata is { Count: > 0 })
            {
                foreach (var kv in routeResult.Metadata)
                    conversation.SetMetadata(kv.Key, kv.Value);
                await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
            }

            await _switchboard.AssignToQueueAsync(conversation.ConversationId, tenantId, routeResult.QueueId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The routing pipeline resolved no queue (e.g. the tenant has no active queue). Keep the
            // conversation Queued+unowned for manual supervisor pickup rather than killing the
            // visitor's WebSocket session.
            LogNoQueueResolved(ex, tenantId.Value, sessionId);
        }
    }

    // The message is stored, so this read must not fail the request: every WebChat message gets a new external id
    // (WebChatEndpoints), so a resend would store a second message, not find a duplicate. A failed read gives a null
    // conversation (an empty audience, readers only), and the request's token is not taken, so a visitor who hangs up here
    // does not lose the publish, as on the webhook path. A conversation left unrouted this way is still Queued and
    // owner-less, so the session's next message routes it (WebhookInboundProcessor.NeedsRouting), unless the queue timeout
    // abandoned it first (ConversationTimeoutWorker).
    private async Task<Conversation?> ReadForPublishAsync(TenantId tenantId, EntityId conversationId)
    {
        try
        {
            return await _conversationStore.GetByIdAsync(tenantId, conversationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The type only: a database error can quote the row.
            LogConversationReadFailed(tenantId.Value, conversationId.Value, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(
        EventId = 7402,
        Level = LogLevel.Warning,
        Message = "WebChat message of tenant {TenantId} conversation {ConversationId} is published to the readers only, and not routed: the conversation could not be read ({ExceptionType}). The next inbound message retries.")]
    private partial void LogConversationReadFailed(string tenantId, string conversationId, string exceptionType);

    [LoggerMessage(
        EventId = 7401,
        Level = LogLevel.Warning,
        Message = "WebChat inbound routing resolved no queue for tenant {TenantId} session {SessionId}; conversation left unrouted for manual pickup.")]
    private partial void LogNoQueueResolved(Exception ex, string tenantId, string sessionId);
}
