using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantRetentionPolicyStore : ITenantRetentionPolicyStore
{
    private readonly ConcurrentDictionary<string, TenantRetentionPolicy> _policies = new();

    public Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct)
    {
        _policies.TryGetValue(tenantId, out var policy);
        return Task.FromResult(policy);
    }

    public Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct)
    {
        _policies[policy.TenantId] = policy;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantRetentionPolicy> result = _policies.Values
            .Where(p => p.ConversationRetentionDays.HasValue
                     || p.AuthEventRetentionDays.HasValue
                     || p.AuditRetentionDays.HasValue
                     || p.UsageRecordRetentionDays.HasValue)
            .ToList();
        return Task.FromResult(result);
    }
}
