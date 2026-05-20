using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresDispositionStore : IDispositionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDispositionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Disposition?> GetByIdAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT disposition_id, tenant_id, name, category, is_active, created_at " +
            "FROM dispositions WHERE tenant_id = @TenantId AND disposition_id = @DispositionId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("DispositionId", dispositionId.Value)); },
            DispositionRow.Map, ct);
        return row?.ToDisposition();
    }

    public async Task<IReadOnlyList<Disposition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT disposition_id, tenant_id, name, category, is_active, created_at " +
            "FROM dispositions WHERE tenant_id = @TenantId ORDER BY name",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            DispositionRow.Map, ct);
        return rows.Select(r => r.ToDisposition()).ToList();
    }

    public async Task SaveAsync(Disposition disposition, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO dispositions (disposition_id, tenant_id, name, category, is_active, created_at) " +
            "VALUES (@DispositionId, @TenantId, @Name, @Category, @IsActive, @CreatedAt) " +
            "ON CONFLICT (tenant_id, disposition_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, category = EXCLUDED.category, is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("DispositionId", disposition.DispositionId.Value));
                p.Add(new NpgsqlParameter("TenantId", disposition.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", disposition.Name));
                p.Add(new NpgsqlParameter("Category", (int)disposition.Category));
                p.Add(new NpgsqlParameter("IsActive", disposition.IsActive));
                p.Add(new NpgsqlParameter("CreatedAt", disposition.CreatedAt));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM dispositions WHERE tenant_id = @TenantId AND disposition_id = @DispositionId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("DispositionId", dispositionId.Value)); },
            ct);
    }

    private sealed class DispositionRow
    {
        public string disposition_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int category { get; init; }
        public bool is_active { get; init; }
        public DateTime created_at { get; init; }

        public static DispositionRow Map(NpgsqlDataReader r) => new()
        {
            disposition_id = r.GetString("disposition_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            category = r.GetInt32("category"),
            is_active = r.GetBoolean("is_active"),
            created_at = r.GetDateTime("created_at"),
        };

        public Disposition ToDisposition() => new()
        {
            DispositionId = EntityId.From(disposition_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            Category = (DispositionCategory)category,
            IsActive = is_active,
            CreatedAt = created_at,
        };
    }
}
