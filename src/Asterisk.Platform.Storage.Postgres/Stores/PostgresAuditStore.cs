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
            "performed_by, details, occurred_at, impersonator_id) " +
            "VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId, " +
            "@PerformedBy, @Details::jsonb, @OccurredAt, @ImpersonatorId)",
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
                ImpersonatorId = entry.ImpersonatorId,
            });
    }

    public async Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(
        TenantId tenantId, string entityType, string entityId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuditRow>(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id " +
            "FROM audit_entries WHERE tenant_id = @TenantId AND entity_type = @EntityType AND entity_id = @EntityId " +
            "ORDER BY occurred_at",
            new { TenantId = tenantId.Value, EntityType = entityType, EntityId = entityId });
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<PagedResult<AuditEntry>> SearchAsync(
        TenantId tenantId, AuditQuery query, CancellationToken ct)
    {
        var (where, parameters) = BuildWhereClause(tenantId, query);
        var offset = (query.Page - 1) * query.PageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition($"SELECT COUNT(*) FROM audit_entries WHERE {where}", parameters, cancellationToken: ct));

        parameters.Add("Limit", query.PageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<AuditRow>(
            new CommandDefinition(
                "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id " +
                $"FROM audit_entries WHERE {where} ORDER BY occurred_at DESC LIMIT @Limit OFFSET @Offset",
                parameters,
                cancellationToken: ct));

        var items = rows.Select(r => r.ToEntry()).ToList();
        return new PagedResult<AuditEntry>(items, total, query.Page, query.PageSize);
    }

    public async IAsyncEnumerable<AuditEntry> StreamAsync(
        TenantId tenantId,
        AuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Page through results in batches so the export endpoint never buffers
        // the full result set in memory. Batch size 500 balances round-trips vs
        // peak memory; the writer flushes between batches.
        const int batchSize = 500;
        var (where, parameters) = BuildWhereClause(tenantId, query);
        parameters.Add("Limit", batchSize);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var lastOccurredAt = (DateTimeOffset?)null;
        var lastEntryId = (string?)null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string sql;
            if (lastOccurredAt is null)
            {
                sql =
                    "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id " +
                    $"FROM audit_entries WHERE {where} ORDER BY occurred_at DESC, entry_id DESC LIMIT @Limit";
            }
            else
            {
                // Keyset pagination on (occurred_at DESC, entry_id DESC) — avoids
                // OFFSET cost growing with result size.
                parameters.Add("CursorOccurredAt", lastOccurredAt.Value);
                parameters.Add("CursorEntryId", lastEntryId);
                sql =
                    "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id " +
                    $"FROM audit_entries WHERE {where} AND " +
                    "(occurred_at < @CursorOccurredAt OR (occurred_at = @CursorOccurredAt AND entry_id < @CursorEntryId)) " +
                    "ORDER BY occurred_at DESC, entry_id DESC LIMIT @Limit";
            }

            var rows = (await conn.QueryAsync<AuditRow>(
                new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

            if (rows.Count == 0)
                yield break;

            foreach (var row in rows)
            {
                yield return row.ToEntry();
            }

            if (rows.Count < batchSize)
                yield break;

            var last = rows[^1];
            lastOccurredAt = last.occurred_at;
            lastEntryId = last.entry_id;
        }
    }

    private static (string Where, DynamicParameters Parameters) BuildWhereClause(TenantId tenantId, AuditQuery query)
    {
        var conditions = new List<string> { "tenant_id = @TenantId" };
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId.Value);

        if (!string.IsNullOrEmpty(query.Action))
        {
            conditions.Add("action = @Action");
            parameters.Add("Action", query.Action);
        }
        if (!string.IsNullOrEmpty(query.ActionPrefix))
        {
            conditions.Add("action LIKE @ActionPrefix");
            // Escape any LIKE wildcards in caller-supplied prefix so "mfa.admin._"
            // doesn't accidentally widen the match.
            var escaped = query.ActionPrefix
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add("ActionPrefix", escaped + "%");
        }
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            conditions.Add("entity_type = @EntityType");
            parameters.Add("EntityType", query.EntityType);
        }
        if (!string.IsNullOrEmpty(query.TargetType))
        {
            conditions.Add("entity_type = @TargetType");
            parameters.Add("TargetType", query.TargetType);
        }
        if (!string.IsNullOrEmpty(query.TargetId))
        {
            conditions.Add("entity_id = @TargetId");
            parameters.Add("TargetId", query.TargetId);
        }
        if (!string.IsNullOrEmpty(query.PerformedBy))
        {
            conditions.Add("performed_by = @PerformedBy");
            parameters.Add("PerformedBy", query.PerformedBy);
        }
        if (!string.IsNullOrEmpty(query.ActorId))
        {
            conditions.Add("performed_by = @ActorId");
            parameters.Add("ActorId", query.ActorId);
        }
        if (!string.IsNullOrEmpty(query.ActorSearch))
        {
            conditions.Add("performed_by ILIKE @ActorSearch");
            var escaped = query.ActorSearch
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add("ActorSearch", "%" + escaped + "%");
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

        return (string.Join(" AND ", conditions), parameters);
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
        public string? impersonator_id { get; init; }

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
                ImpersonatorId = impersonator_id,
            };
        }
    }
}
