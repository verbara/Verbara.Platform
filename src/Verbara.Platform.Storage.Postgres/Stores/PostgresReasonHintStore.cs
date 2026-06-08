using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresReasonHintStore : IReasonHintStore
{
    private const string SelectColumns =
        "tenant_id, hint_id, scope, scope_ref, reason_path, priority, is_active";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresReasonHintStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ReasonHint?> GetByIdAsync(TenantId tenantId, EntityId hintId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM reason_hints " +
            "WHERE tenant_id = @TenantId AND hint_id = @HintId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("HintId", hintId.Value));
            },
            HintRow.Map, ct);
        return row?.ToHint();
    }

    public async Task<IReadOnlyList<ReasonHint>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM reason_hints WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            HintRow.Map, ct);
        return rows.Select(r => r.ToHint()).ToList();
    }

    public async Task<IReadOnlyList<ReasonHint>> ListByScopeAsync(TenantId tenantId, ReasonHintScope scope, string scopeRef, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM reason_hints " +
            "WHERE tenant_id = @TenantId AND scope = @Scope AND scope_ref = @ScopeRef",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Scope", scope.ToString()));
                p.Add(new NpgsqlParameter("ScopeRef", scopeRef));
            },
            HintRow.Map, ct);
        return rows.Select(r => r.ToHint()).ToList();
    }

    public async Task SaveAsync(ReasonHint hint, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO reason_hints " +
            "(tenant_id, hint_id, scope, scope_ref, reason_path, priority, is_active) " +
            "VALUES (@TenantId, @HintId, @Scope, @ScopeRef, @ReasonPath, @Priority, @IsActive) " +
            "ON CONFLICT (tenant_id, hint_id) DO UPDATE SET " +
            "  scope = EXCLUDED.scope, scope_ref = EXCLUDED.scope_ref, reason_path = EXCLUDED.reason_path, " +
            "  priority = EXCLUDED.priority, is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", hint.TenantId.Value));
                p.Add(new NpgsqlParameter("HintId", hint.HintId.Value));
                p.Add(new NpgsqlParameter("Scope", hint.Scope.ToString()));
                p.Add(new NpgsqlParameter("ScopeRef", hint.ScopeRef));
                p.Add(new NpgsqlParameter("ReasonPath", hint.ReasonPath));
                p.Add(new NpgsqlParameter("Priority", hint.Priority));
                p.Add(new NpgsqlParameter("IsActive", hint.IsActive));
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId hintId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM reason_hints WHERE tenant_id = @TenantId AND hint_id = @HintId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("HintId", hintId.Value));
            },
            ct);
    }

    private sealed class HintRow
    {
        public string tenant_id { get; init; } = null!;
        public string hint_id { get; init; } = null!;
        public string scope { get; init; } = null!;
        public string scope_ref { get; init; } = null!;
        public string reason_path { get; init; } = null!;
        public int priority { get; init; }
        public bool is_active { get; init; }

        public static HintRow Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            hint_id = r.GetString("hint_id"),
            scope = r.GetString("scope"),
            scope_ref = r.GetString("scope_ref"),
            reason_path = r.GetString("reason_path"),
            priority = r.GetInt32("priority"),
            is_active = r.GetBoolean("is_active"),
        };

        public ReasonHint ToHint() => new()
        {
            TenantId = new TenantId(tenant_id),
            HintId = EntityId.From(hint_id),
            Scope = Enum.Parse<ReasonHintScope>(scope),
            ScopeRef = scope_ref,
            ReasonPath = reason_path,
            Priority = priority,
            IsActive = is_active,
        };
    }
}
