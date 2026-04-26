using System.Collections.Frozen;

namespace Asterisk.Platform.Api.Endpoints.Retention;

/// <summary>
/// Static lookup table mapping the well-known <c>IRetentionTarget.Name</c>
/// values shipped by Pro v1.8.0-pro to their underlying Postgres
/// <c>schema</c>+<c>table</c>. Maintained on the Platform side so the Pro
/// surface stays minimal (the SDK contract is intentionally just
/// <c>Name + CustomWindow</c>).
/// </summary>
/// <remarks>
/// Unknown targets fall back to <c>"unknown"</c> for both fields. This is a
/// deliberately tolerant design: future Pro releases that add new targets
/// will surface in the admin overview before the lookup is updated, with
/// the row labelled "unknown.unknown" until ops adds the mapping.
/// </remarks>
internal static class RetentionTargetMetadata
{
    private static readonly FrozenDictionary<string, (string Schema, string Table)> s_lookup =
        new Dictionary<string, (string Schema, string Table)>(StringComparer.Ordinal)
        {
            // Pro.EventStore.Postgres
            ["session_events"]      = ("pro_eventstore", "session_events"),
            ["completed_sessions"]  = ("pro_eventstore", "completed_sessions"),

            // Pro.Dialer.Storage.Postgres
            ["call_attempts"]       = ("pro_dialer", "call_attempts"),
            ["dialer_contacts"]     = ("pro_dialer", "dialer_contacts"),

            // Pro.Analytics.Storage.Postgres
            ["analytics_interval_snapshots"] = ("pro_analytics", "analytics_interval_snapshots"),
            ["live_queue_snapshots"]         = ("pro_analytics", "live_queue_snapshots"),

            // Pro.AgentAssist.Storage.Postgres
            ["agent_assist_sessions"] = ("pro_agentassist", "agent_assist_sessions"),

            // Pro.CallAnalytics.Storage.Postgres
            ["call_analysis_results"] = ("pro_callanalytics", "call_analysis_results"),
        }
        .ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Returns the schema/table pair for a known target name, or
    /// <c>("unknown", "unknown")</c> for unrecognised names.
    /// </summary>
    public static (string Schema, string Table) Resolve(string targetName)
    {
        if (s_lookup.TryGetValue(targetName, out var hit))
            return hit;
        return ("unknown", "unknown");
    }
}
