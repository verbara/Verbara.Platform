using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Stores;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken ct);
    Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct);
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct);
    Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct);
    Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct);

    /// <summary>
    /// Records that the provider accepted an outbound message: stamps the provider's message id and moves the
    /// status to <see cref="MessageDeliveryStatus.Sent"/> in one atomic write, so a status webhook that arrives
    /// right after the provider's response already finds the message. A message whose status is already past
    /// <c>Pending</c> keeps it (monotonic, see <see cref="MessageDeliveryStatusRules"/>).
    /// </summary>
    Task MarkSentAsync(TenantId tenantId, EntityId messageId, string? externalMessageId, CancellationToken ct);

    /// <summary>
    /// Persists an inbound message unless the tenant already holds one with the same provider message id,
    /// and returns the stored message — the new one, or the existing one when a concurrent or replayed
    /// delivery got there first. A message without a provider id is always inserted.
    /// </summary>
    Task<Message> InsertInboundIfAbsentAsync(Message message, CancellationToken ct);

    /// <summary>The conversation's most recent inbound message, or <c>null</c> when it has none.</summary>
    Task<Message?> FindLastInboundAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);

    /// <summary>Returns all messages across multiple conversations (GDPR export).</summary>
    Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes all messages for the given conversations and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes messages whose conversation no longer exists (retention cleanup).</summary>
    Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct);
}
