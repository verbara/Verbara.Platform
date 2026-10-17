namespace Verbara.Platform.Api.Services;

/// <summary>
/// Leader-election resource for campaign-metrics webhooks (dialer-sees-every-tenants-campaigns D5): every
/// replica polls and publishes campaign metrics to its own SSE clients, and only the holder of this lease
/// creates <c>campaign.metrics_updated</c> webhook deliveries. Leased only on a pod that runs the metrics
/// poller; every other pod gets an <see cref="AlwaysLeader"/> stub.
/// </summary>
internal static class CampaignMetricsLeaderResources
{
    internal const string Webhooks = "campaign-metrics:webhooks";
}
