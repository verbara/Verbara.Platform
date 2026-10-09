using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Message> _items = new();

    // Serialises the check-then-write paths (provider-id uniqueness, monotonic status, the Sent stamp).
    private readonly Lock _writeLock = new();

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
        lock (_writeLock)
        {
            // Monotonic (design D9): a replayed or late callback never moves a message backwards or out of Failed.
            if (_items.TryGetValue((tenantId, messageId), out var message)
                && MessageDeliveryStatusRules.CanAdvance(message.DeliveryStatus, status))
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

    public Task MarkSentAsync(TenantId tenantId, EntityId messageId, string? externalMessageId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (_items.TryGetValue((tenantId, messageId), out var message))
            {
                // ExternalMessageId is init-only: swap in a copy carrying the provider id and (from Pending) Sent,
                // in one dictionary write — the in-memory analogue of the single UPDATE in Postgres.
                _items[(tenantId, messageId)] = CopyWith(
                    message,
                    externalMessageId ?? message.ExternalMessageId,
                    message.DeliveryStatus == MessageDeliveryStatus.Pending ? MessageDeliveryStatus.Sent : message.DeliveryStatus);
            }
        }

        return Task.CompletedTask;
    }

    public Task<Message> InsertInboundIfAbsentAsync(Message message, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (message.ExternalMessageId is not null)
            {
                var existing = _items.Values.FirstOrDefault(m =>
                    m.TenantId == message.TenantId && m.ExternalMessageId == message.ExternalMessageId);
                if (existing is not null)
                    return Task.FromResult(existing);
            }

            _items[(message.TenantId, message.MessageId)] = message;
            return Task.FromResult(message);
        }
    }

    public Task<Message?> FindLastInboundAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var result = _items.Values
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.MessageId.Value, StringComparer.Ordinal)
            .FirstOrDefault();

        return Task.FromResult(result);
    }

    private static Message CopyWith(Message m, string? externalMessageId, MessageDeliveryStatus status) => new()
    {
        MessageId = m.MessageId,
        ConversationId = m.ConversationId,
        TenantId = m.TenantId,
        Direction = m.Direction,
        Channel = m.Channel,
        SenderId = m.SenderId,
        Content = m.Content,
        DeliveryStatus = status,
        ExternalMessageId = externalMessageId,
        CreatedAt = m.CreatedAt,
        DeliveredAt = m.DeliveredAt,
        ReadAt = m.ReadAt,
        UpdatedAt = m.UpdatedAt,
        CreatedBy = m.CreatedBy,
        UpdatedBy = m.UpdatedBy,
    };

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
