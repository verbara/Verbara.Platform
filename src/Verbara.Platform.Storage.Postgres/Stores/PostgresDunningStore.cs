using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Sdk.Data.Npgsql;
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
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM dunning_records WHERE tenant_id = @TenantId AND is_active = TRUE",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            DunningRecordRow.Map, ct);

        return row?.ToModel();
    }

    public async Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM dunning_records WHERE invoice_id = @InvoiceId",
            p => p.Add(new NpgsqlParameter("InvoiceId", invoiceId)),
            DunningRecordRow.Map, ct);

        return row?.ToModel();
    }

    public async Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM dunning_records WHERE is_active = TRUE",
            p => { },
            DunningRecordRow.Map, ct);

        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task UpsertAsync(DunningRecord record, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
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
            p =>
            {
                p.Add(new NpgsqlParameter("DunningId",    record.DunningId));
                p.Add(new NpgsqlParameter("TenantId",     record.TenantId));
                p.Add(new NpgsqlParameter("InvoiceId",    record.InvoiceId));
                p.Add(new NpgsqlParameter("CurrentStage", record.CurrentStage.ToString()));
                p.Add(new NpgsqlParameter("StartedAt",    record.StartedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("EscalatedAt",  (object?)record.EscalatedAt?.UtcDateTime ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ResolvedAt",   (object?)record.ResolvedAt?.UtcDateTime ?? DBNull.Value));
                p.Add(new NpgsqlParameter("IsPaused",     record.IsPaused));
                p.Add(new NpgsqlParameter("IsActive",     record.IsActive));
            },
            ct);
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

        public static DunningRecordRow Map(NpgsqlDataReader r) => new()
        {
            dunning_id    = r.GetString("dunning_id"),
            tenant_id     = r.GetString("tenant_id"),
            invoice_id    = r.GetString("invoice_id"),
            current_stage = r.GetString("current_stage"),
            started_at    = r.GetDateTime("started_at"),
            escalated_at  = r.GetDateTimeOrNull("escalated_at"),
            resolved_at   = r.GetDateTimeOrNull("resolved_at"),
            is_paused     = r.GetBoolean("is_paused"),
            is_active     = r.GetBoolean("is_active"),
        };

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
