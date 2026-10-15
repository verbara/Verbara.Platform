using Verbara.Platform.Core.Webhooks;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public class InMemoryWebhookDeliveryStoreTests
{
    private readonly InMemoryWebhookDeliveryStore _store = new();

    private static WebhookDelivery CreateDelivery(
        string? id = null, string subId = "sub1", string tenantId = "t1",
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending,
        DateTimeOffset? nextRetryAt = null, int attempts = 0) => new(
        DeliveryId: id ?? Guid.NewGuid().ToString("N"),
        TenantId: tenantId,
        SubscriptionId: subId,
        EventType: "conversation.message",
        Payload: "{}",
        Status: status,
        Attempts: attempts,
        MaxAttempts: 8,
        NextRetryAt: nextRetryAt,
        LastResponseCode: null,
        LastError: null,
        CreatedAt: DateTimeOffset.UtcNow,
        DeliveredAt: null);

    [Fact]
    public async Task SaveAsync_ShouldPersist_WhenCalledWithNewDelivery()
    {
        var delivery = CreateDelivery(id: "d1");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var result = await _store.GetByIdAsync("d1", CancellationToken.None);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldReturnOnlyDueDeliveries()
    {
        var now = DateTimeOffset.UtcNow;

        await _store.SaveAsync(CreateDelivery(id: "due", nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "future", nextRetryAt: now.AddMinutes(10)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "delivered",
            status: WebhookDeliveryStatus.Delivered, nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "no-retry",
            status: WebhookDeliveryStatus.Pending, nextRetryAt: null), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 100, CancellationToken.None);
        result.Should().HaveCount(1);
        result[0].DeliveryId.Should().Be("due");
    }

    [Fact]
    public async Task ListPendingRetriesAsync_ShouldRespectBatchSize()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
            await _store.SaveAsync(CreateDelivery(nextRetryAt: now.AddMinutes(-1)), CancellationToken.None);

        var result = await _store.ListPendingRetriesAsync(now, 3, CancellationToken.None);
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListDeadLetterAsync_ShouldReturnPaginatedDeadLetters()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(tenantId: "t1",
                status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(tenantId: "t2",
            status: WebhookDeliveryStatus.DeadLetter), CancellationToken.None);

        var result = await _store.ListDeadLetterAsync("t1", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceDelivery()
    {
        var delivery = CreateDelivery(id: "upd");
        await _store.SaveAsync(delivery, CancellationToken.None);

        var updated = delivery with { Status = WebhookDeliveryStatus.Delivered };
        await _store.UpdateAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync("upd", CancellationToken.None);
        result!.Status.Should().Be(WebhookDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task DeleteBySubscriptionAsync_ShouldRemoveAllDeliveriesForSubscription()
    {
        await _store.SaveAsync(CreateDelivery(id: "d1", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d2", subId: "sub-del"), CancellationToken.None);
        await _store.SaveAsync(CreateDelivery(id: "d3", subId: "sub-keep"), CancellationToken.None);

        await _store.DeleteBySubscriptionAsync("sub-del", CancellationToken.None);

        (await _store.GetByIdAsync("d1", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d2", CancellationToken.None)).Should().BeNull();
        (await _store.GetByIdAsync("d3", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ListBySubscriptionAsync_ShouldReturnPaginatedResults()
    {
        for (int i = 0; i < 5; i++)
            await _store.SaveAsync(CreateDelivery(subId: "sub-page"), CancellationToken.None);

        var result = await _store.ListBySubscriptionAsync("sub-page", 1, 3, CancellationToken.None);
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }

    // ── Claims (webhook-delivery-runs-once 3.1; design D2, D5, D8; Platform/ADR-0043) ──────────────────────
    // The same scenarios as Storage.Postgres.Tests/Stores/PostgresWebhookDeliveryStoreClaimTests, so the InMemory
    // store gives the claim semantics of Postgres. The claim columns are not on the record, so the claim is
    // observed through what the store then allows: a refused claim, Reclaimed, and the fence.

    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Only bounds a regression to a wait; a passing run never comes near it.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private async Task<WebhookDelivery> SavedAsync(WebhookDelivery delivery)
    {
        await _store.SaveAsync(delivery, CancellationToken.None);
        return delivery;
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldClaimEachDueDeliveryExactlyOnce_WhenFourClaimersRunConcurrently()
    {
        var ids = new List<string>();
        for (var i = 0; i < 40; i++)
            ids.Add((await SavedAsync(CreateDelivery(nextRetryAt: T0.AddSeconds(-i)))).DeliveryId);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var claimed = new List<string>();
            while (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None) is { } claim)
                claimed.Add(claim.Delivery.DeliveryId);
            return claimed;
        })).ToList();
        start.SetResult();
        var perClaimer = await Task.WhenAll(claimers).WaitAsync(Guard);

        var union = perClaimer.SelectMany(c => c).ToList();
        union.Should().OnlyHaveUniqueItems("a delivery claimed once is not claimable again while its lease runs");
        union.Should().BeEquivalentTo(ids, "every due delivery is claimed, none is missed");
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldSkipDelivery_WhenItsClaimHasNotExpired()
    {
        var older = await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-2)));
        var newer = await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-1)));

        var first = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        first!.Delivery.Should().Be(older, "the delivery due the longest is claimed first");

        var second = await _store.ClaimNextDueAsync(T0.AddMinutes(4), T0.AddMinutes(4) + Lease, CancellationToken.None);
        second!.Delivery.Should().Be(newer, "the older delivery's claim is live until T0 + lease");

        (await _store.ClaimNextDueAsync(T0 + Lease - TimeSpan.FromSeconds(1), T0 + 2 * Lease, CancellationToken.None))
            .Should().BeNull("both claims are live");
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldReclaimAndReportReclaimed_WhenTheClaimHasExpired()
    {
        var delivery = await SavedAsync(CreateDelivery(nextRetryAt: T0, attempts: 3));

        var first = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        first!.Reclaimed.Should().BeFalse("nothing held the delivery before");

        // At exactly T0 + lease the claim has expired (claim_expires_at <= now).
        var second = await _store.ClaimNextDueAsync(T0 + Lease, T0 + 2 * Lease, CancellationToken.None);

        second.Should().NotBeNull("an expired claim no longer protects the delivery");
        second!.Delivery.Should().Be(delivery, "a claim changes no delivery field, attempts included");
        second.Reclaimed.Should().BeTrue("it took over a claim that never recorded an outcome");
        second.ClaimId.Should().NotBe(first.ClaimId).And.MatchRegex("^[0-9a-f]{32}$");
        (await _store.ClaimNextDueAsync(T0 + Lease, T0 + 2 * Lease, CancellationToken.None))
            .Should().BeNull("the new claim is live");
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldIgnoreDelivery_WhenNotPendingOrNotDue()
    {
        var notClaimable = new[]
        {
            await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.Delivered)),
            await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.Failed)),
            await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.DeadLetter)),
            await SavedAsync(CreateDelivery(nextRetryAt: T0.AddSeconds(1))),
            await SavedAsync(CreateDelivery(nextRetryAt: null)),
        };

        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None)).Should().BeNull();
        foreach (var delivery in notClaimable)
        {
            (await _store.TryClaimAsync(delivery.DeliveryId, T0, T0 + Lease, CancellationToken.None))
                .Should().BeNull("a delivery that is not Pending, or not due, is not claimable by id either");
        }

        var due = await SavedAsync(CreateDelivery(nextRetryAt: T0));
        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None))!.Delivery.Should().Be(due,
            "a Pending delivery due exactly now is claimable");
    }

    [Fact]
    public async Task TryClaimAsync_ShouldReturnNull_WhenAnotherClaimIsLive()
    {
        var polled = await SavedAsync(CreateDelivery(nextRetryAt: T0.AddMinutes(-1)));
        var free = await SavedAsync(CreateDelivery(nextRetryAt: T0));
        var pollClaim = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        pollClaim!.Delivery.DeliveryId.Should().Be(polled.DeliveryId);

        (await _store.TryClaimAsync(polled.DeliveryId, T0.AddMinutes(1), T0.AddMinutes(1) + Lease, CancellationToken.None))
            .Should().BeNull("the poll's claim is live");
        (await _store.ReleaseClaimAsync(polled.DeliveryId, pollClaim.ClaimId, T0, CancellationToken.None))
            .Should().BeTrue("a refused claim leaves the poll's claim current");

        var byId = await _store.TryClaimAsync(free.DeliveryId, T0.AddMinutes(1), T0.AddMinutes(1) + Lease, CancellationToken.None);
        byId.Should().NotBeNull("an unclaimed due delivery is claimable by id");
        byId!.Delivery.Should().Be(free);
        byId.Reclaimed.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAttemptAsync_ShouldReturnFalseAndLeaveRowUnchanged_WhenClaimIdIsStale()
    {
        var delivery = await SavedAsync(CreateDelivery(nextRetryAt: T0));
        var stale = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        var current = await _store.ClaimNextDueAsync(T0 + Lease, T0 + 2 * Lease, CancellationToken.None);
        current!.Reclaimed.Should().BeTrue();
        var rowBefore = await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None);

        var lateFailure = stale!.Delivery with
        {
            Attempts = 1,
            NextRetryAt = T0.AddMinutes(30),
            LastResponseCode = 500,
            LastError = "HTTP 500",
        };
        (await _store.CompleteAttemptAsync(lateFailure, stale.ClaimId, CancellationToken.None))
            .Should().BeFalse("another attempt has taken the claim over");

        (await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None)).Should().Be(rowBefore);
        (await _store.ReleaseClaimAsync(delivery.DeliveryId, stale.ClaimId, T0.AddHours(1), CancellationToken.None))
            .Should().BeFalse("a stale claim cannot release either");
        (await _store.TryClaimAsync(delivery.DeliveryId, T0 + Lease, T0 + 2 * Lease, CancellationToken.None))
            .Should().BeNull("the current claim is untouched and still live");

        var delivered = current.Delivery with
        {
            Status = WebhookDeliveryStatus.Delivered,
            Attempts = 1,
            NextRetryAt = null,
            LastResponseCode = 200,
            DeliveredAt = T0 + Lease,
        };
        (await _store.CompleteAttemptAsync(delivered, current.ClaimId, CancellationToken.None))
            .Should().BeTrue("the current claim still records its outcome");
    }

    [Fact]
    public async Task CompleteAttemptAsync_ShouldWriteOutcomeAndClearClaim_WhenClaimIdIsCurrent()
    {
        var delivery = await SavedAsync(CreateDelivery(nextRetryAt: T0));

        var firstAttempt = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        var retry = firstAttempt!.Delivery with
        {
            Attempts = 1,
            NextRetryAt = T0.AddMinutes(1),
            LastResponseCode = 500,
            LastError = "HTTP 500",
        };
        (await _store.CompleteAttemptAsync(retry, firstAttempt.ClaimId, CancellationToken.None)).Should().BeTrue();
        (await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None)).Should().Be(retry);

        // Long before the first claim's lease would have passed: the claim is gone, not expired.
        var secondAttempt = await _store.ClaimNextDueAsync(T0.AddMinutes(1), T0.AddMinutes(1) + Lease, CancellationToken.None);
        secondAttempt.Should().NotBeNull("the scheduled retry is due and nothing holds it");
        secondAttempt!.Reclaimed.Should().BeFalse("the first claim was cleared, not taken over");
        secondAttempt.Delivery.Should().Be(retry);

        var delivered = secondAttempt.Delivery with
        {
            Status = WebhookDeliveryStatus.Delivered,
            Attempts = 2,
            NextRetryAt = null,
            LastResponseCode = 200,
            LastError = null,
            DeliveredAt = T0.AddMinutes(1),
        };
        (await _store.CompleteAttemptAsync(delivered, secondAttempt.ClaimId, CancellationToken.None)).Should().BeTrue();
        (await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None)).Should().Be(delivered);
        (await _store.CompleteAttemptAsync(delivered, secondAttempt.ClaimId, CancellationToken.None))
            .Should().BeFalse("the claim was cleared by the outcome it recorded");
    }

    [Fact]
    public async Task ReleaseClaimAsync_ShouldClearClaimAndKeepAttempts_WhenClaimIdIsCurrent()
    {
        var delivery = await SavedAsync(CreateDelivery(nextRetryAt: T0, attempts: 2));
        var claim = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        var probeAt = T0.AddMinutes(10);

        (await _store.ReleaseClaimAsync(delivery.DeliveryId, claim!.ClaimId, probeAt, CancellationToken.None))
            .Should().BeTrue();

        (await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None))
            .Should().Be(delivery with { NextRetryAt = probeAt }, "a release changes next_retry_at only: attempts stays 2");
        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None))
            .Should().BeNull("a released delivery is not due again before its new next_retry_at");
        (await _store.ReleaseClaimAsync(delivery.DeliveryId, claim.ClaimId, probeAt, CancellationToken.None))
            .Should().BeFalse("the claim is gone");

        var atProbe = await _store.ClaimNextDueAsync(probeAt, probeAt + Lease, CancellationToken.None);
        atProbe.Should().NotBeNull();
        atProbe!.Reclaimed.Should().BeFalse("the claim was released, not taken over");
        atProbe.Delivery.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_ShouldKeepTheClaim_WhenTheRowIsClaimed()
    {
        var delivery = await SavedAsync(CreateDelivery(nextRetryAt: T0));
        var claim = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);

        await _store.UpdateAsync(delivery with { LastError = "edited" }, CancellationToken.None);

        (await _store.TryClaimAsync(delivery.DeliveryId, T0, T0 + Lease, CancellationToken.None))
            .Should().BeNull("Postgres' UpdateAsync does not touch the claim columns, and neither does this store");
        (await _store.ReleaseClaimAsync(delivery.DeliveryId, claim!.ClaimId, T0, CancellationToken.None))
            .Should().BeTrue("the claim survived the update");
    }
}
