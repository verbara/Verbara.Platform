using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantAddOnStore : ITenantAddOnStore
{
    private readonly ConcurrentDictionary<(string TenantId, PlanFeature Feature), TenantAddOn> _store = new();

    public Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var result = _store.Values
            .Where(a => a.TenantId == tenantId)
            .ToList();
        return Task.FromResult<IReadOnlyList<TenantAddOn>>(result);
    }

    public Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default)
    {
        _store[(addOn.TenantId, addOn.Feature)] = addOn;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default)
    {
        _store.TryRemove((tenantId, feature), out _);
        return Task.CompletedTask;
    }
}
