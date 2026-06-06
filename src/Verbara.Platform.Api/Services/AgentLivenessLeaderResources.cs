namespace Verbara.Platform.Api.Services;

/// <summary>W3 — leader-election resource for the agent-liveness sweep (one pod sweeps cluster-wide).</summary>
internal static class AgentLivenessLeaderResources
{
    internal const string Sweep = "agent-liveness:sweep";
}
