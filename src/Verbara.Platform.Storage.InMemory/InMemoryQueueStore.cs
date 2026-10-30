using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.InMemory;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Mirrors the domain entity naming")]
internal sealed class InMemoryQueueStore : IQueueStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Queue> _items = new();

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, queueId), out var item);
        return Task.FromResult(item);
    }

    // Tenant-scoped, exact (ordinal) name; names are not unique, so the active queue wins, then the lowest id —
    // the order PostgresQueueStore.GetByNameAsync applies (dialer-places-calls-end-to-end, design D11).
    public Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct)
    {
        var match = _items.Values
            .Where(q => q.TenantId == tenantId && string.Equals(q.Name, name, StringComparison.Ordinal))
            .OrderByDescending(q => q.IsActive)
            .ThenBy(q => q.QueueId.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(q => q.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Queue>(items, totalCount, query.Page, query.PageSize));
    }

    public Task<IReadOnlyList<Queue>> ListPageAfterAsync(TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Ordinal order on queue_id: consistent with itself, which is all a keyset walk needs.
        string? after = afterQueueId is { } afterId ? afterId.Value : null;
        IReadOnlyList<Queue> page = _items.Values
            .Where(q => q.TenantId == tenantId)
            .Where(q => after is null || string.CompareOrdinal(q.QueueId.Value, after) > 0)
            .OrderBy(q => q.QueueId.Value, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return Task.FromResult(page);
    }

    public Task SaveAsync(Queue queue, CancellationToken ct)
    {
        _items[(queue.TenantId, queue.QueueId)] = queue;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, queueId), out _);
        return Task.CompletedTask;
    }
}
