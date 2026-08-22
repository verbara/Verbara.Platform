using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing.Tests;

public class CreditGrantMintWorkerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    private static (CreditGrantMintWorker Worker, ITenantQuotaStore Quotas, ICreditLedgerStore Ledger) Build(
        IClock? clock = null,
        ICreditLedgerStore? ledgerOverride = null)
    {
        var resolvedClock = clock ?? FixedClock(NowUtc);
        var quotas = new InMemoryTenantQuotaStore();
        var ledger = ledgerOverride ?? new InMemoryCreditLedgerStore();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantQuotaStore>(quotas);
        services.AddSingleton(ledger);
        services.AddSingleton(resolvedClock);

        var sp = services.BuildServiceProvider();
        var worker = new CreditGrantMintWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CreditGrantMintWorker>.Instance,
            Options.Create(new DunningConfig()));

        return (worker, quotas, ledger);
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

    [Fact]
    public async Task ProcessMintCycle_ShouldMintOneSubscriptionGrant_WhenTenantHasAllowance()
    {
        var (worker, quotas, ledger) = Build();
        var tenant = new TenantId("tenant-1");
        await quotas.UpsertAsync(Quota("tenant-1", 5000L), CancellationToken.None);

        await worker.ProcessMintCycleAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(5000m);

        var entries = await ledger.GetEntriesAsync(tenant, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().ContainSingle(e =>
            e.EntryType == CreditEntryType.Grant
            && e.Source == CreditSource.Subscription
            && e.PeriodKey == "2026-04"
            && e.Amount == 5000m);
    }

    [Fact]
    public async Task ProcessMintCycle_ShouldBeIdempotent_WhenRunTwiceInSamePeriod()
    {
        var (worker, quotas, ledger) = Build();
        var tenant = new TenantId("tenant-1");
        await quotas.UpsertAsync(Quota("tenant-1", 5000L), CancellationToken.None);

        await worker.ProcessMintCycleAsync(CancellationToken.None);
        await worker.ProcessMintCycleAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(5000m);

        var entries = await ledger.GetEntriesAsync(tenant, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().ContainSingle(e => e.EntryType == CreditEntryType.Grant);
    }

    [Fact]
    public async Task ProcessMintCycle_ShouldNotMint_WhenAiCreditsMonthlyIsNull()
    {
        var (worker, quotas, ledger) = Build();
        var tenant = new TenantId("tenant-unlimited");
        await quotas.UpsertAsync(Quota("tenant-unlimited", null), CancellationToken.None);

        await worker.ProcessMintCycleAsync(CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);

        var entries = await ledger.GetEntriesAsync(tenant, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessMintCycle_ShouldStillMintOtherTenants_WhenOneTenantFails()
    {
        var failingTenant = new TenantId("tenant-fail");
        var okTenant = new TenantId("tenant-ok");

        var ledger = Substitute.For<ICreditLedgerStore>();
        ledger.PostGrantAsync(
                Arg.Is<CreditLedgerEntry>(e => e != null && e.TenantId == failingTenant),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("ledger error")));

        var (worker, quotas, _) = Build(ledgerOverride: ledger);
        await quotas.UpsertAsync(Quota("tenant-fail", 1000L), CancellationToken.None);
        await quotas.UpsertAsync(Quota("tenant-ok", 2000L), CancellationToken.None);

        var act = () => worker.ProcessMintCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await ledger.Received(1).PostGrantAsync(
            Arg.Is<CreditLedgerEntry>(e => e != null && e.TenantId == okTenant && e.Amount == 2000m),
            Arg.Any<CancellationToken>());
    }
}
