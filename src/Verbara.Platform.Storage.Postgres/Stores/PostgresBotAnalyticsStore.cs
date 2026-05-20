using Npgsql;
using Verbara.Platform.Bot;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresBotAnalyticsStore : IBotAnalyticsStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBotAnalyticsStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task RecordEventAsync(TenantId tenantId, BotAnalyticsRecord record, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO bot_analytics (tenant_id, event_type, bot_id, conversation_id, turn_count, handoff_reason, created_at) " +
            "VALUES (@TenantId, @EventType, @BotId, @ConversationId, @TurnCount, @HandoffReason, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("EventType", record.EventType));
                p.Add(new NpgsqlParameter("BotId", record.BotId));
                p.Add(new NpgsqlParameter("ConversationId", record.ConversationId));
                p.Add(new NpgsqlParameter("TurnCount", record.TurnCount));
                p.Add(new NpgsqlParameter("HandoffReason", (object?)record.HandoffReason ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedAt", record.CreatedAt));
            },
            ct);
    }

    public async Task<BotAnalyticsSummary> GetSummaryAsync(
        TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT event_type, turn_count FROM bot_analytics " +
            "WHERE tenant_id = @TenantId AND created_at >= @From AND created_at <= @To",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("From", periodStart)); p.Add(new NpgsqlParameter("To", periodEnd)); },
            BotAnalyticsRow.Map, ct);

        var started = rows.Count(r => r.EventType == nameof(BotAnalyticsEventType.ConversationStarted));
        var handedOff = rows.Count(r => r.EventType is nameof(BotAnalyticsEventType.HandedOff) or nameof(BotAnalyticsEventType.MaxTurnsReached));
        var resolved = rows.Count(r => r.EventType == nameof(BotAnalyticsEventType.Resolved));
        var failed = rows.Count(r => r.EventType == nameof(BotAnalyticsEventType.Failed));
        var total = started > 0 ? started : (handedOff + resolved + failed);

        var avgTurns = rows.Count > 0 ? rows.Average(r => r.TurnCount) : 0.0;

        return new BotAnalyticsSummary
        {
            TotalConversations = total,
            HandedOff = handedOff,
            Resolved = resolved,
            Failed = failed,
            HandoffRate = total > 0 ? (double)handedOff / total : 0,
            ResolutionRate = total > 0 ? (double)resolved / total : 0,
            AvgTurns = Math.Round(avgTurns, 1),
            FailureRate = total > 0 ? (double)failed / total : 0,
        };
    }

    private sealed class BotAnalyticsRow
    {
        public string EventType { get; init; } = "";
        public int TurnCount { get; init; }

        public static BotAnalyticsRow Map(NpgsqlDataReader r) => new()
        {
            EventType = r.GetString("event_type"),
            TurnCount = r.GetInt32("turn_count"),
        };
    }
}
