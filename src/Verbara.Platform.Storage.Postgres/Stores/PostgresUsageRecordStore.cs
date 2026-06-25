using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUsageRecordStore : IUsageRecordStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUsageRecordStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(UsageRecord record, CancellationToken ct)
    {
        var metadataJson = record.Metadata != null
            ? JsonSerializer.Serialize(record.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;

        await _dataSource.ExecuteAsync(
            "INSERT INTO usage_records (record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata) " +
            "VALUES (@RecordId, @TenantId, @UsageType, @Quantity, @Unit, @Channel, @ReferenceId, @RecordedAt, @Metadata::jsonb)",
            p =>
            {
                p.Add(new NpgsqlParameter("RecordId", record.RecordId.Value));
                p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                p.Add(new NpgsqlParameter("UsageType", (short)record.UsageType));
                p.Add(new NpgsqlParameter("Quantity", record.Quantity));
                p.Add(new NpgsqlParameter("Unit", (short)record.Unit));
                p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Text) { Value = (object?)record.Channel ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ReferenceId", NpgsqlDbType.Text) { Value = (object?)record.ReferenceId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("RecordedAt", record.RecordedAt));
                p.Add(new NpgsqlParameter("Metadata", (object?)metadataJson ?? DBNull.Value));
            },
            ct);
    }

    public async Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var record in records)
        {
            var metadataJson = record.Metadata != null
                ? JsonSerializer.Serialize(record.Metadata, PostgresJson.Ctx.DictionaryStringString)
                : null;

            await conn.ExecuteAsync(
                "INSERT INTO usage_records (record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata) " +
                "VALUES (@RecordId, @TenantId, @UsageType, @Quantity, @Unit, @Channel, @ReferenceId, @RecordedAt, @Metadata::jsonb)",
                p =>
                {
                    p.Add(new NpgsqlParameter("RecordId", record.RecordId.Value));
                    p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                    p.Add(new NpgsqlParameter("UsageType", (short)record.UsageType));
                    p.Add(new NpgsqlParameter("Quantity", record.Quantity));
                    p.Add(new NpgsqlParameter("Unit", (short)record.Unit));
                    p.Add(new NpgsqlParameter("Channel", NpgsqlDbType.Text) { Value = (object?)record.Channel ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("ReferenceId", NpgsqlDbType.Text) { Value = (object?)record.ReferenceId ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("RecordedAt", record.RecordedAt));
                    p.Add(new NpgsqlParameter("Metadata", (object?)metadataJson ?? DBNull.Value));
                },
                tx, ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until " +
            "GROUP BY usage_type",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("From", from.UtcDateTime));
                p.Add(new NpgsqlParameter("Until", until.UtcDateTime));
            },
            SummaryRow.Map, ct);

        return rows.Select(r => new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = until,
            UsageType = (UsageType)r.usage_type,
            TotalQuantity = r.total_quantity,
            RecordCount = r.record_count,
            LastUpdatedAt = r.last_updated_at,
        }).ToList();
    }

    public async Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND usage_type = @UsageType AND recorded_at >= @From AND recorded_at < @Until " +
            "GROUP BY usage_type",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UsageType", (short)type));
                p.Add(new NpgsqlParameter("From", from.UtcDateTime));
                p.Add(new NpgsqlParameter("Until", until.UtcDateTime));
            },
            SummaryRow.Map, ct);

        if (row is null) return null;

        return new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = until,
            UsageType = type,
            TotalQuantity = row.total_quantity,
            RecordCount = row.record_count,
            LastUpdatedAt = row.last_updated_at,
        };
    }

    public async Task<AiTokenBreakdown> GetAiTokenBreakdownAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        // Single-row aggregate (COALESCE → always exactly one row). Uses jsonb_exists(...) rather than the
        // `?` operator to avoid Npgsql positional-placeholder ambiguity. NULL/missing-key rows fall to the
        // unsplit (Quantity) bucket and are never silently dropped.
        var row = await _dataSource.QuerySingleAsync(
            "SELECT " +
            "COALESCE(SUM(CASE WHEN metadata IS NOT NULL AND jsonb_exists(metadata, 'inputTokens') AND jsonb_exists(metadata, 'outputTokens') " +
            "THEN (metadata->>'inputTokens')::numeric ELSE 0 END), 0) AS input_tokens, " +
            "COALESCE(SUM(CASE WHEN metadata IS NOT NULL AND jsonb_exists(metadata, 'inputTokens') AND jsonb_exists(metadata, 'outputTokens') " +
            "THEN (metadata->>'outputTokens')::numeric ELSE 0 END), 0) AS output_tokens, " +
            "COALESCE(SUM(CASE WHEN metadata IS NULL OR NOT (jsonb_exists(metadata, 'inputTokens') AND jsonb_exists(metadata, 'outputTokens')) " +
            "THEN quantity ELSE 0 END), 0) AS unsplit_tokens " +
            "FROM usage_records " +
            "WHERE tenant_id = @TenantId AND usage_type = @UsageType AND recorded_at >= @From AND recorded_at < @Until",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UsageType", (short)type));
                p.Add(new NpgsqlParameter("From", from.UtcDateTime));
                p.Add(new NpgsqlParameter("Until", until.UtcDateTime));
            },
            BreakdownRow.Map, ct);

        return new AiTokenBreakdown(row.input_tokens, row.output_tokens, row.unsplit_tokens);
    }

    public async Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct)
    {
        List<RecordRow> rows;

        if (type is not null)
        {
            rows = await _dataSource.QueryListAsync(
                "SELECT record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata " +
                "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until " +
                "AND usage_type = @UsageType " +
                "ORDER BY recorded_at DESC LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("From", from.UtcDateTime));
                    p.Add(new NpgsqlParameter("Until", until.UtcDateTime));
                    p.Add(new NpgsqlParameter("UsageType", (short)type.Value));
                    p.Add(new NpgsqlParameter("Limit", pageSize));
                    p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize));
                },
                RecordRow.Map, ct);
        }
        else
        {
            rows = await _dataSource.QueryListAsync(
                "SELECT record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata " +
                "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until " +
                "ORDER BY recorded_at DESC LIMIT @Limit OFFSET @Offset",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("From", from.UtcDateTime));
                    p.Add(new NpgsqlParameter("Until", until.UtcDateTime));
                    p.Add(new NpgsqlParameter("Limit", pageSize));
                    p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize));
                },
                RecordRow.Map, ct);
        }

        return rows.Select(r => new UsageRecord
        {
            RecordId = EntityId.From(r.record_id),
            TenantId = new TenantId(r.tenant_id),
            UsageType = (UsageType)r.usage_type,
            Quantity = r.quantity,
            Unit = (UsageUnit)r.unit,
            Channel = r.channel,
            ReferenceId = r.reference_id,
            RecordedAt = r.recorded_at,
            Metadata = r.metadata != null
                ? JsonSerializer.Deserialize(r.metadata, PostgresJson.Ctx.DictionaryStringString)
                : null,
        }).ToList();
    }

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM usage_records WHERE tenant_id = @TenantId AND recorded_at < @Cutoff",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Cutoff", cutoff.UtcDateTime));
            },
            ct);
    }

    private sealed class SummaryRow
    {
        public short usage_type { get; init; }
        public decimal total_quantity { get; init; }
        public int record_count { get; init; }
        public DateTime last_updated_at { get; init; }

        public static SummaryRow Map(NpgsqlDataReader r) => new()
        {
            usage_type = r.GetInt16("usage_type"),
            total_quantity = r.GetDecimal("total_quantity"),
            record_count = r.GetInt32("record_count"),
            last_updated_at = r.GetDateTime("last_updated_at"),
        };
    }

    private sealed class BreakdownRow
    {
        public decimal input_tokens { get; init; }
        public decimal output_tokens { get; init; }
        public decimal unsplit_tokens { get; init; }

        public static BreakdownRow Map(NpgsqlDataReader r) => new()
        {
            input_tokens = r.GetDecimal("input_tokens"),
            output_tokens = r.GetDecimal("output_tokens"),
            unsplit_tokens = r.GetDecimal("unsplit_tokens"),
        };
    }

    private sealed class RecordRow
    {
        public string record_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public short usage_type { get; init; }
        public decimal quantity { get; init; }
        public short unit { get; init; }
        public string? channel { get; init; }
        public string? reference_id { get; init; }
        public DateTime recorded_at { get; init; }
        public string? metadata { get; init; }

        public static RecordRow Map(NpgsqlDataReader r) => new()
        {
            record_id = r.GetString("record_id"),
            tenant_id = r.GetString("tenant_id"),
            usage_type = r.GetInt16("usage_type"),
            quantity = r.GetDecimal("quantity"),
            unit = r.GetInt16("unit"),
            channel = r.GetStringOrNull("channel"),
            reference_id = r.GetStringOrNull("reference_id"),
            recorded_at = r.GetDateTime("recorded_at"),
            metadata = r.GetStringOrNull("metadata"),
        };
    }
}
