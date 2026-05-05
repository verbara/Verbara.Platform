using System.Collections.Concurrent;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

internal sealed record ResolvedFeatures(
    TenantPlan EffectivePlan,
    IReadOnlySet<PlanFeature> Features,
    int MaxChannels,
    int AuditRetentionDays,
    int MaxWebhookSubscriptions,
    int MaxScheduledReports);

internal sealed class FeatureGateCache
{
    private readonly ConcurrentDictionary<string, ResolvedFeatures> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedFeatures? Get(string tenantId)
        => _cache.GetValueOrDefault(tenantId);

    public void Set(string tenantId, ResolvedFeatures features)
        => _cache[tenantId] = features;

    public void Remove(string tenantId)
        => _cache.TryRemove(tenantId, out _);
}
