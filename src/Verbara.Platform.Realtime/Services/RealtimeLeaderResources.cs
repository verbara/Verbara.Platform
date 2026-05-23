namespace Verbara.Platform.Realtime.Services;

/// <summary>
/// Central registry of Pro.Cluster leader-election resource names used by
/// <c>Verbara.Platform.Realtime</c>. Constants live in one place so the
/// keyed-DI registration (<see cref="Verbara.Sdk.Pro.Cluster.Leadership.VerbaraClusterOptionsBuilder.RegisterLeader(string)"/>)
/// and the consuming <c>[FromKeyedServices]</c> attributes can never drift.
/// </summary>
internal static class RealtimeLeaderResources
{
    /// <summary>
    /// Per-resource leader that gates <see cref="PushToHubRelay"/>'s cross-pod
    /// SignalR fanout. ADR-0022 Phase A.5: only the leader pod publishes
    /// SignalR group broadcasts; non-leader pods short-circuit so the Redis
    /// backplane fans the leader's broadcasts to every pod's connected clients.
    /// </summary>
    public const string Fanout = "realtime:fanout:leader";
}
