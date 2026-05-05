using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryDispositionStore : IDispositionStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Disposition> _items = new();

    public Task<Disposition?> GetByIdAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, dispositionId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<Disposition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var result = _items.Values
            .Where(d => d.TenantId == tenantId)
            .ToList();

        return Task.FromResult<IReadOnlyList<Disposition>>(result);
    }

    public Task SaveAsync(Disposition disposition, CancellationToken ct)
    {
        _items[(disposition.TenantId, disposition.DispositionId)] = disposition;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, dispositionId), out _);
        return Task.CompletedTask;
    }
}
