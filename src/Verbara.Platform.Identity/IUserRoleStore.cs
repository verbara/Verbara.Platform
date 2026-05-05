using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public interface IUserRoleStore
{
    Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task AssignAsync(TenantId tenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct);
    Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct);
    Task ReplaceAllAsync(TenantId tenantId, EntityId userId, IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct);
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
