using System.Net;
using FluentAssertions.Execution;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Webhooks;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Api.Tests.Services;

public class WebhookDeliveryServiceTests
{
    private static readonly string[] EventTypes = ["event.fired"];
    private const string ClaimId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 300)]
    [InlineData(3, 1800)]
    [InlineData(4, 7200)]
    [InlineData(5, 18000)]
    [InlineData(6, 28800)]
    [InlineData(7, 28800)]
    [InlineData(100, 28800)] // Beyond array bounds clamps to last
    public void GetBackoffSeconds_ShouldReturnExpectedDelay(int attempt, int expectedSeconds)
    {
        WebhookDeliveryService.GetBackoffSeconds(attempt).Should().Be(expectedSeconds);
    }

    [Fact]
    public void BackoffSchedule_ShouldTotalApproximately24Hours()
    {
        var totalSeconds = 0;
        for (int i = 0; i < 8; i++)
            totalSeconds += WebhookDeliveryService.GetBackoffSeconds(i);

        // Total should be ~84660 seconds (~23.5 hours)
        totalSeconds.Should().BeInRange(80000, 90000);
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        // Keyed singleton registered in Program.cs depends on this exact string.
        WebhookDeliveryService.ResiliencePolicyKey.Should().Be("webhook.delivery");
    }

    [Fact]
    public void ClaimLease_ShouldExceedWorstCaseAttempt()
    {
        // webhook-delivery-runs-once, design D4: a claim must outlast the longest possible attempt, or a live
        // attempt is taken over and its delivery POSTed twice. Update this with the constants it reads.
        //
        // Transport: the keyed "webhook.delivery" policy in Program.cs runs up to 3 tries, each bounded by
        // HttpTimeout (and by the policy's own 10-s timeout), with a 0.5-s then 1-s backoff under +-20 % jitter
        // (1.8 s at most, rounded up to 2 s).
        const int transportTries = 3;
        var transportBackoff = TimeSpan.FromSeconds(2);

        // Database: every round trip from the claim statement (the lease is counted from the instant read just
        // before it) to the fenced outcome, each up to Npgsql's default 30-s command timeout. The longest path is
        // a send that throws: claim, subscription read, Open->HalfOpen circuit write, subscription re-read,
        // circuit write, fenced outcome. That is 6, stricter than design D4's count of 4.
        const int databaseRoundTrips = 6;
        var commandTimeout = TimeSpan.FromSeconds(30);

        var worstCaseAttempt = (transportTries * WebhookDeliveryService.HttpTimeout)
            + transportBackoff
            + (databaseRoundTrips * commandTimeout);

        WebhookDeliveryService.ClaimLease.Should().BeGreaterThan(worstCaseAttempt);
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldInvokeHttpSendOnce_WhenDeliverySucceeds()
    {
        // Happy path: subscription exists, circuit closed, HTTP returns 200.
        // The wrapped ResiliencePolicy must invoke the inner send exactly once.
        var sut = BuildSut(
            responseStatus: HttpStatusCode.OK,
            failFirstAttempt: false);

        await sut.Service.DeliverForTestAsync(sut.Delivery, CancellationToken.None);

        sut.Handler.Attempts.Should().Be(1);
        // The immediate path claims the dequeued delivery by id, for one lease from now (design D3, D4).
        await sut.DeliveryStore.Received(1).TryClaimAsync(
            sut.Delivery.DeliveryId, sut.Now, sut.Now + WebhookDeliveryService.ClaimLease, Arg.Any<CancellationToken>());
        // The outcome is recorded under that claim, never through an unfenced UpdateAsync (design D5).
        await sut.DeliveryStore.Received(1).CompleteAttemptAsync(
            Arg.Is<WebhookDelivery>(d => d != null && d.Status == WebhookDeliveryStatus.Delivered && d.Attempts == 1),
            ClaimId,
            Arg.Any<CancellationToken>());
        await sut.DeliveryStore.DidNotReceive().UpdateAsync(Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldRetryAndSucceed_WhenFirstHttpAttemptThrowsTransientError()
    {
        // First HTTP call throws HttpRequestException (transient blip).
        // Policy is configured with retry=2/1ms, so second attempt succeeds with 200.
        var sut = BuildSut(
            responseStatus: HttpStatusCode.OK,
            failFirstAttempt: true);

        await sut.Service.DeliverForTestAsync(sut.Delivery, CancellationToken.None);

        sut.Handler.Attempts.Should().Be(2, "first attempt threw, policy retried and succeeded");
        await sut.DeliveryStore.Received(1).CompleteAttemptAsync(
            Arg.Is<WebhookDelivery>(d => d != null && d.Status == WebhookDeliveryStatus.Delivered && d.Attempts == 1),
            ClaimId,
            Arg.Any<CancellationToken>());
        await sut.DeliveryStore.DidNotReceive().UpdateAsync(Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverForTestAsync_ShouldLeaveTheDeliveryToThePoll_WhenTheClaimThrows()
    {
        // A failing claim on the immediate path must neither send nor take the worker down: the row is saved and
        // still due, so the retry poll sends it.
        var sut = BuildSut(responseStatus: HttpStatusCode.OK, failFirstAttempt: false);
        sut.DeliveryStore.TryClaimAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("claim failure"));

        var act = async () => await sut.Service.DeliverForTestAsync(sut.Delivery, CancellationToken.None);

        await act.Should().NotThrowAsync();
        sut.Handler.Attempts.Should().Be(0, "nothing is sent without a claim");
        await sut.DeliveryStore.DidNotReceive().CompleteAttemptAsync(
            Arg.Any<WebhookDelivery>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sut.DeliveryStore.DidNotReceive().UpdateAsync(Arg.Any<WebhookDelivery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverAsync_ShouldDeadLetterWithoutIncrementingAttempts_WhenSubscriptionDeleted()
    {
        // On Postgres, deleting a subscription deletes its deliveries (ON DELETE CASCADE), so a delivery that
        // outlives its subscription exists on the InMemory stores, which are separate.
        var ct = CancellationToken.None;
        var deliveryStore = new InMemoryWebhookDeliveryStore();
        var subscriptionStore = new InMemoryWebhookSubscriptionStore();
        using var harness = new WebhookDeliveryHarness(deliveryStore, subscriptionStore, holdFirstRequest: false);
        var service = harness.CreateService();
        var now = harness.Clock.UtcNow;

        await subscriptionStore.SaveAsync(WebhookDeliveryPostgresFixture.Subscription, ct);
        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: now.AddMinutes(-1), attempts: 2);
        await deliveryStore.SaveAsync(delivery, ct);
        await subscriptionStore.DeleteAsync(WebhookDeliveryPostgresFixture.SubscriptionId, ct);

        await service.ProcessPendingRetriesOnceAsync(ct);

        var row = await deliveryStore.GetByIdAsync(delivery.DeliveryId, ct);
        // The claim is not on the record. Re-enqueued as the dead-letter retry endpoint does it, the delivery must be
        // claimable at once, as a fresh claim and not a takeover: then the outcome cleared the claim.
        await deliveryStore.UpdateAsync(row! with { Status = WebhookDeliveryStatus.Pending, NextRetryAt = now }, ct);
        var claimAfterRequeue = await deliveryStore.TryClaimAsync(
            delivery.DeliveryId, now, now + WebhookDeliveryService.ClaimLease, ct);

        using (new AssertionScope())
        {
            harness.Receiver.Received.Should().BeEmpty("nothing is sent for a deleted subscription");
            (row?.Status).Should().Be(WebhookDeliveryStatus.DeadLetter);
            (row?.Attempts).Should().Be(2, "no POST was sent, so attempts is unchanged (design D5)");
            (row?.LastError).Should().Be("Subscription deleted");
            claimAfterRequeue.Should().NotBeNull("recording the outcome cleared the claim");
            (claimAfterRequeue?.Reclaimed).Should().BeFalse("no claim was left behind to take over");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldClaimAtMostOneHundredDeliveries_WhenMoreAreDue()
    {
        // One tick claims and sends at most 100 deliveries, one at a time (design D3); the rest wait for the next.
        var ct = CancellationToken.None;
        var deliveryStore = new InMemoryWebhookDeliveryStore();
        var subscriptionStore = new InMemoryWebhookSubscriptionStore();
        using var harness = new WebhookDeliveryHarness(deliveryStore, subscriptionStore, holdFirstRequest: false);
        var service = harness.CreateService();
        await subscriptionStore.SaveAsync(WebhookDeliveryPostgresFixture.Subscription, ct);

        var ids = new List<string>();
        for (var i = 0; i < 101; i++)
        {
            var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: harness.Clock.UtcNow.AddMinutes(-1));
            await deliveryStore.SaveAsync(delivery, ct);
            ids.Add(delivery.DeliveryId);
        }

        await service.ProcessPendingRetriesOnceAsync(ct);
        var afterFirstTick = harness.Receiver.Received.Count;
        var statusesAfterFirstTick = new List<WebhookDeliveryStatus?>();
        foreach (var id in ids)
            statusesAfterFirstTick.Add((await deliveryStore.GetByIdAsync(id, ct))?.Status);
        var pendingAfterFirstTick = statusesAfterFirstTick.Count(s => s == WebhookDeliveryStatus.Pending);

        await service.ProcessPendingRetriesOnceAsync(ct);

        using (new AssertionScope())
        {
            afterFirstTick.Should().Be(100);
            pendingAfterFirstTick.Should().Be(1);
            harness.Receiver.Received.Select(r => r.WebhookId).Should().BeEquivalentTo(ids, "the next tick sends the rest");
            harness.ErrorLogs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldClaimWhatWasDueAtTickStartUnderAFreshLease_WhenAnAttemptTakesTime()
    {
        // A tick claims only deliveries due when it started, so a delivery it released or rescheduled is never
        // claimed twice in one tick however long the tick runs (spec: a released delivery SHALL NOT be claimed
        // again in the same poll); each lease still starts at its own claim, so a late claim in a long tick gets a
        // full lease.
        var ct = CancellationToken.None;
        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();
        var subscriptionStore = new InMemoryWebhookSubscriptionStore();
        var postDuration = TimeSpan.FromMinutes(1);
        WebhookDeliveryHarness? clockOwner = null;
        using var harness = new WebhookDeliveryHarness(
            deliveryStore,
            subscriptionStore,
            holdFirstRequest: false,
            answer: _ =>
            {
                clockOwner!.Clock.Advance(postDuration);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        clockOwner = harness;
        await subscriptionStore.SaveAsync(WebhookDeliveryPostgresFixture.Subscription, ct);

        var tickStart = harness.Clock.UtcNow;
        var first = WebhookDeliveryPostgresFixture.PendingDelivery(nextRetryAt: tickStart.AddMinutes(-1));
        deliveryStore.ClaimNextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new WebhookDeliveryClaim(first, ClaimId, Reclaimed: false), default(WebhookDeliveryClaim?));
        deliveryStore.CompleteAttemptAsync(Arg.Any<WebhookDelivery>(), ClaimId, Arg.Any<CancellationToken>())
            .Returns(true);

        await harness.CreateService().ProcessPendingRetriesOnceAsync(ct);

        harness.Receiver.Received.Should().ContainSingle();
        // The first claim, before the POST; the second, after it moved the clock on by postDuration.
        await deliveryStore.Received(1).ClaimNextDueAsync(
            tickStart, tickStart + WebhookDeliveryService.ClaimLease, Arg.Any<CancellationToken>());
        await deliveryStore.Received(1).ClaimNextDueAsync(
            tickStart, tickStart + postDuration + WebhookDeliveryService.ClaimLease, Arg.Any<CancellationToken>());
        await deliveryStore.Received(2).ClaimNextDueAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record Sut(
        WebhookDeliveryService Service,
        CountingHttpHandler Handler,
        IWebhookDeliveryStore DeliveryStore,
        WebhookDelivery Delivery,
        DateTimeOffset Now);

    private static Sut BuildSut(HttpStatusCode responseStatus, bool failFirstAttempt)
    {
        var handler = new CountingHttpHandler(responseStatus, failFirstAttempt ? 1 : 0);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var sub = new WebhookSubscription(
            SubscriptionId: "sub-1",
            TenantId: "tenant-1",
            Name: "Test",
            EndpointUrl: "https://example.com/hook",
            Secret: "secret",
            EventTypes: EventTypes,
            IsActive: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var subStore = Substitute.For<IWebhookSubscriptionStore>();
        subStore.GetByIdAsync("sub-1", Arg.Any<CancellationToken>()).Returns(sub);

        var now = DateTimeOffset.UtcNow;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);

        var delivery = new WebhookDelivery(
            DeliveryId: "delivery-1",
            TenantId: "tenant-1",
            SubscriptionId: "sub-1",
            EventType: "event.fired",
            Payload: """{"hello":"world"}""",
            Status: WebhookDeliveryStatus.Pending,
            Attempts: 0,
            MaxAttempts: 8,
            NextRetryAt: now,
            LastResponseCode: null,
            LastError: null,
            CreatedAt: now,
            DeliveredAt: null);

        // The store hands out one claim on the delivery and accepts the outcome recorded under it.
        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();
        deliveryStore.TryClaimAsync(
                "delivery-1", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new WebhookDeliveryClaim(delivery, ClaimId, Reclaimed: false));
        deliveryStore.CompleteAttemptAsync(Arg.Any<WebhookDelivery>(), ClaimId, Arg.Any<CancellationToken>())
            .Returns(true);

        var circuitOptions = Options.Create(new CircuitBreakerOptions
        {
            FailureThreshold = 5,
            CooldownSeconds = 30,
            MaxCooldownSeconds = 300,
            CooldownMultiplier = 2.0,
        });
        var circuitBreaker = new CircuitBreakerPolicy(circuitOptions);

        // DeliverForTestAsync bypasses the channel/dispatcher loop; we only need a non-null
        // dispatcher so the constructor is satisfied.
        var eventBus = new PlatformEventBus();
        var dispatcher = new WebhookDispatcher(
            eventBus,
            subStore,
            deliveryStore,
            clock,
            NullLogger<WebhookDispatcher>.Instance);

        // Real ResiliencePolicy — retry once with negligible backoff so tests stay fast.
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();

        var service = new WebhookDeliveryService(
            dispatcher,
            deliveryStore,
            subStore,
            httpClientFactory,
            circuitBreaker,
            clock,
            NullLogger<WebhookDeliveryService>.Instance,
            policy);

        return new Sut(service, handler, deliveryStore, delivery, now);
    }

    /// <summary>
    /// HTTP handler that optionally throws on the first N requests (simulating a transient
    /// failure) and then returns <paramref name="responseStatus"/> for subsequent requests.
    /// </summary>
    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _responseStatus;
        private readonly int _failFirstCount;
        private int _attempts;

        public CountingHttpHandler(HttpStatusCode responseStatus, int failFirstCount)
        {
            _responseStatus = responseStatus;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failFirstCount)
                throw new HttpRequestException($"transient failure (attempt {attempt})");

            return Task.FromResult(new HttpResponseMessage(_responseStatus));
        }
    }
}
