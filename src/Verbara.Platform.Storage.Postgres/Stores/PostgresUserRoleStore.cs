using Dapper;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUserRoleStore : IUserRoleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserRoleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<UserRoleRow>(
            "SELECT tenant_id, user_id, role_id, assigned_at, assigned_by " +
            "FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId " +
            "ORDER BY assigned_at",
            new { TenantId = tenantId.Value, UserId = userId.Value });
        return rows.Select(r => r.ToAssignment()).ToList();
    }

    public async Task AssignAsync(TenantId tenantId, EntityId userId, string roleId,
        string? assignedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
            "VALUES (@TenantId, @UserId, @RoleId, now(), @AssignedBy) " +
            "ON CONFLICT (tenant_id, user_id, role_id) DO NOTHING",
            new { TenantId = tenantId.Value, UserId = userId.Value, RoleId = roleId, AssignedBy = assignedBy });
    }

    public async Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId AND role_id = @RoleId",
            new { TenantId = tenantId.Value, UserId = userId.Value, RoleId = roleId });
    }

    public async Task ReplaceAllAsync(TenantId tenantId, EntityId userId,
        IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId.Value, UserId = userId.Value }, tx);

        if (roleIds.Count > 0)
        {
            await conn.ExecuteAsync(
                "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
                "VALUES (@TenantId, @UserId, @RoleId, now(), @AssignedBy)",
                roleIds.Select(r => new
                {
                    TenantId = tenantId.Value,
                    UserId = userId.Value,
                    RoleId = r,
                    AssignedBy = assignedBy,
                }),
                tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Get all permissions from all roles assigned to the user, plus implied permissions
        var directPermissions = await conn.QueryAsync<string>(
            "SELECT DISTINCT trp.permission_id " +
            "FROM user_roles ur " +
            "JOIN tenant_role_permissions trp ON ur.tenant_id = trp.tenant_id AND ur.role_id = trp.role_id " +
            "WHERE ur.tenant_id = @TenantId AND ur.user_id = @UserId",
            new { TenantId = tenantId.Value, UserId = userId.Value });

        var directSet = new HashSet<string>(directPermissions);

        // Expand implied permissions
        var allImplies = await conn.QueryAsync<ImpliesRow>(
            "SELECT permission_id, implies FROM permissions WHERE implies IS NOT NULL AND array_length(implies, 1) > 0");

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
    }
}
