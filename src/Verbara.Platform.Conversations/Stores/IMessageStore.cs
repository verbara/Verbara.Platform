using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Stores;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken ct);
    Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct);
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct);
    Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct);
    Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct);

    /// <summary>Returns all messages across multiple conversations (GDPR export).</summary>
    Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes all messages for the given conversations and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes messages whose conversation no longer exists (retention cleanup).</summary>
    Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct);
}
