using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// HTTP-surface projection of <see cref="TenantAuthConfig"/> that is safe to
/// emit to any caller satisfying <c>AdminOnly</c>. Closes
/// <c>PREPUB-2026-05-09-ADMIN-001</c>: the OIDC client secret is NEVER
/// returned in HTTP responses.
/// </summary>
/// <remarks>
/// <para>
/// Two replacement fields stand in for the raw secret:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="OidcClientSecretSet"/> — boolean
///   indicating whether a secret is currently configured;</description></item>
///   <item><description><see cref="OidcClientSecretFingerprint"/> — the
///   first 8 hex characters of the SHA-256 of the raw secret. Lets operators
///   verify "did the secret rotate?" without ever exposing the value. Mirrors
///   the Scope 5.4 reveal-once / fingerprint pattern documented in
///   <c>docs/security/audit-checklist.md</c>.</description></item>
/// </list>
/// <para>
/// Field naming preserves the existing <see cref="TenantAuthConfig"/> shape so
/// existing consumers continue to deserialize the non-secret surface
/// unchanged.
/// </para>
/// </remarks>
public sealed record TenantAuthConfigResponse(
    string TenantId,
    string MfaPolicy,
    IReadOnlyList<string> MfaRequiredRoles,
    int PasswordMinLength,
    bool PasswordRequireUppercase,
    bool PasswordRequireNumber,
    bool PasswordRequireSpecial,
    int LockoutThreshold,
    int LockoutDurationMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteTimeoutHours,
    int PendingPauseTimeoutMinutes,
    bool OidcEnabled,
    string? OidcAuthority,
    string? OidcClientId,
    bool OidcClientSecretSet,
    string? OidcClientSecretFingerprint,
    bool OidcAutoCreateUsers,
    string OidcDefaultRole,
    int ImpersonationMaxConcurrentSessions,
    int ImpersonationAutoTimeoutMinutes,
    bool IpAllowlistEnabled,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>
    /// Projects a <see cref="TenantAuthConfig"/> to the HTTP-safe response,
    /// computing the SHA-256 fingerprint of the raw secret if present. The
    /// raw secret value is never assigned to any response field.
    /// </summary>
    public static TenantAuthConfigResponse FromConfig(TenantAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TenantAuthConfigResponse(
            TenantId: config.TenantId,
            MfaPolicy: config.MfaPolicy,
            MfaRequiredRoles: config.MfaRequiredRoles,
            PasswordMinLength: config.PasswordMinLength,
            PasswordRequireUppercase: config.PasswordRequireUppercase,
            PasswordRequireNumber: config.PasswordRequireNumber,
            PasswordRequireSpecial: config.PasswordRequireSpecial,
            LockoutThreshold: config.LockoutThreshold,
            LockoutDurationMinutes: config.LockoutDurationMinutes,
            SessionIdleTimeoutMinutes: config.SessionIdleTimeoutMinutes,
            SessionAbsoluteTimeoutHours: config.SessionAbsoluteTimeoutHours,
            PendingPauseTimeoutMinutes: config.PendingPauseTimeoutMinutes,
            OidcEnabled: config.OidcEnabled,
            OidcAuthority: config.OidcAuthority,
            OidcClientId: config.OidcClientId,
            OidcClientSecretSet: !string.IsNullOrEmpty(config.OidcClientSecret),
            OidcClientSecretFingerprint: ComputeFingerprint(config.OidcClientSecret),
            OidcAutoCreateUsers: config.OidcAutoCreateUsers,
            OidcDefaultRole: config.OidcDefaultRole,
            ImpersonationMaxConcurrentSessions: config.ImpersonationMaxConcurrentSessions,
            ImpersonationAutoTimeoutMinutes: config.ImpersonationAutoTimeoutMinutes,
            IpAllowlistEnabled: config.IpAllowlistEnabled,
            UpdatedAt: config.UpdatedAt);
    }

    /// <summary>
    /// First 8 hex characters of <c>SHA-256(secret)</c>. Returns <c>null</c>
    /// when no secret is configured.
    /// </summary>
    private static string? ComputeFingerprint(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash)[..8];
    }
}
