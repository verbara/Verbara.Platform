using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Core.Tests;

public sealed class TenantExtensionsTests
{
    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenMetadataNull()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenKeyMissing()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = new() };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnTier_WhenMetadataSet()
    {
        var tenant = new Tenant
        {
            TenantId = "t1", Name = "T1",
            Metadata = new() { ["RateLimitTier"] = "Enterprise" },
        };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenInvalidValue()
    {
        var tenant = new Tenant
        {
            TenantId = "t1", Name = "T1",
            Metadata = new() { ["RateLimitTier"] = "InvalidTier" },
        };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetPlan_ShouldReturnStarter_WhenMetadataNull()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
        tenant.GetPlan().Should().Be(TenantPlan.Starter);
    }

    [Fact]
    public void GetPlan_ShouldReturnStarter_WhenKeyMissing()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = new() };
        tenant.GetPlan().Should().Be(TenantPlan.Starter);
    }

    [Fact]
    public void GetPlan_ShouldReturnPlan_WhenMetadataSet()
    {
        var tenant = new Tenant
        {
            TenantId = "t1", Name = "T1",
            Metadata = new() { ["Plan"] = "Enterprise" },
        };
        tenant.GetPlan().Should().Be(TenantPlan.Enterprise);
    }

    [Fact]
    public void SetPlan_ShouldSetMetadata()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = new() };
        tenant.SetPlan(TenantPlan.Pro);
        tenant.Metadata.Should().ContainKey("Plan").WhoseValue.Should().Be("Pro");
    }

    [Fact]
    public void SetPlan_ShouldBeNoOp_WhenMetadataNull()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
        tenant.SetPlan(TenantPlan.Enterprise);
        tenant.Metadata.Should().BeNull();
    }
}
