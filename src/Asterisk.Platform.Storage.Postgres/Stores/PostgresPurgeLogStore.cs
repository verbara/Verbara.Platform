using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPurgeLogStore : IPurgeLogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPurgeLogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(PurgeEntry entry, CancellationToken ct)
    {
        var entitiesJson = JsonSerializer.Serialize(
            entry.EntitiesDeleted, PostgresJson.Ctx.DictionaryStringInt32);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO purge_log (purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at) " +
            "VALUES (@PurgeId, @TenantId, @SubjectType, @SubjectId, @PerformedBy, @Reason, @EntitiesDeleted::jsonb, @PurgedAt)",
            new
            {
                entry.PurgeId,
                entry.TenantId,
                entry.SubjectType,
                entry.SubjectId,
                entry.PerformedBy,
                entry.Reason,
                EntitiesDeleted = entitiesJson,
                entry.PurgedAt,
            });
    }

    public async Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? until,
        int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(tenantId))
        {
            conditions.Add("tenant_id = @TenantId");
            parameters.Add("TenantId", tenantId);
        }
        if (from.HasValue)
        {
            conditions.Add("purged_at >= @From");
            parameters.Add("From", from.Value);
        }
        if (until.HasValue)
        {
            conditions.Add("purged_at <= @Until");
            parameters.Add("Until", until.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var offset = (page - 1) * pageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM purge_log {where}", parameters);

        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<PurgeLogRow>(
            "SELECT purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at " +
            $"FROM purge_log {where} ORDER BY purged_at DESC LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToPurgeEntry()).ToList();
        return new PagedResult<PurgeEntry>(items, total, page, pageSize);
    }

    private sealed class PurgeLogRow
    {
        public string purge_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string subject_type { get; init; } = null!;
        public string subject_id { get; init; } = null!;
        public string performed_by { get; init; } = null!;
        public string reason { get; init; } = null!;
        public string entities_deleted { get; init; } = null!;
        public DateTime purged_at { get; init; }

        public PurgeEntry ToPurgeEntry() => new()
        {
            PurgeId = purge_id,
            TenantId = tenant_id,
            SubjectType = subject_type,
            SubjectId = subject_id,
            PerformedBy = performed_by,
            Reason = reason,
            EntitiesDeleted = JsonSerializer.Deserialize(
                entities_deleted, PostgresJson.Ctx.DictionaryStringInt32) ?? [],
            PurgedAt = purged_at,
        };
    }
}
