using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

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

        await _dataSource.ExecuteAsync(
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, " +
            "performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash) " +
            "VALUES (@EntryId, @TenantId, @Action, @EntityType, @EntityId, " +
            "@PerformedBy, @Details::jsonb, @OccurredAt, @ImpersonatorId, " +
            "@Category, @Severity, @ActorType, @BeforeJson::jsonb, @AfterJson::jsonb, @IntegrityHash)",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", entry.EntryId.Value));
                p.Add(new NpgsqlParameter("TenantId", entry.TenantId.Value));
                p.Add(new NpgsqlParameter("Action", entry.Action));
                p.Add(new NpgsqlParameter("EntityType", (object?)entry.TargetType ?? DBNull.Value));
                p.Add(new NpgsqlParameter("EntityId", (object?)entry.TargetId ?? DBNull.Value));
                p.Add(new NpgsqlParameter("PerformedBy", (object?)entry.ActorId ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Details", (object?)metadataJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("OccurredAt", entry.OccurredAt));
                p.Add(new NpgsqlParameter("ImpersonatorId", (object?)entry.ImpersonatorId ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Category", (object?)entry.Category ?? DBNull.Value));
                p.Add(new NpgsqlParameter("Severity", (object?)entry.Severity ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ActorType", (object?)entry.ActorType ?? DBNull.Value));
                p.Add(new NpgsqlParameter("BeforeJson", (object?)beforeJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("AfterJson", (object?)afterJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("IntegrityHash", (object?)entry.IntegrityHash ?? DBNull.Value));
            },
            ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(
        TenantId tenantId, string entityType, string entityId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash " +
            "FROM audit_entries WHERE tenant_id = @TenantId AND entity_type = @EntityType AND entity_id = @EntityId " +
            "ORDER BY occurred_at",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("EntityType", entityType));
                p.Add(new NpgsqlParameter("EntityId", entityId));
            },
            AuditRow.Map, ct);
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<PagedResult<AuditEntry>> SearchAsync(
        TenantId tenantId, AuditQuery query, CancellationToken ct)
    {
        var (where, binders) = BuildWhereClause(tenantId, query);
        var offset = (query.Page - 1) * query.PageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            $"SELECT COUNT(*) FROM audit_entries WHERE {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
            "category, severity, actor_type, before_json, after_json, integrity_hash " +
            $"FROM audit_entries WHERE {where} ORDER BY occurred_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", query.PageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            AuditRow.Map, ct);

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
        var (where, binders) = BuildWhereClause(tenantId, query);

        var lastOccurredAt = (DateTimeOffset?)null;
        var lastEntryId = (string?)null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string sql;
            // Capture the cursor for this iteration so the bind delegate (which
            // runs when the command executes) closes over the current value.
            var cursorOccurredAt = lastOccurredAt;
            var cursorEntryId = lastEntryId;

            if (cursorOccurredAt is null)
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
                sql =
                    "SELECT entry_id, tenant_id, action, entity_type, entity_id, performed_by, details, occurred_at, impersonator_id, " +
                    "category, severity, actor_type, before_json, after_json, integrity_hash " +
                    $"FROM audit_entries WHERE {where} AND " +
                    "(occurred_at < @CursorOccurredAt OR (occurred_at = @CursorOccurredAt AND entry_id < @CursorEntryId)) " +
                    "ORDER BY occurred_at DESC, entry_id DESC LIMIT @Limit";
            }

            var rows = await _dataSource.QueryListAsync(
                sql,
                p =>
                {
                    foreach (var b in binders) b(p);
                    p.Add(new NpgsqlParameter("Limit", batchSize));
                    if (cursorOccurredAt is not null)
                    {
                        p.Add(new NpgsqlParameter("CursorOccurredAt", cursorOccurredAt.Value));
                        p.Add(new NpgsqlParameter("CursorEntryId", (object?)cursorEntryId ?? DBNull.Value));
                    }
                },
                AuditRow.Map, ct);

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

    private static (string Where, List<Action<NpgsqlParameterCollection>> Binders) BuildWhereClause(
        TenantId tenantId, AuditQuery query)
    {
        var conditions = new List<string> { "tenant_id = @TenantId" };
        var binders = new List<Action<NpgsqlParameterCollection>>
        {
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
        };

        if (!string.IsNullOrEmpty(query.Action))
        {
            conditions.Add("action = @Action");
            binders.Add(p => p.Add(new NpgsqlParameter("Action", query.Action)));
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
            binders.Add(p => p.Add(new NpgsqlParameter("ActionPrefix", escaped + "%")));
        }
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            conditions.Add("entity_type = @EntityType");
            binders.Add(p => p.Add(new NpgsqlParameter("EntityType", query.EntityType)));
        }
        if (!string.IsNullOrEmpty(query.TargetType))
        {
            conditions.Add("entity_type = @TargetType");
            binders.Add(p => p.Add(new NpgsqlParameter("TargetType", query.TargetType)));
        }
        if (!string.IsNullOrEmpty(query.TargetId))
        {
            conditions.Add("entity_id = @TargetId");
            binders.Add(p => p.Add(new NpgsqlParameter("TargetId", query.TargetId)));
        }
        if (!string.IsNullOrEmpty(query.PerformedBy))
        {
            conditions.Add("performed_by = @PerformedBy");
            binders.Add(p => p.Add(new NpgsqlParameter("PerformedBy", query.PerformedBy)));
        }
        if (!string.IsNullOrEmpty(query.ActorId))
        {
            conditions.Add("performed_by = @ActorId");
            binders.Add(p => p.Add(new NpgsqlParameter("ActorId", query.ActorId)));
        }
        if (!string.IsNullOrEmpty(query.ActorSearch))
        {
            conditions.Add("performed_by ILIKE @ActorSearch");
            var escaped = query.ActorSearch
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            binders.Add(p => p.Add(new NpgsqlParameter("ActorSearch", "%" + escaped + "%")));
        }
        if (!string.IsNullOrEmpty(query.Category))
        {
            // R5.3 A.1 — typed column lookup. Backed by idx_audit_category
            // (tenant_id, category, occurred_at DESC).
            conditions.Add("category = @Category");
            binders.Add(p => p.Add(new NpgsqlParameter("Category", query.Category)));
        }
        if (!string.IsNullOrEmpty(query.Severity))
        {
            // R5.3 A.1 — typed column lookup. Backed by idx_audit_severity
            // (tenant_id, severity, occurred_at DESC).
            conditions.Add("severity = @Severity");
            binders.Add(p => p.Add(new NpgsqlParameter("Severity", query.Severity)));
        }
        if (query.From.HasValue)
        {
            conditions.Add("occurred_at >= @From");
            binders.Add(p => p.Add(new NpgsqlParameter("From", query.From.Value)));
        }
        if (query.To.HasValue)
        {
            conditions.Add("occurred_at <= @To");
            binders.Add(p => p.Add(new NpgsqlParameter("To", query.To.Value)));
        }

        return (string.Join(" AND ", conditions), binders);
    }

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM audit_entries WHERE tenant_id = @TenantId AND occurred_at < @Cutoff",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Cutoff", cutoff));
            },
            ct);
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

        public static AuditRow Map(NpgsqlDataReader r) => new()
        {
            entry_id = r.GetString("entry_id"),
            tenant_id = r.GetString("tenant_id"),
            action = r.GetString("action"),
            entity_type = r.GetStringOrNull("entity_type"),
            entity_id = r.GetStringOrNull("entity_id"),
            performed_by = r.GetStringOrNull("performed_by"),
            details = r.GetStringOrNull("details"),
            occurred_at = r.GetDateTime("occurred_at"),
            impersonator_id = r.GetStringOrNull("impersonator_id"),
            category = r.GetStringOrNull("category"),
            severity = r.GetStringOrNull("severity"),
            actor_type = r.GetStringOrNull("actor_type"),
            before_json = r.GetStringOrNull("before_json"),
            after_json = r.GetStringOrNull("after_json"),
            integrity_hash = r.GetStringOrNull("integrity_hash"),
        };

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
