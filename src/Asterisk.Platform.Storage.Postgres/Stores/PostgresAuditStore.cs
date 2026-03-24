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
        var detailsJson = entry.Details != null
            ? JsonSerializer.Serialize(entry.Details, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
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
                entry.EntityType,
                entry.EntityId,
                entry.PerformedBy,
                Details = detailsJson,
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

    private sealed record AuditRow(
        string entry_id,
        string tenant_id,
        string action,
        string entity_type,
        string entity_id,
        string? performed_by,
        string? details,
        DateTimeOffset occurred_at)
    {
        public AuditEntry ToEntry()
        {
            IReadOnlyDictionary<string, string>? detailsDict = null;
            if (!string.IsNullOrEmpty(details))
            {
                detailsDict = JsonSerializer.Deserialize(details, PostgresJson.Ctx.IReadOnlyDictionaryStringString);
            }

            return new AuditEntry
            {
                EntryId = EntityId.From(entry_id),
                TenantId = new TenantId(tenant_id),
                Action = action,
                EntityType = entity_type,
                EntityId = entity_id,
                PerformedBy = performed_by,
                Details = detailsDict,
                OccurredAt = occurred_at,
            };
        }
    }
}
