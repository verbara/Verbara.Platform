namespace Asterisk.Platform.Api.Services;

/// <summary>
/// AHH Phase 2 — discriminated command set written to the
/// <see cref="AuthWriteQueue"/> by the success-side login flow. Each command
/// is processed off the request critical path by the queue's background
/// consumer, persisted via <see cref="Asterisk.Platform.Identity.IUserStore"/>
/// or <see cref="Asterisk.Platform.Identity.IAuthEventStore"/>.
/// </summary>
/// <remarks>
/// <b>Failure-path operations stay synchronous.</b> See ADR-0011 for the
/// security invariant: an attacker fishing credentials must not be able to
/// outpace the audit log, so <see cref="AuthEventService.LogAsync"/>
/// (sync) is the canonical path for <c>LoginFailure</c>, <c>Lockout</c>,
/// and similar adversarial signals. Only <c>LoginSuccess</c> rides this
/// queue.
/// </remarks>
internal abstract record AuthWriteCommand
{
    /// <summary>Stable string used as the <c>type</c> meter dimension.</summary>
    public abstract string TypeName { get; }
}

/// <summary>Defer a <c>users.last_login_at</c> upsert.</summary>
internal sealed record UpdateLastLoginAtCommand(
    string TenantId,
    string UserId,
    DateTimeOffset At) : AuthWriteCommand
{
    public override string TypeName => "update_last_login_at";
}

/// <summary>Defer a <c>users.failed_login_attempts</c> + <c>users.locked_until</c> reset.</summary>
internal sealed record ResetLockoutCountersCommand(
    string TenantId,
    string UserId) : AuthWriteCommand
{
    public override string TypeName => "reset_lockout_counters";
}

/// <summary>Defer an <c>auth_events</c> insert for the success-path login event.</summary>
internal sealed record LogSuccessEventCommand(
    string TenantId,
    string UserId,
    string EventType,
    string? IpAddress,
    string? UserAgent) : AuthWriteCommand
{
    public override string TypeName => "log_success_event";
}
