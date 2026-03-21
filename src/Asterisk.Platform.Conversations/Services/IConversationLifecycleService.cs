using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Services;

public interface IConversationLifecycleService
{
    Task<Conversation> CreateAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);
    Task TransitionAsync(TenantId tenantId, EntityId conversationId, ConversationState newState, CancellationToken ct);
    Task CloseAsync(TenantId tenantId, EntityId conversationId, WrapUpRecord? wrapUp, CancellationToken ct);
}
