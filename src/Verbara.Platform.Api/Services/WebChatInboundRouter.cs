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
/// message that opened a new conversation (the session's conversation ended, so the pipeline created a
/// fresh owner-less one, mirroring the webhook path's <c>IsNewConversation</c> trigger); other messages
/// are no-ops.
/// </summary>
internal sealed partial class WebChatInboundRouter
{
    private readonly IInboundRouter _router;
    private readonly IConversationSwitchboard _switchboard;
    private readonly IConversationStore _conversationStore;
    private readonly IContactStore _contactStore;
    private readonly PlatformEventBus _eventBus;
    private readonly WebChatSessionManager _sessionManager;
    private readonly ILogger<WebChatInboundRouter> _logger;

    public WebChatInboundRouter(
        IInboundRouter router,
        IConversationSwitchboard switchboard,
        IConversationStore conversationStore,
        IContactStore contactStore,
        PlatformEventBus eventBus,
        WebChatSessionManager sessionManager,
        ILogger<WebChatInboundRouter> logger)
    {
        _router = router;
        _switchboard = switchboard;
        _conversationStore = conversationStore;
        _contactStore = contactStore;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Routes and assigns the conversation to a queue on the session's first inbound message, or when
    /// the message opened a new conversation. Other calls return immediately (the session is marked
    /// routed atomically, first-wins). Publishes the same SSE events as the webhook path so the agent
    /// UI updates live.
    /// </summary>
    public async Task RouteFirstInboundAsync(
        string sessionId,
        TenantId tenantId,
        PipelineResult pipelineResult,
        MessageEnvelope? content,
        CancellationToken ct)
    {
        // Route the session's first message, and any message that opened a new conversation (the session's
        // conversation ended, so the pipeline created a fresh owner-less one for the same contact).
        var firstOfSession = _sessionManager.TryMarkRouted(sessionId);
        if (!firstOfSession && !pipelineResult.IsNewConversation)
        {
            return;
        }

        if (pipelineResult.IsNewConversation
            && await _sessionManager.GetSessionAsync(sessionId).ConfigureAwait(false) is { } session)
        {
            // Typing indicators and later lookups follow the conversation the visitor is now in.
            session.ConversationId = pipelineResult.ConversationId;
        }

        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, pipelineResult.ConversationId.Value, "", "Queued"));
        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, pipelineResult.ConversationId.Value,
            pipelineResult.MessageId.Value, "Inbound", ChannelType.WebChat.ToString()));

        var conversation = await _conversationStore.GetByIdAsync(tenantId, pipelineResult.ConversationId, ct).ConfigureAwait(false);
        var contact = await _contactStore.GetByIdAsync(tenantId, pipelineResult.ContactId, ct).ConfigureAwait(false);
        if (conversation is null || contact is null)
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

    [LoggerMessage(
        EventId = 7401,
        Level = LogLevel.Warning,
        Message = "WebChat inbound routing resolved no queue for tenant {TenantId} session {SessionId}; conversation left unrouted for manual pickup.")]
    private partial void LogNoQueueResolved(Exception ex, string tenantId, string sessionId);
}
