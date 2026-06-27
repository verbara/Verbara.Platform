using System.Collections.Concurrent;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

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

    public Task<IReadOnlyList<TenantQuota>> ListWithAiCreditsAsync(CancellationToken ct)
    {
        // Snapshot of every quota that carries an AI-credit allowance — the mint-worker work-list.
        IReadOnlyList<TenantQuota> result = _quotas.Values
            .Where(q => q.AiCreditsMonthly is not null)
            .ToList();
        return Task.FromResult(result);
    }
}
