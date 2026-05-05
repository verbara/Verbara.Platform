using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Pipeline;

public sealed class MessagePersistenceStep : IPipelineStep
{
    private readonly IMessageStore _messageStore;

    public MessagePersistenceStep(IMessageStore messageStore)
    {
        _messageStore = messageStore;
    }

    public async Task<PipelineContext> ExecuteAsync(PipelineContext context, CancellationToken ct)
    {
        var conversation = context.Conversation
            ?? throw new InvalidOperationException("Conversation must be resolved before message persistence.");

        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = context.TenantId,
            Direction = MessageDirection.Inbound,
            Channel = context.Channel,
            SenderId = context.InboundMessage.From.Address,
            Content = context.InboundMessage.Content,
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = context.InboundMessage.ExternalMessageId,
            CreatedAt = context.InboundMessage.Timestamp,
            DeliveredAt = context.InboundMessage.Timestamp,
        };

        await _messageStore.SaveAsync(message, ct);
        context.PersistedMessage = message;

        return context;
    }
}
