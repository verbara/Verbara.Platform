using Dapper;
using Npgsql;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresBotConfigStore : IBotConfigStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBotConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<BotConfiguration?> GetByIdAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BotRow>(
            "SELECT bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
            "confidence_threshold, max_turns_before_handoff, is_active " +
            "FROM bot_configurations WHERE tenant_id = @TenantId AND bot_id = @BotId",
            new { TenantId = tenantId.Value, BotId = botId.Value });
        return row?.ToConfig();
    }

    public async Task<BotConfiguration?> GetDefaultAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<BotRow>(
            "SELECT bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
            "confidence_threshold, max_turns_before_handoff, is_active " +
            "FROM bot_configurations WHERE tenant_id = @TenantId AND is_active = true LIMIT 1",
            new { TenantId = tenantId.Value });
        return row?.ToConfig();
    }

    public async Task SaveAsync(BotConfiguration config, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO bot_configurations (bot_id, tenant_id, name, default_flow_id, fallback_queue_id, " +
            "confidence_threshold, max_turns_before_handoff, is_active) " +
            "VALUES (@BotId, @TenantId, @Name, @DefaultFlowId, @FallbackQueueId, " +
            "@ConfidenceThreshold, @MaxTurnsBeforeHandoff, @IsActive) " +
            "ON CONFLICT (tenant_id, bot_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, default_flow_id = EXCLUDED.default_flow_id, " +
            "  fallback_queue_id = EXCLUDED.fallback_queue_id, confidence_threshold = EXCLUDED.confidence_threshold, " +
            "  max_turns_before_handoff = EXCLUDED.max_turns_before_handoff, is_active = EXCLUDED.is_active",
            new
            {
                BotId = config.BotId.Value,
                TenantId = config.TenantId.Value,
                config.Name,
                DefaultFlowId = config.DefaultFlowId.Value,
                FallbackQueueId = config.FallbackQueueId?.Value,
                config.ConfidenceThreshold,
                config.MaxTurnsBeforeHandoff,
                config.IsActive,
            });
    }

    private sealed record BotRow(
        string bot_id,
        string tenant_id,
        string name,
        string default_flow_id,
        string? fallback_queue_id,
        double confidence_threshold,
        int max_turns_before_handoff,
        bool is_active)
    {
        public BotConfiguration ToConfig() => new()
        {
            BotId = EntityId.From(bot_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            DefaultFlowId = EntityId.From(default_flow_id),
            FallbackQueueId = fallback_queue_id != null ? EntityId.From(fallback_queue_id) : null,
            ConfidenceThreshold = confidence_threshold,
            MaxTurnsBeforeHandoff = max_turns_before_handoff,
            IsActive = is_active,
        };
    }
}
