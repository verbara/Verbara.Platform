using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

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
        if (Status != UserStatus.Active)
            return false;

        return s_rolePermissions.TryGetValue(Role, out var granted) &&
               (granted & permission) == permission;
    }
}
