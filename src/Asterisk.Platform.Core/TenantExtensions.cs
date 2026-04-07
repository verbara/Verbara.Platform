using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Core;

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
}
