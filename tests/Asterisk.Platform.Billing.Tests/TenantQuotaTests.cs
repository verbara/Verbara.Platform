using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class TenantQuotaTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void TenantQuota_ShouldHaveDefaults()
    {
        var quota = new TenantQuota { TenantId = Tenant1 };

        quota.MaxConcurrentChannels.Should().Be(100);
        quota.MaxActiveCampaigns.Should().Be(10);
        quota.MaxMonthlyVoiceMinutes.Should().BeNull();
        quota.MaxMonthlyMessages.Should().BeNull();
        quota.MaxStorageBytes.Should().BeNull();
        quota.MaxActiveAgents.Should().BeNull();
        quota.QuotaAction.Should().Be(QuotaAction.Warn);
    }

    [Fact]
    public void TenantQuota_ShouldAllowCustomLimits()
    {
        var quota = new TenantQuota
        {
            TenantId = Tenant1,
            MaxConcurrentChannels = 200,
            MaxActiveCampaigns = 50,
            MaxMonthlyVoiceMinutes = 10_000,
            MaxMonthlyMessages = 50_000,
            MaxStorageBytes = 10L * 1024 * 1024 * 1024,
            MaxActiveAgents = 100,
            QuotaAction = QuotaAction.HardBlock,
        };

        quota.MaxConcurrentChannels.Should().Be(200);
        quota.MaxActiveCampaigns.Should().Be(50);
        quota.MaxMonthlyVoiceMinutes.Should().Be(10_000);
        quota.MaxMonthlyMessages.Should().Be(50_000);
        quota.MaxStorageBytes.Should().Be(10L * 1024 * 1024 * 1024);
        quota.MaxActiveAgents.Should().Be(100);
        quota.QuotaAction.Should().Be(QuotaAction.HardBlock);
    }

    [Fact]
    public void TenantQuota_ShouldImplementITenantScoped()
    {
#pragma warning disable CA1859
        ITenantScoped scoped = new TenantQuota { TenantId = Tenant1 };
#pragma warning restore CA1859
        scoped.TenantId.Should().Be(Tenant1);
    }
}

public class QuotaCheckResultTests
{
    [Fact]
    public void QuotaCheckResult_ShouldHoldAllProperties()
    {
        var result = new QuotaCheckResult(true, null, 45.0);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.UsagePercent.Should().Be(45.0);
    }

    [Fact]
    public void QuotaCheckResult_ShouldRepresentDenied()
    {
        var result = new QuotaCheckResult(false, "Monthly voice minutes exceeded", 100.5);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("Monthly voice minutes exceeded");
        result.UsagePercent.Should().Be(100.5);
    }
}
