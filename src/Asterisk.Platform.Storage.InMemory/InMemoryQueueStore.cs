using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.InMemory;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Mirrors the domain entity naming")]
internal sealed class InMemoryQueueStore : IQueueStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Queue> _items = new();

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, queueId), out var item);
        return Task.FromResult(item);
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
