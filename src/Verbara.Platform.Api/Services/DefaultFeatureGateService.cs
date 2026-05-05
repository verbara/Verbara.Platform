using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

internal sealed class DefaultFeatureGateService : IFeatureGateService
{
    private static readonly IReadOnlySet<PlanFeature> EmptyFeatures = new HashSet<PlanFeature>().AsReadOnly();

    private readonly FeatureGateCache _cache;

    public DefaultFeatureGateService(FeatureGateCache cache)
    {
        _cache = cache;
    }

    public bool IsFeatureEnabled(string tenantId, PlanFeature feature)
        => _cache.Get(tenantId)?.Features.Contains(feature) ?? false;

    public IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId)
        => _cache.Get(tenantId)?.Features ?? EmptyFeatures;

    public int GetMaxChannels(string tenantId)
        => _cache.Get(tenantId)?.MaxChannels ?? PlanDefinition.GetMaxChannels(TenantPlan.Starter);

    public int GetAuditRetentionDays(string tenantId)
        => _cache.Get(tenantId)?.AuditRetentionDays ?? PlanDefinition.GetAuditRetentionDays(TenantPlan.Starter);

    public int GetMaxWebhookSubscriptions(string tenantId)
        => _cache.Get(tenantId)?.MaxWebhookSubscriptions ?? PlanDefinition.GetMaxWebhookSubscriptions(TenantPlan.Starter);

    public int GetMaxScheduledReports(string tenantId)
        => _cache.Get(tenantId)?.MaxScheduledReports ?? PlanDefinition.GetMaxScheduledReports(TenantPlan.Starter);
}
