using System.Text.Json;

namespace Asterisk.Platform.Identity;

public sealed class AuthEvent
{
    public required string EventId { get; init; }
    public required string TenantId { get; init; }
    public string? UserId { get; init; }
    public required string EventType { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public JsonDocument? Details { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public static class AuthEventTypes
{
    public const string LoginSuccess = "login_success";
    public const string LoginFailure = "login_failure";
    public const string Logout = "logout";
    public const string PasswordChange = "password_change";
    public const string MfaEnroll = "mfa_enroll";
    public const string MfaDisable = "mfa_disable";
    public const string RecoveryCodesRegenerated = "recovery_codes_regenerated";
    public const string Lockout = "lockout";
    public const string SessionRevoked = "session_revoked";
    public const string PasswordReset = "password_reset";
    public const string PasswordResetRequest = "password_reset_request";
    public const string ImpersonationStarted = "impersonation_started";
    public const string ImpersonationEnded = "impersonation_ended";

    /// <summary>Written to the TARGET tenant's audit log when a platform/parent admin
    /// begins impersonating into it. Gives target-tenant admins visibility of who
    /// accessed their tenant via impersonation. See P0 hierarchy check (v1.9.0).</summary>
    public const string ImpersonationTargetAccessed = "impersonation_target_accessed";

    /// <summary>Written to the CALLER's audit log when an impersonation attempt is
    /// rejected because the target tenant is not in the caller's hierarchy. This is
    /// a security-critical event (privilege escalation attempt). Severity: error.
    /// See P0 hierarchy check (v1.9.0).</summary>
    public const string ImpersonationPrivilegeEscalationAttempted = "impersonation_privilege_escalation_attempted";

    public const string OidcLoginSuccess = "oidc_login_success";
    public const string OidcLoginFailure = "oidc_login_failure";
}
