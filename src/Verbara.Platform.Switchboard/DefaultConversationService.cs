using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Switchboard;

internal sealed partial class DefaultConversationService : IConversationService
{
    private readonly IConversationStore _conversationStore;
    private readonly IContactStore _contactStore;
    private readonly IMessageStore _messageStore;
    private readonly IChannelRegistry _channelRegistry;
    private readonly IConversationLifecycleService _lifecycleService;
    private readonly IClock _clock;
    private readonly ILogger<DefaultConversationService> _logger;
    private readonly PlatformEventBus _eventBus;
    private readonly IWhatsAppSessionWindow _whatsAppWindow;

    public DefaultConversationService(
        IConversationStore conversationStore,
        IContactStore contactStore,
        IMessageStore messageStore,
        IChannelRegistry channelRegistry,
        IConversationLifecycleService lifecycleService,
        IClock clock,
        ILogger<DefaultConversationService> logger,
        PlatformEventBus eventBus,
        IWhatsAppSessionWindow whatsAppWindow)
    {
        _conversationStore = conversationStore;
        _contactStore = contactStore;
        _messageStore = messageStore;
        _channelRegistry = channelRegistry;
        _lifecycleService = lifecycleService;
        _clock = clock;
        _logger = logger;
        _eventBus = eventBus;
        _whatsAppWindow = whatsAppWindow;
    }

    /// <summary>Stores an outbound message on the conversation and hands it to the channel's connector.</summary>
    /// <remarks>
    /// A send a channel rule forbids (a WhatsApp reply outside the 24-hour window without a template) reaches no
    /// provider and is stored as <see cref="MessageDeliveryStatus.Failed"/>: the system callers of this method (bot
    /// replies, automations, surveys) have nobody to show a refusal to. The agent reply path uses
    /// <see cref="TrySendMessageAsync"/>, which stores nothing and returns the refusal code.
    /// </remarks>
    public async Task<Message> SendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        CancellationToken ct)
    {
        var outcome = await SendCoreAsync(
            conversationId, tenantId, envelope, senderId, senderKind, templateId: null, storeRefusal: true, ct).ConfigureAwait(false);
        return outcome.Message!;
    }

    public Task<MessageSendOutcome> TrySendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        string? templateId,
        CancellationToken ct) =>
        SendCoreAsync(conversationId, tenantId, envelope, senderId, senderKind, templateId, storeRefusal: false, ct);

    private async Task<MessageSendOutcome> SendCoreAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        string? templateId,
        bool storeRefusal,
        CancellationToken ct)
    {
        var conversation = await _conversationStore.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found for tenant '{tenantId}'.");

        ValidateOwnership(conversation, senderId, senderKind);
        ValidateState(conversation);

        // WhatsApp's customer-service window, read from the stored inbound history (whatsapp-outbound, design D7).
        string? refusalCode = null;
        if (conversation.Channel == ChannelType.WhatsApp)
        {
            var window = await _whatsAppWindow.DecideAsync(tenantId, conversationId, templateId, ct).ConfigureAwait(false);
            refusalCode = window.RefusalCode;
        }

        if (refusalCode is not null)
        {
            LogSendRefused(_logger, conversationId.Value, refusalCode);
            if (!storeRefusal)
                return MessageSendOutcome.Refused(refusalCode);
        }

        // The send claim (conversations-close-after-inactivity, design D9): the row must still be sendable right before
        // the message is stored, so an inactivity close that committed after ValidateState refuses the send with
        // ValidateState's own exception (the send endpoint's 400) and nothing is stored or sent. The clock moves only
        // for a message stored Pending for a connector; a refused send is not activity (D2). A failure after this
        // point (provider error, no address) keeps the clock it advanced.
        var now = _clock.UtcNow;
        if (!await _conversationStore.TryClaimForSendAsync(
                tenantId, conversationId, now, advanceClock: refusalCode is null, ct).ConfigureAwait(false))
        {
            var current = await _conversationStore.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Cannot send message in conversation state '{(current is null ? "Deleted" : current.State.ToString())}'.");
        }

        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = conversation.Channel,
            SenderId = senderId.Value,
            Content = envelope,
            DeliveryStatus = refusalCode is null ? MessageDeliveryStatus.Pending : MessageDeliveryStatus.Failed,
            CreatedAt = now,
        };

        await _messageStore.SaveAsync(message, ct).ConfigureAwait(false);

        if (refusalCode is null)
            await DeliverAsync(conversation, message, templateId, ct).ConfigureAwait(false);

        // The live-stream audience (contact-history-opens-by-assignment, design D17), stamped from the conversation this
        // send loaded; the send changes no ownership. A transfer another request commits during the provider delivery
        // is not seen here (design D17 "Staleness"): it applies from the next message.
        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversationId.Value, message.MessageId.Value,
            "Outbound", conversation.Channel.ToString())
        {
            Audience = ConversationOffer.AudienceOf(conversation),
        });

        return MessageSendOutcome.Stored(message);
    }

    private async Task DeliverAsync(Conversation conversation, Message message, string? templateId, CancellationToken ct)
    {
        var tenantId = conversation.TenantId;
        var contact = await _contactStore.GetByIdAsync(tenantId, conversation.ContactId, ct).ConfigureAwait(false);
        var address = contact?.FindAddress(conversation.Channel);

        if (address is null)
        {
            // Nowhere to send it (on WhatsApp also a contact known only by a business-scoped user id, which the
            // Cloud API cannot send to yet): the message fails through the same write a provider refusal takes, so
            // the sender sees it did not go out instead of a Pending that never resolves.
            await _messageStore.UpdateDeliveryStatusAsync(
                tenantId, message.MessageId, MessageDeliveryStatus.Failed, null, ct).ConfigureAwait(false);
            message.DeliveryStatus = MessageDeliveryStatus.Failed;
            LogNoAddress(_logger, conversation.ConversationId.Value, conversation.Channel.ToString(), message.MessageId.Value);
            return;
        }

        var connector = _channelRegistry.GetConnector(conversation.Channel);
        var outbound = new OutboundMessage(address, message.Content, tenantId, conversation.ConversationId, templateId);
        var result = await connector.SendAsync(outbound, ct).ConfigureAwait(false);

        if (result.Success)
        {
            // The provider id and Sent land in one write (message-delivery-correlation, design D8): the
            // provider's sent/delivered callback can arrive before a second write would have committed.
            await _messageStore.MarkSentAsync(tenantId, message.MessageId, result.ExternalMessageId, ct).ConfigureAwait(false);
            message.DeliveryStatus = MessageDeliveryStatus.Sent;
        }
        else
        {
            await _messageStore.UpdateDeliveryStatusAsync(
                tenantId, message.MessageId, MessageDeliveryStatus.Failed, null, ct).ConfigureAwait(false);
            message.DeliveryStatus = MessageDeliveryStatus.Failed;
            LogSendFailed(_logger, conversation.ConversationId.Value, result.ErrorCode, result.ErrorMessage);
        }
    }

    public async Task<Conversation> GetOrCreateForContactAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelType channel,
        CancellationToken ct)
    {
        var existing = await _conversationStore.FindActiveByContactAsync(tenantId, contactId, channel, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        return await _lifecycleService.CreateAsync(tenantId, contactId, channel, ct).ConfigureAwait(false);
    }

    private static void ValidateOwnership(Conversation conversation, EntityId senderId, ConversationOwnerKind senderKind)
    {
        if (senderKind == ConversationOwnerKind.System)
            return;

        if (senderKind == ConversationOwnerKind.Agent)
        {
            var owner = conversation.Owner;
            if (owner is null || owner.Kind != ConversationOwnerKind.Agent || owner.OwnerId != senderId)
                throw new InvalidOperationException(
                    $"Agent '{senderId}' is not the owner of conversation '{conversation.ConversationId}'.");
        }

        if (senderKind == ConversationOwnerKind.Bot)
        {
            var owner = conversation.Owner;
            if (owner is null || owner.Kind != ConversationOwnerKind.Bot)
                throw new InvalidOperationException(
                    $"Conversation '{conversation.ConversationId}' is not owned by a bot.");
        }
    }

    private static void ValidateState(Conversation conversation)
    {
        var valid = conversation.State is ConversationState.Active
            or ConversationState.OnHold
            or ConversationState.Consulting;

        if (!valid)
            throw new InvalidOperationException(
                $"Cannot send message in conversation state '{conversation.State}'.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel send failed for conversation {ConversationId}: [{ErrorCode}] {ErrorMessage}")]
    private static partial void LogSendFailed(ILogger logger, string conversationId, string? errorCode, string? errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Send refused for conversation {ConversationId}: {RefusalCode}; nothing reached the provider.")]
    private static partial void LogSendRefused(ILogger logger, string conversationId, string refusalCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No sendable {Channel} address on the contact of conversation {ConversationId} (none stored, or on WhatsApp only a business-scoped user id); message {MessageId} marked Failed, nothing reached the provider.")]
    private static partial void LogNoAddress(ILogger logger, string conversationId, string channel, string messageId);
}
