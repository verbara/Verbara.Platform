namespace Verbara.Platform.Api.Services;

/// <summary>W5 — leader-election resource for the work-failover sweep (one pod re-queues cluster-wide).</summary>
internal static class WorkFailoverLeaderResources
{
    internal const string Sweep = "work-failover:sweep";
}
