using Npgsql;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPermissionStore : IPermissionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPermissionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT permission_id, category, resource, action, description, implies " +
            "FROM permissions ORDER BY category, resource, action",
            p => { },
            PermissionRow.Map, ct);
        return rows.Select(r => r.ToDefinition()).ToList();
    }

    public async Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT permission_id, category, resource, action, description, implies " +
            "FROM permissions WHERE category = @Category ORDER BY resource, action",
            p => p.Add(new NpgsqlParameter("Category", category)),
            PermissionRow.Map, ct);
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

        public static PermissionRow Map(NpgsqlDataReader r) => new()
        {
            permission_id = r.GetString("permission_id"),
            category      = r.GetString("category"),
            resource      = r.GetString("resource"),
            action        = r.GetString("action"),
            description   = r.GetString("description"),
            implies       = r.IsDBNull(r.GetOrdinal("implies")) ? null : r.GetFieldValue<string[]>(r.GetOrdinal("implies")),
        };

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
