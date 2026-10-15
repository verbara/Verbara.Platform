using System.Net;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// webhook-delivery-runs-once, task 3.2 (design D2-D8; Platform/ADR-0043; spec <c>outbound-webhook-delivery</c>):
/// how <see cref="WebhookDeliveryService"/> uses the delivery claims on real Postgres. Lease recovery, the fenced
/// outcome, the circuit-open release, the immediate path's <c>SKIP LOCKED</c> trade-off, shutdown, and the identity
/// (<c>X-Webhook-Id</c> and body) every request of one delivery carries. No wall-clock waits: time moves through the
/// harness clock, a held POST is a <see cref="TaskCompletionSource"/> gate, and every <c>WaitAsync</c> is a guard
/// timeout, never a fence.
/// </summary>
[Collection(WebhookDeliveryPostgresSuite.Name)]
public sealed class WebhookDeliveryClaimTests(WebhookDeliveryPostgresFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Epoch = WebhookDeliveryPostgresFixture.Epoch;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldDeliverOnce_WhenAClaimExpiredAfterItsInstanceStopped()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture, holdFirstRequest: false);
        var service = harness.CreateService();

        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch.AddMinutes(-1));
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);

        // An instance claims the delivery and stops before it sends anything or records an outcome.
        var orphanedClaim = await fixture.DeliveryStore.ClaimNextDueAsync(
            Epoch, Epoch + WebhookDeliveryService.ClaimLease, guard.Token);
        orphanedClaim.Should().NotBeNull("the delivery is due and unclaimed");

        // While its lease runs, no poll touches the delivery.
        await service.ProcessPendingRetriesOnceAsync(guard.Token);
        harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(0, "a live claim is respected by every poll");

        // The lease ends at Epoch + ClaimLease; the next poll takes the delivery over and sends it.
        harness.Clock.Advance(WebhookDeliveryService.ClaimLease);
        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        var claim = await fixture.ClaimColumnsAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(1);
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(1, "the interrupted attempt sent nothing and does not count (design D5)");
            (row?.DeliveredAt).Should().Be(Epoch + WebhookDeliveryService.ClaimLease);
            claim.ClaimId.Should().BeNull("recording the outcome clears the claim");
            claim.ExpiresAt.Should().BeNull();
            harness.Logs.Records.Should().ContainSingle(
                r => r.Level == LogLevel.Warning && r.Contains(delivery.DeliveryId)
                    && r.Message.Contains("reclaimed", StringComparison.Ordinal),
                "a takeover of an expired claim is logged");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeliverAsync_ShouldDiscardOutcome_WhenAnotherAttemptTookOverTheClaim()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        // The first POST (attempt A) is held open and then answers 500; every later one answers 200.
        using var harness = new WebhookDeliveryHarness(
            fixture,
            holdFirstRequest: true,
            answer: n => new HttpResponseMessage(n == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        var instanceA = harness.CreateReplica();
        var instanceB = harness.CreateReplica();

        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch.AddMinutes(-1));
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);

        // Attempt A: instance A's poll claims the delivery and POSTs; the receiver holds that POST open.
        var pollA = instanceA.ProcessPendingRetriesOnceAsync(guard.Token);
        await harness.Receiver.FirstRequestArrived.WaitAsync(guard.Token);

        // A's lease runs out with its POST still open. Attempt B: instance B's poll takes the delivery over, sends
        // it and records Delivered.
        harness.Clock.Advance(WebhookDeliveryService.ClaimLease);
        await instanceB.ProcessPendingRetriesOnceAsync(guard.Token);
        var deliveredByB = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);

        // A's POST finally fails, and A tries to record a scheduled retry under a claim that is no longer current.
        harness.Receiver.ReleaseFirstRequest();
        await pollA.WaitAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        var claim = await fixture.ClaimColumnsAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(2,
                "A's POST and B's: once a lease expires mid-attempt, delivery is at-least-once");
            (deliveredByB?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            row.Should().Be(deliveredByB, "A's late failure is discarded and changes nothing");
            (row?.Attempts).Should().Be(1, "B's attempt is the only recorded one");
            (row?.NextRetryAt).Should().BeNull("a delivered webhook is never scheduled again");
            (row?.LastResponseCode).Should().Be(200);
            claim.ClaimId.Should().BeNull();
            harness.Logs.Records.Should().ContainSingle(
                r => r.Level == LogLevel.Warning && r.Contains(delivery.DeliveryId)
                    && r.Message.Contains("Discarded", StringComparison.Ordinal),
                "A's discarded outcome is logged");
            harness.Logs.Records.Should().NotContain(
                r => r.Contains(delivery.DeliveryId) && r.Message.Contains("scheduled for", StringComparison.Ordinal),
                "A's retry was never scheduled");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldReleaseWithoutAttemptAndDeferToProbe_WhenCircuitIsOpen()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture, holdFirstRequest: false);
        var service = harness.CreateService();

        var probeAt = Epoch.AddMinutes(10);
        await fixture.SubscriptionStore.SaveAsync(
            WebhookDeliveryPostgresFixture.Subscription with
            {
                CircuitStatus = CircuitStatus.Open,
                CircuitFailures = 5,
                CircuitOpenedAt = Epoch.AddMinutes(-1),
                CircuitNextProbeAt = probeAt,
            },
            guard.Token);
        const string closedSubscriptionId = "sub-webhook-claims-closed";
        await fixture.SubscriptionStore.SaveAsync(
            WebhookDeliveryPostgresFixture.Subscription with { SubscriptionId = closedSubscriptionId }, guard.Token);

        // The blocked delivery has been due the longest, so the tick's first claim takes it.
        var blocked = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch.AddMinutes(-2), attempts: 2);
        var other = WebhookDeliveryPostgresFixture.PendingDelivery(
            nextRetryAt: Epoch.AddMinutes(-1), subscriptionId: closedSubscriptionId);
        await fixture.DeliveryStore.SaveAsync(blocked, guard.Token);
        await fixture.DeliveryStore.SaveAsync(other, guard.Token);

        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        var blockedRow = await fixture.DeliveryStore.GetByIdAsync(blocked.DeliveryId, guard.Token);
        var blockedClaim = await fixture.ClaimColumnsAsync(blocked.DeliveryId, guard.Token);
        var otherRow = await fixture.DeliveryStore.GetByIdAsync(other.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(blocked.DeliveryId).Should().Be(0, "the circuit is open");
            (blockedRow?.Status).Should().Be(WebhookDeliveryStatus.Pending);
            (blockedRow?.Attempts).Should().Be(2, "a circuit-open release is not an attempt (design D6)");
            (blockedRow?.NextRetryAt).Should().Be(probeAt, "the delivery is next due at the circuit's probe time");
            (blockedRow?.LastError).Should().BeNull();
            (blockedRow?.LastResponseCode).Should().BeNull();
            blockedClaim.ClaimId.Should().BeNull("the release clears the claim");
            blockedClaim.ExpiresAt.Should().BeNull();
            harness.Receiver.PostsFor(other.DeliveryId).Should().Be(1,
                "the same tick goes on to the next due delivery instead of claiming the released one again");
            (otherRow?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldDeferToTheNextPoll_WhenOpenCircuitHasNoProbeTime()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture, holdFirstRequest: false);
        var service = harness.CreateService();

        await fixture.SubscriptionStore.SaveAsync(
            WebhookDeliveryPostgresFixture.Subscription with
            {
                CircuitStatus = CircuitStatus.Open,
                CircuitFailures = 5,
                CircuitOpenedAt = Epoch.AddMinutes(-1),
                CircuitNextProbeAt = null,
            },
            guard.Token);
        var blocked = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch.AddMinutes(-1), attempts: 1);
        await fixture.DeliveryStore.SaveAsync(blocked, guard.Token);

        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(blocked.DeliveryId, guard.Token);
        var claim = await fixture.ClaimColumnsAsync(blocked.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(blocked.DeliveryId).Should().Be(0);
            (row?.Status).Should().Be(WebhookDeliveryStatus.Pending);
            (row?.Attempts).Should().Be(1, "a circuit-open release is not an attempt");
            (row?.NextRetryAt).Should().Be(Epoch + WebhookDeliveryService.RetryPollInterval,
                "with no probe time, the delivery is next due at the next poll");
            claim.ClaimId.Should().BeNull();
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldSkipAndPollShouldPostOnce_WhenThePollHoldsTheRowLock()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture, holdFirstRequest: false);
        var service = harness.CreateService();

        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch);
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);

        // A separate transaction holds the row lock, as a poll's claim statement does for its instant. The held
        // transaction is the fence: no timing.
        await using (var conn = await fixture.DataSource.OpenConnectionAsync(guard.Token))
        await using (var tx = await conn.BeginTransactionAsync(guard.Token))
        {
            await using (var lockRow = new NpgsqlCommand(
                "SELECT 1 FROM webhook_deliveries WHERE delivery_id = @Id FOR UPDATE", conn, tx))
            {
                lockRow.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Varchar) { Value = delivery.DeliveryId });
                await lockRow.ExecuteScalarAsync(guard.Token);
            }

            // The immediate path for the dequeued item skips the locked row instead of waiting for it (design D2).
            await service.DeliverForTestAsync(delivery, guard.Token).WaitAsync(guard.Token);
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(0,
                "the immediate path cannot claim a row another claim statement holds, so it sends nothing");

            await tx.CommitAsync(guard.Token);
        }

        // The lock holder never claimed it, so the row is still due: the next poll sends it.
        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(1, "skipped is late, never lost");
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(1);
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldNotSendAgain_WhenThePollAlreadyDeliveredTheQueuedItem()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture, holdFirstRequest: false);
        var service = harness.CreateService();

        harness.EventBus.Publish(new ConversationMessageEvent(
            WebhookDeliveryPostgresFixture.TenantId, "conv-queued", "msg-queued", "customer", "hello"));
        await harness.Dispatcher.WaitForDispatchAsync(guard.Token);
        harness.Dispatcher.DeliveryReader.TryRead(out var item).Should().BeTrue("the dispatcher enqueues the delivery it saved");

        // The poll gets there first and delivers it while the item still waits in the channel.
        await service.ProcessPendingRetriesOnceAsync(guard.Token);
        var delivered = await fixture.DeliveryStore.GetByIdAsync(item!.DeliveryId, guard.Token);

        // The immediate path reaches the item later.
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await service.DeliverForTestAsync(item, guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(item.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(item.DeliveryId).Should().Be(1, "a finished delivery is not claimable");
            (delivered?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            row.Should().Be(delivered, "status, attempts and delivered_at are unchanged");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldLeaveTheClaim_WhenTheHostStopsMidAttempt()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var harness = new WebhookDeliveryHarness(fixture);
        var service = harness.CreateService();

        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch.AddMinutes(-1));
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);
        var stored = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);

        var poll = service.ProcessPendingRetriesOnceAsync(stopping.Token);
        await harness.Receiver.FirstRequestArrived.WaitAsync(guard.Token);

        // The host stops while the POST is open.
        await stopping.CancelAsync();
        await poll.WaitAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        var claim = await fixture.ClaimColumnsAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(1);
            row.Should().Be(stored, "a stopped attempt records no outcome");
            claim.ClaimId.Should().NotBeNull("shutdown leaves the claim, and the lease recovers the delivery (design D7)");
            claim.ExpiresAt.Should().Be(Epoch + WebhookDeliveryService.ClaimLease);
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendSameIdAndBody_WhenTransportRetriesWithinOneAttempt()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        // The receiver processes the first request, but its response never reaches the sender.
        using var harness = new WebhookDeliveryHarness(
            fixture,
            holdFirstRequest: false,
            answer: n => n == 1
                ? throw new HttpRequestException("the receiver processed the request; its response never arrived")
                : new HttpResponseMessage(HttpStatusCode.OK));
        // One attempt's transport policy: 2 tries, and the Sdk policy retries any exception.
        var service = harness.CreateService(new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build());

        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: Epoch);
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);
        // jsonb keeps no text: the stored payload is what every request must carry.
        var stored = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);

        await service.DeliverForTestAsync(delivery, guard.Token);

        var received = harness.Receiver.Received;
        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            received.Should().HaveCount(2, "the transport retried once within the attempt");
            received.Should().OnlyContain(r => r.WebhookId == delivery.DeliveryId);
            received.Should().OnlyContain(r => r.Body == stored!.Payload, "both tries send the stored body");
            received.Select(r => (r.Timestamp, r.Signature)).Distinct().Should().ContainSingle(
                "the timestamp and the signature are computed once per attempt, before the transport policy runs");
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(1, "two transport tries are one attempt");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendSameIdAndBody_WhenScheduledRetryFollowsImmediateAttempt()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(
            fixture,
            holdFirstRequest: false,
            answer: n => new HttpResponseMessage(n <= 2 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        var service = harness.CreateService();

        // The real dispatcher saves the row and enqueues its own copy of the delivery.
        harness.EventBus.Publish(new ConversationMessageEvent(
            WebhookDeliveryPostgresFixture.TenantId, "conv-identity", "msg-identity", "customer", "hello"));
        await harness.Dispatcher.WaitForDispatchAsync(guard.Token);
        harness.Dispatcher.DeliveryReader.TryRead(out var item).Should().BeTrue("the dispatcher enqueues the delivery it saved");

        // Attempt 1, on the immediate path: HTTP 500, and a retry is scheduled.
        await service.DeliverForTestAsync(item!, guard.Token);

        // Attempt 2, from the poll once the retry is due: HTTP 500 again.
        harness.Clock.Advance(TimeSpan.FromSeconds(WebhookDeliveryService.GetBackoffSeconds(1)));
        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        // Attempt 3, from the poll: HTTP 200.
        harness.Clock.Advance(TimeSpan.FromSeconds(WebhookDeliveryService.GetBackoffSeconds(2)));
        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        var received = harness.Receiver.Received;
        var row = await fixture.DeliveryStore.GetByIdAsync(item!.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            received.Should().HaveCount(3);
            received.Should().OnlyContain(r => r.WebhookId == item.DeliveryId);
            received.Select(r => r.Body).Distinct().Should().ContainSingle(
                "the immediate attempt and the scheduled retries send byte-identical bodies");
            received.Should().OnlyContain(
                r => WebhookSignatureService.VerifySignature(
                    r.Timestamp, r.Body, WebhookDeliveryPostgresFixture.Subscription.Secret, r.Signature),
                "each request carries a signature valid for its own timestamp");
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(3, "each POST attempt counts once");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }
}
