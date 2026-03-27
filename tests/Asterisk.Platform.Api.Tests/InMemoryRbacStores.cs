using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Tests;

internal sealed class InMemoryPermissionStore : IPermissionStore
{
    private readonly List<PermissionDefinition> _permissions = [];

    public Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(_permissions);

    public Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(
            _permissions.Where(p => p.Category == category).ToList());
}

internal sealed class InMemoryRoleTemplateStore : IRoleTemplateStore
{
    private readonly List<RoleTemplate> _templates = [];
    private readonly Dictionary<string, List<string>> _permissions = new();

    public Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleTemplate>>(_templates);

    public Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct)
        => Task.FromResult(_templates.FirstOrDefault(t => t.TemplateId == templateId));

    public Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(
            _permissions.GetValueOrDefault(templateId, []));
}

internal sealed class InMemoryTenantRoleStore : ITenantRoleStore
{
    private readonly List<TenantRole> _roles = [];
    private readonly Dictionary<string, List<string>> _permissions = new();

    public Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TenantRole>>(
            _roles.Where(r => r.TenantId == tenantId).ToList());

    public Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var role = _roles.FirstOrDefault(r => r.TenantId == tenantId && r.RoleId == roleId);
        if (role is not null)
        {
            var key = $"{tenantId.Value}:{roleId}";
            role.Permissions = _permissions.GetValueOrDefault(key, []);
        }
        return Task.FromResult(role);
    }

    public Task SaveAsync(TenantRole role, CancellationToken ct)
    {
        _roles.RemoveAll(r => r.TenantId == role.TenantId && r.RoleId == role.RoleId);
        _roles.Add(role);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        _roles.RemoveAll(r => r.TenantId == tenantId && r.RoleId == roleId);
        _permissions.Remove($"{tenantId.Value}:{roleId}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var key = $"{tenantId.Value}:{roleId}";
        return Task.FromResult<IReadOnlyList<string>>(_permissions.GetValueOrDefault(key, []));
    }

    public Task SetPermissionsAsync(TenantId tenantId, string roleId, IReadOnlyList<string> permissionIds, CancellationToken ct)
    {
        var key = $"{tenantId.Value}:{roleId}";
        _permissions[key] = permissionIds.ToList();
        return Task.CompletedTask;
    }

    public Task CloneFromTemplateAsync(TenantId tenantId, string roleId, string templateId, string name, string? description, CancellationToken ct)
    {
        _roles.Add(new TenantRole
        {
            RoleId = roleId,
            TenantId = tenantId,
            Name = name,
            Description = description,
            SourceTemplateId = templateId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }

    public Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct)
        => Task.FromResult(0); // Simplified for tests
}

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
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()); // Simplified for tests
}
