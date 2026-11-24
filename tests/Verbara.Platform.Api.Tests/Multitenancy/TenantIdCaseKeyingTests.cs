using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// Per-tenant state keyed by tenant id must key it exactly: <c>ACME</c> and <c>acme</c> are two tenants.
/// </summary>
public sealed class TenantIdCaseKeyingTests
{
    [Fact]
    public void FeatureGateCache_ShouldNotReturnEntryOfOtherTenant_WhenIdsDifferOnlyInCase()
    {
        var cache = new FeatureGateCache();
        cache.Set("ACME", new ResolvedFeatures(TenantPlan.Enterprise, new HashSet<PlanFeature>(), 10, 365, 10, 10));

        cache.Get("acme").Should().BeNull(because: "'acme' is a different tenant from 'ACME'");
        cache.Get("ACME").Should().NotBeNull();
    }

    [Fact]
    public void TenantTierCache_ShouldNotReturnTierOfOtherTenant_WhenIdsDifferOnlyInCase()
    {
        var cache = new TenantTierCache();
        var nonDefault = Enum.GetValues<RateLimitTier>().First(t => t != RateLimitTier.Standard);
        cache.SetTier("ACME", nonDefault);

        cache.GetTier("acme").Should().Be(RateLimitTier.Standard, because: "'acme' is a different tenant from 'ACME'");
        cache.GetTier("ACME").Should().Be(nonDefault);
    }

    [Fact]
    public async Task InMemoryTenantStore_ShouldKeepBothTenants_WhenIdsDifferOnlyInCase()
    {
        var store = new InMemoryTenantStore();
        await store.UpsertAsync(new Tenant { TenantId = "ACME", Name = "upper", Status = TenantStatus.Active, Type = TenantType.Customer });
        await store.UpsertAsync(new Tenant { TenantId = "acme", Name = "lower", Status = TenantStatus.Active, Type = TenantType.Customer });

        (await store.GetAsync("ACME"))!.Name.Should().Be("upper");
        (await store.GetAsync("acme"))!.Name.Should().Be("lower");
        (await store.GetAsync("Acme")).Should().BeNull(because: "the database keys tenant ids exactly");
    }
}
