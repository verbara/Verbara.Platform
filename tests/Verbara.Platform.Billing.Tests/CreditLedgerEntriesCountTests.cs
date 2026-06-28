using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Billing.Tests;

/// <summary>
/// Tests the <see cref="ICreditLedgerStore.GetEntriesCountAsync"/> contract through the InMemory twin — the
/// <c>TotalCount</c> backing a <c>PagedResult</c> over <see cref="ICreditLedgerStore.GetEntriesAsync"/>
/// (credit-ledger-topups c1, ADR-0033 (c) addendum). The count is independent of the page window and never
/// counts deduplicated grants.
/// </summary>
public sealed class CreditLedgerEntriesCountTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CreditLedgerEntry Grant(decimal amount, string? periodKey = "2026-06", string? externalRef = null) => new()
    {
        EntryId = EntityId.New(),
        TenantId = Tenant1,
        EntryType = CreditEntryType.Grant,
        Source = periodKey is null ? CreditSource.TopUp : CreditSource.Subscription,
        Amount = amount,
        PeriodKey = periodKey,
        ExternalRef = externalRef,
        CreatedAt = BaseTime,
    };

    [Fact]
    public async Task GetEntriesCountAsync_ShouldReturnZero_WhenTenantHasNoLedger()
    {
        var store = new InMemoryCreditLedgerStore();

        var count = await store.GetEntriesCountAsync(Tenant1, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetEntriesCountAsync_ShouldEqualEntryCount_WhenNGrantsAndDebitsPosted()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(Grant(1000m, periodKey: "2026-06"), CancellationToken.None);
        await store.PostGrantAsync(Grant(50m, periodKey: null, externalRef: "topup-1"), CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 10m, CreditSource.Subscription, "u1", CancellationToken.None);
        await store.TryPostDebitAsync(Tenant1, 20m, CreditSource.Subscription, "u2", CancellationToken.None);

        // 2 grants + 2 debits = 4, and the count is independent of the page window.
        var count = await store.GetEntriesCountAsync(Tenant1, CancellationToken.None);
        var firstPage = await store.GetEntriesAsync(Tenant1, 1, 2, CancellationToken.None);

        count.Should().Be(4);
        firstPage.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEntriesCountAsync_ShouldNotCountDuplicateGrant_WhenIdempotentRepost()
    {
        var store = new InMemoryCreditLedgerStore();
        await store.PostGrantAsync(Grant(50m, periodKey: null, externalRef: "topup-1"), CancellationToken.None);
        // Same external_ref — idempotent no-op, no second row appended.
        await store.PostGrantAsync(Grant(50m, periodKey: null, externalRef: "topup-1"), CancellationToken.None);

        (await store.GetEntriesCountAsync(Tenant1, CancellationToken.None)).Should().Be(1);
    }
}
