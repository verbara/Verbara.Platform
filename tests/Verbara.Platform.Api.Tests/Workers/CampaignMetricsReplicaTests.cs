using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Workers;

/// <summary>
/// Pins one <c>campaign.metrics_updated</c> webhook delivery per metrics change whatever the
/// replica count (design D5). Two "replicas", A and B, each with its own
/// <see cref="PlatformEventBus"/>, <see cref="CampaignMetricsPoller"/> and
/// <see cref="WebhookDispatcher"/>, share one campaign store, one subscription store and one
/// recording delivery store, as two Api pods share one database. Each replica has its own
/// <see cref="IClusterLeader"/> substitute for <see cref="CampaignMetricsLeaderResources.Webhooks"/>,
/// shared by its poller and its dispatcher as production shares one keyed singleton.
/// </summary>
public sealed class CampaignMetricsReplicaTests : IDisposable
{
    private const string MetricsEventType = "campaign.metrics_updated";
    private const string MessageEventType = "conversation.message";

    private readonly CampaignStoreBase _store = Substitute.For<CampaignStoreBase>();
    private readonly IWebhookSubscriptionStore _subStore = Substitute.For<IWebhookSubscriptionStore>();
    private readonly IWebhookDeliveryStore _deliveryStore = Substitute.For<IWebhookDeliveryStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<WebhookDelivery> _saved = [];
    private readonly List<Replica> _replicas = [];
    private Func<IReadOnlyList<TenantCampaignMetricsSnapshot>> _active = () => [Acme11()];

    public CampaignMetricsReplicaTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _store.GetAllActiveCampaignMetricsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IReadOnlyList<TenantCampaignMetricsSnapshot>>(_active()));
#pragma warning restore CA2012

        var acmeSub = new WebhookSubscription("sub-acme", "acme", "Acme hook", "https://acme.example/hook",
            "secret-a", [MetricsEventType, MessageEventType], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _subStore.GetActiveByEventTypeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<WebhookSubscription> subs =
                    call.ArgAt<string>(0) == "acme" && acmeSub.EventTypes.Contains(call.ArgAt<string>(1))
                        ? [acmeSub]
                        : [];
                return Task.FromResult(subs);
            });

        _deliveryStore.SaveAsync(Arg.Do<WebhookDelivery>(d => { lock (_saved) _saved.Add(d); }),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private static TenantCampaignMetricsSnapshot Acme11() =>
        new("acme", new CampaignMetricsSnapshot(11, "Acme renewals", CampaignStatus.Active,
            ContactsDialed: 5, ContactsRemaining: 95, ConnectRate: 0.4, AbandonRate: 0.01));

    private Replica CreateReplica(bool isLeader)
    {
        var replica = new Replica(_store, _subStore, _deliveryStore, _clock) { IsLeader = isLeader };
        _replicas.Add(replica);
        return replica;
    }

    private int SavedCount
    {
        get
        {
            lock (_saved) return _saved.Count;
        }
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldSaveOneDelivery_WhenTwoReplicasPollTheSameChange()
    {
        var a = CreateReplica(isLeader: true);
        var b = CreateReplica(isLeader: false);

        await a.PollAsync();
        await b.PollAsync();

        _saved.Should().ContainSingle("one change of campaign 11 is one delivery, whatever the replica count")
            .Which.Should().Match<WebhookDelivery>(d =>
                d.SubscriptionId == "sub-acme" && d.TenantId == "acme" && d.EventType == MetricsEventType
                && d.Payload.Contains("\"campaignId\":11"));
        a.Published.Should().ContainSingle(e => e.CampaignId == 11, "replica A's SSE still gets the event");
        b.Published.Should().ContainSingle(e => e.CampaignId == 11, "replica B's SSE still gets the event");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldRepublishOnceFromNewHolder_WhenLeaseMovesWithUnchangedMetrics()
    {
        var a = CreateReplica(isLeader: true);
        var b = CreateReplica(isLeader: false);
        await a.PollAsync();
        await b.PollAsync();
        _saved.Should().ContainSingle();

        a.IsLeader = false;
        b.IsLeader = true;
        await a.PollAsync();
        await b.PollAsync();

        _saved.Should().HaveCount(2, "the new holder sends the current state once on gaining the lease");
        _saved[1].Payload.Should().Contain("\"campaignId\":11").And.Contain("\"contactsDialed\":5");
        a.Published.Should().HaveCount(1, "A's metrics did not change, and A did not gain the lease");
        b.Published.Should().HaveCount(2, "B's SSE sees the repeated event from the reset");

        await a.PollAsync();
        await b.PollAsync();

        _saved.Should().HaveCount(2, "unchanged metrics after the reset are not delivered again");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldDeliverOnce_WhenLeaseIsGainedDuringTheStoreCall()
    {
        var b = CreateReplica(isLeader: false);
        await b.PollAsync();
        SavedCount.Should().Be(0, "a follower dispatches no metrics webhook");

        _active = () =>
        {
            b.IsLeader = true;   // false when the store call is entered, true after it returns
            return [Acme11()];
        };
        await b.PollAsync();

        SavedCount.Should().Be(1, "the lease read after the store call sees the gain and resets the snapshots");

        _active = () => [Acme11()];
        await b.PollAsync();

        SavedCount.Should().Be(1, "the next unchanged poll delivers nothing");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldSaveNoDelivery_WhenNoReplicaHoldsTheLease()
    {
        var a = CreateReplica(isLeader: false);
        var b = CreateReplica(isLeader: false);

        await a.PollAsync();
        await b.PollAsync();

        SavedCount.Should().Be(0);
        a.Published.Should().ContainSingle(e => e.CampaignId == 11, "SSE publication continues without a holder");
        b.Published.Should().ContainSingle(e => e.CampaignId == 11, "SSE publication continues without a holder");
    }

    [Fact]
    public async Task OnEvent_ShouldDispatchOtherEventTypes_WhenReplicaDoesNotHoldTheLease()
    {
        var b = CreateReplica(isLeader: false);

        await b.PublishAsync(new ConversationMessageEvent("acme", "c1", "m1", "user", "hello"));

        _saved.Should().ContainSingle("the lease gates only events published on every replica")
            .Which.EventType.Should().Be(MessageEventType);
    }

    public void Dispose()
    {
        foreach (var replica in _replicas)
        {
            replica.Dispose();
        }
    }

    private sealed class Replica : IDisposable
    {
        private readonly PlatformEventBus _bus = new();
        private readonly IDisposable _recorder;
        private readonly CampaignMetricsPoller _poller;
        private readonly WebhookDispatcher _dispatcher;

        public Replica(
            CampaignStoreBase store,
            IWebhookSubscriptionStore subStore,
            IWebhookDeliveryStore deliveryStore,
            IClock clock)
        {
            _recorder = _bus.Events.Subscribe(evt =>
            {
                if (evt is CampaignMetricsUpdatedEvent metrics)
                {
                    Published.Add(metrics);
                }
            });

            // One substitute per replica, read by both its poller and its dispatcher.
            var leader = Substitute.For<IClusterLeader>();
            leader.Resource.Returns(CampaignMetricsLeaderResources.Webhooks);
            leader.IsLeader.Returns(_ => IsLeader);

            _poller = new CampaignMetricsPoller(store, _bus, NullLogger<CampaignMetricsPoller>.Instance, leader);
            _dispatcher = new WebhookDispatcher(
                _bus, subStore, deliveryStore, clock, NullLogger<WebhookDispatcher>.Instance, leader);
        }

        /// <summary>Whether this replica holds <see cref="CampaignMetricsLeaderResources.Webhooks"/>.</summary>
        public bool IsLeader { get; set; }

        /// <summary>The SSE stand-in: every metrics event this replica's bus carried.</summary>
        public List<CampaignMetricsUpdatedEvent> Published { get; } = [];

        public async Task PollAsync()
        {
            await _poller.PollMetricsAsync(CancellationToken.None);
            await _dispatcher.WaitForDispatchAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }

        public async Task PublishAsync(PlatformEvent evt)
        {
            _bus.Publish(evt);
            await _dispatcher.WaitForDispatchAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
            _recorder.Dispose();
            _bus.Dispose();
        }
    }
}
