using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Services;

/// <summary>
/// Source-gen <see cref="LoggerMessage"/> entries for cross-cutting Realtime
/// concerns that don't belong to a single relay (e.g. ADR-0022 Phase A.5
/// leader-gating diagnostics).
/// </summary>
/// <remarks>
/// Pro.Cluster ships <c>LeadershipLog.SkippedForwardNotLeader</c> as
/// <c>internal</c> (its public API surface is the <c>IClusterLeader</c>
/// interface, not the diagnostic log entries). The relay still needs a
/// Trace-level structured log on every skip so support can correlate
/// "no SignalR delivery" complaints with leadership state. Declared here
/// with EventId 3001 to keep it stable across releases.
/// </remarks>
internal static partial class RealtimeLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Trace,
        Message = "[RELAY] Skipping forward of {EventType} — not leader for {Resource}")]
    public static partial void SkippedForwardNotLeader(ILogger logger, string eventType, string resource);
}
