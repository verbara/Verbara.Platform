using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantRoleStore : ITenantRoleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantRoleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at " +
            "FROM tenant_roles WHERE tenant_id = @TenantId ORDER BY name",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            TenantRoleRow.Map, ct);
        return rows.Select(r => r.ToTenantRole()).ToList();
    }

    public async Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at " +
            "FROM tenant_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            TenantRoleRow.Map, ct);
        if (row is null) return null;

        var role = row.ToTenantRole();
        var perms = await _dataSource.QueryListAsync(
            "SELECT permission_id FROM tenant_role_permissions " +
            "WHERE tenant_id = @TenantId AND role_id = @RoleId ORDER BY permission_id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            r => r.GetString("permission_id"), ct);
        role.Permissions = perms;
        return role;
    }

    public async Task SaveAsync(TenantRole role, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default, created_at, updated_at) " +
            "VALUES (@RoleId, @TenantId, @Name, @Description, @SourceTemplateId, @IsDefault, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, role_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, description = EXCLUDED.description, " +
            "  is_default = EXCLUDED.is_default, updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("RoleId", role.RoleId));
                p.Add(new NpgsqlParameter("TenantId", role.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", role.Name));
                p.Add(new NpgsqlParameter("Description", NpgsqlDbType.Text) { Value = (object?)role.Description ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SourceTemplateId", NpgsqlDbType.Text) { Value = (object?)role.SourceTemplateId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("IsDefault", role.IsDefault));
                p.Add(new NpgsqlParameter("CreatedAt", role.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)role.UpdatedAt ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        // CASCADE deletes tenant_role_permissions and user_roles entries
        await _dataSource.ExecuteAsync(
            "DELETE FROM tenant_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            ct);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var perms = await _dataSource.QueryListAsync(
            "SELECT permission_id FROM tenant_role_permissions " +
            "WHERE tenant_id = @TenantId AND role_id = @RoleId ORDER BY permission_id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            r => r.GetString("permission_id"), ct);
        return perms;
    }

    public async Task SetPermissionsAsync(TenantId tenantId, string roleId,
        IReadOnlyList<string> permissionIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM tenant_role_permissions WHERE tenant_id = @TenantId AND role_id = @RoleId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            tx, ct);

        foreach (var permissionId in permissionIds)
        {
            await conn.ExecuteAsync(
                "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
                "VALUES (@TenantId, @RoleId, @PermissionId)",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("RoleId", roleId));
                    p.Add(new NpgsqlParameter("PermissionId", permissionId));
                },
                tx, ct);
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
            p =>
            {
                p.Add(new NpgsqlParameter("RoleId", roleId));
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Name", name));
                p.Add(new NpgsqlParameter("Description", NpgsqlDbType.Text) { Value = (object?)description ?? DBNull.Value });
                p.Add(new NpgsqlParameter("TemplateId", templateId));
            },
            tx, ct);

        // Copy permissions from template
        await conn.ExecuteAsync(
            "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
            "SELECT @TenantId, @RoleId, permission_id " +
            "FROM role_template_permissions WHERE template_id = @TemplateId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
                p.Add(new NpgsqlParameter("TemplateId", templateId));
            },
            tx, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        return (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM user_roles WHERE tenant_id = @TenantId AND role_id = @RoleId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            }, ct) ?? 0L);
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

        public static TenantRoleRow Map(NpgsqlDataReader r) => new()
        {
            role_id = r.GetString("role_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            description = r.GetStringOrNull("description"),
            source_template_id = r.GetStringOrNull("source_template_id"),
            is_default = r.GetBoolean("is_default"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
        };

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
