namespace Verbara.Platform.Api.Services;

/// <summary>W4 — leader-election resource for the deferred-pause drain sweep (one pod applies cluster-wide).</summary>
internal static class PendingPauseLeaderResources
{
    internal const string Sweep = "pending-pause:sweep";
}
