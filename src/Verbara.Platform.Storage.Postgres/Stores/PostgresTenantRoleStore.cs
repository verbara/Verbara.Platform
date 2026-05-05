using Dapper;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantRoleStore : ITenantRoleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantRoleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TenantRoleRow>(
            "SELECT role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at " +
            "FROM tenant_roles WHERE tenant_id = @TenantId ORDER BY name",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToTenantRole()).ToList();
    }

    public async Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TenantRoleRow>(
            "SELECT role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at " +
            "FROM tenant_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            new { TenantId = tenantId.Value, RoleId = roleId });
        if (row is null) return null;

        var role = row.ToTenantRole();
        var perms = await conn.QueryAsync<string>(
            "SELECT permission_id FROM tenant_role_permissions " +
            "WHERE tenant_id = @TenantId AND role_id = @RoleId ORDER BY permission_id",
            new { TenantId = tenantId.Value, RoleId = roleId });
        role.Permissions = perms.ToList();
        return role;
    }

    public async Task SaveAsync(TenantRole role, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at) " +
            "VALUES (@RoleId, @TenantId, @Name, @Description, @SourceTemplateId, @IsDefault, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, role_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, description = EXCLUDED.description, " +
            "  is_default = EXCLUDED.is_default, updated_at = EXCLUDED.updated_at",
            new
            {
                role.RoleId,
                TenantId = role.TenantId.Value,
                role.Name,
                role.Description,
                role.SourceTemplateId,
                role.IsDefault,
                role.CreatedAt,
                role.UpdatedAt,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // CASCADE deletes tenant_role_permissions and user_roles entries
        await conn.ExecuteAsync(
            "DELETE FROM tenant_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            new { TenantId = tenantId.Value, RoleId = roleId });
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var perms = await conn.QueryAsync<string>(
            "SELECT permission_id FROM tenant_role_permissions " +
            "WHERE tenant_id = @TenantId AND role_id = @RoleId ORDER BY permission_id",
            new { TenantId = tenantId.Value, RoleId = roleId });
        return perms.ToList();
    }

    public async Task SetPermissionsAsync(TenantId tenantId, string roleId,
        IReadOnlyList<string> permissionIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM tenant_role_permissions WHERE tenant_id = @TenantId AND role_id = @RoleId",
            new { TenantId = tenantId.Value, RoleId = roleId }, tx);

        if (permissionIds.Count > 0)
        {
            await conn.ExecuteAsync(
                "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
                "VALUES (@TenantId, @RoleId, @PermissionId)",
                permissionIds.Select(p => new { TenantId = tenantId.Value, RoleId = roleId, PermissionId = p }),
                tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task CloneFromTemplateAsync(TenantId tenantId, string roleId,
        string templateId, string name, string? description, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Create the tenant role
        await conn.ExecuteAsync(
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default, created_at) " +
            "VALUES (@RoleId, @TenantId, @Name, @Description, @TemplateId, false, now())",
            new { RoleId = roleId, TenantId = tenantId.Value, Name = name, Description = description, TemplateId = templateId },
            tx);

        // Copy permissions from template
        await conn.ExecuteAsync(
            "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
            "SELECT @TenantId, @RoleId, permission_id " +
            "FROM role_template_permissions WHERE template_id = @TemplateId",
            new { TenantId = tenantId.Value, RoleId = roleId, TemplateId = templateId },
            tx);

        await tx.CommitAsync(ct);
    }

    public async Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM user_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            new { TenantId = tenantId.Value, RoleId = roleId });
    }

    private sealed class TenantRoleRow
    {
        public string role_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string? description { get; init; }
        public string? source_template_id { get; init; }
        public bool is_default { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }

        public TenantRole ToTenantRole() => new()
        {
            RoleId = role_id,
            TenantId = new TenantId(tenant_id),
            Name = name,
            Description = description,
            SourceTemplateId = source_template_id,
            IsDefault = is_default,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }
}
