using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public interface IConversationService
{
    Task<Message> SendMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope envelope,
        EntityId senderId,
        ConversationOwnerKind senderKind,
        CancellationToken ct);

    Task<Conversation> GetOrCreateForContactAsync(
        TenantId tenantId,
        EntityId contactId,
        ChannelType channel,
        CancellationToken ct);
}
