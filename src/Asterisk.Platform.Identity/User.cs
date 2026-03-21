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
