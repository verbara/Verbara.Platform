using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Stores;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken ct);
    Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct);
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct);
    Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct);
    Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct);
}
