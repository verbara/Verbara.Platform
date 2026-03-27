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
    public const string Lockout = "lockout";
    public const string SessionRevoked = "session_revoked";
    public const string PasswordReset = "password_reset";
    public const string PasswordResetRequest = "password_reset_request";
}
