using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
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
/// recording delivery store, as two Api pods share one database.
/// </summary>
public sealed class CampaignMetricsReplicaTests : IDisposable
{
    private const string MetricsEventType = "campaign.metrics_updated";

    private readonly CampaignStoreBase _store = Substitute.For<CampaignStoreBase>();
    private readonly IWebhookSubscriptionStore _subStore = Substitute.For<IWebhookSubscriptionStore>();
    private readonly IWebhookDeliveryStore _deliveryStore = Substitute.For<IWebhookDeliveryStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<WebhookDelivery> _saved = [];
    private readonly List<Replica> _replicas = [];

    public CampaignMetricsReplicaTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _store.GetAllActiveCampaignMetricsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IReadOnlyList<TenantCampaignMetricsSnapshot>>([Acme11()]));
#pragma warning restore CA2012

        var acmeSub = new WebhookSubscription("sub-acme", "acme", "Acme hook", "https://acme.example/hook",
            "secret-a", [MetricsEventType], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _subStore.GetActiveByEventTypeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<WebhookSubscription> subs =
                    call.ArgAt<string>(0) == "acme" && call.ArgAt<string>(1) == MetricsEventType
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

    private Replica CreateReplica()
    {
        var replica = new Replica(_store, _subStore, _deliveryStore, _clock);
        _replicas.Add(replica);
        return replica;
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldSaveOneDelivery_WhenTwoReplicasPollTheSameChange()
    {
        var a = CreateReplica();
        var b = CreateReplica();

        await a.PollAsync();
        await b.PollAsync();

        _saved.Should().ContainSingle("one change of campaign 11 is one delivery, whatever the replica count")
            .Which.Should().Match<WebhookDelivery>(d =>
                d.SubscriptionId == "sub-acme" && d.TenantId == "acme" && d.EventType == MetricsEventType);
        a.Published.Should().ContainSingle(e => e.CampaignId == 11, "replica A's SSE still gets the event");
        b.Published.Should().ContainSingle(e => e.CampaignId == 11, "replica B's SSE still gets the event");
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
            _poller = new CampaignMetricsPoller(store, _bus, NullLogger<CampaignMetricsPoller>.Instance);
            _dispatcher = new WebhookDispatcher(
                _bus, subStore, deliveryStore, clock, NullLogger<WebhookDispatcher>.Instance);
        }

        /// <summary>The SSE stand-in: every metrics event this replica's bus carried.</summary>
        public List<CampaignMetricsUpdatedEvent> Published { get; } = [];

        public async Task PollAsync()
        {
            await _poller.PollMetricsAsync(CancellationToken.None);
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
