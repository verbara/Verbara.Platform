using Dapper;
using Npgsql;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresDispositionStore : IDispositionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDispositionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Disposition?> GetByIdAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DispositionRow>(
            "SELECT disposition_id, tenant_id, name, category, is_active, created_at " +
            "FROM dispositions WHERE tenant_id = @TenantId AND disposition_id = @DispositionId",
            new { TenantId = tenantId.Value, DispositionId = dispositionId.Value });
        return row?.ToDisposition();
    }

    public async Task<IReadOnlyList<Disposition>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DispositionRow>(
            "SELECT disposition_id, tenant_id, name, category, is_active, created_at " +
            "FROM dispositions WHERE tenant_id = @TenantId ORDER BY name",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToDisposition()).ToList();
    }

    public async Task SaveAsync(Disposition disposition, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO dispositions (disposition_id, tenant_id, name, category, is_active, created_at) " +
            "VALUES (@DispositionId, @TenantId, @Name, @Category, @IsActive, @CreatedAt) " +
            "ON CONFLICT (tenant_id, disposition_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, category = EXCLUDED.category, is_active = EXCLUDED.is_active",
            new
            {
                DispositionId = disposition.DispositionId.Value,
                TenantId = disposition.TenantId.Value,
                disposition.Name,
                Category = (int)disposition.Category,
                disposition.IsActive,
                disposition.CreatedAt,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId dispositionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM dispositions WHERE tenant_id = @TenantId AND disposition_id = @DispositionId",
            new { TenantId = tenantId.Value, DispositionId = dispositionId.Value });
    }

    private sealed record DispositionRow(
        string disposition_id,
        string tenant_id,
        string name,
        int category,
        bool is_active,
        DateTimeOffset created_at)
    {
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
