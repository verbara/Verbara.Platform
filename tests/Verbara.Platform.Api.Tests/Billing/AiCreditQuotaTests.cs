using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Tests.Billing;

public sealed class AiCreditQuotaTests
{
    private static DefaultQuotaEnforcementService Build(long? aiCredits, decimal consumedTokens, QuotaAction action)
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        quotaStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = new TenantId("t1"), AiCreditsMonthly = aiCredits, QuotaAction = action });
        var usageStore = Substitute.For<IUsageRecordStore>();
        usageStore.GetSummaryByTypeAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new UsageSummary { TenantId = new TenantId("t1"), PeriodStart = default, PeriodEnd = default,
                UsageType = UsageType.AiAnalysis, TotalQuantity = consumedTokens, RecordCount = 1, LastUpdatedAt = default });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero));
        var ledger = Substitute.For<ICreditLedgerStore>();
        return new DefaultQuotaEnforcementService(quotaStore, usageStore, clock, ledger,
            Options.Create(new PlatformLlmOptions { CreditTokenRatio = 1000 }));
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenUnderCreditAllowance()
    {
        var svc = Build(aiCredits: 10, consumedTokens: 5000m, QuotaAction.SoftBlock); // 5 of 10 credits
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldSoftBlock_WhenCreditAllowanceExhausted()
    {
        var svc = Build(aiCredits: 10, consumedTokens: 10000m, QuotaAction.SoftBlock); // 10 of 10 credits
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllowUnlimited_WhenAiCreditsMonthlyNull()
    {
        var svc = Build(aiCredits: null, consumedTokens: 999999m, QuotaAction.HardBlock);
        var r = await svc.CheckQuotaAsync(new TenantId("t1"), UsageType.AiAnalysis, 1m, CancellationToken.None);
        r.Allowed.Should().BeTrue(); // null = unlimited
    }
}
