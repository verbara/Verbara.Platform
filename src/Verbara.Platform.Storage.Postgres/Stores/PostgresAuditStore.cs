using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAuditStore : IAuditStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuditStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AuditEntry entry, CancellationToken ct)
    {
        var metadataJson = entry.Metadata != null
            ? JsonSerializer.Serialize(entry.Metadata, PostgresJson.Ctx.IReadOnlyDictionaryStringString)
            : null;

        var beforeJson = SerializeChange(entry.Changes?.Before);
        var afterJson = SerializeChange(entry.Changes?.After);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash) " +
            "VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId, " +
            "@PerformedBy, @Details::jsonb, @OccurredAt, @ImpersonatorId, " +
            "@Category, @Severity, @ActorType, @BeforeJson::jsonb, @AfterJson::jsonb, @IntegrityHash)",
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
                Category = entry.Category,
                Severity = entry.Severity,
                ActorType = entry.ActorType,
                BeforeJson = beforeJson,
                AfterJson = afterJson,
                IntegrityHash = entry.IntegrityHash,
            });
    }

    public async Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(
        TenantId tenantId, string entityType, string entityId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuditRow>(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash " +
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
                "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
                "category, severity, actor_type, before_json, after_json, integrity_hash " +
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
                    "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
                    "category, severity, actor_type, before_json, after_json, integrity_hash " +
                    $"FROM audit_entries WHERE {where} ORDER BY occurred_at DESC, entry_id DESC LIMIT @Limit";
            }
            else
            {
                // Keyset pagination on (occurred_at DESC, entry_id DESC) — avoids
                // OFFSET cost growing with result size.
                parameters.Add("CursorOccurredAt", lastOccurredAt.Value);
                parameters.Add("CursorEntryId", lastEntryId);
                sql =
                    "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
                    "category, severity, actor_type, before_json, after_json, integrity_hash " +
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
        if (!string.IsNullOrEmpty(query.Category))
        {
            // R5.3 A.1 — typed column lookup. Backed by idx_audit_category
            // (tenant_id, category, occurred_at DESC).
            conditions.Add("category = @Category");
            parameters.Add("Category", query.Category);
        }
        if (!string.IsNullOrEmpty(query.Severity))
        {
            // R5.3 A.1 — typed column lookup. Backed by idx_audit_severity
            // (tenant_id, severity, occurred_at DESC).
            conditions.Add("severity = @Severity");
            parameters.Add("Severity", query.Severity);
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

    /// <summary>
    /// Serialises an arbitrary <c>object?</c> from <see cref="AuditChanges"/>
    /// (Before / After) into a JSON string suitable for the <c>jsonb</c>
    /// column. Returns <c>null</c> when the payload is null or already
    /// serialised as a JSON string.
    /// </summary>
    /// <remarks>
    /// AOT note: <see cref="AuditChanges"/> is contractually <c>object?</c>
    /// because audit call sites pass arbitrary anonymous types and DTOs.
    /// We forward to <see cref="JsonSerializer.Serialize(object?, Type, JsonSerializerOptions?)"/>
    /// with the runtime type. The trim/AOT analyzers warn here because the
    /// type is not statically known; the suppression is justified: this is
    /// the documented audit-trail boundary, and the prior implementation
    /// silently lost 100 % of <c>Changes</c> — any structured persistence is
    /// strictly better. Pre-serialised strings (already JSON) are passed
    /// through verbatim.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Audit-trail boundary — call sites pass arbitrary object?; serialization fallback documented in ADR-0006.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Audit-trail boundary — call sites pass arbitrary object?; serialization fallback documented in ADR-0006.")]
    private static string? SerializeChange(object? value)
    {
        if (value is null)
            return null;

        // Pre-serialised JSON string passes through unchanged.
        if (value is string s)
            return s;

        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), SerializeChangeOptions);
        }
        catch (NotSupportedException)
        {
            // Type lacks a serializer in trimmed/AOT image — fall back to
            // ToString() so the audit row still carries a human-readable
            // breadcrumb instead of silently dropping the change.
            return JsonSerializer.Serialize(value.ToString(), PostgresJson.Ctx.String);
        }
    }

    private static readonly JsonSerializerOptions SerializeChangeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

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
        public string? category { get; init; }
        public string? severity { get; init; }
        public string? actor_type { get; init; }
        public string? before_json { get; init; }
        public string? after_json { get; init; }
        public string? integrity_hash { get; init; }

        public AuditEntry ToEntry()
        {
            IReadOnlyDictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(details))
            {
                metadata = JsonSerializer.Deserialize(details, PostgresJson.Ctx.IReadOnlyDictionaryStringString);
            }

            // Hydrate Changes from the typed before_json / after_json columns
            // (R5.3 A.1 / ADR-0006). Pre-R5.3 rows have NULL for both columns
            // and surface as `Changes = null`. Post-R5.3 rows populate either
            // or both via SerializeChange() — we round-trip through JsonElement
            // so the consumer sees a queryable structure rather than an
            // opaque string.
            AuditChanges? changes = null;
            if (!string.IsNullOrEmpty(before_json) || !string.IsNullOrEmpty(after_json))
            {
                changes = new AuditChanges(
                    Before: ParseJsonElement(before_json),
                    After: ParseJsonElement(after_json));
            }

            return new AuditEntry
            {
                EntryId = EntityId.From(entry_id),
                TenantId = new TenantId(tenant_id),
                Action = action,
                Category = category ?? "config",
                Severity = severity ?? "info",
                ActorId = performed_by ?? "system",
                ActorType = actor_type ?? (performed_by is null ? "system" : "user"),
                TargetType = entity_type,
                TargetId = entity_id,
                Metadata = metadata,
                Changes = changes,
                IntegrityHash = integrity_hash,
                OccurredAt = occurred_at,
                ImpersonatorId = impersonator_id,
            };
        }

        private static JsonElement? ParseJsonElement(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            // JsonDocument is AOT-safe (no reflection); the parsed element is
            // copied via Clone() so the underlying document can be disposed.
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}
