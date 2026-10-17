using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// Substitutes for the keyed <see cref="CampaignMetricsLeaderResources.Webhooks"/> leader that
/// <see cref="WebhookDispatcher"/> and <see cref="CampaignMetricsPoller"/> require (design D5).
/// </summary>
internal static class MetricsWebhooksLeader
{
    /// <summary>A leader that holds the lease: every event dispatches, as on a single node.</summary>
    public static IClusterLeader Holding() => Stub(isLeader: true);

    public static IClusterLeader Stub(bool isLeader)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(CampaignMetricsLeaderResources.Webhooks);
        return leader;
    }
}
