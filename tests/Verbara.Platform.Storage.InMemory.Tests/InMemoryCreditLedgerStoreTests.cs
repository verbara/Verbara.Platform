using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// Unit tests for the <see cref="InMemoryCreditLedgerStore"/> twin. The InMemory store MUST have semantics
/// identical to <c>PostgresCreditLedgerStore</c>: an O(1) balance projection, unconditional idempotent grants
/// (keyed on <c>period_key</c> / <c>external_ref</c>), and a guarded compare-and-decrement debit that can
/// never drive the balance negative — even under concurrency. See ADR-0033 / the credit-ledger-substrate
/// spec delta.
/// </summary>
public sealed class InMemoryCreditLedgerStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CreditLedgerEntry MakeGrant(
        TenantId? tenantId = null,
        decimal amount = 300m,
        CreditSource source = CreditSource.Subscription,
        string? periodKey = "2026-06",
        string? externalRef = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        EntryId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        EntryType = CreditEntryType.Grant,
        Source = source,
        Amount = amount,
        PeriodKey = periodKey,
        ExternalRef = externalRef,
        ExpiresAt = expiresAt,
        UsageRecordId = null,
        CreatedAt = BaseTime,
    };

    [Fact]
    public async Task GetBalanceAsync_ShouldReturnZero_WhenNoLedger()
    {
        var store = new InMemoryCreditLedgerStore();

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);

        balance.Should().Be(0m);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldIncreaseBalance_WhenApplied()
    {
        var store = new InMemoryCreditLedgerStore();

        await store.PostGrantAsync(MakeGrant(amount: 300m), CancellationToken.None);

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(300m);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldBeNoOp_WhenDuplicatePeriodKey()
    {
        var store = new InMemoryCreditLedgerStore();

        await store.PostGrantAsync(MakeGrant(amount: 300m, periodKey: "2026-06"), CancellationToken.None);
        // Same (tenant, period_key, entry_type) — must be a no-op: neither double-inserts nor double-credits.
        await store.PostGrantAsync(MakeGrant(amount: 300m, periodKey: "2026-06"), CancellationToken.None);

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(300m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldBeNoOp_WhenDuplicateExternalRef()
    {
        var store = new InMemoryCreditLedgerStore();

        await store.PostGrantAsync(
            MakeGrant(amount: 100m, source: CreditSource.TopUp, periodKey: null, externalRef: "topup-1"),
            CancellationToken.None);
        await store.PostGrantAsync(
            MakeGrant(amount: 100m, source: CreditSource.TopUp, periodKey: null, externalRef: "topup-1"),
            CancellationToken.None);

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(100m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldAlwaysInsert_WhenNoIdempotencyKey()
    {
        var store = new InMemoryCreditLedgerStore();

        await store.PostGrantAsync(MakeGrant(amount: 50m, source: CreditSource.Promo, periodKey: null), CancellationToken.None);
        await store.PostGrantAsync(MakeGrant(amount: 50m, source: CreditSource.Promo, periodKey: null), CancellationToken.None);

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(100m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task TryPostDebitAsync_ShouldPost_WhenBalanceSufficient()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 300m), CancellationToken.None);

        var result = await store.TryPostDebitAsync(Tenant1, 100m, "usage-1", CancellationToken.None);

        result.IsPosted.Should().BeTrue();
        result.Outcome.Should().Be(CreditDebitOutcome.Posted);
        result.NewBalance.Should().Be(200m);

        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(200m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(2);
        var debit = entries[0]; // most-recent-first
        debit.EntryType.Should().Be(CreditEntryType.Debit);
        debit.Amount.Should().Be(-100m);
        debit.UsageRecordId.Should().Be("usage-1");
    }

    [Fact]
    public async Task TryPostDebitAsync_ShouldReject_WhenBalanceInsufficient()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 50m), CancellationToken.None);

        var result = await store.TryPostDebitAsync(Tenant1, 100m, "usage-1", CancellationToken.None);

        result.IsPosted.Should().BeFalse();
        result.Outcome.Should().Be(CreditDebitOutcome.RejectedInsufficientBalance);

        // Nothing written: balance unchanged, no debit ledger row.
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(50m);
        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(1);
        entries[0].EntryType.Should().Be(CreditEntryType.Grant);
    }

    [Fact]
    public async Task TryPostDebitAsync_ShouldReject_WhenNoLedgerExists()
    {
        var store = new InMemoryCreditLedgerStore();

        var result = await store.TryPostDebitAsync(Tenant1, 1m, null, CancellationToken.None);

        result.IsPosted.Should().BeFalse();
        result.Outcome.Should().Be(CreditDebitOutcome.RejectedInsufficientBalance);
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);
        (await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task TryPostDebitAsync_ShouldNeverGoNegative_UnderConcurrentDebits()
    {
        var store = new InMemoryCreditLedgerStore();
        // Balance exactly 5; many concurrent debits of 5 — at most one can win.
        await store.PostGrantAsync(MakeGrant(amount: 5m), CancellationToken.None);

        const int concurrency = 64;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => store.TryPostDebitAsync(Tenant1, 5m, null, CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsPosted).Should().Be(1);
        results.Count(r => r.Outcome == CreditDebitOutcome.RejectedInsufficientBalance).Should().Be(concurrency - 1);

        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);

        // Exactly one debit ledger row was appended (the single winner).
        var entries = await store.GetEntriesAsync(Tenant1, 1, 200, CancellationToken.None);
        entries.Count(e => e.EntryType == CreditEntryType.Debit).Should().Be(1);
    }

    [Fact]
    public async Task GetEntriesAsync_ShouldReturnMostRecentFirst_WhenPaginated()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 1000m), CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 10m, "u1", CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 20m, "u2", CancellationToken.None);

        var page1 = await store.GetEntriesAsync(Tenant1, 1, 2, CancellationToken.None);
        page1.Should().HaveCount(2);
        page1[0].UsageRecordId.Should().Be("u2"); // most recent debit first
        page1[1].UsageRecordId.Should().Be("u1");

        var page2 = await store.GetEntriesAsync(Tenant1, 2, 2, CancellationToken.None);
        page2.Should().HaveCount(1);
        page2[0].EntryType.Should().Be(CreditEntryType.Grant);
    }

    [Fact]
    public async Task GetBalanceAsync_ShouldIsolateTenants_WhenMultipleTenants()
    {
        var store = new InMemoryCreditLedgerStore();
        var tenant2 = new TenantId("tenant-2");
        await store.PostGrantAsync(MakeGrant(tenantId: Tenant1, amount: 300m), CancellationToken.None);
        await store.PostGrantAsync(MakeGrant(tenantId: tenant2, amount: 50m), CancellationToken.None);

        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(300m);
        (await store.GetBalanceAsync(tenant2, CancellationToken.None)).Should().Be(50m);
    }
}
