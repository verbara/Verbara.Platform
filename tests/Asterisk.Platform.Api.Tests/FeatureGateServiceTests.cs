using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class FeatureGateServiceTests
{
    private readonly FeatureGateCache _cache = new();

    private DefaultFeatureGateService CreateService() => new(_cache);

    [Fact]
    public void IsFeatureEnabled_ShouldReturnFalse_WhenStarterAndDialer()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Starter,
            PlanDefinition.GetFeatures(TenantPlan.Starter),
            3, 7, 0, 0));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeFalse();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnTrue_WhenProAndDialer()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Pro,
            PlanDefinition.GetFeatures(TenantPlan.Pro),
            7, 30, 5, 5));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeTrue();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnTrue_WhenStarterWithAddOn()
    {
        var features = new HashSet<PlanFeature>(PlanDefinition.GetFeatures(TenantPlan.Starter))
        {
            PlanFeature.Dialer,
        };
        _cache.Set("t1", new ResolvedFeatures(TenantPlan.Starter, features.AsReadOnly(), 3, 7, 0, 0));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeTrue();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnFalse_WhenNotInCache()
    {
        var service = CreateService();
        service.IsFeatureEnabled("unknown", PlanFeature.Dialer).Should().BeFalse();
    }

    [Fact]
    public void GetMaxChannels_ShouldReturnCachedValue()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Pro,
            PlanDefinition.GetFeatures(TenantPlan.Pro),
            7, 30, 5, 5));

        var service = CreateService();
        service.GetMaxChannels("t1").Should().Be(7);
    }

    [Fact]
    public void GetEnabledFeatures_ShouldReturnEmpty_WhenNotInCache()
    {
        var service = CreateService();
        service.GetEnabledFeatures("unknown").Should().BeEmpty();
    }
}
