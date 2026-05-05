namespace Verbara.Platform.Core.Impersonation;

/// <summary>
/// Lifecycle state of an impersonation session tracked by
/// <see cref="IImpersonationSessionStore"/>.
/// </summary>
public enum ImpersonationSessionStatus
{
    /// <summary>Session is active and the shadow JWT is still within its lifetime.</summary>
    Active = 0,

    /// <summary>Session was explicitly ended by the impersonator (legitimate exit).</summary>
    Completed = 1,

    /// <summary>Session was forcibly revoked by an admin (manual revoke from the admin UI).</summary>
    ManuallyRevoked = 2,

    /// <summary>Session was revoked by the periodic timeout sweep (exceeded tenant timeout).</summary>
    AutoTimedOut = 3,
}

/// <summary>
/// R5.2 PB.2 — admin-visible record of a single impersonation session.
/// Recorded when <c>POST /management/impersonate</c> issues a shadow JWT;
/// closed when the matching <c>DELETE /management/impersonate</c> arrives,
/// when the admin UI revokes it, or when the auto-timeout sweep marks it
/// expired.
/// </summary>
public sealed class ImpersonationSession
{
    /// <summary>Stable session identifier (used in revoke endpoint and audit links).</summary>
    public required string Id { get; init; }

    /// <summary>User id of the admin who initiated the impersonation.</summary>
    public required string ActorUserId { get; init; }

    /// <summary>Tenant id of the admin who initiated the impersonation.</summary>
    public required string ActorTenantId { get; init; }

    /// <summary>User id of the impersonation target (when known — empty for tenant-wide impersonation).</summary>
    public string? TargetUserId { get; init; }

    /// <summary>Tenant id being impersonated.</summary>
    public required string TargetTenantId { get; init; }

    /// <summary>Free-text reason supplied at start (when present).</summary>
    public string? Reason { get; init; }

    /// <summary>True if the shadow JWT is read-only (view-only permission set).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>When the session was started (used to compute time remaining).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the session ended (revoked, completed, or auto-timed-out). Null while active.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public ImpersonationSessionStatus Status { get; set; } = ImpersonationSessionStatus.Active;

    /// <summary>
    /// Reason why the session was closed (when <see cref="Status"/> is not
    /// <see cref="ImpersonationSessionStatus.Active"/>): "manual_revoke",
    /// "auto_timeout", "completed", or admin-supplied free text.
    /// </summary>
    public string? CloseReason { get; set; }

    /// <summary>Optional client IP captured at session start (for audit cross-reference).</summary>
    public string? ClientIp { get; init; }

    /// <summary>Optional user-agent captured at session start.</summary>
    public string? UserAgent { get; init; }
}
