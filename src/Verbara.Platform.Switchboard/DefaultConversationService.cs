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

    public DefaultConversationService(
        IConversationStore conversationStore,
        IContactStore contactStore,
        IMessageStore messageStore,
        IChannelRegistry channelRegistry,
        IConversationLifecycleService lifecycleService,
        IClock clock,
        ILogger<DefaultConversationService> logger,
        PlatformEventBus eventBus)
    {
        _conversationStore = conversationStore;
        _contactStore = contactStore;
        _messageStore = messageStore;
        _channelRegistry = channelRegistry;
        _lifecycleService = lifecycleService;
        _clock = clock;
        _logger = logger;
        _eventBus = eventBus;
    }

    public async Task<Message> SendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        CancellationToken ct)
    {
        var conversation = await _conversationStore.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found for tenant '{tenantId}'.");

        ValidateOwnership(conversation, senderId, senderKind);
        ValidateState(conversation);

        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = conversation.Channel,
            SenderId = senderId.Value,
            Content = envelope,
            DeliveryStatus = MessageDeliveryStatus.Pending,
            CreatedAt = _clock.UtcNow,
        };

        await _messageStore.SaveAsync(message, ct).ConfigureAwait(false);

        var contact = await _contactStore.GetByIdAsync(tenantId, conversation.ContactId, ct).ConfigureAwait(false);
        var address = contact?.FindAddress(conversation.Channel);

        if (address is not null)
        {
            var connector = _channelRegistry.GetConnector(conversation.Channel);
            var outbound = new OutboundMessage(address, envelope, tenantId, conversationId);
            var result = await connector.SendAsync(outbound, ct).ConfigureAwait(false);

            var status = result.Success ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Failed;
            var sentAt = result.Success ? _clock.UtcNow : (DateTimeOffset?)null;
            await _messageStore.UpdateDeliveryStatusAsync(tenantId, message.MessageId, status, sentAt, ct).ConfigureAwait(false);
            message.DeliveryStatus = status;

            if (!result.Success)
            {
                LogSendFailed(_logger, conversationId.Value, result.ErrorCode, result.ErrorMessage);
            }
        }
        else
        {
            LogNoAddress(_logger, conversationId.Value, conversation.Channel.ToString());
        }

        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversationId.Value, message.MessageId.Value,
            "Outbound", conversation.Channel.ToString()));

        return message;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No channel address found for conversation {ConversationId} on channel {Channel}; message persisted but not delivered.")]
    private static partial void LogNoAddress(ILogger logger, string conversationId, string channel);
}
