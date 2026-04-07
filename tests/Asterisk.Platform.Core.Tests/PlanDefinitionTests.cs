using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Core.Tests;

public sealed class PlanDefinitionTests
{
    [Fact]
    public void GetFeatures_ShouldReturnEmpty_WhenStarter()
    {
        PlanDefinition.GetFeatures(TenantPlan.Starter).Should().BeEmpty();
    }

    [Fact]
    public void GetFeatures_ShouldReturn8Features_WhenPro()
    {
        var features = PlanDefinition.GetFeatures(TenantPlan.Pro);
        features.Should().HaveCount(8);
        features.Should().Contain(PlanFeature.Dialer);
        features.Should().Contain(PlanFeature.Flows);
        features.Should().Contain(PlanFeature.Recordings);
    }

    [Fact]
    public void GetFeatures_ShouldReturnAll13Features_WhenEnterprise()
    {
        var all = Enum.GetValues<PlanFeature>().ToHashSet();
        PlanDefinition.GetFeatures(TenantPlan.Enterprise).Should().BeEquivalentTo(all);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnStandard_WhenStarter()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Starter).Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnProfessional_WhenPro()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Pro).Should().Be(RateLimitTier.Professional);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnEnterprise_WhenEnterprise()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Enterprise).Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn3_WhenStarter()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Starter).Should().Be(3);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn7_WhenPro()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Pro).Should().Be(7);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn11_WhenEnterprise()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Enterprise).Should().Be(11);
    }

    [Fact]
    public void GetMaxWebhookSubscriptions_ShouldReturn0_WhenStarter()
    {
        PlanDefinition.GetMaxWebhookSubscriptions(TenantPlan.Starter).Should().Be(0);
    }

    [Fact]
    public void StarterFeatures_ShouldNotContainDialer()
    {
        PlanDefinition.GetFeatures(TenantPlan.Starter).Should().NotContain(PlanFeature.Dialer);
    }
}
