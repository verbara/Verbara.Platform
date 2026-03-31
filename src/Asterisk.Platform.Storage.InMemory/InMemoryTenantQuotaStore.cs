using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantQuotaStore : ITenantQuotaStore
{
    private readonly ConcurrentDictionary<TenantId, TenantQuota> _quotas = new();

    public Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        _quotas.TryGetValue(tenantId, out var quota);
        return Task.FromResult(quota);
    }

    public Task UpsertAsync(TenantQuota quota, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quota);
        _quotas[quota.TenantId] = quota;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, CancellationToken ct)
    {
        _quotas.TryRemove(tenantId, out _);
        return Task.CompletedTask;
    }
}
