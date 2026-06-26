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
        var result = await store.TryPostDebitAsync(tenant, 100m, "usage-1", CancellationToken.None);

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

        var result = await store.TryPostDebitAsync(tenant, 100m, usageRecordId: null, CancellationToken.None);

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

        var result = await store.TryPostDebitAsync(tenant, 100m, "usage-x", CancellationToken.None);

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
        var debitA = store.TryPostDebitAsync(tenant, 5m, "a", CancellationToken.None);
        var debitB = store.TryPostDebitAsync(tenant, 5m, "b", CancellationToken.None);
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
            .Select(i => store.TryPostDebitAsync(tenant, 10m, $"u{i}", CancellationToken.None));
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
        await store.TryPostDebitAsync(tenant, 100m, "usage-7", CancellationToken.None);

        var entries = await store.GetEntriesAsync(tenant, page: 1, pageSize: 10, CancellationToken.None);

        entries.Should().HaveCount(2);
        // Debit (−100) was posted after the grant → first when ordered most-recent-first.
        entries[0].EntryType.Should().Be(CreditEntryType.Debit);
        entries[0].Amount.Should().Be(-100m);
        entries[0].Source.Should().Be(CreditSource.PostPaid);
        entries[0].UsageRecordId.Should().Be("usage-7");
        entries[1].EntryType.Should().Be(CreditEntryType.Grant);
        entries[1].Amount.Should().Be(300m);
        entries[1].PeriodKey.Should().Be("2026-06");
        entries[1].ExpiresAt.Should().NotBeNull();
    }
}
