using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Message> _items = new();

    public Task SaveAsync(Message message, CancellationToken ct)
    {
        _items[(message.TenantId, message.MessageId)] = message;
        return Task.CompletedTask;
    }

    public Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, messageId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct)
    {
        IReadOnlyList<Message> result = _items.Values
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult(result);
    }

    public Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct)
    {
        if (_items.TryGetValue((tenantId, messageId), out var message))
        {
            message.DeliveryStatus = status;

            if (timestamp.HasValue)
            {
                if (status == MessageDeliveryStatus.Delivered)
                    message.DeliveredAt = timestamp;
                else if (status == MessageDeliveryStatus.Read)
                    message.ReadAt = timestamp;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.ExternalMessageId == externalMessageId);

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        var idSet = new HashSet<EntityId>(conversationIds);
        IReadOnlyList<Message> result = _items.Values
            .Where(m => m.TenantId == tenantId && idSet.Contains(m.ConversationId))
            .OrderBy(m => m.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        var idSet = new HashSet<EntityId>(conversationIds);
        var toDelete = _items
            .Where(kv => kv.Value.TenantId == tenantId && idSet.Contains(kv.Value.ConversationId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct)
    {
        // This requires knowing which conversations still exist — in-memory approximation:
        // We cannot query the conversation store from here, so orphaned = messages whose
        // conversation_id is not present in our own message set is not meaningful.
        // Instead, return 0 — orphan detection only works via SQL JOIN in Postgres.
        return Task.FromResult(0);
    }
}
