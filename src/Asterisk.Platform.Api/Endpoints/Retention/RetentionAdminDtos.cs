namespace Asterisk.Platform.Api.Endpoints.Retention;

/// <summary>
/// Snapshot of a single registered <c>IRetentionTarget</c> exposed by the
/// admin overview surface (R5.2 PC.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Schema"/> + <see cref="Table"/> are derived locally on the
/// Platform side via <c>RetentionTargetMetadata.Resolve</c> rather than
/// extending the Pro surface (the SDK <c>IRetentionTarget</c> contract only
/// exposes <c>Name</c> + <c>CustomWindow</c>). Unknown targets fall back to
/// <c>"unknown"</c>; consumers should treat the values as informational.
/// </para>
/// <para>
/// <see cref="LastExecutionAt"/> / <see cref="LastRowsPurged"/> / <see cref="LastStatus"/>
/// are tracked in-process by <c>RetentionExecutionTracker</c>. They are
/// <see langword="null"/> until the target has been executed at least once
/// in the current process lifetime (background cron run or manual trigger).
/// </para>
/// </remarks>
public sealed record RetentionTargetDto
{
    /// <summary>Stable target name from <c>IRetentionTarget.Name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Postgres schema (e.g. <c>pro_eventstore</c>) or <c>"unknown"</c>.</summary>
    public required string Schema { get; init; }

    /// <summary>Underlying table (e.g. <c>session_events</c>) or <c>"unknown"</c>.</summary>
    public required string Table { get; init; }

    /// <summary>Effective retention window (custom override or policy default).</summary>
    public required int WindowDays { get; init; }

    /// <summary>Last execution timestamp, or <see langword="null"/> if never run this process.</summary>
    public required DateTimeOffset? LastExecutionAt { get; init; }

    /// <summary>Rows purged (or counted in dry-run) in the last execution.</summary>
    public required long? LastRowsPurged { get; init; }

    /// <summary>One of <c>success</c>, <c>skipped</c>, <c>error</c>, or <see langword="null"/> if never run.</summary>
    public required string? LastStatus { get; init; }

    /// <summary>True if last execution was a dry run (count only).</summary>
    public required bool LastWasDryRun { get; init; }
}

/// <summary>
/// Global retention configuration snapshot returned by GET /management/retention/config
/// and accepted (partial) by PATCH /management/retention/config.
/// </summary>
public sealed record RetentionConfigDto
{
    /// <summary>True when targets count rows instead of deleting.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Default retention window in days.</summary>
    public required int DefaultWindowDays { get; init; }

    /// <summary>Batch size used by per-target DELETE loops.</summary>
    public required int BatchSize { get; init; }

    /// <summary>Cron expression (5-field "M H * * *" — daily) for the background loop.</summary>
    public required string CronExpression { get; init; }

    /// <summary>Number of targets currently registered.</summary>
    public required int RegisteredTargetCount { get; init; }
}

/// <summary>
/// PATCH body for <c>/management/retention/config</c>. Only <c>DryRun</c> can be
/// flipped at runtime in v1 — window / batch / cron require process restart per
/// the underlying <c>IOptions&lt;RetentionOptions&gt;</c> snapshot semantics.
/// </summary>
public sealed record RetentionConfigPatchDto
{
    /// <summary>
    /// New value for <c>DryRun</c>. <see langword="null"/> = leave unchanged.
    /// Toggling emits an <c>retention.dryrun_toggled</c> audit entry with the
    /// previous + new value in metadata.
    /// </summary>
    public bool? DryRun { get; init; }
}

/// <summary>
/// Result of POST /management/retention/run-now. Echoes the input + per-target
/// row counts so the UI can render an outcome summary without re-fetching.
/// </summary>
public sealed record RetentionRunResultDto
{
    /// <summary>True if the run was a dry run (count only).</summary>
    public required bool DryRun { get; init; }

    /// <summary>UTC timestamp the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC timestamp the run completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Per-target outcomes, in registration order.</summary>
    public required IReadOnlyList<RetentionRunTargetOutcomeDto> Targets { get; init; }
}

/// <summary>
/// Per-target outcome from a manual run.
/// </summary>
public sealed record RetentionRunTargetOutcomeDto
{
    public required string Name { get; init; }
    public required string Status { get; init; }   // "success" | "error" | "skipped"
    public required long RowsPurged { get; init; } // 0 on error / skipped
    public required string? ErrorMessage { get; init; }
    public required double DurationMs { get; init; }
}
