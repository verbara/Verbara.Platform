using FluentAssertions.Execution;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// webhook-delivery-runs-once, tasks 1.2 and 1.3 (spec <c>outbound-webhook-delivery</c>, "A delivery MUST be claimed
/// before each POST, with at most one attempt in flight"). Regression tests for F3, written red against the unfixed
/// code: the immediate path and the retry poll of one instance overlapping, and two instances polling the same due
/// delivery. Real Postgres, a receiver that holds its first POST open, no wall-clock waits: every
/// <c>WaitAsync</c> here is a guard timeout, never a fence.
/// </summary>
[Collection(WebhookDeliveryPostgresSuite.Name)]
public sealed class WebhookDeliveryOverlapTests(WebhookDeliveryPostgresFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeliverAndPoll_ShouldPostOncePerDeliveryId_WhenPollRunsWhileFirstPostIsInFlight()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture);
        var service = harness.CreateService();

        // 1. The real dispatcher produces the row (Pending, NextRetryAt = now) and enqueues it.
        harness.EventBus.Publish(new ConversationMessageEvent(
            WebhookDeliveryPostgresFixture.TenantId, "conv-overlap", "msg-overlap", "customer", "hello"));
        await harness.Dispatcher.WaitForDispatchAsync(guard.Token);
        harness.Dispatcher.DeliveryReader.TryRead(out var item).Should().BeTrue("the dispatcher enqueues the delivery it saved");

        // 2. The immediate path, not awaited.
        var immediate = service.DeliverForTestAsync(item!, guard.Token);

        // 3. Its POST is now in flight, held open by the receiver.
        await harness.Receiver.FirstRequestArrived.WaitAsync(guard.Token);

        // 4. One poll tick of the same instance, at the same clock.
        await service.ProcessPendingRetriesOnceAsync(guard.Token);

        // 5. The receiver answers the first POST; the immediate path records its outcome.
        harness.Receiver.ReleaseFirstRequest();
        await immediate.WaitAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(item!.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(item.DeliveryId).Should().Be(1,
                "the poll must not POST a delivery whose first attempt is still in flight");
            row.Should().NotBeNull();
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(1, "one POST was sent, and attempts counts POST attempts");
            harness.ErrorLogs.Should().BeEmpty("neither path may fail silently behind the poll's catch-all");
        }
    }

    [Fact]
    public async Task ProcessPendingRetriesOnceAsync_ShouldPostOncePerDeliveryId_WhenTwoInstancesPollTheSameDueDelivery()
    {
        using var guard = new CancellationTokenSource(GuardTimeout);
        using var harness = new WebhookDeliveryHarness(fixture);

        // 1. Two instances sharing the fixture's database and one gated receiver.
        var instanceA = harness.CreateReplica();
        var instanceB = harness.CreateReplica();

        // 2. One Pending delivery that is due.
        var delivery = WebhookDeliveryPostgresFixture.PendingDelivery(
            nextRetryAt: WebhookDeliveryPostgresFixture.Epoch.AddMinutes(-1));
        await fixture.DeliveryStore.SaveAsync(delivery, guard.Token);

        // 3. Instance A's poll, not awaited; 4. its POST is now in flight, held open by the receiver.
        var pollA = instanceA.ProcessPendingRetriesOnceAsync(guard.Token);
        await harness.Receiver.FirstRequestArrived.WaitAsync(guard.Token);

        // 5. Instance B's poll runs to completion while A's POST is still open.
        await instanceB.ProcessPendingRetriesOnceAsync(guard.Token);

        // 6. The receiver answers A's POST; A records its outcome.
        harness.Receiver.ReleaseFirstRequest();
        await pollA.WaitAsync(guard.Token);

        var row = await fixture.DeliveryStore.GetByIdAsync(delivery.DeliveryId, guard.Token);
        using (new AssertionScope())
        {
            harness.Receiver.PostsFor(delivery.DeliveryId).Should().Be(1,
                "two instances polling together must not both POST the same due delivery");
            row.Should().NotBeNull();
            (row?.Status).Should().Be(WebhookDeliveryStatus.Delivered);
            (row?.Attempts).Should().Be(1, "one POST was sent, and attempts counts POST attempts");
            harness.ErrorLogs.Should().BeEmpty("neither poll may fail silently behind its catch-all");
        }
    }
}
