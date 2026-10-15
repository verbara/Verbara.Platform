using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory;

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
        // An update keeps the template the role was cloned from, as the Postgres upsert does (it never
        // writes source_template_id on conflict): a rename must not change which template a role came from.
        var existing = _roles.FirstOrDefault(r => r.TenantId == role.TenantId && r.RoleId == role.RoleId);
        if (existing is not null && !string.Equals(existing.SourceTemplateId, role.SourceTemplateId, StringComparison.Ordinal))
        {
            role = new TenantRole
            {
                RoleId = role.RoleId,
                TenantId = role.TenantId,
                Name = role.Name,
                Description = role.Description,
                SourceTemplateId = existing.SourceTemplateId,
                IsDefault = role.IsDefault,
                CreatedAt = role.CreatedAt,
                UpdatedAt = role.UpdatedAt,
                Permissions = role.Permissions,
            };
        }

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
        => Task.FromResult(0);
}
