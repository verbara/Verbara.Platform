namespace Verbara.Platform.Identity;

public sealed class TenantAuthConfig
{
    public required string TenantId { get; init; }
    public string MfaPolicy { get; set; } = "optional";
    public IReadOnlyList<string> MfaRequiredRoles { get; set; } = [];
    public int PasswordMinLength { get; set; } = 12;
    public bool PasswordRequireUppercase { get; set; } = true;
    public bool PasswordRequireNumber { get; set; } = true;
    public bool PasswordRequireSpecial { get; set; }
    public int LockoutThreshold { get; set; } = 5;
    public int LockoutDurationMinutes { get; set; } = 15;
    public int SessionIdleTimeoutMinutes { get; set; } = 30;
    public int SessionAbsoluteTimeoutHours { get; set; } = 12;
    public bool OidcEnabled { get; set; }
    public string? OidcAuthority { get; set; }
    public string? OidcClientId { get; set; }
    public string? OidcClientSecret { get; set; }
    public bool OidcAutoCreateUsers { get; set; } = true;
    public string OidcDefaultRole { get; set; } = "Agent";

    /// <summary>
    /// R5.2 PB.2 / C.7 — maximum number of concurrent impersonation sessions
    /// allowed for actors operating against this tenant. Default 3 protects
    /// against an admin token leak fanning out into many shadow tokens.
    /// </summary>
    public int ImpersonationMaxConcurrentSessions { get; set; } = 3;

    /// <summary>
    /// R5.2 PB.2 / C.7 — auto-timeout (minutes) after which an active
    /// impersonation session is forcibly revoked by the
    /// <c>ImpersonationSessionTimeoutService</c> sweep. Default 240 (4 hours)
    /// matches the audit-control window most enterprise SOC2 reviewers expect.
    /// </summary>
    public int ImpersonationAutoTimeoutMinutes { get; set; } = 240;

    /// <summary>
    /// v1.3.0 IP Allowlist — when true, requests from IPs not matching any
    /// row in tenant_ip_allowlist are rejected with 403. When false, the
    /// allowlist is dormant regardless of the entries that may exist.
    /// Cannot be flipped to true while the entry list is empty.
    /// See docs/specs/2026-05-02-ip-allowlist-design.md §4.
    /// </summary>
    public bool IpAllowlistEnabled { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsMfaRequiredForRole(string role) =>
        MfaPolicy switch
        {
            "required_all" => true,
            "required_for_roles" => MfaRequiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
}
