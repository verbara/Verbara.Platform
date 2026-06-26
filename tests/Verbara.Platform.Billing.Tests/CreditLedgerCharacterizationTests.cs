using Microsoft.Extensions.Options;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// Characterization tests pinning the <b>current</b> (pre-cutover) numeric outputs of
/// <see cref="DefaultQuotaEnforcementService.CheckQuotaAsync"/> (AiAnalysis — per-direction + flat) and the
/// AI-credit invoice line item produced by <see cref="DefaultInvoiceGenerationService"/>
/// (<c>BuildAiCreditLineItemAsync</c>, characterized through the public <c>GenerateWithRateCardAsync</c>
/// boundary). These assert exact values so the credit-ledger cutover (ADR-0033 change b) can prove
/// byte-for-byte equivalence. They must remain green and unchanged through the Phase-A period-helper
/// refactor (which must not move any boundary).
/// </summary>
public class CreditLedgerCharacterizationTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    // Fixed mid-month instant: period = [2026-03-01, 2026-04-01).
    private static readonly DateTimeOffset QuotaNow = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    // ─── CheckQuotaAsync(AiAnalysis) — per-direction credit basis ──────────────────────────────────

    private static DefaultQuotaEnforcementService BuildQuota(
        ITenantQuotaStore quotaStore, IUsageRecordStore usageStore,
        long creditTokenRatio, long? inputRatio, long? outputRatio)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(QuotaNow);
        return new DefaultQuotaEnforcementService(quotaStore, usageStore, clock,
            Options.Create(new PlatformLlmOptions
            {
                CreditTokenRatio = creditTokenRatio,
                InputCreditTokenRatio = inputRatio,
                OutputCreditTokenRatio = outputRatio,
            }));
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldYieldExactUsagePercent_WhenAiAnalysisPerDirectionMixedBuckets()
    {
        // input 300/2000 + output 100/500 + unsplit 500/1000 = 0.15 + 0.20 + 0.50 = 0.85 credits.
        // additional 0. limit 10 credits -> 8.5%.
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetAiTokenBreakdownAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(300m, 100m, 500m));

        var service = BuildQuota(quotaStore, usageStore, creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.UsagePercent.Should().BeApproximately(8.5d, 1e-9);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDenyWithExactPercent_WhenAiAnalysisPerDirectionExceedsHardBlock()
    {
        // input 20000/2000 = 10 credits == limit; additional 1000/1000 = +1 -> projected 11, 110%.
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetAiTokenBreakdownAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(20000m, 0m, 0m));

        var service = BuildQuota(quotaStore, usageStore, creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 1000m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("AiAnalysis credit quota exceeded: 11.00/10 (110.0%)");
        result.UsagePercent.Should().BeApproximately(110.0d, 1e-9);
    }

    // ─── CheckQuotaAsync(AiAnalysis) — flat token basis (no per-direction ratios) ───────────────────

    [Fact]
    public async Task CheckQuotaAsync_ShouldYieldExactUsagePercent_WhenAiAnalysisFlatTokenBasis()
    {
        // flat: AiCreditsMonthly 10 * ratio 1000 = 10000 token limit; usage 5000 tokens -> 50%.
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.SoftBlock });
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 5000m,
                RecordCount = 1,
                LastUpdatedAt = QuotaNow,
            });

        var service = BuildQuota(quotaStore, usageStore, creditTokenRatio: 1000, inputRatio: null, outputRatio: null);
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 0m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.UsagePercent.Should().BeApproximately(50.0d, 1e-9);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDenyWithExactReason_WhenAiAnalysisFlatExceedsHardBlock()
    {
        // flat limit 10 credits -> 10000 tokens; usage 9995 + additional 10 = 10005 tokens -> 100.05%.
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10, QuotaAction = QuotaAction.HardBlock });
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 9995m,
                RecordCount = 1,
                LastUpdatedAt = QuotaNow,
            });

        var service = BuildQuota(quotaStore, usageStore, creditTokenRatio: 1000, inputRatio: null, outputRatio: null);
        var result = await service.CheckQuotaAsync(Tenant1, UsageType.AiAnalysis, 10m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        // flat path uses the token-denominated message: projected 10005 / 10000 token limit. The %F1 of the
        // 100.05 percent renders "100.0" (the binary double for 100.05 lies just below the .05 midpoint) —
        // characterized exactly so the cutover must reproduce it.
        result.Reason.Should().Be("AiAnalysis quota exceeded: 10005.0/10000 (100.0%)");
        result.UsagePercent.Should().BeApproximately(100.05d, 1e-9);
    }

    // ─── BuildAiCreditLineItemAsync — through GenerateWithRateCardAsync ─────────────────────────────

    private static readonly DateTimeOffset InvoiceNow = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    private static DefaultInvoiceGenerationService BuildInvoice(
        IRateCardStore rateCardStore, IUsageRecordStore usageStore, ITenantQuotaStore quotaStore,
        long creditTokenRatio, long? inputRatio, long? outputRatio)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(InvoiceNow);
        return new DefaultInvoiceGenerationService(rateCardStore, usageStore, quotaStore, clock,
            Options.Create(new PlatformLlmOptions
            {
                CreditTokenRatio = creditTokenRatio,
                InputCreditTokenRatio = inputRatio,
                OutputCreditTokenRatio = outputRatio,
            }));
    }

    private static RateCard AiOnlyRateCard(decimal unitPrice) => new()
    {
        RateCardId = EntityId.New(),
        TenantId = Tenant1,
        Name = "AI",
        Currency = "USD",
        EffectiveFrom = PeriodStart.AddMonths(-1),
        Rates = new List<RateEntry> { new() { UsageType = UsageType.AiAnalysis, UnitPrice = unitPrice } },
        IsDefault = true,
    };

    [Fact]
    public async Task BuildAiCreditLineItem_ShouldPinExactAmounts_WhenFlatBasisWithOverage()
    {
        // flat: consumed 12000 tokens / 1000 = 12 credits; allowance 10 -> overage 2; price 0.50 -> amount 1.00.
        var rateCardStore = Substitute.For<IRateCardStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var quotaStore = Substitute.For<ITenantQuotaStore>();

        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>());
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 12000m,
                RecordCount = 1,
                LastUpdatedAt = InvoiceNow,
            });
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10 });

        var service = BuildInvoice(rateCardStore, usageStore, quotaStore, creditTokenRatio: 1000, inputRatio: null, outputRatio: null);
        var invoice = await service.GenerateWithRateCardAsync(Tenant1, AiOnlyRateCard(0.50m), PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems.Single(li => li.UsageType == UsageType.AiAnalysis);
        line.Description.Should().Be("AiAnalysis");
        line.Quantity.Should().Be(12m);
        line.IncludedQuantity.Should().Be(10m);
        line.OverageQuantity.Should().Be(2m);
        line.UnitPrice.Should().Be(0.50m);
        line.Amount.Should().Be(1.00m);
        invoice.Total.Should().Be(1.00m);
    }

    [Fact]
    public async Task BuildAiCreditLineItem_ShouldPinZeroAmount_WhenFlatBasisUnderAllowance()
    {
        // consumed 8000/1000 = 8 credits; allowance 10 -> overage 0 -> amount 0.
        var rateCardStore = Substitute.For<IRateCardStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var quotaStore = Substitute.For<ITenantQuotaStore>();

        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>());
        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = 8000m,
                RecordCount = 1,
                LastUpdatedAt = InvoiceNow,
            });
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10 });

        var service = BuildInvoice(rateCardStore, usageStore, quotaStore, creditTokenRatio: 1000, inputRatio: null, outputRatio: null);
        var invoice = await service.GenerateWithRateCardAsync(Tenant1, AiOnlyRateCard(0.50m), PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems.Single(li => li.UsageType == UsageType.AiAnalysis);
        line.Quantity.Should().Be(8m);
        line.IncludedQuantity.Should().Be(10m);
        line.OverageQuantity.Should().Be(0m);
        line.Amount.Should().Be(0m);
    }

    [Fact]
    public async Task BuildAiCreditLineItem_ShouldPinExactAmounts_WhenPerDirectionBasisWithOverage()
    {
        // per-direction: input 20000/2000 = 10 + output 5000/500 = 10 + unsplit 1000/1000 = 1 -> 21 credits.
        // allowance 10 -> overage 11; price 0.25 -> amount 2.75.
        var rateCardStore = Substitute.For<IRateCardStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var quotaStore = Substitute.For<ITenantQuotaStore>();

        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>());
        usageStore.GetAiTokenBreakdownAsync(Tenant1, UsageType.AiAnalysis, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new AiTokenBreakdown(20000m, 5000m, 1000m));
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, AiCreditsMonthly = 10 });

        var service = BuildInvoice(rateCardStore, usageStore, quotaStore, creditTokenRatio: 1000, inputRatio: 2000, outputRatio: 500);
        var invoice = await service.GenerateWithRateCardAsync(Tenant1, AiOnlyRateCard(0.25m), PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems.Single(li => li.UsageType == UsageType.AiAnalysis);
        line.Description.Should().Be("AiAnalysis (input/output pricing)");
        line.Quantity.Should().Be(21m);
        line.IncludedQuantity.Should().Be(10m);
        line.OverageQuantity.Should().Be(11m);
        line.UnitPrice.Should().Be(0.25m);
        line.Amount.Should().Be(2.75m);
        invoice.Total.Should().Be(2.75m);
    }
}
