namespace Asterisk.Platform.Identity;

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
    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsMfaRequiredForRole(string role) =>
        MfaPolicy switch
        {
            "required_all" => true,
            "required_for_roles" => MfaRequiredRoles.Contains(role, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
}
