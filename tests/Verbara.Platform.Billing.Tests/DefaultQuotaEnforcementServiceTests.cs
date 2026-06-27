using Microsoft.Extensions.Options;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

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

        var ledger = Substitute.For<ICreditLedgerStore>();
        var service = new DefaultQuotaEnforcementService(quotaStore, usageStore, clock, ledger,
            Options.Create(new PlatformLlmOptions { CreditTokenRatio = 1000 }));
        return (service, quotaStore, usageStore);
    }

    private static (DefaultQuotaEnforcementService Service, ITenantQuotaStore QuotaStore, IUsageRecordStore UsageStore) BuildPerDirection(
        long creditTokenRatio = 1000, long? inputRatio = 2000, long? outputRatio = 500)
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var ledger = Substitute.For<ICreditLedgerStore>();
        var service = new DefaultQuotaEnforcementService(quotaStore, usageStore, clock, ledger,
            Options.Create(new PlatformLlmOptions
            {
                CreditTokenRatio = creditTokenRatio,
                InputCreditTokenRatio = inputRatio,
                OutputCreditTokenRatio = outputRatio,
            }));
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

    [Fact]
    public async Task CheckQuotaAsync_ShouldUsePerDirectionCredits_WhenRatiosActive()
    {
        // input 300/2000 = 0.15, output 100/500 = 0.20 -> 0.35 credits, of a 10-credit allowance.
        var (service, quotaStore, usageStore) = BuildPerDirection(creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(300m, 100m, 0m));

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        // 0.35 credits / 10 limit = 3.5%
        result.UsagePercent.Should().BeApproximately(3.5, 0.001);
        await usageStore.DidNotReceive().GetSummaryByTypeAsync(Arg.Any<TenantId>(), Arg.Any<UsageType>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldFallBackToFlatRatio_WhenUnsplitTokens()
    {
        // unsplit 500 / flat 1000 = 0.5 credits of a 10-credit allowance.
        var (service, quotaStore, usageStore) = BuildPerDirection(creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(0m, 0m, 500m));

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        // 0.5 credits / 10 limit = 5%
        result.UsagePercent.Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldSumMixedBuckets_WithoutCrossContamination()
    {
        // 300/2000 + 100/500 + 500/1000 = 0.15 + 0.20 + 0.50 = 0.85 credits of a 10-credit allowance.
        var (service, quotaStore, usageStore) = BuildPerDirection(creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(300m, 100m, 500m));

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        // 0.85 credits / 10 limit = 8.5%
        result.UsagePercent.Should().BeApproximately(8.5, 0.001);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldUseFlatTokenPath_WhenPerDirectionRatiosAbsent()
    {
        // Only flat ratio configured -> existing summary-based token path, no breakdown query.
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.SoftBlock });
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 5000m, // 5 of 10 credits (× 1000 ratio = 5000 of 10000 tokens)
                RecordCount = 1,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().BeApproximately(50.0, 0.001);
        await usageStore.DidNotReceive().GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), Arg.Any<UsageType>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldExhaust_WhenDifferentiatedCreditsReachLimit()
    {
        // Differentiated credits == limit, plus additional pushes over -> deny under HardBlock.
        var (service, quotaStore, usageStore) = BuildPerDirection(creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        // input 20000/2000 = 10, output 0, unsplit 0 -> 10 credits == limit.
        usageStore.GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), UsageType.AiAnalysis,
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(20000m, 0m, 0m));

        // additionalQuantity 1000 tokens / flat 1000 = +1 credit -> projected 11 > 10.
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("AiAnalysis");
        result.UsagePercent.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllowUnlimited_WhenAiCreditsNull_AndPerDirectionActive()
    {
        var (service, quotaStore, usageStore) = BuildPerDirection(creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = null, QuotaAction = QuotaAction.HardBlock });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 999999m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        await usageStore.DidNotReceive().GetAiTokenBreakdownAsync(Arg.Any<TenantId>(), Arg.Any<UsageType>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    // ─── Credit-ledger cutover (B3 / ADR-0033 addendum, Model C) — LedgerEnforcementEnabled = true ──────

    private static (DefaultQuotaEnforcementService Service, ITenantQuotaStore QuotaStore, ICreditLedgerStore Ledger) BuildLedger(
        long creditTokenRatio = 1000)
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var ledger = Substitute.For<ICreditLedgerStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultQuotaEnforcementService(quotaStore, usageStore, clock, ledger,
            Options.Create(new PlatformLlmOptions
            {
                CreditTokenRatio = creditTokenRatio,
                LedgerEnforcementEnabled = true,
            }));
        return (service, quotaStore, ledger);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenLedgerEnabled_AndAllowanceNull()
    {
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = null, QuotaAction = QuotaAction.HardBlock });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Outcome.Should().Be(QuotaOutcome.Allow);
        // null allowance = unlimited / pay-as-you-go — the ledger balance is never consulted.
        await ledger.DidNotReceive().GetBalanceAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenLedgerEnabled_AndBalanceCoversDebit()
    {
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        ledger.GetBalanceAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(5m); // 5 credits remain

        // additional 1000 tokens / 1000 ratio = 1 credit; balance 5 >= 1 → covered.
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Outcome.Should().Be(QuotaOutcome.Allow);
        result.Reason.Should().BeNull();
        // consumed = 10 - 5 = 5, projected = 5 + 1 = 6, of 10 → 60%.
        result.UsagePercent.Should().BeApproximately(60.0, 0.001);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllowAndWarn_WhenLedgerEnabled_AndBalanceZero_AndWarn()
    {
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.Warn });
        ledger.GetBalanceAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(0m);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        // Warn tenant overflows into billable PostPaid — keeps serving (Allowed=true, Outcome=Warn).
        result.Allowed.Should().BeTrue();
        result.Outcome.Should().Be(QuotaOutcome.Warn);
        result.Reason.Should().Contain("AiAnalysis");
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldHardBlock_WhenLedgerEnabled_AndBalanceZero_AndHardBlock()
    {
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        ledger.GetBalanceAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(0m);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Outcome.Should().Be(QuotaOutcome.HardBlock);
        result.Reason.Should().Contain("AiAnalysis");
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldSoftBlock_WhenLedgerEnabled_AndBalanceZero_AndSoftBlock()
    {
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.SoftBlock });
        ledger.GetBalanceAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(0m);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Outcome.Should().Be(QuotaOutcome.SoftBlock);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenLedgerEnabled_AndBalanceExactlyCoversDebit()
    {
        // Boundary: balance == additionalCredits → the stock exactly covers this debit (>= serves).
        var (service, quotaStore, ledger) = BuildLedger();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        ledger.GetBalanceAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(1m); // exactly 1 credit

        // additional 1000 tokens / 1000 = 1 credit; balance 1 >= 1 → covered (boundary inclusive).
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Outcome.Should().Be(QuotaOutcome.Allow);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldSetHardBlockOutcome_WhenLedgerDisabled_AndFlatPathExceeded()
    {
        // Flag OFF: the legacy flat token path runs; the Outcome must now be set (flag-independent endpoint switch).
        // Existing numeric/string outputs (Allowed/Reason/UsagePercent) MUST stay byte-identical.
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 10000m, // 10 of 10 credits (× 1000 ratio)
                RecordCount = 1,
                LastUpdatedAt = FixedNow,
            });

        // additional 1000 tokens pushes projected over the 10000-token (10-credit) limit.
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Outcome.Should().Be(QuotaOutcome.HardBlock);
        result.Reason.Should().Be("AiAnalysis quota exceeded: 11000.0/10000 (110.0%)");
        result.UsagePercent.Should().BeApproximately(110.0, 0.001);
    }
}
