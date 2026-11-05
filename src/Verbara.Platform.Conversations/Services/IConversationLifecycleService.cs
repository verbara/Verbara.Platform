using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public interface IConversationLifecycleService
{
    Task<Conversation> CreateAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

    /// <summary>Creates a WebChat conversation that records <paramref name="sessionAddress"/> in the insert that creates it.</summary>
    Task<Conversation> CreateForWebChatSessionAsync(TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, CancellationToken ct);
    Task TransitionAsync(TenantId tenantId, EntityId conversationId, ConversationState newState, CancellationToken ct);
    Task CloseAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
