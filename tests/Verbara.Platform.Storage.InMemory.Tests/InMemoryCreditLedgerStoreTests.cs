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

    // A grant with an explicit EntryId and CreatedAt (and a distinct external_ref so it always inserts) —
    // lets the deterministic-tiebreak test pin the (CreatedAt DESC, EntryId DESC) ordering at a same instant.
    private static CreditLedgerEntry SameInstantGrant(string entryId, DateTimeOffset createdAt, string externalRef) => new()
    {
        EntryId = EntityId.From(entryId),
        TenantId = Tenant1,
        EntryType = CreditEntryType.Grant,
        Source = CreditSource.TopUp,
        Amount = 1m,
        PeriodKey = null,
        ExternalRef = externalRef,
        ExpiresAt = null,
        UsageRecordId = null,
        CreatedAt = createdAt,
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

        var result = await store.TryPostDebitAsync(Tenant1, 100m, CreditSource.Subscription, "usage-1", CancellationToken.None);

        result.IsPosted.Should().BeTrue();
        result.Outcome.Should().Be(CreditDebitOutcome.Posted);
        result.NewBalance.Should().Be(200m);

        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(200m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        entries.Should().HaveCount(2);
        var debit = entries[0]; // most-recent-first
        debit.EntryType.Should().Be(CreditEntryType.Debit);
        debit.Amount.Should().Be(-100m);
        // A covered draw records the lot it drew from (Subscription), NOT a hard-coded PostPaid.
        debit.Source.Should().Be(CreditSource.Subscription);
        debit.UsageRecordId.Should().Be("usage-1");
    }

    [Fact]
    public async Task TryPostDebitAsync_ShouldReject_WhenBalanceInsufficient()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 50m), CancellationToken.None);

        var result = await store.TryPostDebitAsync(Tenant1, 100m, CreditSource.Subscription, "usage-1", CancellationToken.None);

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

        var result = await store.TryPostDebitAsync(Tenant1, 1m, CreditSource.Subscription, null, CancellationToken.None);

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
            .Select(_ => Task.Run(() => store.TryPostDebitAsync(Tenant1, 5m, CreditSource.Subscription, null, CancellationToken.None)))
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
        await store.TryPostDebitAsync(Tenant1, 10m, CreditSource.Subscription, "u1", CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 20m, CreditSource.Subscription, "u2", CancellationToken.None);

        var page1 = await store.GetEntriesAsync(Tenant1, 1, 2, CancellationToken.None);
        page1.Should().HaveCount(2);
        page1[0].UsageRecordId.Should().Be("u2"); // most recent debit first
        page1[1].UsageRecordId.Should().Be("u1");

        var page2 = await store.GetEntriesAsync(Tenant1, 2, 2, CancellationToken.None);
        page2.Should().HaveCount(1);
        page2[0].EntryType.Should().Be(CreditEntryType.Grant);
    }

    [Fact]
    public async Task GetEntriesAsync_ShouldOrderByEntryIdTiebreak_WhenSameInstant()
    {
        var store = new InMemoryCreditLedgerStore();
        var sameInstant = BaseTime;

        // Three same-instant grants inserted out of EntryId order. Distinct external_refs keep all three
        // inserting (no dedupe). Most-recent-first with the (CreatedAt DESC, EntryId DESC) tiebreak MUST
        // sort them by EntryId descending — "ccc", "bbb", "aaa" — regardless of insertion order.
        await store.PostGrantAsync(SameInstantGrant("bbb", sameInstant, "ref-b"), CancellationToken.None);
        await store.PostGrantAsync(SameInstantGrant("aaa", sameInstant, "ref-a"), CancellationToken.None);
        await store.PostGrantAsync(SameInstantGrant("ccc", sameInstant, "ref-c"), CancellationToken.None);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 10, CancellationToken.None);

        entries.Should().HaveCount(3);
        entries.Select(e => e.EntryId.Value).Should().ContainInOrder("ccc", "bbb", "aaa");
    }

    [Fact]
    public async Task GetEntriesCountAsync_ShouldReturnZero_WhenNoLedger()
    {
        var store = new InMemoryCreditLedgerStore();

        var count = await store.GetEntriesCountAsync(Tenant1, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetEntriesCountAsync_ShouldReturnEntryCount_WhenGrantsAndDebitsPosted()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 1000m), CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 10m, CreditSource.Subscription, "u1", CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 20m, CreditSource.Subscription, "u2", CancellationToken.None);

        // 1 grant + 2 debit rows = 3, independent of the page window.
        var count = await store.GetEntriesCountAsync(Tenant1, CancellationToken.None);

        count.Should().Be(3);
    }

    [Fact]
    public async Task GetEntriesCountAsync_ShouldNotCountDeduplicatedGrants_WhenDuplicatePeriodKey()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 300m, periodKey: "2026-06"), CancellationToken.None);
        // Duplicate (tenant, period_key, entry_type) is a no-op — no second row appended.
        await store.PostGrantAsync(MakeGrant(amount: 300m, periodKey: "2026-06"), CancellationToken.None);

        (await store.GetEntriesCountAsync(Tenant1, CancellationToken.None)).Should().Be(1);
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

    [Fact]
    public async Task PostMeteredDebitAsync_ShouldDrawFromCoveredLot_WhenFullyCovered()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 10m), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(Tenant1, 4m, CreditSource.Subscription, "usage-c", CancellationToken.None);

        result.NewBalance.Should().Be(6m);
        result.CoveredAmount.Should().Be(4m);
        result.PostPaidAmount.Should().Be(0m);
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(6m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        var debits = entries.Where(e => e.EntryType == CreditEntryType.Debit).ToList();
        debits.Should().HaveCount(1);
        debits[0].Source.Should().Be(CreditSource.Subscription);
        debits[0].Amount.Should().Be(-4m);
    }

    [Fact]
    public async Task PostMeteredDebitAsync_ShouldFloorAndPostPostPaidTail_WhenOverflowing()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 3m), CancellationToken.None);

        var result = await store.PostMeteredDebitAsync(Tenant1, 5m, CreditSource.Subscription, "usage-o", CancellationToken.None);

        result.NewBalance.Should().Be(0m);
        result.CoveredAmount.Should().Be(3m);
        result.PostPaidAmount.Should().Be(2m);
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        var debits = entries.Where(e => e.EntryType == CreditEntryType.Debit).ToList();
        debits.Count(d => d.Source == CreditSource.Subscription && d.Amount == -3m).Should().Be(1);
        debits.Count(d => d.Source == CreditSource.PostPaid && d.Amount == -2m).Should().Be(1);
    }

    [Fact]
    public async Task PostMeteredDebitAsync_ShouldPostFullPostPaidTail_WhenNoPrepaidBalance()
    {
        var store = new InMemoryCreditLedgerStore();

        var result = await store.PostMeteredDebitAsync(Tenant1, 7m, CreditSource.Subscription, "usage-z", CancellationToken.None);

        result.NewBalance.Should().Be(0m);
        result.CoveredAmount.Should().Be(0m);
        result.PostPaidAmount.Should().Be(7m);
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);

        var entries = await store.GetEntriesAsync(Tenant1, 1, 50, CancellationToken.None);
        var debits = entries.Where(e => e.EntryType == CreditEntryType.Debit).ToList();
        debits.Should().HaveCount(1);
        debits[0].Source.Should().Be(CreditSource.PostPaid);
        debits[0].Amount.Should().Be(-7m);
    }

    [Fact]
    public async Task PostMeteredDebitAsync_ShouldNeverOverdrawPrepaid_WhenConcurrent()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 4m), CancellationToken.None);

        const int concurrency = 32;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => store.PostMeteredDebitAsync(Tenant1, 3m, CreditSource.Subscription, null, CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // The projection never goes negative; total covered across all calls equals the original balance.
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(0m);
        results.Sum(r => r.CoveredAmount).Should().Be(4m);
        // Every call posts its full debit; the uncovered remainder all lands as PostPaid tail.
        results.Sum(r => r.PostPaidAmount).Should().Be(concurrency * 3m - 4m);
    }

    [Fact]
    public async Task GetPostPaidDebitsTotalAsync_ShouldSumOnlyPeriodPostPaidDebits_WhenMixedEntries()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 3m), CancellationToken.None);

        // Covered 3 + PostPaid tail 2, then a pure PostPaid debit of 4 (balance already 0).
        await store.PostMeteredDebitAsync(Tenant1, 5m, CreditSource.Subscription, "u1", CancellationToken.None);
        await store.PostMeteredDebitAsync(Tenant1, 4m, CreditSource.Subscription, "u2", CancellationToken.None);

        var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var total = await store.GetPostPaidDebitsTotalAsync(Tenant1, periodStart, periodEnd, CancellationToken.None);

        // PostPaid tail 2 + PostPaid 4 = 6 (the -3 covered Subscription debit is excluded).
        total.Should().Be(6m);

        var emptyTotal = await store.GetPostPaidDebitsTotalAsync(
            Tenant1,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        emptyTotal.Should().Be(0m);
    }

    [Fact]
    public async Task GetPostPaidDebitsTotalAsync_ShouldReturnZero_WhenNoLedger()
    {
        var store = new InMemoryCreditLedgerStore();

        var total = await store.GetPostPaidDebitsTotalAsync(
            Tenant1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        total.Should().Be(0m);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldMintLot_WhenGrantInserted()
    {
        var store = new InMemoryCreditLedgerStore();
        var expiry = BaseTime.AddMonths(1);

        await store.PostGrantAsync(
            MakeGrant(amount: 100m, source: CreditSource.Promo, periodKey: null, externalRef: "promo-1", expiresAt: expiry),
            CancellationToken.None);

        var lots = await store.GetLotsAsync(Tenant1, CancellationToken.None);
        lots.Should().HaveCount(1);
        var lot = lots[0];
        lot.Source.Should().Be(CreditSource.Promo);
        lot.Original.Should().Be(100m);
        lot.Remaining.Should().Be(100m);
        lot.ExpiresAt.Should().Be(expiry);
        lot.GrantedAt.Should().Be(BaseTime);
        lot.LotSeq.Should().Be(0L);

        // GetRemainingBySourceAsync reports Promo=100 (now is before the expiry).
        var bySource = await store.GetRemainingBySourceAsync(Tenant1, BaseTime, CancellationToken.None);
        bySource.Should().ContainSingle(s => s.Source == CreditSource.Promo && s.Remaining == 100m);
    }

    [Fact]
    public async Task PostGrantAsync_ShouldNotMintSecondLot_WhenGrantDeduped()
    {
        var store = new InMemoryCreditLedgerStore();

        await store.PostGrantAsync(
            MakeGrant(amount: 100m, source: CreditSource.TopUp, periodKey: null, externalRef: "topup-dup"),
            CancellationToken.None);
        // Same external_ref → deduplicated grant: no second lot, balance credited once.
        await store.PostGrantAsync(
            MakeGrant(amount: 100m, source: CreditSource.TopUp, periodKey: null, externalRef: "topup-dup"),
            CancellationToken.None);

        var lots = await store.GetLotsAsync(Tenant1, CancellationToken.None);
        lots.Should().HaveCount(1);
        lots[0].Remaining.Should().Be(100m);
        (await store.GetBalanceAsync(Tenant1, CancellationToken.None)).Should().Be(100m);
    }

    [Fact]
    public async Task GetRemainingBySourceAsync_ShouldSumToBalance_WhenMultipleSources()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(MakeGrant(amount: 300m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(MakeGrant(amount: 100m, source: CreditSource.TopUp, periodKey: null, externalRef: "top-1"), CancellationToken.None);
        await store.PostGrantAsync(MakeGrant(amount: 50m, source: CreditSource.Promo, periodKey: null, externalRef: "promo-x", expiresAt: BaseTime.AddMonths(1)), CancellationToken.None);

        var balance = await store.GetBalanceAsync(Tenant1, CancellationToken.None);
        balance.Should().Be(450m);

        var bySource = await store.GetRemainingBySourceAsync(Tenant1, BaseTime, CancellationToken.None);

        bySource.Sum(s => s.Remaining).Should().Be(balance);
        bySource.Should().Contain(s => s.Source == CreditSource.Subscription && s.Remaining == 300m);
        bySource.Should().Contain(s => s.Source == CreditSource.TopUp && s.Remaining == 100m);
        bySource.Should().Contain(s => s.Source == CreditSource.Promo && s.Remaining == 50m);
    }

    [Fact]
    public async Task GetRemainingBySourceAsync_ShouldExcludeExpiredPromo_WhenNowPastExpiry()
    {
        var store = new InMemoryCreditLedgerStore();
        var promoExpiry = BaseTime.AddDays(10);
        await store.PostGrantAsync(MakeGrant(amount: 300m, source: CreditSource.Subscription, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(MakeGrant(amount: 50m, source: CreditSource.Promo, periodKey: null, externalRef: "promo-exp", expiresAt: promoExpiry), CancellationToken.None);

        // A `now` strictly after the promo expiry: the promo lot is excluded; only Subscription remains.
        var now = promoExpiry.AddSeconds(1);
        var bySource = await store.GetRemainingBySourceAsync(Tenant1, now, CancellationToken.None);

        bySource.Should().ContainSingle();
        bySource[0].Source.Should().Be(CreditSource.Subscription);
        bySource[0].Remaining.Should().Be(300m);
    }
}
