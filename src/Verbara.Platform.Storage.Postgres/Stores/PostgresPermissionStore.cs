using Dapper;
using Npgsql;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPermissionStore : IPermissionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPermissionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PermissionRow>(
            "SELECT permission_id, category, resource, action, description, implies " +
            "FROM permissions ORDER BY category, resource, action");
        return rows.Select(r => r.ToDefinition()).ToList();
    }

    public async Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PermissionRow>(
            "SELECT permission_id, category, resource, action, description, implies " +
            "FROM permissions WHERE category = @Category ORDER BY resource, action",
            new { Category = category });
        return rows.Select(r => r.ToDefinition()).ToList();
    }

    private sealed class PermissionRow
    {
        public string permission_id { get; init; } = null!;
        public string category { get; init; } = null!;
        public string resource { get; init; } = null!;
        public string action { get; init; } = null!;
        public string description { get; init; } = null!;
        public string[]? implies { get; init; }

        public PermissionDefinition ToDefinition() => new()
        {
            PermissionId = permission_id,
            Category = category,
            Resource = resource,
            Action = action,
            Description = description,
            Implies = implies ?? [],
        };
    }
}
