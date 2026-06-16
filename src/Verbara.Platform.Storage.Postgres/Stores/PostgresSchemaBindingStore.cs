using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresSchemaBindingStore : ISchemaBindingStore
{
    private const string SelectColumns =
        "tenant_id, binding_id, scope, scope_ref, schema_id, subtree_root_node_id, priority, ai_config_override, created_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSchemaBindingStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<SchemaBinding?> GetByIdAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_bindings " +
            "WHERE tenant_id = @TenantId AND binding_id = @BindingId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("BindingId", bindingId.Value));
            },
            BindingRow.Map, ct);
        return row?.ToBinding();
    }

    public async Task<IReadOnlyList<SchemaBinding>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM typification_bindings WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            BindingRow.Map, ct);
        return rows.Select(r => r.ToBinding()).ToList();
    }

    public async Task<IReadOnlyList<SchemaBinding>> ListByScopeAsync(TenantId tenantId, BindingScope scope, string? scopeRef, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM typification_bindings " +
            "WHERE tenant_id = @TenantId AND scope = @Scope " +
            "AND (scope_ref = @ScopeRef OR (@ScopeRef IS NULL AND scope_ref IS NULL))",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Scope", scope.ToString()));
                p.Add(new NpgsqlParameter("ScopeRef", NpgsqlDbType.Text) { Value = (object?)scopeRef ?? DBNull.Value });
            },
            BindingRow.Map, ct);
        return rows.Select(r => r.ToBinding()).ToList();
    }

    public async Task SaveAsync(SchemaBinding binding, CancellationToken ct)
    {
        // E1 — serialize the optional AI config override to JSONB (null → DBNull). The
        // ::jsonb cast on the text param keeps Postgres typing the column correctly; the
        // nullable param sets an explicit NpgsqlDbType.Text so a null never trips 42P08.
        var aiConfigOverrideJson = binding.AiConfigOverride is { } ovr
            ? JsonSerializer.Serialize(ovr, PostgresJson.Ctx.TypificationAiConfig)
            : null;

        await _dataSource.ExecuteAsync(
            "INSERT INTO typification_bindings " +
            "(tenant_id, binding_id, scope, scope_ref, schema_id, subtree_root_node_id, priority, ai_config_override, created_at) " +
            "VALUES (@TenantId, @BindingId, @Scope, @ScopeRef, @SchemaId, @SubTreeRootNodeId, @Priority, @AiConfigOverride::jsonb, @CreatedAt) " +
            // created_at is intentionally NOT updated on conflict: an UPDATE must
            // preserve the original creation instant (the resolution tiebreak anchor).
            "ON CONFLICT (tenant_id, binding_id) DO UPDATE SET " +
            "  scope = EXCLUDED.scope, scope_ref = EXCLUDED.scope_ref, schema_id = EXCLUDED.schema_id, " +
            "  subtree_root_node_id = EXCLUDED.subtree_root_node_id, priority = EXCLUDED.priority, " +
            "  ai_config_override = EXCLUDED.ai_config_override",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", binding.TenantId.Value));
                p.Add(new NpgsqlParameter("BindingId", binding.BindingId.Value));
                p.Add(new NpgsqlParameter("Scope", binding.Scope.ToString()));
                p.Add(new NpgsqlParameter("ScopeRef", NpgsqlDbType.Text) { Value = (object?)binding.ScopeRef ?? DBNull.Value });
                p.Add(new NpgsqlParameter("SchemaId", binding.SchemaId.Value));
                p.Add(new NpgsqlParameter("SubTreeRootNodeId", NpgsqlDbType.Text) { Value = (object?)binding.SubTreeRootNodeId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Priority", binding.Priority));
                p.Add(new NpgsqlParameter("AiConfigOverride", NpgsqlDbType.Text) { Value = (object?)aiConfigOverrideJson ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", binding.CreatedAt));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM typification_bindings WHERE tenant_id = @TenantId AND binding_id = @BindingId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("BindingId", bindingId.Value));
            },
            ct);
    }

    private sealed class BindingRow
    {
        public string tenant_id { get; init; } = null!;
        public string binding_id { get; init; } = null!;
        public string scope { get; init; } = null!;
        public string? scope_ref { get; init; }
        public string schema_id { get; init; } = null!;
        public string? subtree_root_node_id { get; init; }
        public int priority { get; init; }
        public string? ai_config_override { get; init; }
        public DateTimeOffset created_at { get; init; }

        public static BindingRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            binding_id = r.GetString("binding_id"),
            scope = r.GetString("scope"),
            scope_ref = r.GetStringOrNull("scope_ref"),
            schema_id = r.GetString("schema_id"),
            subtree_root_node_id = r.GetStringOrNull("subtree_root_node_id"),
            priority = r.GetInt32("priority"),
            ai_config_override = r.GetStringOrNull("ai_config_override"),
            created_at = r.GetDateTimeOffset("created_at"),
        };

        public SchemaBinding ToBinding() => new()
        {
            TenantId = new TenantId(tenant_id),
            BindingId = EntityId.From(binding_id),
            Scope = Enum.Parse<BindingScope>(scope),
            ScopeRef = scope_ref,
            SchemaId = EntityId.From(schema_id),
            SubTreeRootNodeId = subtree_root_node_id is { } s ? EntityId.From(s) : null,
            Priority = priority,
            // E1 — deserialize the optional override (null-tolerant). Mirror the schema store's
            // defense-in-depth: STJ source-gen does NOT honor the record's `PiiPolicy = DenyAll`
            // initializer, so coalesce a missing nested PiiPolicy to the safe DenyAll default.
            AiConfigOverride = ai_config_override is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, PostgresJson.Ctx.TypificationAiConfig) is { } ovr
                    ? ovr with { PiiPolicy = ovr.PiiPolicy ?? PiiPolicy.DenyAll }
                    : null
                : null,
            CreatedAt = created_at,
        };
    }
}
