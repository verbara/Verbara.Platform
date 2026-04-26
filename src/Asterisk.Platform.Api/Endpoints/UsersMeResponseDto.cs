using Asterisk.Platform.Api.Endpoints.Mfa;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Endpoints;

/// <summary>
/// Response payload for <c>GET /users/me</c>. Mirrors the shape of <see cref="User"/>
/// (kept stable for the Web client) and surfaces an additional
/// <see cref="MfaPolicy"/> snapshot so the UI can hide the "Disable MFA" button
/// proactively when the tenant policy enforces MFA — closing the
/// <c>TODO(v1.7.0)</c> tracked in <c>security-page.tsx</c>.
/// </summary>
internal sealed record UserMeResponseDto(
    string UserId,
    string TenantId,
    string Email,
    string DisplayName,
    UserRole Role,
    UserStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy,
    bool MfaEnabled,
    DateTimeOffset? MfaConfirmedAt,
    bool EmailVerified,
    int FailedLoginAttempts,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? PasswordChangedAt,
    DateTimeOffset? LastLoginAt,
    string AuthProvider,
    string? ExternalId,
    string? OidcSubject,
    MfaPolicyDto MfaPolicy)
{
    public static UserMeResponseDto FromUser(User user, MfaPolicyDto policy)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        return new UserMeResponseDto(
            UserId: user.UserId.Value,
            TenantId: user.TenantId.Value,
            Email: user.Email,
            DisplayName: user.DisplayName,
            Role: user.Role,
            Status: user.Status,
            CreatedAt: user.CreatedAt,
            UpdatedAt: user.UpdatedAt,
            CreatedBy: user.CreatedBy,
            UpdatedBy: user.UpdatedBy,
            MfaEnabled: user.MfaEnabled,
            MfaConfirmedAt: user.MfaConfirmedAt,
            EmailVerified: user.EmailVerified,
            FailedLoginAttempts: user.FailedLoginAttempts,
            LockedUntil: user.LockedUntil,
            PasswordChangedAt: user.PasswordChangedAt,
            LastLoginAt: user.LastLoginAt,
            AuthProvider: user.AuthProvider,
            ExternalId: user.ExternalId,
            OidcSubject: user.OidcSubject,
            MfaPolicy: policy);
    }
}
