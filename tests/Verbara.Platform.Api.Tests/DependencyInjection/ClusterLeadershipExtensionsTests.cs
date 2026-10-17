using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Storage.Postgres;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.DependencyInjection;

/// <summary>
/// Pins the leadership wiring of <see cref="ClusterLeadershipExtensions"/> (dialer-sees-every-tenants-campaigns
/// D5): the campaign-metrics webhook lease is a real lease only on a clustered pod that runs the metrics
/// poller, and an <see cref="AlwaysLeader"/> stub on every other pod, because <see cref="WebhookDispatcher"/>
/// requires it on every pod. The cluster cases run on a bare <see cref="ServiceCollection"/> over an
/// unreachable data source: nothing connects until a leader's hosted service starts, and none is started.
/// </summary>
public sealed class ClusterLeadershipExtensionsTests : IClassFixture<PlatformApiFactory>
{
    private const string UnreachableCluster = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    private static readonly string[] AlwaysLeasedResources =
    [
        AgentLivenessLeaderResources.Sweep,
        PendingPauseLeaderResources.Sweep,
        WorkFailoverLeaderResources.Sweep,
    ];

    private readonly PlatformApiFactory _factory;

    public ClusterLeadershipExtensionsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public void Host_ShouldResolveHoldingMetricsWebhooksLeader_WhenNoClusterConnectionIsConfigured()
    {
        var leader = _factory.Services.GetRequiredKeyedService<IClusterLeader>(CampaignMetricsLeaderResources.Webhooks);

        leader.IsLeader.Should().BeTrue("a deployment without a cluster connection always holds the lease");
        leader.Resource.Should().Be(CampaignMetricsLeaderResources.Webhooks);
    }

    [Fact]
    public void AddSingleNodeLeaders_ShouldRegisterAlwaysLeaderForEveryResource_WhenCalled()
    {
        using var provider = new ServiceCollection().AddSingleNodeLeaders().BuildServiceProvider();

        foreach (var resource in AlwaysLeasedResources.Append(CampaignMetricsLeaderResources.Webhooks))
        {
            provider.GetRequiredKeyedService<IClusterLeader>(resource).Should().BeOfType<AlwaysLeader>()
                .Which.Resource.Should().Be(resource);
        }
    }

    [Fact]
    public async Task AddClusteredLeaders_ShouldStubMetricsWebhooksLeader_WhenDialerIsDisabled()
    {
        await using var provider = BuildClustered(dialerEnabled: false);

        provider.GetRequiredKeyedService<IClusterLeader>(CampaignMetricsLeaderResources.Webhooks)
            .Should().BeOfType<AlwaysLeader>("a pod without the metrics poller must never hold the lease");
        AssertAlwaysLeasedResourcesAreLeases(provider);
    }

    [Fact]
    public async Task AddClusteredLeaders_ShouldLeaseMetricsWebhooks_WhenDialerIsEnabled()
    {
        await using var provider = BuildClustered(dialerEnabled: true);

        var leader = provider.GetRequiredKeyedService<IClusterLeader>(CampaignMetricsLeaderResources.Webhooks);
        leader.Should().NotBeOfType<AlwaysLeader>("a pod that runs the poller competes for the lease");
        leader.GetType().Name.Should().Be("LeaderElectionService");
        AssertAlwaysLeasedResourcesAreLeases(provider);
    }

    private static ServiceProvider BuildClustered(bool dialerEnabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClusteredLeaders(
            NamedDataSource.Build(UnreachableCluster, "cluster"),
            voiceInboundEnabled: false,
            voiceAmiEnabled: false,
            dialerEnabled: dialerEnabled);
        return services.BuildServiceProvider();
    }

    private static void AssertAlwaysLeasedResourcesAreLeases(IServiceProvider provider)
    {
        foreach (var resource in AlwaysLeasedResources)
        {
            var leader = provider.GetRequiredKeyedService<IClusterLeader>(resource);
            leader.Should().NotBeOfType<AlwaysLeader>("W3/W4/W5 are real leases in every clustered deployment");
            leader.Resource.Should().Be(resource);
        }
    }
}
