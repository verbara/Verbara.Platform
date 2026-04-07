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
}
