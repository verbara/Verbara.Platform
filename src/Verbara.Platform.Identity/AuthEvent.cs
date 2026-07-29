using System.Text.Json;

namespace Verbara.Platform.Identity;

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
    public const string PasswordChangeFailure = "password_change_failure";
    public const string MfaEnroll = "mfa_enroll";
    public const string MfaDisable = "mfa_disable";

    /// <summary>
    /// A second-factor verification attempt at <c>POST /auth/mfa/verify</c> failed — a wrong TOTP
    /// code, a wrong recovery code, or no factor supplied. Distinct from
    /// <see cref="LoginFailure"/>, which covers the password stage: the two happen at different
    /// steps and conflating them would misreport which factor the caller failed. Emitted alongside
    /// a lockout attempt so second-factor guessing is both audited and throttled
    /// (<c>docs/security/audit-checklist.md</c> Scope 3.4).
    /// </summary>
    public const string MfaVerificationFailure = "mfa_verification_failure";
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

    /// <summary>Written to the CALLER's audit log when an MFA-admin operation
    /// (<c>POST /management/mfa/users/{id}/reset</c> or <c>.../sessions/revoke</c>)
    /// is rejected because the caller-supplied <c>?targetTenant=</c> override is
    /// outside the caller's tenant hierarchy. Closes
    /// <c>PREPUB-2026-05-09-MFA-001</c>. Severity: error.</summary>
    public const string MfaPrivilegeEscalationAttempted = "mfa_privilege_escalation_attempted";

    public const string OidcLoginSuccess = "oidc_login_success";
    public const string OidcLoginFailure = "oidc_login_failure";
}
