using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pins the <c>per-tenant</c> rate-limit partition resolution.
///
/// <para>
/// Historically <c>UseRateLimiter()</c> ran <b>before</b> <c>TenantResolutionMiddleware</c>,
/// so <c>Items["TenantId"]</c> was unset when the partition resolver ran and every request
/// collapsed to the shared <c>"__global__"</c> partition (→ Unlimited tier → NoLimiter): the
/// generic per-tenant limit never throttled anyone, and two tenants shared one bucket. The
/// pipeline reorder (limiter now runs after tenant resolution) makes <c>Items["TenantId"]</c>
/// available; these tests pin that two resolved tenants get <b>independent</b> partitions.
/// </para>
/// </summary>
public sealed class TenantRateLimitPolicyTests
{
    private static DefaultHttpContext ContextFor(string? tenantId, TenantTierCache? tierCache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tierCache ?? new TenantTierCache());
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (tenantId is not null)
            context.Items["TenantId"] = tenantId;
        return context;
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldPartitionTenantsIndependently_WhenTwoTenantsResolved()
    {
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Standard);
        cache.SetTier("tenant-b", RateLimitTier.Standard);

        var a = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-a", cache));
        var b = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-b", cache));

        a.PartitionKey.Should().Be("tenant-a");
        b.PartitionKey.Should().Be("tenant-b");
        a.PartitionKey.Should().NotBe(
            b.PartitionKey,
            because: "each resolved tenant must get its own bucket, not collapse to __global__");
        a.Tier.IsUnlimited().Should().BeFalse(
            because: "a resolved tenant on a limited tier must actually be throttled");
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldUseConfiguredTier_WhenTierCacheHasTenant()
    {
        var cache = new TenantTierCache();
        cache.SetTier("tenant-a", RateLimitTier.Standard);

        var target = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor("tenant-a", cache));

        target.PartitionKey.Should().Be("tenant-a");
        target.Tier.Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void ResolvePerTenantTarget_ShouldFallBackToGlobalUnlimited_WhenTenantUnresolved()
    {
        var target = TenantRateLimitPolicy.ResolvePerTenantTarget(ContextFor(tenantId: null));

        target.PartitionKey.Should().Be("__global__");
        target.Tier.Should().Be(
            RateLimitTier.Unlimited,
            because: "an unresolvable tenant lands in the shared bucket, never a per-tenant throttle");
    }
}
