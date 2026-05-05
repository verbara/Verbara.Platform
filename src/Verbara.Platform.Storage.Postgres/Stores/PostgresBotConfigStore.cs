using Dapper;
using Npgsql;
using Verbara.Platform.Bot;
using Verbara.Platform.Core;

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BotRow>(
            $"SELECT {SelectColumns} FROM bot_configurations WHERE tenant_id = @TenantId AND bot_id = @BotId",
            new { TenantId = tenantId.Value, BotId = botId.Value });
        return row?.ToConfig();
    }

    public async Task<BotConfiguration?> GetDefaultAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<BotRow>(
            $"SELECT {SelectColumns} FROM bot_configurations " +
            "WHERE tenant_id = @TenantId AND is_active = true LIMIT 1",
            new { TenantId = tenantId.Value });
        return row?.ToConfig();
    }

    public async Task<IReadOnlyList<BotConfiguration>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BotRow>(
            $"SELECT {SelectColumns} FROM bot_configurations " +
            "WHERE tenant_id = @TenantId ORDER BY created_at ASC",
            new { TenantId = tenantId.Value });
        return rows.Select(r => r.ToConfig()).ToArray();
    }

    public async Task SaveAsync(BotConfiguration config, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var createdAt = config.CreatedAt == default ? DateTimeOffset.UtcNow : config.CreatedAt;
        await conn.ExecuteAsync(
            "INSERT INTO bot_configurations (bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
            "confidence_threshold, max_turns_before_handoff, is_active, created_at) " +
            "VALUES (@BotId, @TenantId, @Name, @DefaultFlowId, @FallbackQueueId, " +
            "@ConfidenceThreshold, @MaxTurnsBeforeHandoff, @IsActive, @CreatedAt) " +
            "ON CONFLICT (tenant_id, bot_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, default_flow_id = EXCLUDED.default_flow_id, " +
            "  fallback_queue_id = EXCLUDED.fallback_queue_id, confidence_threshold = EXCLUDED.confidence_threshold, " +
            "  max_turns_before_handoff = EXCLUDED.max_turns_before_handoff, is_active = EXCLUDED.is_active",
            new
            {
                BotId = config.BotId.Value,
                TenantId = config.TenantId.Value,
                config.Name,
                DefaultFlowId = config.DefaultFlowId?.Value,
                FallbackQueueId = config.FallbackQueueId?.Value,
                config.ConfidenceThreshold,
                config.MaxTurnsBeforeHandoff,
                config.IsActive,
                CreatedAt = createdAt.UtcDateTime,
            });
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(
            "DELETE FROM bot_configurations WHERE tenant_id = @TenantId AND bot_id = @BotId",
            new { TenantId = tenantId.Value, BotId = botId.Value });
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
