using System.Text.Json;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPurgeLogStore : IPurgeLogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPurgeLogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(PurgeEntry entry, CancellationToken ct)
    {
        var entitiesJson = JsonSerializer.Serialize(
            entry.EntitiesDeleted, PostgresJson.Ctx.DictionaryStringInt32);

        await _dataSource.ExecuteAsync(
            "INSERT INTO purge_log (purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at) " +
            "VALUES (@PurgeId, @TenantId, @SubjectType, @SubjectId, @PerformedBy, @Reason, @EntitiesDeleted::jsonb, @PurgedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("PurgeId", entry.PurgeId));
                p.Add(new NpgsqlParameter("TenantId", entry.TenantId));
                p.Add(new NpgsqlParameter("SubjectType", entry.SubjectType));
                p.Add(new NpgsqlParameter("SubjectId", entry.SubjectId));
                p.Add(new NpgsqlParameter("PerformedBy", entry.PerformedBy));
                p.Add(new NpgsqlParameter("Reason", entry.Reason));
                p.Add(new NpgsqlParameter("EntitiesDeleted", entitiesJson));
                p.Add(new NpgsqlParameter("PurgedAt", entry.PurgedAt));
            },
            ct);
    }

    public async Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? until,
        int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string>();
        var binders = new List<Action<NpgsqlParameterCollection>>();

        if (!string.IsNullOrEmpty(tenantId))
        {
            conditions.Add("tenant_id = @TenantId");
            binders.Add(p => p.Add(new NpgsqlParameter("TenantId", tenantId)));
        }
        if (from.HasValue)
        {
            conditions.Add("purged_at >= @From");
            binders.Add(p => p.Add(new NpgsqlParameter("From", from.Value)));
        }
        if (until.HasValue)
        {
            conditions.Add("purged_at <= @Until");
            binders.Add(p => p.Add(new NpgsqlParameter("Until", until.Value)));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var offset = (page - 1) * pageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            $"SELECT COUNT(*) FROM purge_log {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at " +
            $"FROM purge_log {where} ORDER BY purged_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            PurgeLogRow.Map, ct);

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

        public static PurgeLogRow Map(NpgsqlDataReader r) => new()
        {
            purge_id = r.GetString("purge_id"),
            tenant_id = r.GetString("tenant_id"),
            subject_type = r.GetString("subject_type"),
            subject_id = r.GetString("subject_id"),
            performed_by = r.GetString("performed_by"),
            reason = r.GetString("reason"),
            entities_deleted = r.GetString("entities_deleted"),
            purged_at = r.GetDateTime("purged_at"),
        };

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
