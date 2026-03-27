using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public sealed class UserRoleAssignment
{
    public required TenantId TenantId { get; init; }
    public required EntityId UserId { get; init; }
    public required string RoleId { get; init; }
    public required DateTimeOffset AssignedAt { get; init; }
    public string? AssignedBy { get; init; }
}
