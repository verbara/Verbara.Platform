using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAuditStore : IAuditStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuditStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AuditEntry entry, CancellationToken ct)
    {
        var metadataJson = entry.Metadata != null
            ? JsonSerializer.Serialize(entry.Metadata, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
            : null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "performed_by, details, occurred_at) " +
            "VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId, " +
            "@PerformedBy, @Details::jsonb, @OccurredAt)",
            new
            {
                EntryId = entry.EntryId.Value,
                TenantId = entry.TenantId.Value,
                entry.Action,
                EntityType = entry.TargetType,
                EntityId = entry.TargetId,
                PerformedBy = entry.ActorId,
                Details = metadataJson,
                entry.OccurredAt,
            });
    }

    public async Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(
        TenantId tenantId, string entityType, string entityId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuditRow>(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at " +
            "FROM audit_entries WHERE tenant_id = @TenantId AND entity_type = @EntityType AND entity_id = @EntityId " +
            "ORDER BY occurred_at",
            new { TenantId = tenantId.Value, EntityType = entityType, EntityId = entityId });
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<PagedResult<AuditEntry>> SearchAsync(
        TenantId tenantId, AuditQuery query, CancellationToken ct)
    {
        var conditions = new List<string> { "tenant_id = @TenantId" };
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId.Value);

        if (!string.IsNullOrEmpty(query.Action))
        {
            conditions.Add("action = @Action");
            parameters.Add("Action", query.Action);
        }
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            conditions.Add("entity_type = @EntityType");
            parameters.Add("EntityType", query.EntityType);
        }
        if (!string.IsNullOrEmpty(query.PerformedBy))
        {
            conditions.Add("performed_by = @PerformedBy");
            parameters.Add("PerformedBy", query.PerformedBy);
        }
        if (query.From.HasValue)
        {
            conditions.Add("occurred_at >= @From");
            parameters.Add("From", query.From.Value);
        }
        if (query.To.HasValue)
        {
            conditions.Add("occurred_at <= @To");
            parameters.Add("To", query.To.Value);
        }

        var where = string.Join(" AND ", conditions);
        var offset = (query.Page - 1) * query.PageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM audit_entries WHERE {where}", parameters);

        parameters.Add("Limit", query.PageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<AuditRow>(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at " +
            $"FROM audit_entries WHERE {where} ORDER BY occurred_at DESC LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToEntry()).ToList();
        return new PagedResult<AuditEntry>(items, total, query.Page, query.PageSize);
    }

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM audit_entries WHERE tenant_id = @TenantId AND occurred_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }

    private sealed class AuditRow
    {
        public string entry_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string action { get; init; } = null!;
        public string? entity_type { get; init; }
        public string? entity_id { get; init; }
        public string? performed_by { get; init; }
        public string? details { get; init; }
        public DateTime occurred_at { get; init; }

        public AuditEntry ToEntry()
        {
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(details))
            {
                metadata = JsonSerializer.Deserialize(details, PostgresJson.Ctx.IReadOnlyDictionaryStringString);
            }

            return new AuditEntry
            {
                EntryId = EntityId.From(entry_id),
                TenantId = new TenantId(tenant_id),
                Action = action,
                ActorId = performed_by ?? "system",
                ActorType = performed_by is null ? "system" : "user",
                TargetType = entity_type,
                TargetId = entity_id,
                Metadata = metadata,
                OccurredAt = occurred_at,
            };
        }
    }
}
