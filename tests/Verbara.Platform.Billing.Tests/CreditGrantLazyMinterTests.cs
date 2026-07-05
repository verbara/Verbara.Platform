using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// Unit tests for <see cref="CreditGrantLazyMinter"/> — the credit-grant-lazy-mint-rollover fast-follow. Uses
/// the InMemory <see cref="ICreditLedgerStore"/> twin plus a deterministic <see cref="IClock"/> mock (a fixed
/// <see cref="DateTimeOffset"/>, per the test-determinism fences — no wall-clock/Task.Delay races) to drive
/// the rollover boundary explicitly.
/// </summary>
public class CreditGrantLazyMinterTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    private static TenantQuota Quota(long? aiCreditsMonthly, string tenantId = "tenant-1") => new()
    {
        TenantId = new TenantId(tenantId),
        AiCreditsMonthly = aiCreditsMonthly,
        QuotaAction = QuotaAction.Warn,
    };

    private static IClock FixedClock(DateTimeOffset now)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        return clock;
    }

    [Fact]
    public async Task EnsureCurrentPeriodGrantAsync_ShouldBeNoOp_WhenQuotaIsNull()
    {
        var ledger = new InMemoryCreditLedgerStore();
        var minter = new CreditGrantLazyMinter(ledger, FixedClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        await minter.EnsureCurrentPeriodGrantAsync(null, CancellationToken.None);

        (await ledger.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);
    }

    [Fact]
    public async Task EnsureCurrentPeriodGrantAsync_ShouldBeNoOp_WhenAiCreditsMonthlyIsNull()
    {
        var ledger = new InMemoryCreditLedgerStore();
        var minter = new CreditGrantLazyMinter(ledger, FixedClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));

        await minter.EnsureCurrentPeriodGrantAsync(Quota(null), CancellationToken.None);

        (await ledger.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);
    }

    // Scenario: First read after rollover mints the grant (deterministic rollover across the UTC month
    // boundary — the worker has NOT ticked yet for the new period).
    [Fact]
    public async Task EnsureCurrentPeriodGrantAsync_ShouldMintCurrentPeriodGrant_WhenFirstReadAfterRollover()
    {
        var ledger = new InMemoryCreditLedgerStore();

        // June: the worker minted June's grant (simulates the steady-state prior period).
        var juneClock = FixedClock(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var juneMinter = new CreditGrantLazyMinter(ledger, juneClock);
        await juneMinter.EnsureCurrentPeriodGrantAsync(Quota(1000L), CancellationToken.None);
        (await ledger.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(1000m);

        // The UTC month rolls to July, but the CreditGrantMintWorker's next tick has NOT happened yet
        // (the ≤ CheckIntervalHours window). The first balance read in July must observe July's grant inline.
        var julyClock = FixedClock(new DateTimeOffset(2026, 7, 1, 0, 30, 0, TimeSpan.Zero));
        var julyMinter = new CreditGrantLazyMinter(ledger, julyClock);

        await julyMinter.EnsureCurrentPeriodGrantAsync(Quota(1000L), CancellationToken.None);

        var balance = await ledger.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(2000m); // June's 1000 (no carryover reclaim in this test) + July's lazy-minted 1000.

        var entries = await ledger.GetEntriesAsync(Tenant1, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().Contain(e => e.PeriodKey == "2026-06" && e.Amount == 1000m);
        entries.Should().Contain(e => e.PeriodKey == "2026-07" && e.Amount == 1000m);
    }

    // Scenario: Concurrent first reads mint exactly once.
    [Fact]
    public async Task EnsureCurrentPeriodGrantAsync_ShouldMintExactlyOnce_WhenConcurrentFirstReads()
    {
        var ledger = new InMemoryCreditLedgerStore();
        var clock = FixedClock(new DateTimeOffset(2026, 7, 1, 0, 5, 0, TimeSpan.Zero));
        var minter = new CreditGrantLazyMinter(ledger, clock);
        var quota = Quota(1000L);

        var readA = minter.EnsureCurrentPeriodGrantAsync(quota, CancellationToken.None);
        var readB = minter.EnsureCurrentPeriodGrantAsync(quota, CancellationToken.None);
        await Task.WhenAll(readA, readB);

        (await ledger.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(1000m);
        var entries = await ledger.GetEntriesAsync(Tenant1, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().ContainSingle(e => e.EntryType == CreditEntryType.Grant && e.PeriodKey == "2026-07");
    }

    // Scenario: Worker tick after a lazy mint is a no-op — simulated here by calling the SAME posting path
    // (PostGrantAsync) again with the period's canonical grant, mirroring CreditGrantMintWorker.ProcessMintCycleAsync.
    [Fact]
    public async Task PostGrantAsync_ShouldBeNoOp_WhenWorkerTicksAfterALazyMint()
    {
        var ledger = new InMemoryCreditLedgerStore();
        var clock = FixedClock(new DateTimeOffset(2026, 7, 1, 0, 5, 0, TimeSpan.Zero));
        var minter = new CreditGrantLazyMinter(ledger, clock);
        var quota = Quota(1000L);

        await minter.EnsureCurrentPeriodGrantAsync(quota, CancellationToken.None);

        // The worker's next cycle re-mints the same period — idempotent, neither double-inserts nor double-credits.
        await ledger.PostGrantAsync(new CreditLedgerEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            EntryType = CreditEntryType.Grant,
            Source = CreditSource.Subscription,
            Amount = 1000m,
            PeriodKey = "2026-07",
            ExpiresAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);

        (await ledger.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(1000m);
        var entries = await ledger.GetEntriesAsync(Tenant1, page: 1, pageSize: 50, CancellationToken.None);
        entries.Should().ContainSingle(e => e.EntryType == CreditEntryType.Grant);
    }

    // Scenario: Steady-state reads perform no writes — once a period's grant already exists, the lazy minter
    // must not touch PostGrantAsync (asserted here via the balance/entries being unchanged, since InMemory has
    // no interception seam; the write-free contract is asserted directly against ICreditLedgerStore below).
    [Fact]
    public async Task EnsureCurrentPeriodGrantAsync_ShouldNotWrite_WhenCurrentPeriodGrantAlreadyExists()
    {
        var ledger = Substitute.For<ICreditLedgerStore>();
        var clock = FixedClock(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var minter = new CreditGrantLazyMinter(ledger, clock);
        var quota = Quota(1000L);

        ledger.HasCurrentPeriodGrantAsync(Tenant1, "2026-07", Arg.Any<CancellationToken>()).Returns(true);

        await minter.EnsureCurrentPeriodGrantAsync(quota, CancellationToken.None);

        await ledger.Received(1).HasCurrentPeriodGrantAsync(Tenant1, "2026-07", Arg.Any<CancellationToken>());
        await ledger.DidNotReceive().PostGrantAsync(Arg.Any<CreditLedgerEntry>(), Arg.Any<CancellationToken>());
    }
}
