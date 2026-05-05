using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Billing.Tests;

public class DefaultQuotaEnforcementServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static (DefaultQuotaEnforcementService Service, ITenantQuotaStore QuotaStore, IUsageRecordStore UsageStore) Build()
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultQuotaEnforcementService(quotaStore, usageStore, clock);
        return (service, quotaStore, usageStore);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenNoQuotaConfigured()
    {
        var (service, quotaStore, _) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>()).Returns((TenantQuota?)null);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().Be(0);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenNoLimitForType()
    {
        var (service, quotaStore, _) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1 });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().Be(0);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenUnderLimit()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000 });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 500m,
                RecordCount = 100,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().BeApproximately(51.0, 0.1);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDeny_WhenOverLimit_AndHardBlock()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.HardBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 995m,
                RecordCount = 200,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("VoiceInbound");
        result.UsagePercent.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenOverLimit_AndWarnAction()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.Warn });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 995m,
                RecordCount = 200,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().NotBeNull();
        result.UsagePercent.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDeny_WhenOverLimit_AndSoftBlock()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyMessages = 5000, QuotaAction = QuotaAction.SoftBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.SmsOutbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.SmsOutbound,
                TotalQuantity = 5000m,
                RecordCount = 5000,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.SmsOutbound, 1m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("SmsOutbound");
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldHandleNoExistingUsage()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.HardBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns((UsageSummary?)null);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 5m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public async Task GetQuotaStatusAsync_ShouldReturnQuotaAndUsage()
    {
        var (service, quotaStore, usageStore) = Build();
        var quota = new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000 };
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(quota);

        var summaries = new List<UsageSummary>
        {
            new()
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 500m,
                RecordCount = 100,
                LastUpdatedAt = FixedNow,
            },
        };
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(summaries);

        var status = await service.GetQuotaStatusAsync(Tenant1, CancellationToken.None);

        status.TenantId.Should().Be(Tenant1);
        status.Quota.Should().BeSameAs(quota);
        status.CurrentUsage.Should().BeSameAs(summaries);
    }
}
