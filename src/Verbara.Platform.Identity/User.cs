using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public sealed class User : ITenantScoped, IAuditable
{
    public required EntityId UserId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; set; }
    public required UserRole Role { get; set; }
    public required UserStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    // Auth Enterprise fields
    public string? PasswordHash { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecret { get; set; }
    public IReadOnlyList<string>? MfaRecoveryCodes { get; set; }
    public DateTimeOffset? MfaConfirmedAt { get; set; }
    public bool EmailVerified { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string AuthProvider { get; set; } = "local";
    public string? ExternalId { get; set; }
    public string? OidcSubject { get; set; }

    public bool IsLockedOut(DateTimeOffset now) =>
        LockedUntil.HasValue && now < LockedUntil.Value;

    /// <summary>
    /// A copy that shares nothing a caller can change with this instance, the way a fresh read of
    /// the row is a separate object. Stores hand out copies so that one caller changing the object
    /// it read never changes what another caller, or the store itself, holds.
    /// </summary>
    public User Clone()
    {
        var copy = (User)MemberwiseClone();
        copy.MfaRecoveryCodes = MfaRecoveryCodes?.ToArray();
        return copy;
    }

    /// <summary>
    /// Whether this account may authenticate at all: obtain tokens, refresh them, use a user-bound
    /// API key, open a live connection. Only <see cref="UserStatus.Active"/> may; Suspended and
    /// Deactivated — and any status added later — are refused.
    /// </summary>
    /// <remarks>
    /// The single account-status rule. Every authentication path (password login, MFA completion,
    /// refresh, API-key login and per-request API keys, OIDC, impersonation start and every request
    /// made with an impersonation token, the SSE stream and the Realtime hub connect check) asks
    /// this property instead of comparing <see cref="Status"/> itself, so the paths cannot disagree
    /// about which statuses get in.
    /// </remarks>
    public bool CanAuthenticate => Status == UserStatus.Active;

    private static readonly Dictionary<UserRole, Permission> s_rolePermissions =
        new Dictionary<UserRole, Permission>
        {
            [UserRole.Agent] = Permission.HandleConversations,
            [UserRole.Supervisor] = Permission.HandleConversations | Permission.ViewReports | Permission.ManageQueues,
            [UserRole.Admin] = (Permission)((1 << 10) - 1), // all flags
            [UserRole.Api] = Permission.HandleConversations | Permission.ViewReports,
        };

    public bool HasPermission(Permission permission)
    {
        if (!CanAuthenticate)
            return false;

        return s_rolePermissions.TryGetValue(Role, out var granted) &&
               (granted & permission) == permission;
    }
}
