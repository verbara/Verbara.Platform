using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// Drives <see cref="CreditLotExpiryReclaimWorker.ProcessExpiryCycleAsync"/> directly (no timer loop) over the
/// InMemory ledger twin with a fixed <see cref="IClock"/>, asserting the sweeper reclaims every expired
/// non-empty lot idempotently across tenants (ADR-0033 / the 2026-06-28 (c2) resolution addendum, Group 4).
/// </summary>
public class CreditLotExpiryReclaimWorkerTests
{
    private static readonly DateTimeOffset GrantTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static (CreditLotExpiryReclaimWorker Worker, ICreditLedgerStore Ledger) Build(
        DateTimeOffset now,
        ICreditLedgerStore? ledgerOverride = null)
    {
        var clock = FixedClock(now);
        var ledger = ledgerOverride ?? new InMemoryCreditLedgerStore();

        var services = new ServiceCollection();
        services.AddSingleton(ledger);
        services.AddSingleton(clock);

        var sp = services.BuildServiceProvider();
        var worker = new CreditLotExpiryReclaimWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CreditLotExpiryReclaimWorker>.Instance,
            Options.Create(new DunningConfig()));

        return (worker, ledger);
    }

    private static IClock FixedClock(DateTimeOffset now)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        return clock;
    }

    private static CreditLedgerEntry Grant(
        TenantId tenantId,
        decimal amount,
        CreditSource source,
        string externalRef,
        DateTimeOffset? expiresAt) => new()
    {
        EntryId = EntityId.New(),
        TenantId = tenantId,
        EntryType = CreditEntryType.Grant,
        Source = source,
        Amount = amount,
        PeriodKey = null,
        ExternalRef = externalRef,
        ExpiresAt = expiresAt,
        UsageRecordId = null,
        CreatedAt = GrantTime,
    };

    [Fact]
    public async Task ProcessExpiryCycle_ShouldReclaimAllExpired_WhenSweeping()
    {
        // Drive `now` past every lot's expiry; two tenants each hold one expired lot + tenant-1 also holds a
        // non-expiring lot that must survive the sweep.
        var expiry = GrantTime.AddDays(15);
        var now = expiry.AddDays(1);
        var (worker, ledger) = Build(now);

        var tenant1 = new TenantId("tenant-1");
        var tenant2 = new TenantId("tenant-2");

        await ledger.PostGrantAsync(Grant(tenant1, 100m, CreditSource.Promo, "promo-1", expiry), CancellationToken.None);
        await ledger.PostGrantAsync(Grant(tenant1, 200m, CreditSource.Subscription, "sub-keep-1", expiresAt: null), CancellationToken.None);
        await ledger.PostGrantAsync(Grant(tenant2, 400m, CreditSource.Subscription, "sub-exp-2", expiry), CancellationToken.None);

        await worker.ProcessExpiryCycleAsync(CancellationToken.None);

        // tenant-1: the expired Promo 100 reclaimed, the non-expiring Subscription 200 survives.
        (await ledger.GetBalanceAsync(tenant1, CancellationToken.None)).Should().Be(200m);
        // tenant-2: the expired Subscription 400 fully reclaimed → no carryover.
        (await ledger.GetBalanceAsync(tenant2, CancellationToken.None)).Should().Be(0m);

        // No expired non-empty lots remain after the sweep.
        (await ledger.GetExpiredLotsAsync(now, CancellationToken.None)).Should().BeEmpty();

        // Offsetting debits carry the lot's own source.
        var t2Debits = (await ledger.GetEntriesAsync(tenant2, 1, 50, CancellationToken.None))
            .Where(e => e.EntryType == CreditEntryType.Debit).ToList();
        t2Debits.Count(d => d.Source == CreditSource.Subscription && d.Amount == -400m).Should().Be(1);
    }

    [Fact]
    public async Task ProcessExpiryCycle_ShouldBeIdempotent_WhenRunTwice()
    {
        var expiry = GrantTime.AddDays(15);
        var now = expiry.AddDays(1);
        var (worker, ledger) = Build(now);
        var tenant = new TenantId("tenant-1");

        await ledger.PostGrantAsync(Grant(tenant, 100m, CreditSource.Promo, "promo-idem", expiry), CancellationToken.None);

        await worker.ProcessExpiryCycleAsync(CancellationToken.None);
        await worker.ProcessExpiryCycleAsync(CancellationToken.None);

        (await ledger.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        // One grant + exactly one offsetting reclaim debit (the second cycle is a complete no-op).
        (await ledger.GetEntriesCountAsync(tenant, CancellationToken.None)).Should().Be(2);
    }

    [Fact]
    public async Task ProcessExpiryCycle_ShouldDoNothing_WhenNoLotsExpired()
    {
        // `now` is BEFORE the expiry → nothing is reclaimable.
        var expiry = GrantTime.AddDays(15);
        var now = GrantTime.AddDays(1);
        var (worker, ledger) = Build(now);
        var tenant = new TenantId("tenant-1");

        await ledger.PostGrantAsync(Grant(tenant, 100m, CreditSource.Promo, "promo-live", expiry), CancellationToken.None);

        await worker.ProcessExpiryCycleAsync(CancellationToken.None);

        (await ledger.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(100m);
        var lots = await ledger.GetLotsAsync(tenant, CancellationToken.None);
        lots.Single().Remaining.Should().Be(100m);
    }

    [Fact]
    public async Task ProcessExpiryCycle_ShouldStillReclaimOtherLots_WhenOneReclaimFails()
    {
        var expiry = GrantTime.AddDays(15);
        var now = expiry.AddDays(1);

        var failTenant = new TenantId("tenant-fail");
        var okTenant = new TenantId("tenant-ok");
        var failLot = new CreditLot
        {
            LotId = "lot-fail",
            TenantId = failTenant,
            Source = CreditSource.Promo,
            Original = 10m,
            Remaining = 10m,
            ExpiresAt = expiry,
            GrantedAt = GrantTime,
            LotSeq = 0,
        };
        var okLot = new CreditLot
        {
            LotId = "lot-ok",
            TenantId = okTenant,
            Source = CreditSource.Promo,
            Original = 20m,
            Remaining = 20m,
            ExpiresAt = expiry,
            GrantedAt = GrantTime,
            LotSeq = 0,
        };

        var ledger = Substitute.For<ICreditLedgerStore>();
        ledger.GetExpiredLotsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<CreditLot> { failLot, okLot });
        ledger.ReclaimExpiredLotAsync(failTenant, "lot-fail", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<decimal>(_ => throw new InvalidOperationException("reclaim error"));
        ledger.ReclaimExpiredLotAsync(okTenant, "lot-ok", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(20m);

        var (worker, _) = Build(now, ledger);

        var act = () => worker.ProcessExpiryCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await ledger.Received(1).ReclaimExpiredLotAsync(okTenant, "lot-ok", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
