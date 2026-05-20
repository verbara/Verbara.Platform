using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUserRoleStore : IUserRoleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserRoleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT tenant_id, user_id, role_id, assigned_at, assigned_by " +
            "FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId " +
            "ORDER BY assigned_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            UserRoleRow.Map, ct);
        return rows.Select(r => r.ToAssignment()).ToList();
    }

    public async Task AssignAsync(TenantId tenantId, EntityId userId, string roleId,
        string? assignedBy, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
            "VALUES (@TenantId, @UserId, @RoleId, now(), @AssignedBy) " +
            "ON CONFLICT (tenant_id, user_id, role_id) DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
                p.Add(new NpgsqlParameter("AssignedBy", (object?)assignedBy ?? DBNull.Value));
            },
            ct);
    }

    public async Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId AND role_id = @RoleId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
                p.Add(new NpgsqlParameter("RoleId", roleId));
            },
            ct);
    }

    public async Task ReplaceAllAsync(TenantId tenantId, EntityId userId,
        IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            tx, ct);

        foreach (var roleId in roleIds)
        {
            await conn.ExecuteAsync(
                "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
                "VALUES (@TenantId, @UserId, @RoleId, now(), @AssignedBy)",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("UserId", userId.Value));
                    p.Add(new NpgsqlParameter("RoleId", roleId));
                    p.Add(new NpgsqlParameter("AssignedBy", (object?)assignedBy ?? DBNull.Value));
                },
                tx, ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        // Get all permissions from all roles assigned to the user, plus implied permissions
        var directPermissions = await _dataSource.QueryListAsync(
            "SELECT DISTINCT trp.permission_id " +
            "FROM user_roles ur " +
            "JOIN tenant_role_permissions trp ON ur.tenant_id = trp.tenant_id AND ur.role_id = trp.role_id " +
            "WHERE ur.tenant_id = @TenantId AND ur.user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            r => r.GetString("permission_id"), ct);

        var directSet = new HashSet<string>(directPermissions);

        // Expand implied permissions
        var allImplies = await _dataSource.QueryListAsync(
            "SELECT permission_id, implies FROM permissions WHERE implies IS NOT NULL AND array_length(implies, 1) > 0",
            static p => { },
            ImpliesRow.Map, ct);

        var impliesMap = allImplies.ToDictionary(r => r.permission_id, r => r.implies ?? []);

        var expanded = new HashSet<string>(directSet);
        var queue = new Queue<string>(directSet);
        while (queue.Count > 0)
        {
            var perm = queue.Dequeue();
            if (impliesMap.TryGetValue(perm, out var implied))
            {
                foreach (var imp in implied)
                {
                    if (expanded.Add(imp))
                        queue.Enqueue(imp);
                }
            }
        }

        return expanded;
    }

    private sealed class UserRoleRow
    {
        public string tenant_id { get; init; } = null!;
        public string user_id { get; init; } = null!;
        public string role_id { get; init; } = null!;
        public DateTime assigned_at { get; init; }
        public string? assigned_by { get; init; }

        public static UserRoleRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            user_id = r.GetString("user_id"),
            role_id = r.GetString("role_id"),
            assigned_at = r.GetDateTime("assigned_at"),
            assigned_by = r.GetStringOrNull("assigned_by"),
        };

        public UserRoleAssignment ToAssignment() => new()
        {
            TenantId = new TenantId(tenant_id),
            UserId = EntityId.From(user_id),
            RoleId = role_id,
            AssignedAt = assigned_at,
            AssignedBy = assigned_by,
        };
    }

    private sealed class ImpliesRow
    {
        public string permission_id { get; init; } = null!;
        public string[]? implies { get; init; }

        public static ImpliesRow Map(NpgsqlDataReader r) => new()
        {
            permission_id = r.GetString("permission_id"),
            implies = r.IsDBNull(r.GetOrdinal("implies")) ? null : r.GetFieldValue<string[]>(r.GetOrdinal("implies")),
        };
    }
}
