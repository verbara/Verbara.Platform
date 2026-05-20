using Npgsql;
using Verbara.Platform.Bot;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresBotConfigStore : IBotConfigStore
{
    private const string SelectColumns =
        "bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
        "confidence_threshold, max_turns_before_handoff, is_active, created_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresBotConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<BotConfiguration?> GetByIdAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM bot_configurations WHERE tenant_id = @TenantId AND bot_id = @BotId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("BotId", botId.Value)); },
            BotRow.Map, ct);
        return row?.ToConfig();
    }

    public async Task<BotConfiguration?> GetDefaultAsync(TenantId tenantId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM bot_configurations " +
            "WHERE tenant_id = @TenantId AND is_active = true LIMIT 1",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); },
            BotRow.Map, ct);
        return row?.ToConfig();
    }

    public async Task<IReadOnlyList<BotConfiguration>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM bot_configurations " +
            "WHERE tenant_id = @TenantId ORDER BY created_at ASC",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); },
            BotRow.Map, ct);
        return rows.Select(r => r.ToConfig()).ToArray();
    }

    public async Task SaveAsync(BotConfiguration config, CancellationToken ct)
    {
        var createdAt = config.CreatedAt == default ? DateTimeOffset.UtcNow : config.CreatedAt;
        await _dataSource.ExecuteAsync(
            "INSERT INTO bot_configurations (bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
            "confidence_threshold, max_turns_before_handoff, is_active, created_at) " +
            "VALUES (@BotId, @TenantId, @Name, @DefaultFlowId, @FallbackQueueId, " +
            "@ConfidenceThreshold, @MaxTurnsBeforeHandoff, @IsActive, @CreatedAt) " +
            "ON CONFLICT (tenant_id, bot_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, default_flow_id = EXCLUDED.default_flow_id, " +
            "  fallback_queue_id = EXCLUDED.fallback_queue_id, confidence_threshold = EXCLUDED.confidence_threshold, " +
            "  max_turns_before_handoff = EXCLUDED.max_turns_before_handoff, is_active = EXCLUDED.is_active",
            p =>
            {
                p.Add(new NpgsqlParameter("BotId", config.BotId.Value));
                p.Add(new NpgsqlParameter("TenantId", config.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", config.Name));
                p.Add(new NpgsqlParameter("DefaultFlowId", (object?)config.DefaultFlowId?.Value ?? DBNull.Value));
                p.Add(new NpgsqlParameter("FallbackQueueId", (object?)config.FallbackQueueId?.Value ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ConfidenceThreshold", config.ConfidenceThreshold));
                p.Add(new NpgsqlParameter("MaxTurnsBeforeHandoff", config.MaxTurnsBeforeHandoff));
                p.Add(new NpgsqlParameter("IsActive", config.IsActive));
                p.Add(new NpgsqlParameter("CreatedAt", createdAt.UtcDateTime));
            },
            ct);
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        var affected = await _dataSource.ExecuteAsync(
            "DELETE FROM bot_configurations WHERE tenant_id = @TenantId AND bot_id = @BotId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("BotId", botId.Value)); },
            ct);
        return affected > 0;
    }

    private sealed class BotRow
    {
        public string bot_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string? default_flow_id { get; init; }
        public string? fallback_queue_id { get; init; }
        public double confidence_threshold { get; init; }
        public int max_turns_before_handoff { get; init; }
        public bool is_active { get; init; }
        public DateTime created_at { get; init; }

        public static BotRow Map(NpgsqlDataReader r) => new()
        {
            bot_id = r.GetString("bot_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            default_flow_id = r.GetStringOrNull("default_flow_id"),
            fallback_queue_id = r.GetStringOrNull("fallback_queue_id"),
            confidence_threshold = r.GetDouble("confidence_threshold"),
            max_turns_before_handoff = r.GetInt32("max_turns_before_handoff"),
            is_active = r.GetBoolean("is_active"),
            created_at = r.GetDateTime("created_at"),
        };

        public BotConfiguration ToConfig() => new()
        {
            BotId = EntityId.From(bot_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            DefaultFlowId = default_flow_id != null ? EntityId.From(default_flow_id) : null,
            FallbackQueueId = fallback_queue_id != null ? EntityId.From(fallback_queue_id) : null,
            ConfidenceThreshold = confidence_threshold,
            MaxTurnsBeforeHandoff = max_turns_before_handoff,
            IsActive = is_active,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(created_at, DateTimeKind.Utc)),
        };
    }
}
