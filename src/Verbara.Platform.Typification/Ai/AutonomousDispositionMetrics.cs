using System.Diagnostics.Metrics;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Owns the <c>verbara.platform.typification.autonomous</c> <see cref="Meter"/> and the instruments
/// emitted by the autonomous AI disposition close path (ADR-0034). All counters are
/// tenant-dimensioned (a <c>tenant</c> tag).
/// </summary>
/// <remarks>
/// <para>
/// Follows the house pattern from <see cref="TypificationAiMetrics"/> / <c>LlmMetrics</c>: constructed
/// via an optional <see cref="IMeterFactory"/> (production DI path) with a plain <c>new Meter(...)</c>
/// fallback so unit-test constructors that omit the factory still compile and run. AOT-safe — no
/// reflection; the <see cref="Meter"/> + <see cref="Counter{T}"/> instruments are the standard
/// <c>System.Diagnostics.Metrics</c> surface.
/// </para>
/// <para>
/// <c>autonomous_commits_total</c> and <c>autonomous_skipped_total</c> (with a <c>reason</c> tag) are
/// emitted by the close-path worker (<c>ConversationTimeoutWorker</c>);
/// <c>autonomous_corrections_total</c> is emitted by the supervisor correction endpoint (task group 6)
/// — its definition lives here so all three autonomous instruments share one Meter.
/// </para>
/// <para>
/// <strong>Disposal:</strong> this type owns and explicitly disposes its <see cref="Meter"/> to
/// prevent leaks in test or recycled-DI scenarios.
/// </para>
/// </remarks>
public sealed class AutonomousDispositionMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> registered with OpenTelemetry.</summary>
    public const string MeterName = "verbara.platform.typification.autonomous";

    private readonly Meter _meter;

    /// <summary>
    /// Total autonomous AI dispositions successfully stamped on the abandoned-wrap-up close path
    /// (the conditional close won the CAS and the <c>AutoAi</c> submission was persisted).
    /// </summary>
    public Counter<long> Commits { get; }

    /// <summary>
    /// Total autonomous-disposition candidates skipped, dimensioned by the
    /// <see cref="AutonomousSkipReason"/> (<c>reason</c> tag) the policy returned.
    /// </summary>
    public Counter<long> Skipped { get; }

    /// <summary>
    /// Total supervisor corrections of an autonomously stamped disposition (emitted by the
    /// correction endpoint, task group 6). The overturn ratio is derivable from this and
    /// <see cref="Commits"/>.
    /// </summary>
    public Counter<long> Corrections { get; }

    public AutonomousDispositionMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);

        Commits = _meter.CreateCounter<long>(
            "autonomous_commits_total",
            description: "Autonomous AI dispositions stamped on the abandoned-wrap-up close path (ADR-0034).");

        Skipped = _meter.CreateCounter<long>(
            "autonomous_skipped_total",
            description: "Autonomous-disposition candidates skipped, tagged by reason (ADR-0034).");

        Corrections = _meter.CreateCounter<long>(
            "autonomous_corrections_total",
            description: "Supervisor corrections of an autonomously stamped disposition (ADR-0034).");
    }

    /// <summary>Increments <see cref="Commits"/> for the given tenant.</summary>
    public void RecordCommit(string tenantId) =>
        Commits.Add(1, new KeyValuePair<string, object?>("tenant", tenantId));

    /// <summary>Increments <see cref="Skipped"/> for the given tenant and skip reason.</summary>
    public void RecordSkip(string tenantId, AutonomousSkipReason reason) =>
        Skipped.Add(
            1,
            new KeyValuePair<string, object?>("tenant", tenantId),
            new KeyValuePair<string, object?>("reason", reason.ToString()));

    /// <summary>Increments <see cref="Corrections"/> for the given tenant.</summary>
    public void RecordCorrection(string tenantId) =>
        Corrections.Add(1, new KeyValuePair<string, object?>("tenant", tenantId));

    public void Dispose() => _meter.Dispose();
}
