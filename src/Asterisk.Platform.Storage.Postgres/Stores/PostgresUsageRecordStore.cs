using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUsageRecordStore : IUsageRecordStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUsageRecordStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(UsageRecord record, CancellationToken ct)
    {
        var metadataJson = record.Metadata != null
            ? JsonSerializer.Serialize(record.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO usage_records (record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata) " +
            "VALUES (@RecordId, @TenantId, @UsageType, @Quantity, @Unit, @Channel, @ReferenceId, @RecordedAt, @Metadata::jsonb)",
            new
            {
                RecordId = record.RecordId.Value,
                TenantId = record.TenantId.Value,
                UsageType = (short)record.UsageType,
                record.Quantity,
                Unit = (short)record.Unit,
                record.Channel,
                record.ReferenceId,
                record.RecordedAt,
                Metadata = metadataJson,
            });
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
                new
                {
                    RecordId = record.RecordId.Value,
                    TenantId = record.TenantId.Value,
                    UsageType = (short)record.UsageType,
                    record.Quantity,
                    Unit = (short)record.Unit,
                    record.Channel,
                    record.ReferenceId,
                    record.RecordedAt,
                    Metadata = metadataJson,
                },
                tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until " +
            "GROUP BY usage_type",
            new { TenantId = tenantId.Value, From = from, Until = until });

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SummaryRow?>(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND usage_type = @UsageType AND recorded_at >= @From AND recorded_at < @Until " +
            "GROUP BY usage_type",
            new { TenantId = tenantId.Value, UsageType = (short)type, From = from, Until = until });

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

    public async Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var sql = "SELECT record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata " +
                  "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until";

        if (type is not null)
            sql += " AND usage_type = @UsageType";

        sql += " ORDER BY recorded_at DESC LIMIT @Limit OFFSET @Offset";

        var rows = await conn.QueryAsync<RecordRow>(sql, new
        {
            TenantId = tenantId.Value,
            From = from,
            Until = until,
            UsageType = type is not null ? (short)type.Value : (short)0,
            Limit = pageSize,
            Offset = (page - 1) * pageSize,
        });

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM usage_records WHERE tenant_id = @TenantId AND recorded_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }

    private sealed record SummaryRow(short usage_type, decimal total_quantity, int record_count, DateTimeOffset last_updated_at);

    private sealed record RecordRow(
        string record_id, string tenant_id, short usage_type, decimal quantity, short unit,
        string? channel, string? reference_id, DateTimeOffset recorded_at, string? metadata);
}
