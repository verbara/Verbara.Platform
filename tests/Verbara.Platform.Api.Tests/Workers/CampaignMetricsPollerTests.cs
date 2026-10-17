using System.Text.Json;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Push.Delivery;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Workers;

/// <summary>
/// Pins that <see cref="CampaignMetricsPoller"/> publishes metrics for every tenant's active
/// campaigns, each event stamped with its owning tenant. The poller reads through the
/// cross-tenant seam <see cref="CampaignStoreBase.GetAllActiveCampaignMetricsAsync"/>, whose
/// items carry their tenant id; the per-tenant read is never used (design D3).
/// </summary>
public sealed class CampaignMetricsPollerTests : IDisposable
{
    private readonly PlatformEventBus _bus = new();
    private readonly List<CampaignMetricsUpdatedEvent> _published = [];
    private readonly IDisposable _subscription;
    private readonly CampaignStoreBase _store = Substitute.For<CampaignStoreBase>();
    private IReadOnlyList<TenantCampaignMetricsSnapshot> _active = [];

    public CampaignMetricsPollerTests()
    {
        _subscription = _bus.Events.Subscribe(evt =>
        {
            if (evt is CampaignMetricsUpdatedEvent metrics)
            {
                _published.Add(metrics);
            }
        });

#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _store.GetAllActiveCampaignMetricsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IReadOnlyList<TenantCampaignMetricsSnapshot>>(_active));
#pragma warning restore CA2012
    }

    private static TenantCampaignMetricsSnapshot Item(
        string tenantId, long campaignId, string name, int contactsDialed = 5) =>
        new(tenantId, new CampaignMetricsSnapshot(campaignId, name, CampaignStatus.Active,
            ContactsDialed: contactsDialed, ContactsRemaining: 100 - contactsDialed,
            ConnectRate: 0.4, AbandonRate: 0.01));

    private static TenantCampaignMetricsSnapshot Acme11(int contactsDialed = 5) =>
        Item("acme", 11, "Acme renewals", contactsDialed);

    private static TenantCampaignMetricsSnapshot Globex12() => Item("globex", 12, "Globex winback");

    private CampaignMetricsPoller CreateSut() =>
        new(_store, _bus, NullLogger<CampaignMetricsPoller>.Instance);

    [Fact]
    public async Task PollMetricsAsync_ShouldPublishEveryTenantsCampaignMetrics_WhenTwoTenantsHaveActiveCampaigns()
    {
        _active = [Acme11(), Globex12()];
        var sut = CreateSut();

        await sut.PollMetricsAsync(CancellationToken.None);

        _published.Should().HaveCount(2, "one active campaign per tenant must each publish its metrics");
        _published.Select(e => (e.CampaignId, e.TenantId)).Should().BeEquivalentTo(
            [(11L, "acme"), (12L, "globex")]);
        _published.Should().NotContain(e => string.IsNullOrEmpty(e.TenantId),
            "an event without a tenant reaches no tenant's subscribers");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldNotRepublish_WhenMetricsAreUnchanged()
    {
        _active = [Acme11(), Globex12()];
        var sut = CreateSut();
        await sut.PollMetricsAsync(CancellationToken.None);
        _published.Clear();

        await sut.PollMetricsAsync(CancellationToken.None);

        _published.Should().BeEmpty("nothing changed since the previous poll");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldRepublishUnderItsTenant_WhenMetricsChange()
    {
        _active = [Acme11(), Globex12()];
        var sut = CreateSut();
        await sut.PollMetricsAsync(CancellationToken.None);
        _published.Clear();

        _active = [Acme11(contactsDialed: 6), Globex12()];
        await sut.PollMetricsAsync(CancellationToken.None);

        _published.Should().ContainSingle()
            .Which.Should().Match<CampaignMetricsUpdatedEvent>(e =>
                e.CampaignId == 11 && e.TenantId == "acme" && e.ContactsDialed == 6);
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldRepublish_WhenCampaignLeavesTheActiveSetAndReturns()
    {
        _active = [Acme11(), Globex12()];
        var sut = CreateSut();
        await sut.PollMetricsAsync(CancellationToken.None);

        _active = [Globex12()];
        await sut.PollMetricsAsync(CancellationToken.None);
        _published.Clear();

        _active = [Acme11(), Globex12()];
        await sut.PollMetricsAsync(CancellationToken.None);

        _published.Should().ContainSingle(
                "a campaign evicted from the active set is published again, unchanged metrics or not")
            .Which.Should().Match<CampaignMetricsUpdatedEvent>(e => e.CampaignId == 11 && e.TenantId == "acme");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PollMetricsAsync_ShouldSkipItem_WhenItsTenantIdIsEmpty(string tenantId)
    {
        _active = [Item(tenantId, 13, "Orphan"), Acme11()];
        var sut = CreateSut();

        await sut.PollMetricsAsync(CancellationToken.None);

        _published.Should().ContainSingle()
            .Which.Should().Match<CampaignMetricsUpdatedEvent>(e => e.CampaignId == 11 && e.TenantId == "acme");
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldNeverCallPerTenantMetricsRead_WhenPolling()
    {
        _active = [Acme11(), Globex12()];
        var sut = CreateSut();

        await sut.PollMetricsAsync(CancellationToken.None);
        await sut.PollMetricsAsync(CancellationToken.None);

#pragma warning disable CA2012 // ValueTask used in NSubstitute mock assertion
        _ = _store.DidNotReceiveWithAnyArgs().GetActiveCampaignMetricsAsync(default!, default);
        _ = _store.Received(2).GetAllActiveCampaignMetricsAsync(Arg.Any<CancellationToken>());
#pragma warning restore CA2012
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldPublishEventNotDeliverableToOtherTenant_WhenFilteredForSse()
    {
        _active = [Acme11()];
        var sut = CreateSut();
        await sut.PollMetricsAsync(CancellationToken.None);
        var filter = new PlatformDeliveryFilter();
        var evt = _published.Should().ContainSingle().Subject;

        static SubscriberContext Subscriber(string tenantId) =>
            new(TenantId: tenantId, UserId: "u1", Roles: new HashSet<string>(),
                Permissions: new HashSet<string>());

        filter.IsDeliverableToSubscriber(evt, Subscriber("globex")).Should().BeFalse();
        filter.IsDeliverableToSubscriber(evt, Subscriber("acme")).Should().BeTrue();
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldSaveOneWebhookDeliveryForOwningTenant_WhenDispatcherListensOnSameBus()
    {
        var subStore = Substitute.For<IWebhookSubscriptionStore>();
        var deliveryStore = Substitute.For<IWebhookDeliveryStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        const string eventType = "campaign.metrics_updated";
        var acmeSub = new WebhookSubscription("sub-acme", "acme", "Acme hook", "https://acme.example/hook",
            "secret-a", [eventType], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var globexSub = new WebhookSubscription("sub-globex", "globex", "Globex hook",
            "https://globex.example/hook", "secret-g", [eventType], true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        subStore.GetActiveByEventTypeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<WebhookSubscription> subs = call.ArgAt<string>(1) != eventType
                    ? []
                    : call.ArgAt<string>(0) switch
                    {
                        "acme" => [acmeSub],
                        "globex" => [globexSub],
                        _ => [],
                    };
                return Task.FromResult(subs);
            });
        var saved = new List<WebhookDelivery>();
        deliveryStore.SaveAsync(Arg.Do<WebhookDelivery>(saved.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        using var dispatcher = new WebhookDispatcher(
            _bus, subStore, deliveryStore, clock, NullLogger<WebhookDispatcher>.Instance);

        _active = [Acme11()];
        await CreateSut().PollMetricsAsync(CancellationToken.None);
        await dispatcher.WaitForDispatchAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        var delivery = saved.Should().ContainSingle().Subject;
        delivery.SubscriptionId.Should().Be("sub-acme");
        delivery.TenantId.Should().Be("acme");
        delivery.EventType.Should().Be(eventType);
        using (var payload = JsonDocument.Parse(delivery.Payload))
        {
            var root = payload.RootElement;
            root.GetProperty("tenantId").GetString().Should().Be("acme");
            var data = root.GetProperty("data");
            data.GetProperty("campaignId").GetInt64().Should().Be(11);
            data.GetProperty("tenantId").GetString().Should().Be("acme");
        }

        await deliveryStore.DidNotReceive().SaveAsync(
            Arg.Is<WebhookDelivery>(d => d.SubscriptionId == "sub-globex"), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _bus.Dispose();
    }
}
