using Npgsql;
using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTimerStore : ITimerStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTimerStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(ScheduledTimer timer, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO scheduled_timers (timer_id, tenant_id, conversation_id, callback_rule_id, fire_at, is_fired, created_at) " +
            "VALUES (@TimerId, @TenantId, @ConversationId, @CallbackRuleId, @FireAt, @IsFired, @CreatedAt) " +
            "ON CONFLICT (timer_id) DO UPDATE SET is_fired = EXCLUDED.is_fired",
            p =>
            {
                p.Add(new NpgsqlParameter("TimerId", timer.TimerId.Value));
                p.Add(new NpgsqlParameter("TenantId", timer.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", timer.ConversationId.Value));
                p.Add(new NpgsqlParameter("CallbackRuleId", timer.CallbackRuleId.Value));
                p.Add(new NpgsqlParameter("FireAt", timer.FireAt));
                p.Add(new NpgsqlParameter("IsFired", timer.IsFired));
                p.Add(new NpgsqlParameter("CreatedAt", timer.CreatedAt));
            },
            ct);
    }

    public async Task<IReadOnlyList<ScheduledTimer>> GetOverdueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT timer_id, tenant_id, conversation_id, callback_rule_id, fire_at, is_fired, created_at " +
            "FROM scheduled_timers WHERE NOT is_fired AND fire_at <= @Now ORDER BY fire_at LIMIT @Limit",
            p =>
            {
                p.Add(new NpgsqlParameter("Now", now.UtcDateTime));
                p.Add(new NpgsqlParameter("Limit", limit));
            },
            TimerRow.Map, ct);
        return rows.Select(r => r.ToTimer()).ToList();
    }

    public async Task MarkFiredAsync(ScheduledTimer timer, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE scheduled_timers SET is_fired = true WHERE timer_id = @TimerId",
            p => p.Add(new NpgsqlParameter("TimerId", timer.TimerId.Value)),
            ct);
    }

    private sealed class TimerRow
    {
        public string timer_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public string callback_rule_id { get; init; } = null!;
        public DateTime fire_at { get; init; }
        public bool is_fired { get; init; }
        public DateTime created_at { get; init; }

        public static TimerRow Map(NpgsqlDataReader r) => new()
        {
            timer_id = r.GetString("timer_id"),
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            callback_rule_id = r.GetString("callback_rule_id"),
            fire_at = r.GetDateTime("fire_at"),
            is_fired = r.GetBoolean("is_fired"),
            created_at = r.GetDateTime("created_at"),
        };

        public ScheduledTimer ToTimer() => new()
        {
            TimerId = EntityId.From(timer_id),
            TenantId = new TenantId(tenant_id),
            ConversationId = EntityId.From(conversation_id),
            CallbackRuleId = EntityId.From(callback_rule_id),
            FireAt = fire_at,
            IsFired = is_fired,
            CreatedAt = created_at,
        };
    }
}
