using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Single-node fallback <see cref="IClusterLeader"/>: always the leader. Registered
/// (keyed) ONLY when no cluster connection string is configured, so leader-gated
/// background work (e.g. the agent-liveness reaper) still runs on a single replica.
/// In clustered deployments the real Postgres-lease-backed leader is used instead.
/// </summary>
internal sealed class AlwaysLeader(string resource) : IClusterLeader
{
    public string Resource { get; } = resource;
    public bool IsLeader => true;
    public string? CurrentLeaderInstanceId => Environment.MachineName;
}
