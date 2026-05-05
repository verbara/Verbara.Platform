using Dapper;
using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresDunningStore : IDunningStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDunningStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "dunning_id, tenant_id, invoice_id, current_stage, started_at, " +
        "escalated_at, resolved_at, is_paused, is_active";

    public async Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DunningRecordRow>(
            $"SELECT {SelectColumns} FROM dunning_records WHERE tenant_id = @TenantId AND is_active = TRUE",
            new { TenantId = tenantId });

        return row?.ToModel();
    }

    public async Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DunningRecordRow>(
            $"SELECT {SelectColumns} FROM dunning_records WHERE invoice_id = @InvoiceId",
            new { InvoiceId = invoiceId });

        return row?.ToModel();
    }

    public async Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DunningRecordRow>(
            $"SELECT {SelectColumns} FROM dunning_records WHERE is_active = TRUE");

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(DunningRecord record, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO dunning_records " +
            "(dunning_id, tenant_id, invoice_id, current_stage, started_at, escalated_at, resolved_at, is_paused, is_active, created_at, updated_at) " +
            "VALUES (@DunningId, @TenantId, @InvoiceId, @CurrentStage, @StartedAt, @EscalatedAt, @ResolvedAt, @IsPaused, @IsActive, NOW(), NOW()) " +
            "ON CONFLICT (dunning_id) DO UPDATE SET " +
            "  current_stage = EXCLUDED.current_stage, " +
            "  escalated_at  = EXCLUDED.escalated_at, " +
            "  resolved_at   = EXCLUDED.resolved_at, " +
            "  is_paused     = EXCLUDED.is_paused, " +
            "  is_active     = EXCLUDED.is_active, " +
            "  updated_at    = NOW()",
            new
            {
                DunningId    = record.DunningId,
                TenantId     = record.TenantId,
                InvoiceId    = record.InvoiceId,
                CurrentStage = record.CurrentStage.ToString(),
                StartedAt    = record.StartedAt.UtcDateTime,
                EscalatedAt  = record.EscalatedAt?.UtcDateTime,
                ResolvedAt   = record.ResolvedAt?.UtcDateTime,
                IsPaused     = record.IsPaused,
                IsActive     = record.IsActive,
            });
    }

    private sealed class DunningRecordRow
    {
        public string dunning_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string invoice_id { get; init; } = null!;
        public string current_stage { get; init; } = null!;
        public DateTime started_at { get; init; }
        public DateTime? escalated_at { get; init; }
        public DateTime? resolved_at { get; init; }
        public bool is_paused { get; init; }
        public bool is_active { get; init; }

        public DunningRecord ToModel() => new()
        {
            DunningId    = dunning_id,
            TenantId     = tenant_id,
            InvoiceId    = invoice_id,
            CurrentStage = Enum.Parse<TenantStatus>(current_stage),
            StartedAt    = new DateTimeOffset(started_at, TimeSpan.Zero),
            EscalatedAt  = escalated_at.HasValue ? new DateTimeOffset(escalated_at.Value, TimeSpan.Zero) : null,
            ResolvedAt   = resolved_at.HasValue ? new DateTimeOffset(resolved_at.Value, TimeSpan.Zero) : null,
            IsPaused     = is_paused,
            IsActive     = is_active,
        };
    }
}
