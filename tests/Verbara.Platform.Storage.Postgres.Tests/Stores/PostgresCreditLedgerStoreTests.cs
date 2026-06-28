using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Round-trips <see cref="PostgresCreditLedgerStore"/> against a real Postgres DB via Testcontainers so the
/// atomic guarded debit/grant primitive (ADR-0033 / credit-ledger-substrate) is exercised against the
/// actual migration-012 schema: the O(1) projection balance read, the <c>ON CONFLICT DO NOTHING</c> grant
/// idempotency over the partial unique indexes, the guarded <c>UPDATE … WHERE balance &gt;= @Amount</c>
/// debit, the ledger-SUM == projection-balance invariant, and the concurrent-debit never-negative guarantee
/// — none of which the InMemory twin can reach.
/// </summary>
[Collection("CreditLedgerStore")]
public sealed class PostgresCreditLedgerStoreTests
{
    private readonly CreditLedgerStoreFixture _fixture;
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public PostgresCreditLedgerStoreTests(CreditLedgerStoreFixture fixture) => _fixture = fixture;

    private static CreditLedgerEntry Grant(
        TenantId tenantId,
        decimal amount,
        CreditSource source = CreditSource.Subscription,
        string? periodKey = null,
        string? externalRef = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        EntryId = EntityId.New(),
        TenantId = tenantId,
        EntryType = CreditEntryType.Grant,
        Source = source,
        Amount = amount,
        PeriodKey = periodKey,
        ExternalRef = externalRef,
        ExpiresAt = expiresAt,
        CreatedAt = BaseTime,
    };

    // Scenario: balance read of an unknown tenant is O(1) zero (no projection row).
    [Fact]
    public async Task GetBalanceAsync_ShouldReturnZero_WhenNoProjectionRowExists()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-empty");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);

        balance.Should().Be(0m);
    }

    // Scenario: a grant raises the projection and the ledger SUM equals the projection balance.
    [Fact]
    public async Task PostGrantAsync_ShouldCreditProjection_WhenGrantPosted()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-grant");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06"), CancellationToken.None);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(300m);
        var ledgerSum = await _fixture.LedgerSumAsync(tenant.Value);
        ledgerSum.Should().Be(balance);
    }

    // Scenario: ledger SUM == projection balance after grant + debit.
    [Fact]
    public async Task PostGrantThenDebit_ShouldKeepLedgerSumEqualToProjectionBalance_WhenBothPosted()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-sum-invariant");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06"), CancellationToken.None);
        var result = await store.TryPostDebitAsync(tenant, 100m, CreditSource.Subscription, "usage-1", CancellationToken.None);

        result.Outcome.Should().Be(CreditDebitOutcome.Posted);
        result.NewBalance.Should().Be(200m);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(200m);
        var ledgerSum = await _fixture.LedgerSumAsync(tenant.Value);
        ledgerSum.Should().Be(balance);
        // One grant (+300) and one debit (−100) row.
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(2);
    }

    // Scenario: a debit within balance is posted atomically.
    [Fact]
    public async Task TryPostDebitAsync_ShouldPostAndDecrement_WhenWithinBalance()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-debit-ok");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 300m), CancellationToken.None);

        var result = await store.TryPostDebitAsync(tenant, 100m, CreditSource.Subscription, usageRecordId: null, CancellationToken.None);

        result.IsPosted.Should().BeTrue();
        result.NewBalance.Should().Be(200m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(200m);
    }

    // Scenario: a debit exceeding balance is rejected and nothing is written.
    [Fact]
    public async Task TryPostDebitAsync_ShouldRejectAndWriteNothing_WhenExceedingBalance()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-debit-reject");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 50m), CancellationToken.None);

        var result = await store.TryPostDebitAsync(tenant, 100m, CreditSource.Subscription, "usage-x", CancellationToken.None);

        result.Outcome.Should().Be(CreditDebitOutcome.RejectedInsufficientBalance);
        result.NewBalance.Should().Be(0m);
        // Balance unchanged, no debit row written (only the original grant remains).
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(50m);
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(1);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(50m);
    }

    // Scenario: a duplicate subscription grant for the same period is a no-op.
    [Fact]
    public async Task PostGrantAsync_ShouldBeNoOp_WhenDuplicateSubscriptionPeriod()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-dup-period");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06"), CancellationToken.None);
        // Same (tenant, period, entry_type) — distinct EntryId/amount, but the partial unique index collides.
        await store.PostGrantAsync(Grant(tenant, 999m, periodKey: "2026-06"), CancellationToken.None);

        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(1);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(300m);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(300m);
    }

    // Scenario: a duplicate top-up for the same external_ref is a no-op.
    [Fact]
    public async Task PostGrantAsync_ShouldBeNoOp_WhenDuplicateExternalRef()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-dup-extref");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 500m, source: CreditSource.TopUp, externalRef: "pay-abc"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 500m, source: CreditSource.TopUp, externalRef: "pay-abc"), CancellationToken.None);

        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(1);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(500m);
    }

    // Scenario: distinct period keys both apply (the partial index does not over-constrain).
    [Fact]
    public async Task PostGrantAsync_ShouldApplyBoth_WhenDistinctPeriods()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-two-periods");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-07"), CancellationToken.None);

        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(2);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(600m);
    }

    // Scenario: concurrent debits cannot drive the balance negative.
    [Fact]
    public async Task TryPostDebitAsync_ShouldNeverGoNegative_WhenConcurrentDebits()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-concurrent");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 5m), CancellationToken.None);

        // Two concurrent debits of 5 against a balance of 5: exactly one must win.
        var debitA = store.TryPostDebitAsync(tenant, 5m, CreditSource.Subscription, "a", CancellationToken.None);
        var debitB = store.TryPostDebitAsync(tenant, 5m, CreditSource.Subscription, "b", CancellationToken.None);
        var results = await Task.WhenAll(debitA, debitB);

        results.Count(r => r.Outcome == CreditDebitOutcome.Posted).Should().Be(1);
        results.Count(r => r.Outcome == CreditDebitOutcome.RejectedInsufficientBalance).Should().Be(1);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);
        balance.Should().BeGreaterThanOrEqualTo(0m);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(balance);
        // One grant (+5) and exactly one debit (−5) row — the rejected debit wrote nothing.
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(2);
    }

    // Scenario: many concurrent debits against a small balance never over-draw.
    [Fact]
    public async Task TryPostDebitAsync_ShouldPostExactlyBalanceWorth_WhenManyConcurrentDebits()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-concurrent-many");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 30m), CancellationToken.None);

        // 20 concurrent debits of 10 against a balance of 30 → exactly 3 succeed, balance lands at 0.
        var debits = Enumerable.Range(0, 20)
            .Select(i => store.TryPostDebitAsync(tenant, 10m, CreditSource.Subscription, $"u{i}", CancellationToken.None));
        var results = await Task.WhenAll(debits);

        results.Count(r => r.Outcome == CreditDebitOutcome.Posted).Should().Be(3);
        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(0m);
    }

    // Scenario: posted debit entries round-trip via GetEntriesAsync, most recent first.
    [Fact]
    public async Task GetEntriesAsync_ShouldReturnEntriesMostRecentFirst_WhenPosted()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-entries");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06", expiresAt: BaseTime.AddMonths(1)), CancellationToken.None);
        await store.TryPostDebitAsync(tenant, 100m, CreditSource.Subscription, "usage-7", CancellationToken.None);

        var entries = await store.GetEntriesAsync(tenant, page: 1, pageSize: 10, CancellationToken.None);

        entries.Should().HaveCount(2);
        // Debit (−100) was posted after the grant → first when ordered most-recent-first.
        entries[0].EntryType.Should().Be(CreditEntryType.Debit);
        entries[0].Amount.Should().Be(-100m);
        // A covered draw records the lot it drew from (Subscription), NOT a hard-coded PostPaid.
        entries[0].Source.Should().Be(CreditSource.Subscription);
        entries[0].UsageRecordId.Should().Be("usage-7");
        entries[1].EntryType.Should().Be(CreditEntryType.Grant);
        entries[1].Amount.Should().Be(300m);
        entries[1].PeriodKey.Should().Be("2026-06");
        entries[1].ExpiresAt.Should().NotBeNull();
    }

    // Scenario: GetEntriesCountAsync returns the total ledger row count (backs PagedResult.TotalCount).
    [Fact]
    public async Task GetEntriesCountAsync_ShouldReturnTotalRowCount_WhenEntriesPosted()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("ledger-count");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        // Unknown tenant → 0 (no rows).
        (await store.GetEntriesCountAsync(tenant, CancellationToken.None)).Should().Be(0);

        await store.PostGrantAsync(Grant(tenant, 300m, periodKey: "2026-06"), CancellationToken.None);
        await store.TryPostDebitAsync(tenant, 100m, CreditSource.Subscription, "usage-1", CancellationToken.None);

        // 1 grant + 1 debit row = 2, regardless of any page window.
        (await store.GetEntriesCountAsync(tenant, CancellationToken.None)).Should().Be(2);
    }

    // Scenario: a metered debit fully covered by the prepaid balance draws entirely from the covered lot.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldDrawFromCoveredLotAndDecrementProjection_WhenFullyCovered()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-covered");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 10m, periodKey: "2026-06"), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(tenant, 4m, "usage-c", CancellationToken.None);

        result.NewBalance.Should().Be(6m);
        result.CoveredAmount.Should().Be(4m);
        result.PostPaidAmount.Should().Be(0m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(6m);
        // One -4 Subscription debit row; no PostPaid row.
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(0);
    }

    // Scenario: a metered debit exceeding the balance floors the projection at 0 and posts a PostPaid tail.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldFloorProjectionAndPostPostPaidTail_WhenOverflowing()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-overflow");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 3m, periodKey: "2026-06"), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(tenant, 5m, "usage-o", CancellationToken.None);

        result.NewBalance.Should().Be(0m);
        result.CoveredAmount.Should().Be(3m);
        result.PostPaidAmount.Should().Be(2m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        // A -3 Subscription covered debit + a -2 PostPaid tail debit.
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);
    }

    // Scenario: a metered debit against an absent projection row posts the full amount as a PostPaid tail.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldPostFullPostPaidTail_WhenNoPrepaidBalance()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-zero");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        var result = await store.PostMeteredDebitAsync(tenant, 7m, "usage-z", CancellationToken.None);

        result.NewBalance.Should().Be(0m);
        result.CoveredAmount.Should().Be(0m);
        result.PostPaidAmount.Should().Be(7m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(0);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);
    }

    // Scenario: concurrent metered debits never overdraw the prepaid lot — total covered == balance.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldNeverOverdrawPrepaid_WhenConcurrent()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-concurrent");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 4m, periodKey: "2026-06"), CancellationToken.None);

        // Two concurrent metered debits of 3 against a balance of 4: covered totals exactly 4, tail totals 2.
        var debitA = store.PostMeteredDebitAsync(tenant, 3m, "a", CancellationToken.None);
        var debitB = store.PostMeteredDebitAsync(tenant, 3m, "b", CancellationToken.None);
        var results = await Task.WhenAll(debitA, debitB);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);
        balance.Should().BeGreaterThanOrEqualTo(0m);
        results.Sum(r => r.CoveredAmount).Should().Be(4m);
        results.Sum(r => r.PostPaidAmount).Should().Be(2m);
    }

    // Scenario: GetPostPaidDebitsTotalAsync sums only PostPaid debits within the period window.
    [Fact]
    public async Task GetPostPaidDebitsTotalAsync_ShouldSumOnlyPeriodPostPaidDebits_WhenMixedEntries()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("postpaid-total");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 3m, periodKey: "2026-06"), CancellationToken.None);

        // Covered 3 + PostPaid tail 2, then a further pure PostPaid debit of 4 (balance already 0).
        await store.PostMeteredDebitAsync(tenant, 5m, "u1", CancellationToken.None);
        await store.PostMeteredDebitAsync(tenant, 4m, "u2", CancellationToken.None);

        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var total = await store.GetPostPaidDebitsTotalAsync(tenant, periodStart, periodEnd, CancellationToken.None);

        // PostPaid tail 2 + PostPaid 4 = 6 (the -3 covered Subscription debit is excluded).
        total.Should().Be(6m);

        // A window that excludes the debits returns 0.
        var emptyTotal = await store.GetPostPaidDebitsTotalAsync(
            tenant,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        emptyTotal.Should().Be(0m);
    }

    // Scenario: the cutover back-fill draws covered from the grant and bills the tail as PostPaid, floored at 0.
    [Fact]
    public async Task PostBackfillConsumptionAsync_ShouldDrawCoveredAndBillTail_WhenConsumedOverBalance()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("backfill-over");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 10m, periodKey: "2026-06"), CancellationToken.None);

        await store.PostBackfillConsumptionAsync(tenant, 12m, "2026-06", CancellationToken.None);

        // Balance floors at 0; covered 10 (Subscription marker debit) + tail 2 (PostPaid debit).
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);

        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (await store.GetPostPaidDebitsTotalAsync(tenant, periodStart, periodEnd, CancellationToken.None)).Should().Be(2m);
    }

    // Scenario: a second back-fill of the same period is a complete no-op (the backfill: marker is the arbiter).
    [Fact]
    public async Task PostBackfillConsumptionAsync_ShouldBeNoOp_WhenRunTwiceForSamePeriod()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("backfill-idem");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 10m, periodKey: "2026-06"), CancellationToken.None);

        await store.PostBackfillConsumptionAsync(tenant, 12m, "2026-06", CancellationToken.None);
        await store.PostBackfillConsumptionAsync(tenant, 12m, "2026-06", CancellationToken.None);

        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        // One grant + one covered Subscription debit + one PostPaid tail — no duplicates from the re-run.
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(3);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);
    }

    // Scenario (Group 3b): the lot-aware back-fill covered draw keeps Σ remaining == balance and decrements lots.
    [Fact]
    public async Task PostBackfillConsumptionAsync_ShouldKeepInvariant_WhenLotsExist()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("backfill-lot-invariant");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        // One Subscription lot of 10 (balance 10); back-fill consumed 6 ⇒ covered 6 (partial), no PostPaid tail.
        await store.PostGrantAsync(Grant(tenant, 10m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        await store.PostBackfillConsumptionAsync(tenant, 6m, "2026-06", CancellationToken.None);

        // Invariant Σ GetRemainingBySourceAsync == GetBalanceAsync after the lot-aware covered draw.
        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(4m);
        var bySource = await store.GetRemainingBySourceAsync(tenant, BaseTime, CancellationToken.None);
        bySource.Sum(s => s.Remaining).Should().Be(balance);

        // The lot's remaining dropped by exactly `covered` (10 − 6 = 4); Σ credit_allocation.amount == covered.
        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Should().ContainSingle();
        lots[0].Remaining.Should().Be(4m);
        (await _fixture.AllocationAmountSumAsync(tenant.Value)).Should().Be(6m);
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(1);
    }

    // Scenario (Group 3b): re-running the same period changes no lot, no balance, no allocation (idempotency holds).
    [Fact]
    public async Task PostBackfillConsumptionAsync_ShouldRemainNoOp_WhenReRunSamePeriod()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("backfill-lot-idem");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 10m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        await store.PostBackfillConsumptionAsync(tenant, 6m, "2026-06", CancellationToken.None);
        await store.PostBackfillConsumptionAsync(tenant, 6m, "2026-06", CancellationToken.None);

        // Second run is a complete no-op: balance, the lot remaining, and the allocations are all unchanged.
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(4m);
        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Should().ContainSingle();
        lots[0].Remaining.Should().Be(4m);
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(1);
        (await _fixture.AllocationAmountSumAsync(tenant.Value)).Should().Be(6m);
        // One grant + one covered Subscription marker debit — no second marker, no PostPaid tail (consumed < balance).
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(2);
    }

    // Scenario: a grant mints exactly one credit_lot mirroring it, and GetRemainingBySourceAsync reports it.
    [Fact]
    public async Task PostGrantAsync_ShouldMintLot_WhenGrantInserted()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("lot-mint");
        var expiry = BaseTime.AddMonths(1);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-1", expiresAt: expiry), CancellationToken.None);

        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Should().HaveCount(1);
        var lot = lots[0];
        lot.Source.Should().Be(CreditSource.Promo);
        lot.Original.Should().Be(100m);
        lot.Remaining.Should().Be(100m);
        lot.ExpiresAt.Should().NotBeNull();
        lot.LotSeq.Should().Be(0L); // brand-new tenant → first lot seq 0

        var bySource = await store.GetRemainingBySourceAsync(tenant, BaseTime, CancellationToken.None);
        bySource.Should().ContainSingle(s => s.Source == CreditSource.Promo && s.Remaining == 100m);
    }

    // Scenario: a deduplicated grant (same external_ref) mints no second lot; balance credited once.
    [Fact]
    public async Task PostGrantAsync_ShouldNotMintSecondLot_WhenGrantDeduped()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("lot-dedup");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.TopUp, externalRef: "topup-dup"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.TopUp, externalRef: "topup-dup"), CancellationToken.None);

        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Should().HaveCount(1);
        lots[0].Remaining.Should().Be(100m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(100m);
    }

    // Scenario: per-source remaining sums to the projection balance across multiple sources.
    [Fact]
    public async Task GetRemainingBySourceAsync_ShouldSumToBalance_WhenMultipleSources()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("lot-sum");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.TopUp, externalRef: "top-1"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 50m, source: CreditSource.Promo, externalRef: "promo-x", expiresAt: BaseTime.AddMonths(1)), CancellationToken.None);

        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(450m);

        var bySource = await store.GetRemainingBySourceAsync(tenant, BaseTime, CancellationToken.None);
        bySource.Sum(s => s.Remaining).Should().Be(balance);
        bySource.Should().Contain(s => s.Source == CreditSource.Subscription && s.Remaining == 300m);
        bySource.Should().Contain(s => s.Source == CreditSource.TopUp && s.Remaining == 100m);
        bySource.Should().Contain(s => s.Source == CreditSource.Promo && s.Remaining == 50m);
    }

    // Scenario: an expired Promo lot is excluded from per-source remaining once `now` is past its expiry.
    [Fact]
    public async Task GetRemainingBySourceAsync_ShouldExcludeExpiredPromo_WhenNowPastExpiry()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("lot-expired");
        var promoExpiry = BaseTime.AddDays(10);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        await store.PostGrantAsync(Grant(tenant, 300m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 50m, source: CreditSource.Promo, externalRef: "promo-exp", expiresAt: promoExpiry), CancellationToken.None);

        var now = promoExpiry.AddSeconds(1);
        var bySource = await store.GetRemainingBySourceAsync(tenant, now, CancellationToken.None);

        bySource.Should().ContainSingle();
        bySource[0].Source.Should().Be(CreditSource.Subscription);
        bySource[0].Remaining.Should().Be(300m);
    }

    // Scenario (Group 3a): a FIFO metered debit spans a Promo lot, a Subscription lot, and a PostPaid tail.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldDrawPromoThenSubscriptionThenPostPaid_WhenSpanning()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-span");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        // Promo 100 (never expires) + Subscription 1000. Balance 1100; debit 1150 ⇒ covered 1100 + tail 50.
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-span"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 1000m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(tenant, 1150m, "usage-span", CancellationToken.None);

        result.CoveredAmount.Should().Be(1100m);
        result.PostPaidAmount.Should().Be(50m);
        result.NewBalance.Should().Be(0m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);

        // One Promo covered row, one Subscription covered row, one PostPaid tail row; two credit_allocation rows.
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Promo)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(2);

        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Should().OnlyContain(l => l.Remaining == 0m);
    }

    // Scenario (Group 3a): n=1 byte-identity — a single Subscription lot degenerates to the two-step covered draw.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldBeByteIdentical_WhenSingleSubscriptionLot()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-n1");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 10m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(tenant, 4m, "usage-n1", CancellationToken.None);

        result.NewBalance.Should().Be(6m);
        result.CoveredAmount.Should().Be(4m);
        result.PostPaidAmount.Should().Be(0m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(6m);
        // Exactly one -4 Subscription debit row, zero PostPaid rows, one invisible credit_allocation row.
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(0);
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(1);
    }

    // Scenario (Group 3a): no open lots ⇒ the whole debit is a single PostPaid tail, no allocation.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldBeAllPostPaid_WhenNoOpenLots()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-nolots");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);

        var result = await store.PostMeteredDebitAsync(tenant, 7m, "usage-nl", CancellationToken.None);

        result.CoveredAmount.Should().Be(0m);
        result.PostPaidAmount.Should().Be(7m);
        result.NewBalance.Should().Be(0m);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.PostPaid)).Should().Be(1);
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(0);
    }

    // Scenario (Group 3a): concurrent debits against a single 4-credit lot never overdraw it.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldNeverOverdraw_WhenConcurrent()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-fifo-concurrent");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 4m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        const int concurrency = 8;
        var debits = Enumerable.Range(0, concurrency)
            .Select(i => store.PostMeteredDebitAsync(tenant, 3m, $"u{i}", CancellationToken.None));
        var results = await Task.WhenAll(debits);

        results.Sum(r => r.CoveredAmount).Should().Be(4m);
        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);
        balance.Should().Be(0m);
        balance.Should().BeGreaterThanOrEqualTo(0m);

        var lots = await store.GetLotsAsync(tenant, CancellationToken.None);
        lots.Sum(l => l.Remaining).Should().Be(0m);
        lots.Should().OnlyContain(l => l.Remaining >= 0m);
    }

    // Scenario (Group 3a): credit_allocation is internal — GetEntries(Count)Async return only ai_credit_ledger rows.
    [Fact]
    public async Task GetEntriesAsync_ShouldExcludeAllocations_WhenLotsDrawn()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-invisible");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-inv"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 1000m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);

        await store.PostMeteredDebitAsync(tenant, 1150m, "usage-inv", CancellationToken.None);

        // 2 grants + 3 debit rows = 5 ledger rows; the 2 allocations are invisible to GetEntries(Count)Async.
        (await store.GetEntriesCountAsync(tenant, CancellationToken.None)).Should().Be(5);
        var entries = await store.GetEntriesAsync(tenant, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(5);
        entries.Should().OnlyContain(e => e.EntryType == CreditEntryType.Grant || e.EntryType == CreditEntryType.Debit);
        // The allocation rows exist underneath but never surface through the ledger API.
        (await _fixture.AllocationRowCountAsync(tenant.Value)).Should().Be(2);
    }

    // Scenario (Group 3a): the lot invariant Σ GetRemainingBySourceAsync == GetBalanceAsync holds after the debit.
    [Fact]
    public async Task PostMeteredDebitAsync_ShouldKeepInvariant_AfterDebit()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("metered-invariant");

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-iv", expiresAt: BaseTime.AddMonths(1)), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 1000m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(tenant, 500m, source: CreditSource.TopUp, externalRef: "top-iv"), CancellationToken.None);

        // 1600 granted; draw 250 (100 Promo + 150 Subscription) ⇒ covered 250, balance 1350.
        await store.PostMeteredDebitAsync(tenant, 250m, "usage-iv", CancellationToken.None);

        var bySource = await store.GetRemainingBySourceAsync(tenant, BaseTime, CancellationToken.None);
        var balance = await store.GetBalanceAsync(tenant, CancellationToken.None);

        balance.Should().Be(1350m);
        bySource.Sum(s => s.Remaining).Should().Be(balance);
    }

    // Scenario (Group 4): a partially-consumed expired Promo lot reclaims only its live remaining, Promo-tagged.
    [Fact]
    public async Task ReclaimExpiredLotAsync_ShouldReclaimRemainingOnly_WhenPartiallyConsumed()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("reclaim-partial");
        var expiry = BaseTime.AddDays(15);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-pc", expiresAt: expiry), CancellationToken.None);

        // Model a prior 30-credit draw against the lot: drop the balance to 70 (a guarded debit, expiry-agnostic)
        // and decrement the lot's remaining to 70 directly so the live lot remaining mirrors the projection.
        await store.TryPostDebitAsync(tenant, 30m, CreditSource.Promo, "usage-pc", CancellationToken.None);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(70m);
        var lotId = (await store.GetLotsAsync(tenant, CancellationToken.None)).Single().LotId;
        await DecrementLotDirectAsync(30m, lotId);
        (await store.GetLotsAsync(tenant, CancellationToken.None)).Single().Remaining.Should().Be(70m);

        var now = expiry.AddSeconds(1);
        var reclaimed = await store.ReclaimExpiredLotAsync(tenant, lotId, now, CancellationToken.None);

        // Reclaims the LIVE remaining (70), not the original 100.
        reclaimed.Should().Be(70m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        (await store.GetLotsAsync(tenant, CancellationToken.None)).Single().Remaining.Should().Be(0m);
        // Two Promo-tagged debit rows: the -30 prior draw + the -70 offsetting reclaim.
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Promo)).Should().Be(2);
    }

    // Scenario (Group 4): a second reclaim of the same lot is a complete no-op.
    [Fact]
    public async Task ReclaimExpiredLotAsync_ShouldBeNoOp_WhenReRun()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("reclaim-rerun");
        var expiry = BaseTime.AddDays(15);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 100m, source: CreditSource.Promo, externalRef: "promo-rr", expiresAt: expiry), CancellationToken.None);

        var lotId = (await store.GetLotsAsync(tenant, CancellationToken.None)).Single().LotId;
        var now = expiry.AddSeconds(1);

        var first = await store.ReclaimExpiredLotAsync(tenant, lotId, now, CancellationToken.None);
        var second = await store.ReclaimExpiredLotAsync(tenant, lotId, now, CancellationToken.None);

        first.Should().Be(100m);
        second.Should().Be(0m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        // 1 grant + exactly 1 reclaim debit = 2 ledger rows (no second reclaim debit).
        (await _fixture.LedgerRowCountAsync(tenant.Value)).Should().Be(2);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(0m);
    }

    // Scenario (Group 4): an unconsumed Subscription lot expiring at period end enforces no-carryover on reclaim.
    [Fact]
    public async Task ReclaimExpiredLotAsync_ShouldEnforceNoCarryover_WhenSubscriptionExpires()
    {
        await _fixture.ResetAsync();
        var tenant = new TenantId("reclaim-no-carryover");
        var periodEnd = BaseTime.AddMonths(1);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(tenant, 400m, source: CreditSource.Subscription, periodKey: "2026-06", expiresAt: periodEnd), CancellationToken.None);

        var lotId = (await store.GetLotsAsync(tenant, CancellationToken.None)).Single().LotId;
        var now = periodEnd.AddSeconds(1);

        var reclaimed = await store.ReclaimExpiredLotAsync(tenant, lotId, now, CancellationToken.None);

        reclaimed.Should().Be(400m);
        (await store.GetBalanceAsync(tenant, CancellationToken.None)).Should().Be(0m);
        (await _fixture.DebitRowCountBySourceAsync(tenant.Value, (short)CreditSource.Subscription)).Should().Be(1);
        (await _fixture.LedgerSumAsync(tenant.Value)).Should().Be(0m);
    }

    // Scenario (Group 4): GetExpiredLotsAsync returns only actually-expired non-empty lots across all tenants.
    [Fact]
    public async Task GetExpiredLotsAsync_ShouldReturnOnlyExpiredNonEmptyLots_AcrossTenants()
    {
        await _fixture.ResetAsync();
        var t1 = new TenantId("expired-1");
        var t2 = new TenantId("expired-2");
        var expiry = BaseTime.AddDays(5);

        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var store = new PostgresCreditLedgerStore(dataSource);
        await store.PostGrantAsync(Grant(t1, 100m, source: CreditSource.Promo, externalRef: "exp-1", expiresAt: expiry), CancellationToken.None);
        await store.PostGrantAsync(Grant(t1, 200m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(t2, 50m, source: CreditSource.Subscription, periodKey: "2026-06", expiresAt: expiry), CancellationToken.None);

        var now = expiry.AddSeconds(1);
        var expired = await store.GetExpiredLotsAsync(now, CancellationToken.None);

        expired.Should().HaveCount(2);
        expired.Should().Contain(l => l.TenantId == t1 && l.Source == CreditSource.Promo);
        expired.Should().Contain(l => l.TenantId == t2 && l.Source == CreditSource.Subscription);
        expired.Should().NotContain(l => l.TenantId == t1 && l.Source == CreditSource.Subscription);
    }

    // Decrements a lot's remaining directly (test helper that mimics a prior FIFO draw against the lot).
    private async Task DecrementLotDirectAsync(decimal by, string lotId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE credit_lot SET remaining = remaining - @By WHERE lot_id = @LotId";
        cmd.Parameters.Add(new NpgsqlParameter("By", by));
        cmd.Parameters.Add(new NpgsqlParameter("LotId", lotId));
        await cmd.ExecuteNonQueryAsync();
    }
}
