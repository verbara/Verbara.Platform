using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryUserRoleStore : IUserRoleStore
{
    private readonly List<UserRoleAssignment> _assignments = [];

    public Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UserRoleAssignment>>(
            _assignments.Where(a => a.TenantId == tenantId && a.UserId == userId).ToList());

    public Task AssignAsync(TenantId tenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct)
    {
        if (!_assignments.Any(a => a.TenantId == tenantId && a.UserId == userId && a.RoleId == roleId))
        {
            _assignments.Add(new UserRoleAssignment
            {
                TenantId = tenantId,
                UserId = userId,
                RoleId = roleId,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = assignedBy,
            });
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct)
    {
        _assignments.RemoveAll(a => a.TenantId == tenantId && a.UserId == userId && a.RoleId == roleId);
        return Task.CompletedTask;
    }

    public Task ReplaceAllAsync(TenantId tenantId, EntityId userId, IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct)
    {
        _assignments.RemoveAll(a => a.TenantId == tenantId && a.UserId == userId);
        foreach (var roleId in roleIds)
        {
            _assignments.Add(new UserRoleAssignment
            {
                TenantId = tenantId,
                UserId = userId,
                RoleId = roleId,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = assignedBy,
            });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
