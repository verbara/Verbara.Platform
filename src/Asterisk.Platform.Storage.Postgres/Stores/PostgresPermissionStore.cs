using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

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

    private sealed record PermissionRow(
        string permission_id, string category, string resource,
        string action, string description, string[]? implies)
    {
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
