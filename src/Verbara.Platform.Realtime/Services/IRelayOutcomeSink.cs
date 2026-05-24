using Verbara.Platform.Realtime.Contracts.Dtos;

namespace Verbara.Platform.Realtime.Services;

/// <summary>
/// Sink that <see cref="PushToHubRelay"/> notifies for every push-event
/// outcome (forward / skip / error). Single contract physical-coupling point
/// between the relay and (a) the in-memory ring buffer surfaced via
/// <c>GET /admin/realtime/audit</c> and (b) the <c>System.Diagnostics.Metrics</c>
/// counters consumed by Prometheus / OpenTelemetry exporters.
/// </summary>
/// <remarks>
/// <para>
/// Recording an outcome MUST be cheap — the relay sits on the hot path of every
/// envelope arriving from <c>Pro.Push</c>'s Redis backplane. Implementations are
/// expected to complete in &lt; 5 µs in steady state.
/// </para>
/// <para>
/// Snapshots support an optional <c>since</c> cursor so a polling
/// E2E harness can fetch only the delta since its previous read. The cursor is
/// exclusive — entries with <c>Ts &lt;= since</c> are omitted.
/// </para>
/// </remarks>
internal interface IRelayOutcomeSink
{
    /// <summary>Records a single outcome row. MUST NOT throw.</summary>
    void Record(RelayOutcomeEntry entry);

    /// <summary>
    /// Returns a point-in-time copy of the ring-buffer contents filtered by
    /// <paramref name="since"/> (exclusive) and capped to <paramref name="limit"/>
    /// rows. Always populated with the buffer metadata (capacity, evictions, etc.)
    /// so callers can detect silent overwrites.
    /// </summary>
    /// <param name="since">Cursor — rows with <c>Ts &lt;= since</c> are excluded. Null returns every retained row.</param>
    /// <param name="limit">Maximum rows to return. Implementations clamp to a sane upper bound (e.g. the ring capacity).</param>
    RelayOutcomePage Snapshot(DateTimeOffset? since, int limit);
}
