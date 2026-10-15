using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Webhooks;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// webhook-delivery-runs-once 3.1 (design D2, D5, D8; Platform/ADR-0043): the delivery claims of
/// <see cref="PostgresWebhookDeliveryStore"/> against the REAL embedded migrations (incl.
/// <c>023_WebhookDeliveryClaim.sql</c>). The claim of the next due delivery is global, not tenant-scoped, so each
/// test starts from empty webhook tables; this class owns its <see cref="MigrationsFixture"/> container, and xUnit
/// runs its tests one after the other. <c>Storage.InMemory.Tests/InMemoryWebhookDeliveryStoreTests</c> runs the
/// same scenarios against the InMemory store.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresWebhookDeliveryStoreClaimTests : IClassFixture<MigrationsFixture>, IAsyncLifetime
{
    private const string TenantId = "tenant-webhook-claims";
    private const string SubscriptionId = "sub-webhook-claims";

    /// <summary>Whole seconds, so every instant round-trips through <c>timestamptz</c> unchanged.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Only bounds a regression to a wait; a passing run never comes near it.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly MigrationsFixture _fixture;
    private readonly PostgresWebhookDeliveryStore _store;

    public PostgresWebhookDeliveryStoreClaimTests(MigrationsFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresWebhookDeliveryStore(fixture.DataSource);
    }

    public async Task InitializeAsync()
    {
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM webhook_deliveries; DELETE FROM webhook_subscriptions;";
            await cmd.ExecuteNonQueryAsync();
        }

        // webhook_deliveries.subscription_id references webhook_subscriptions.
        await new PostgresWebhookSubscriptionStore(_fixture.DataSource).SaveAsync(
            new WebhookSubscription(
                SubscriptionId: SubscriptionId,
                TenantId: TenantId,
                Name: "Delivery claims receiver",
                EndpointUrl: "https://receiver.example.test/webhooks",
                Secret: "whsec-delivery-claims-test",
                EventTypes: [WebhookEventTypes.ConversationMessage],
                IsActive: true,
                CreatedAt: T0,
                UpdatedAt: T0),
            CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // `{}` is the one JSON text that jsonb returns byte-for-byte, so a stored row compares equal to its record.
    private static WebhookDelivery Delivery(
        DateTimeOffset? nextRetryAt,
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending,
        int attempts = 0) => new(
        DeliveryId: Guid.NewGuid().ToString("N"),
        TenantId: TenantId,
        SubscriptionId: SubscriptionId,
        EventType: WebhookEventTypes.ConversationMessage,
        Payload: "{}",
        Status: status,
        Attempts: attempts,
        MaxAttempts: 8,
        NextRetryAt: nextRetryAt,
        LastResponseCode: null,
        LastError: null,
        CreatedAt: T0.AddHours(-1),
        DeliveredAt: status == WebhookDeliveryStatus.Delivered ? T0.AddMinutes(-30) : null);

    private async Task<WebhookDelivery> SavedAsync(WebhookDelivery delivery)
    {
        await _store.SaveAsync(delivery, CancellationToken.None);
        return delivery;
    }

    /// <summary>The two claim columns of one row.</summary>
    private sealed record ClaimColumns(string? ClaimId, DateTimeOffset? ExpiresAt);

    private static readonly ClaimColumns NoClaim = new(null, null);

    /// <summary>The two claim columns, read raw: they are not part of <see cref="WebhookDelivery"/>.</summary>
    private async Task<ClaimColumns> ClaimColumnsAsync(string deliveryId)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT claim_id, claim_expires_at FROM webhook_deliveries WHERE delivery_id = @Id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Varchar) { Value = deliveryId });
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"delivery {deliveryId} exists");
        return new ClaimColumns(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero));
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldClaimEachDueDeliveryExactlyOnce_WhenFourClaimersRunConcurrently()
    {
        var ids = new List<string>();
        for (var i = 0; i < 40; i++)
            ids.Add((await SavedAsync(Delivery(nextRetryAt: T0.AddSeconds(-i)))).DeliveryId);

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
        var older = await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-2)));
        var newer = await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-1)));

        var first = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        first!.Delivery.Should().Be(older, "the delivery due the longest is claimed first");
        (await ClaimColumnsAsync(older.DeliveryId)).Should().Be(new ClaimColumns(first.ClaimId, T0 + Lease),
            "the claim is a committed row change, not a lock");

        var second = await _store.ClaimNextDueAsync(T0.AddMinutes(4), T0.AddMinutes(4) + Lease, CancellationToken.None);
        second!.Delivery.Should().Be(newer, "the older delivery's claim is live until T0 + lease");

        (await _store.ClaimNextDueAsync(T0 + Lease - TimeSpan.FromSeconds(1), T0 + 2 * Lease, CancellationToken.None))
            .Should().BeNull("both claims are live");
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldReclaimAndReportReclaimed_WhenTheClaimHasExpired()
    {
        var delivery = await SavedAsync(Delivery(nextRetryAt: T0, attempts: 3));

        var first = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        first!.Reclaimed.Should().BeFalse("nothing held the delivery before");

        // At exactly T0 + lease the claim has expired (claim_expires_at <= now).
        var second = await _store.ClaimNextDueAsync(T0 + Lease, T0 + 2 * Lease, CancellationToken.None);

        second.Should().NotBeNull("an expired claim no longer protects the delivery");
        second!.Delivery.Should().Be(delivery, "a claim changes no delivery field, attempts included");
        second.Reclaimed.Should().BeTrue("it took over a claim that never recorded an outcome");
        second.ClaimId.Should().NotBe(first.ClaimId).And.MatchRegex("^[0-9a-f]{32}$");
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(new ClaimColumns(second.ClaimId, T0 + 2 * Lease));
    }

    [Fact]
    public async Task ClaimNextDueAsync_ShouldIgnoreDelivery_WhenNotPendingOrNotDue()
    {
        var notClaimable = new[]
        {
            await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.Delivered)),
            await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.Failed)),
            await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-1), status: WebhookDeliveryStatus.DeadLetter)),
            await SavedAsync(Delivery(nextRetryAt: T0.AddSeconds(1))),
            await SavedAsync(Delivery(nextRetryAt: null)),
        };

        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None)).Should().BeNull();
        foreach (var delivery in notClaimable)
        {
            (await _store.TryClaimAsync(delivery.DeliveryId, T0, T0 + Lease, CancellationToken.None))
                .Should().BeNull("a delivery that is not Pending, or not due, is not claimable by id either");
        }

        var due = await SavedAsync(Delivery(nextRetryAt: T0));
        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None))!.Delivery.Should().Be(due,
            "a Pending delivery due exactly now is claimable");
    }

    [Fact]
    public async Task TryClaimAsync_ShouldReturnNull_WhenAnotherClaimIsLive()
    {
        var polled = await SavedAsync(Delivery(nextRetryAt: T0.AddMinutes(-1)));
        var free = await SavedAsync(Delivery(nextRetryAt: T0));
        var pollClaim = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        pollClaim!.Delivery.DeliveryId.Should().Be(polled.DeliveryId);

        (await _store.TryClaimAsync(polled.DeliveryId, T0.AddMinutes(1), T0.AddMinutes(1) + Lease, CancellationToken.None))
            .Should().BeNull("the poll's claim is live");
        (await ClaimColumnsAsync(polled.DeliveryId)).Should().Be(new ClaimColumns(pollClaim.ClaimId, T0 + Lease),
            "a refused claim writes nothing");

        var byId = await _store.TryClaimAsync(free.DeliveryId, T0.AddMinutes(1), T0.AddMinutes(1) + Lease, CancellationToken.None);
        byId.Should().NotBeNull("an unclaimed due delivery is claimable by id");
        byId!.Delivery.Should().Be(free);
        byId.Reclaimed.Should().BeFalse();
    }

    [Fact]
    public async Task TryClaimAsync_ShouldSkipWithoutWaiting_WhenAnotherTransactionHoldsTheRowLock()
    {
        var delivery = await SavedAsync(Delivery(nextRetryAt: T0));

        // What a concurrent claim statement holds for its instant: the row lock, nothing committed yet.
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand(
            "SELECT 1 FROM webhook_deliveries WHERE delivery_id = @Id FOR UPDATE", conn, tx))
        {
            lockRow.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Varchar) { Value = delivery.DeliveryId });
            await lockRow.ExecuteScalarAsync();
        }

        using var cts = new CancellationTokenSource(Guard);
        (await _store.TryClaimAsync(delivery.DeliveryId, T0, T0 + Lease, cts.Token).WaitAsync(Guard))
            .Should().BeNull("SKIP LOCKED: the claim by id skips a row being claimed instead of queueing behind it (design D2)");
        (await _store.ClaimNextDueAsync(T0, T0 + Lease, cts.Token).WaitAsync(Guard))
            .Should().BeNull("the poll's claim skips it too");

        await tx.CommitAsync();

        (await _store.TryClaimAsync(delivery.DeliveryId, T0, T0 + Lease, CancellationToken.None))
            .Should().NotBeNull("the lock is gone and the row was never claimed: it is still due");
    }

    [Fact]
    public async Task CompleteAttemptAsync_ShouldReturnFalseAndLeaveRowUnchanged_WhenClaimIdIsStale()
    {
        var delivery = await SavedAsync(Delivery(nextRetryAt: T0));
        var stale = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        var current = await _store.ClaimNextDueAsync(T0 + Lease, T0 + 2 * Lease, CancellationToken.None);
        current!.Reclaimed.Should().BeTrue();
        var rowBefore = await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None);
        var claimBefore = await ClaimColumnsAsync(delivery.DeliveryId);

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
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(claimBefore, "the current claim is untouched");
        (await _store.ReleaseClaimAsync(delivery.DeliveryId, stale.ClaimId, T0.AddHours(1), CancellationToken.None))
            .Should().BeFalse("a stale claim cannot release either");
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(claimBefore);

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
        var delivery = await SavedAsync(Delivery(nextRetryAt: T0));

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
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(NoClaim,
            "recording the outcome clears both claim columns");

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
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(NoClaim);
        (await _store.CompleteAttemptAsync(delivered, secondAttempt.ClaimId, CancellationToken.None))
            .Should().BeFalse("the claim was cleared by the outcome it recorded");
    }

    [Fact]
    public async Task ReleaseClaimAsync_ShouldClearClaimAndKeepAttempts_WhenClaimIdIsCurrent()
    {
        var delivery = await SavedAsync(Delivery(nextRetryAt: T0, attempts: 2));
        var claim = await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None);
        var probeAt = T0.AddMinutes(10);

        (await _store.ReleaseClaimAsync(delivery.DeliveryId, claim!.ClaimId, probeAt, CancellationToken.None))
            .Should().BeTrue();

        (await _store.GetByIdAsync(delivery.DeliveryId, CancellationToken.None))
            .Should().Be(delivery with { NextRetryAt = probeAt }, "a release changes next_retry_at only: attempts stays 2");
        (await ClaimColumnsAsync(delivery.DeliveryId)).Should().Be(NoClaim);
        (await _store.ClaimNextDueAsync(T0, T0 + Lease, CancellationToken.None))
            .Should().BeNull("a released delivery is not due again before its new next_retry_at");
        (await _store.ReleaseClaimAsync(delivery.DeliveryId, claim.ClaimId, probeAt, CancellationToken.None))
            .Should().BeFalse("the claim is gone");

        var atProbe = await _store.ClaimNextDueAsync(probeAt, probeAt + Lease, CancellationToken.None);
        atProbe.Should().NotBeNull();
        atProbe!.Reclaimed.Should().BeFalse("the claim was released, not taken over");
        atProbe.Delivery.Attempts.Should().Be(2);
    }
}
