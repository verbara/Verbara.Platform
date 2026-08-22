using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing.Tests;

public class CreditLedgerBackfillServiceTests
{
    // Fixed inside the current UTC calendar month so BillingPeriod.Current keys on "2026-04".
    private static readonly DateTimeOffset NowUtc = new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);
    private const string PeriodKey = "2026-04";

    private static (CreditLedgerBackfillService Service, InMemoryTenantQuotaStore Quotas, ICreditLedgerStore Ledger, InMemoryUsageRecordStore Usage) Build(
        PlatformLlmOptions? options = null)
    {
        var clock = FixedClock(NowUtc);
        var quotas = new InMemoryTenantQuotaStore();
        var usage = new InMemoryUsageRecordStore();
        ICreditLedgerStore ledger = new InMemoryCreditLedgerStore();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantQuotaStore>(quotas);
        services.AddSingleton<IUsageRecordStore>(usage);
        services.AddSingleton(ledger);
        services.AddSingleton(clock);

        var sp = services.BuildServiceProvider();
        var service = new CreditLedgerBackfillService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CreditLedgerBackfillService>.Instance,
            Options.Create(options ?? new PlatformLlmOptions { RunLedgerBackfill = true, CreditTokenRatio = 1000 }));

        return (service, quotas, ledger, usage);
    }

    private static IClock FixedClock(DateTimeOffset now)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        return clock;
    }

    private static TenantQuota Quota(string tenantId, long? aiCreditsMonthly) => new()
    {
        TenantId = new TenantId(tenantId),
        AiCreditsMonthly = aiCreditsMonthly,
        QuotaAction = QuotaAction.Warn,
    };

    // Seeds an AiAnalysis usage record of `tokens` nominal tokens (flat-fallback bucket) in the current period.
    private static async Task SeedFlatUsageAsync(InMemoryUsageRecordStore usage, string tenantId, decimal tokens)
    {
        await usage.SaveAsync(
            new UsageRecord
            {
                RecordId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                UsageType = UsageType.AiAnalysis,
                Quantity = tokens,
                Unit = UsageUnit.Tokens,
                RecordedAt = NowUtc,
            },
            CancellationToken.None);
    }

    // Seeds an AiAnalysis usage record carrying the per-direction split metadata.
    private static async Task SeedSplitUsageAsync(InMemoryUsageRecordStore usage, string tenantId, decimal inputTokens, decimal outputTokens)
    {
        await usage.SaveAsync(
            new UsageRecord
            {
                RecordId = EntityId.New(),
                TenantId = new TenantId(tenantId),
                UsageType = UsageType.AiAnalysis,
                Quantity = inputTokens + outputTokens,
                Unit = UsageUnit.Tokens,
                RecordedAt = NowUtc,
                Metadata = new Dictionary<string, string>
                {
                    ["inputTokens"] = inputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["outputTokens"] = outputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task RunBackfill_ShouldLeaveResidualBalanceAndNoPostPaid_WhenConsumedUnderAllowance()
    {
        // allowance 10, consumed 8 (8000 tokens / 1000) ⇒ balance 2, Σ-PostPaid 0.
        var (service, quotas, ledger, usage) = Build();
        var tenant = new TenantId("tenant-under");
        await quotas.UpsertAsync(Quota("tenant-under", 10L), CancellationToken.None);
        await SeedFlatUsageAsync(usage, "tenant-under", 8000m);

        await service.RunBackfillAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(2m);

        var postPaid = await ledger.GetPostPaidDebitsTotalAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
        postPaid.Should().Be(0m);
    }

    [Fact]
    public async Task RunBackfill_ShouldFloorBalanceAndBillTailAsPostPaid_WhenConsumedOverAllowance()
    {
        // allowance 10, consumed 12 (12000 tokens / 1000) ⇒ balance 0, Σ-PostPaid 2.
        var (service, quotas, ledger, usage) = Build();
        var tenant = new TenantId("tenant-over");
        await quotas.UpsertAsync(Quota("tenant-over", 10L), CancellationToken.None);
        await SeedFlatUsageAsync(usage, "tenant-over", 12000m);

        await service.RunBackfillAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);

        var postPaid = await ledger.GetPostPaidDebitsTotalAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
        postPaid.Should().Be(2m);
    }

    [Fact]
    public async Task RunBackfill_ShouldSeedNothing_WhenLedgerEnforcementAlreadyOn()
    {
        // Invariant: back-fill MUST precede enforcement. With enforcement already on, re-deriving consumption from
        // usage_records would double-count live debits — the service refuses and seeds nothing.
        var (service, quotas, ledger, usage) = Build(
            new PlatformLlmOptions { RunLedgerBackfill = true, LedgerEnforcementEnabled = true, CreditTokenRatio = 1000 });
        var tenant = new TenantId("tenant-enf-on");
        await quotas.UpsertAsync(Quota("tenant-enf-on", 10L), CancellationToken.None);
        await SeedFlatUsageAsync(usage, "tenant-enf-on", 12000m);

        await service.RunBackfillAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m); // no grant minted, no debit posted

        var postPaid = await ledger.GetPostPaidDebitsTotalAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
        postPaid.Should().Be(0m);
    }

    [Fact]
    public async Task RunBackfill_ShouldBeNoOp_WhenRunTwiceInSamePeriod()
    {
        // A second pass must not double-grant, double-debit, or double-bill — idempotent on the backfill marker.
        var (service, quotas, ledger, usage) = Build();
        var tenant = new TenantId("tenant-over");
        await quotas.UpsertAsync(Quota("tenant-over", 10L), CancellationToken.None);
        await SeedFlatUsageAsync(usage, "tenant-over", 12000m);

        await service.RunBackfillAsync(CancellationToken.None);
        await service.RunBackfillAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);

        var postPaid = await ledger.GetPostPaidDebitsTotalAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
        postPaid.Should().Be(2m);

        // Exactly one Grant + one Subscription back-fill debit + one PostPaid debit — no duplicates.
        var entries = await ledger.GetEntriesAsync(tenant, page: 1, pageSize: 50, CancellationToken.None);
        entries.Count(e => e.EntryType == CreditEntryType.Grant).Should().Be(1);
        entries.Count(e => e.EntryType == CreditEntryType.Debit && e.Source == CreditSource.Subscription).Should().Be(1);
        entries.Count(e => e.EntryType == CreditEntryType.Debit && e.Source == CreditSource.PostPaid).Should().Be(1);
    }

    [Fact]
    public async Task RunBackfill_ShouldStillBackfillOtherTenants_WhenOneTenantFails()
    {
        var failingTenant = new TenantId("tenant-fail");
        var okTenant = new TenantId("tenant-ok");

        var ledger = Substitute.For<ICreditLedgerStore>();
        ledger.PostGrantAsync(
                Arg.Is<CreditLedgerEntry>(e => e != null && e.TenantId == failingTenant),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("ledger error")));

        var quotas = new InMemoryTenantQuotaStore();
        var usage = new InMemoryUsageRecordStore();
        var clock = FixedClock(NowUtc);

        var services = new ServiceCollection();
        services.AddSingleton<ITenantQuotaStore>(quotas);
        services.AddSingleton<IUsageRecordStore>(usage);
        services.AddSingleton(ledger);
        services.AddSingleton(clock);
        var sp = services.BuildServiceProvider();
        var service = new CreditLedgerBackfillService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CreditLedgerBackfillService>.Instance,
            Options.Create(new PlatformLlmOptions { RunLedgerBackfill = true, CreditTokenRatio = 1000 }));

        await quotas.UpsertAsync(Quota("tenant-fail", 1000L), CancellationToken.None);
        await quotas.UpsertAsync(Quota("tenant-ok", 2000L), CancellationToken.None);

        var act = () => service.RunBackfillAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await ledger.Received(1).PostGrantAsync(
            Arg.Is<CreditLedgerEntry>(e => e != null && e.TenantId == okTenant && e.Amount == 2000m),
            Arg.Any<CancellationToken>());
        await ledger.Received(1).PostBackfillConsumptionAsync(
            okTenant, Arg.Any<decimal>(), PeriodKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSeedNothing_WhenRunLedgerBackfillIsOff()
    {
        var (service, quotas, ledger, usage) = Build(
            new PlatformLlmOptions { RunLedgerBackfill = false, CreditTokenRatio = 1000 });
        var tenant = new TenantId("tenant-1");
        await quotas.UpsertAsync(Quota("tenant-1", 10L), CancellationToken.None);
        await SeedFlatUsageAsync(usage, "tenant-1", 8000m);

        // Drive the gated entry point (StartAsync → ExecuteAsync). The flag is off ⇒ nothing seeded.
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);

        var entries = await ledger.GetEntriesAsync(tenant, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunBackfill_ShouldReconstructConsumedPerDirection_WhenBothRatiosSet()
    {
        // Per-direction: input 4000/200 = 20, output 6000/300 = 20 ⇒ consumed 40. allowance 50 ⇒ balance 10, no tail.
        var options = new PlatformLlmOptions
        {
            RunLedgerBackfill = true,
            CreditTokenRatio = 1000,
            InputCreditTokenRatio = 200,
            OutputCreditTokenRatio = 300,
        };
        var (service, quotas, ledger, usage) = Build(options);
        var tenant = new TenantId("tenant-split");
        await quotas.UpsertAsync(Quota("tenant-split", 50L), CancellationToken.None);
        await SeedSplitUsageAsync(usage, "tenant-split", inputTokens: 4000m, outputTokens: 6000m);

        await service.RunBackfillAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(10m);

        var postPaid = await ledger.GetPostPaidDebitsTotalAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
        postPaid.Should().Be(0m);
    }
}
