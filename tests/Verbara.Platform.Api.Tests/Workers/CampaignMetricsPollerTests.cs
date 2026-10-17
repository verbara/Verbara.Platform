using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Workers;

/// <summary>
/// Pins that <see cref="CampaignMetricsPoller"/> publishes metrics for every tenant's active
/// campaigns, each event stamped with its owning tenant. The store stub reproduces the Postgres
/// store's tenant filter (<c>WHERE tenant_id = @tenantId</c>): an empty tenant id matches no row.
/// </summary>
public sealed class CampaignMetricsPollerTests
{
    private static CampaignMetricsSnapshot Snapshot(long campaignId, string name) =>
        new(campaignId, name, CampaignStatus.Active,
            ContactsDialed: 5, ContactsRemaining: 95, ConnectRate: 0.4, AbandonRate: 0.01);

    private static CampaignStoreBase TenantFilteringStore()
    {
        var store = Substitute.For<CampaignStoreBase>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        store.GetActiveCampaignMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<string>() switch
            {
                "acme" => new ValueTask<IReadOnlyList<CampaignMetricsSnapshot>>(
                    [Snapshot(11, "Acme renewals")]),
                "globex" => new ValueTask<IReadOnlyList<CampaignMetricsSnapshot>>(
                    [Snapshot(12, "Globex winback")]),
                _ => new ValueTask<IReadOnlyList<CampaignMetricsSnapshot>>(
                    Array.Empty<CampaignMetricsSnapshot>()),
            });
#pragma warning restore CA2012
        return store;
    }

    [Fact]
    public async Task PollMetricsAsync_ShouldPublishEveryTenantsCampaignMetrics_WhenTwoTenantsHaveActiveCampaigns()
    {
        using var bus = new PlatformEventBus();
        var published = new List<CampaignMetricsUpdatedEvent>();
        using var subscription = bus.Events.Subscribe(evt =>
        {
            if (evt is CampaignMetricsUpdatedEvent metrics)
            {
                published.Add(metrics);
            }
        });

        var sut = new CampaignMetricsPoller(
            TenantFilteringStore(),
            bus,
            NullLogger<CampaignMetricsPoller>.Instance);

        await sut.PollMetricsAsync(CancellationToken.None);

        published.Should().HaveCount(2, "one active campaign per tenant must each publish its metrics");
        published.Select(e => (e.CampaignId, e.TenantId)).Should().BeEquivalentTo(
            [(11L, "acme"), (12L, "globex")]);
        published.Should().NotContain(e => string.IsNullOrEmpty(e.TenantId),
            "an event without a tenant reaches no tenant's subscribers");
    }
}
