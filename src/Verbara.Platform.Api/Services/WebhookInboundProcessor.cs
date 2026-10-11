using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Processes what one verified provider webhook delivery carried (whatsapp-works-for-real D4): every message and
/// every delivery status is persisted, and each message's side effects run independently of the others.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistence decides the answer.</b> A store failure propagates, so the provider gets a non-2xx and retries;
/// a retry is safe because every message is stored by its provider id at most once and statuses only move
/// forward. Once every item is stored the endpoint answers 200.
/// </para>
/// <para>
/// <b>Side effects run once, and never fail the delivery.</b> A message the store already held
/// (<see cref="PipelineResult.IsDuplicate"/>) has no side effect at all: no event, no routing, no queue
/// assignment, no bot turn. For a new message, a failure of its side effects is logged and the rest of the batch
/// carries on — retrying the delivery could not repair it, because the retry is a duplicate.
/// </para>
/// <para>
/// <b>Routing happens once per conversation</b>, as <see cref="WebChatInboundRouter"/> does per session: when the
/// message opened the conversation, or when the conversation is still <see cref="ConversationState.Queued"/>
/// with no owner (its earlier routing failed, which leaves it in that pre-routing state). A customer's follow-up
/// on a conversation already queued, offered, active or owned by a bot is never routed again.
/// </para>
/// </remarks>
internal sealed partial class WebhookInboundProcessor
{
    private readonly IInboundMessagePipeline _pipeline;
    private readonly IInboundRouter _router;
    private readonly IConversationSwitchboard _switchboard;
    private readonly IConversationService _conversationService;
    private readonly IConversationStore _conversationStore;
    private readonly IContactStore _contactStore;
    private readonly IVirtualAgent _virtualAgent;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly DeliveryStatusHandler _deliveryStatusHandler;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<WebhookInboundProcessor> _logger;

    public WebhookInboundProcessor(
        IInboundMessagePipeline pipeline,
        IInboundRouter router,
        IConversationSwitchboard switchboard,
        IConversationService conversationService,
        IConversationStore conversationStore,
        IContactStore contactStore,
        IVirtualAgent virtualAgent,
        IConversationLifecycleService lifecycleService,
        DeliveryStatusHandler deliveryStatusHandler,
        PlatformEventBus eventBus,
        ILogger<WebhookInboundProcessor> logger)
    {
        _pipeline = pipeline;
        _router = router;
        _switchboard = switchboard;
        _conversationService = conversationService;
        _conversationStore = conversationStore;
        _contactStore = contactStore;
        _virtualAgent = virtualAgent;
        _lifecycleService = lifecycleService;
        _deliveryStatusHandler = deliveryStatusHandler;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>Persists every message and status of <paramref name="result"/> and runs each new message's side effects.</summary>
    public async Task ProcessAsync(TenantId tenantId, ChannelType channel, WebhookResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var inbound in result.Messages)
        {
            var stored = await _pipeline.ProcessAsync(inbound, tenantId, channel, ct).ConfigureAwait(false);
            if (stored.IsDuplicate)
            {
                LogDuplicateSkipped(tenantId.Value, stored.MessageId.Value);
                continue;
            }

            try
            {
                await DispatchAsync(tenantId, channel, inbound, stored, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchFailed(ex, tenantId.Value, stored.ConversationId.Value, stored.MessageId.Value);
            }
        }

        foreach (var statusUpdate in result.StatusUpdates)
            await _deliveryStatusHandler.HandleAsync(tenantId, statusUpdate, ct).ConfigureAwait(false);
    }

    private async Task DispatchAsync(
        TenantId tenantId, ChannelType channel, InboundMessage inbound, PipelineResult stored, CancellationToken ct)
    {
        // SSE events for real-time UI updates.
        if (stored.IsNewConversation)
        {
            _eventBus.Publish(new ConversationStateChangedEvent(
                tenantId.Value, stored.ConversationId.Value, "", "Queued"));
        }

        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, stored.ConversationId.Value, stored.MessageId.Value, "Inbound", channel.ToString()));

        var conversation = await _conversationStore.GetByIdAsync(tenantId, stored.ConversationId, ct).ConfigureAwait(false);
        var contact = await _contactStore.GetByIdAsync(tenantId, stored.ContactId, ct).ConfigureAwait(false);
        if (conversation is null || contact is null)
            return;

        if (NeedsRouting(stored, conversation))
            await RouteAsync(tenantId, channel, inbound, conversation, contact, ct).ConfigureAwait(false);

        // Reload: routing may have changed the owner, and a bot owns the conversation only after a hand-back.
        var current = await _conversationStore.GetByIdAsync(tenantId, conversation.ConversationId, ct).ConfigureAwait(false);
        if (current?.Owner is { Kind: ConversationOwnerKind.Bot, OwnerId: { } botId })
            await RunBotAsync(tenantId, inbound, current, botId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Routing is due when this message opened the conversation, or when the conversation was never routed
    /// (queued and owner-less — the state a failed routing leaves). Anything already assigned keeps its state.
    /// </summary>
    internal static bool NeedsRouting(PipelineResult stored, Conversation conversation) =>
        stored.IsNewConversation
        || (conversation.State == ConversationState.Queued && conversation.Owner is null);

    private async Task RouteAsync(
        TenantId tenantId, ChannelType channel, InboundMessage inbound, Conversation conversation, Contact contact, CancellationToken ct)
    {
        RouteResult routeResult;
        try
        {
            routeResult = await _router.RouteAsync(
                new RoutingContext(conversation, contact, channel, inbound.Content, tenantId), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // No route (no active queue, out of hours without overflow): leave the conversation queued and
            // owner-less for manual pickup, as WebChatInboundRouter does; the next inbound message retries.
            LogNoRoute(ex, tenantId.Value, conversation.ConversationId.Value);
            return;
        }

        // C5 (implicit capture): stamp any reason/metadata the router resolved (e.g. the ReasonHintMiddleware's
        // "reasonPath") onto the conversation BEFORE assignment and any bot processing. AssignToQueueAsync
        // reloads + persists the row, so this metadata round-trips through the assignment.
        if (routeResult.Metadata is { Count: > 0 })
        {
            foreach (var kv in routeResult.Metadata)
                conversation.SetMetadata(kv.Key, kv.Value);
            await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        await _switchboard.AssignToQueueAsync(conversation.ConversationId, tenantId, routeResult.QueueId, ct).ConfigureAwait(false);
    }

    private async Task RunBotAsync(
        TenantId tenantId, InboundMessage inbound, Conversation conversation, EntityId botId, CancellationToken ct)
    {
        var botResponse = await _virtualAgent.ProcessMessageAsync(
            conversation.ConversationId, tenantId, inbound.Content, ct).ConfigureAwait(false);

        switch (botResponse.Action)
        {
            case BotResponseAction.Reply when botResponse.Messages is not null:
                foreach (var reply in botResponse.Messages)
                {
                    await _conversationService.SendMessageAsync(
                        conversation.ConversationId, tenantId, reply, botId, ConversationOwnerKind.Bot, ct).ConfigureAwait(false);
                }

                break;

            case BotResponseAction.TransferToQueue when botResponse.TargetQueueId is null:
                LogTransferWithoutQueue(conversation.ConversationId.Value);
                break;

            case BotResponseAction.TransferToQueue:
                // C6 (explicit capture): apply the bot flow's captured metadata (non-"__" flow variables) onto the
                // conversation at the bot→queue handoff, BEFORE the transfer. The bot runs AFTER routing, so
                // FlowMetadata intentionally OVERWRITES the implicit C5 reasonPath — explicit wins.
                // TransferToQueueAsync reloads + persists, so this metadata round-trips through the transfer.
                if (botResponse.FlowMetadata is { Count: > 0 })
                {
                    foreach (var kv in botResponse.FlowMetadata)
                        conversation.SetMetadata(kv.Key, kv.Value);
                    await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
                }

                await _switchboard.TransferToQueueAsync(
                    conversation.ConversationId, tenantId, botResponse.TargetQueueId.Value, ct).ConfigureAwait(false);
                _eventBus.Publish(new ConversationStateChangedEvent(
                    tenantId.Value, conversation.ConversationId.Value, "Bot", "Queued"));
                break;

            case BotResponseAction.EndConversation:
                await _lifecycleService.CloseAsync(tenantId, conversation.ConversationId, ct).ConfigureAwait(false);
                break;
        }
    }

    [LoggerMessage(
        EventId = 7410,
        Level = LogLevel.Debug,
        Message = "Webhook message {MessageId} for tenant {TenantId} was already stored; replay skipped with no side effect.")]
    private partial void LogDuplicateSkipped(string tenantId, string messageId);

    [LoggerMessage(
        EventId = 7411,
        Level = LogLevel.Warning,
        Message = "Webhook routing resolved no queue for tenant {TenantId} conversation {ConversationId}; conversation left queued and unowned for manual pickup.")]
    private partial void LogNoRoute(Exception ex, string tenantId, string conversationId);

    [LoggerMessage(
        EventId = 7412,
        Level = LogLevel.Error,
        Message = "Webhook side effects failed for tenant {TenantId} conversation {ConversationId} message {MessageId}; the message is stored and the rest of the delivery continues.")]
    private partial void LogDispatchFailed(Exception ex, string tenantId, string conversationId, string messageId);

    [LoggerMessage(
        EventId = 7413,
        Level = LogLevel.Warning,
        Message = "Bot requested TransferToQueue for conversation {ConversationId} without a target queue; skipping handoff.")]
    private partial void LogTransferWithoutQueue(string conversationId);
}
