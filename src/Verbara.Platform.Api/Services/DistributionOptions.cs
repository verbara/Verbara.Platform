namespace Verbara.Platform.Api.Services;

public sealed class DistributionOptions
{
    public int PollIntervalMs { get; set; } = 2000;
    public int OfferTimeoutSeconds { get; set; } = 30;
    public int DefaultQueueTimeoutSeconds { get; set; } = 300;
    public int DefaultWrapUpTimeoutSeconds { get; set; } = 120;
    public int MaxConversationsPerCycle { get; set; } = 50;

    /// <summary>
    /// Startup grace period (milliseconds) before <c>ConversationTimeoutWorker</c> begins its
    /// first sweep, letting dependencies warm up. Options-overridable for deterministic fast
    /// tests (C3); default preserves production cadence.
    /// </summary>
    public int ConversationTimeoutStartupDelayMs { get; set; } = 5000;

    /// <summary>
    /// Interval (milliseconds) between <c>ConversationTimeoutWorker</c> timeout sweeps; also the
    /// heartbeat tick cadence reported for the worker. Options-overridable for deterministic fast
    /// tests (C3); default preserves production cadence.
    /// </summary>
    public int ConversationTimeoutSweepIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Startup warm-up delay (milliseconds) before <c>QueueDistributionWorker</c> begins its
    /// first distribution pass. Options-overridable for deterministic fast tests (C3); default
    /// preserves production cadence.
    /// </summary>
    public int QueueDistributionStartupDelayMs { get; set; } = 3000;

    // ─── Autonomous AI disposition (ADR-0034) ─────────────────────────────────

    /// <summary>
    /// Global circuit breaker for autonomous AI disposition stamping on the abandoned-wrap-up
    /// close path (ADR-0034 Decision 6). <b>Default OFF</b>: when false (or when a tenant has no
    /// active activation gate) the wrap-up close path is byte-identical to its pre-ADR-0034
    /// behaviour (close, no disposition). Flipping this true enables the autonomous branch for
    /// tenants that additionally hold an active gate + license + a high-confidence suggestion.
    /// Code-overridable (read from the <c>Distribution</c> config section like the worker cadence
    /// knobs) — it is a kill-switch, NOT operator-tuned production cadence.
    /// </summary>
    public bool AutonomousDispositionEnabled { get; set; }

    /// <summary>
    /// Maximum number of conversations a single cycle may autonomously stamp per tenant
    /// (ADR-0034 Decision 6 — per-tenant rate cap / blast-radius control). Candidates beyond the
    /// cap in a given cycle are left in <c>WrapUp</c> for the next cycle (NOT blank-closed) while
    /// the gate is ON. Default 50.
    /// </summary>
    public int AutonomousDispositionPerCycleCap { get; set; } = 50;

    /// <summary>
    /// Number of days an autonomous-disposition decision (and its AI-actor audit record) is held
    /// against the blanket retention purge (ADR-0034 Decision 4/5 — the supervisor correction
    /// window). Used to compute the audit record's <c>RetainUntil</c> floor (<c>now + N days</c>).
    /// Default 30.
    /// </summary>
    public int AutonomousCorrectionWindowDays { get; set; } = 30;
}
