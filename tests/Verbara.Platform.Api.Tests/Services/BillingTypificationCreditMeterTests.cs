using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class BillingTypificationCreditMeterTests
{
    private static IOptions<PlatformLlmOptions> FlatOptions(long ratio = 1)
        => Options.Create(new PlatformLlmOptions { CreditTokenRatio = ratio });

    private static BillingTypificationCreditMeter CreateSut(
        IMeteringService metering,
        out IUsageRecordStore usageStore,
        out ITenantQuotaStore quotaStore,
        out INotificationService notifications,
        out ICreditLedgerStore ledger,
        IOptions<PlatformLlmOptions>? options = null)
    {
        usageStore = Substitute.For<IUsageRecordStore>();
        quotaStore = Substitute.For<ITenantQuotaStore>();
        notifications = Substitute.For<INotificationService>();
        ledger = Substitute.For<ICreditLedgerStore>();
        return new BillingTypificationCreditMeter(
            metering,
            Substitute.For<IClock>(),
            usageStore,
            quotaStore,
            notifications,
            ledger,
            options ?? FlatOptions());
    }

    private static BillingTypificationCreditMeter CreateSut(
        IMeteringService metering,
        out IUsageRecordStore usageStore,
        out ITenantQuotaStore quotaStore,
        out INotificationService notifications,
        IOptions<PlatformLlmOptions>? options = null)
        => CreateSut(metering, out usageStore, out quotaStore, out notifications, out _, options);

    private static IOptions<PlatformLlmOptions> LedgerOnOptions(long ratio = 1)
        => Options.Create(new PlatformLlmOptions { CreditTokenRatio = ratio, LedgerEnforcementEnabled = true });

    private static void StubQuota(ITenantQuotaStore quotaStore, TenantId tenant, long? allowance)
        => quotaStore.GetAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(allowance is null
                ? new TenantQuota { TenantId = tenant, AiCreditsMonthly = null }
                : new TenantQuota { TenantId = tenant, AiCreditsMonthly = allowance });

    private static void StubPeriodTotal(IUsageRecordStore usageStore, TenantId tenant, decimal postRecordTotalTokens)
        => usageStore.GetSummaryByTypeAsync(tenant, UsageType.AiAnalysis, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = tenant,
                PeriodStart = DateTimeOffset.UnixEpoch,
                PeriodEnd = DateTimeOffset.UnixEpoch.AddMonths(1),
                UsageType = UsageType.AiAnalysis,
                TotalQuantity = postRecordTotalTokens,
                RecordCount = 1,
                LastUpdatedAt = DateTimeOffset.UnixEpoch,
            });

    [Fact]
    public async Task RecordAsync_ShouldRecordTokensWithInOutMetadata_WhenTotalPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out _, out var quotaStore, out _);
        StubQuota(quotaStore, new TenantId("t1"), null);

        await sut.RecordAsync(new TenantId("t1"), "conv1", promptTokens: 30, completionTokens: 70, totalTokens: 100, "gpt-x", CancellationToken.None);

        await metering.Received(1).RecordBatchAsync(
            Arg.Is<IReadOnlyList<UsageRecord>>(r =>
                r.Count == 1 &&
                r[0].UsageType == UsageType.AiAnalysis &&
                r[0].Unit == UsageUnit.Tokens &&
                r[0].Quantity == 100m &&
                r[0].ReferenceId == "conv1" &&
                r[0].Metadata != null &&
                r[0].Metadata!["inputTokens"] == "30" &&
                r[0].Metadata!["outputTokens"] == "70" &&
                r[0].Metadata!["model"] == "gpt-x"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldDoNothing_WhenTotalTokensNonPositive()
    {
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out _, out _, out _);

        await sut.RecordAsync(new TenantId("t1"), "conv1", 0, 0, 0, "gpt-x", CancellationToken.None);

        await metering.DidNotReceive().RecordBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldDispatchQuotaWarning_WhenUsageFirstCrossesWarningThreshold()
    {
        // AiCreditsMonthly=1000, 799 already consumed, +1 credit brings total to 800 = 80% boundary.
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out var notifications);
        StubQuota(quotaStore, tenant, 1000);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 800); // CreditTokenRatio=1 → credits == tokens

        await sut.RecordAsync(tenant, "conv1", promptTokens: 0, completionTokens: 0, totalTokens: 1, "gpt-x", CancellationToken.None);

        await notifications.Received(1).CreateAsync(
            tenant.Value,
            "billing.quota_warning",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().CreateAsync(
            Arg.Any<string>(), "billing.quota_exceeded", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldNotReDispatchWarning_WhenThresholdAlreadyCrossedSamePeriod()
    {
        // AiCreditsMonthly=1000; previous=820, +40 → 860 (still ≥80% before AND after) → straddle false.
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out var notifications);
        StubQuota(quotaStore, tenant, 1000);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 860);

        await sut.RecordAsync(tenant, "conv1", promptTokens: 0, completionTokens: 0, totalTokens: 40, "gpt-x", CancellationToken.None);

        await notifications.DidNotReceive().CreateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldDispatchQuotaExceeded_WhenUsageFirstCrossesCriticalThreshold()
    {
        // AiCreditsMonthly=1000; previous=990, +20 → 1010 ≥ 100% → critical straddle true.
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out var notifications);
        StubQuota(quotaStore, tenant, 1000);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 1010);

        await sut.RecordAsync(tenant, "conv1", promptTokens: 0, completionTokens: 0, totalTokens: 20, "gpt-x", CancellationToken.None);

        await notifications.Received(1).CreateAsync(
            tenant.Value,
            "billing.quota_exceeded",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldNotDispatchAnyNotification_WhenAllowanceIsNull()
    {
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out var notifications);
        StubQuota(quotaStore, tenant, null);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 5000);

        await sut.RecordAsync(tenant, "conv1", promptTokens: 0, completionTokens: 0, totalTokens: 5000, "gpt-x", CancellationToken.None);

        await notifications.DidNotReceive().CreateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldPostMeteredSubscriptionDebit_WhenLedgerEnforcementEnabled()
    {
        // CreditTokenRatio=10 → 250 tokens / 10 = 25 credits, drawn against the Subscription lot.
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out _, out var ledger, LedgerOnOptions(ratio: 10));
        StubQuota(quotaStore, tenant, null);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 250);

        // Capture the usage_record id that the audit write produced so we can assert the back-reference.
        string? recordedUsageId = null;
        await metering.RecordBatchAsync(
            Arg.Do<IReadOnlyList<UsageRecord>>(r => recordedUsageId = r[0].RecordId.Value),
            Arg.Any<CancellationToken>());

        await sut.RecordAsync(tenant, "conv1", promptTokens: 100, completionTokens: 150, totalTokens: 250, "gpt-x", CancellationToken.None);

        recordedUsageId.Should().NotBeNull();
        await ledger.Received(1).PostMeteredDebitAsync(
            tenant,
            25m,
            CreditSource.Subscription,
            recordedUsageId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldNotPostLedgerDebit_WhenLedgerEnforcementDisabled()
    {
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out _, out var ledger);
        StubQuota(quotaStore, tenant, null);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 250);

        await sut.RecordAsync(tenant, "conv1", promptTokens: 100, completionTokens: 150, totalTokens: 250, "gpt-x", CancellationToken.None);

        await ledger.DidNotReceive().PostMeteredDebitAsync(
            Arg.Any<TenantId>(), Arg.Any<decimal>(), Arg.Any<CreditSource>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldStillRecordMetering_WhenLedgerDebitThrows()
    {
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out var usageStore, out var quotaStore, out _, out var ledger, LedgerOnOptions());
        StubQuota(quotaStore, tenant, null);
        StubPeriodTotal(usageStore, tenant, postRecordTotalTokens: 100);
        ledger.PostMeteredDebitAsync(Arg.Any<TenantId>(), Arg.Any<decimal>(), Arg.Any<CreditSource>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MeteredDebitResult>>(_ => throw new InvalidOperationException("ledger down"));

        var act = async () => await sut.RecordAsync(tenant, "conv1", promptTokens: 30, completionTokens: 70, totalTokens: 100, "gpt-x", CancellationToken.None);

        await act.Should().NotThrowAsync();
        await metering.Received(1).RecordBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldNotPostLedgerDebit_WhenTotalTokensNonPositive()
    {
        var tenant = new TenantId("t1");
        var metering = Substitute.For<IMeteringService>();
        var sut = CreateSut(metering, out _, out _, out _, out var ledger, LedgerOnOptions());

        await sut.RecordAsync(tenant, "conv1", 0, 0, 0, "gpt-x", CancellationToken.None);

        await ledger.DidNotReceive().PostMeteredDebitAsync(
            Arg.Any<TenantId>(), Arg.Any<decimal>(), Arg.Any<CreditSource>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
