using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core;

public static class TenantExtensions
{
    private const string RateLimitTierKey = "RateLimitTier";

    public static RateLimitTier GetRateLimitTier(this Tenant tenant)
        => tenant.Metadata?.GetValueOrDefault(RateLimitTierKey) is string s
            && Enum.TryParse<RateLimitTier>(s, out var tier) ? tier : RateLimitTier.Standard;

    public static void SetRateLimitTier(this Tenant tenant, RateLimitTier tier)
    {
        if (tenant.Metadata is not null)
            tenant.Metadata[RateLimitTierKey] = tier.ToString();
    }

    private const string PlanKey = "Plan";

    public static TenantPlan GetPlan(this Tenant tenant)
        => tenant.Metadata?.GetValueOrDefault(PlanKey) is string s
            && Enum.TryParse<TenantPlan>(s, out var plan) ? plan : TenantPlan.Starter;

    public static void SetPlan(this Tenant tenant, TenantPlan plan)
    {
        if (tenant.Metadata is not null)
            tenant.Metadata[PlanKey] = plan.ToString();
    }
}
