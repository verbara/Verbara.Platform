using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

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

    public Task<IReadOnlyList<string>> MoveAsync(
        TenantId tenantId, EntityId userId, IReadOnlyCollection<string> fromRoleIds, string? toRoleId, string? assignedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fromRoleIds);

        bool Leaving(UserRoleAssignment a) =>
            a.TenantId == tenantId && a.UserId == userId
            && fromRoleIds.Contains(a.RoleId) && !string.Equals(a.RoleId, toRoleId, StringComparison.Ordinal);

        var removed = _assignments.Where(Leaving).Select(a => a.RoleId).ToList();
        _assignments.RemoveAll(Leaving);
        if (toRoleId is not null)
            AssignAsync(tenantId, userId, toRoleId, assignedBy, ct);
        return Task.FromResult<IReadOnlyList<string>>(removed);
    }

    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
