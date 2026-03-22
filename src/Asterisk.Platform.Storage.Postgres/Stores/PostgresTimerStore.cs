using Dapper;
using Npgsql;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTimerStore : ITimerStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTimerStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(ScheduledTimer timer, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO scheduled_timers (timer_id, tenant_id, conversation_id, callback_rule_id, fire_at, is_fired, created_at) " +
            "VALUES (@TimerId, @TenantId, @ConversationId, @CallbackRuleId, @FireAt, @IsFired, @CreatedAt) " +
            "ON CONFLICT (timer_id) DO UPDATE SET is_fired = EXCLUDED.is_fired",
            new
            {
                TimerId = timer.TimerId.Value,
                TenantId = timer.TenantId.Value,
                ConversationId = timer.ConversationId.Value,
                CallbackRuleId = timer.CallbackRuleId.Value,
                timer.FireAt,
                timer.IsFired,
                timer.CreatedAt,
            });
    }

    public async Task<IReadOnlyList<ScheduledTimer>> GetOverdueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TimerRow>(
            "SELECT timer_id, tenant_id, conversation_id, callback_rule_id, fire_at, is_fired, created_at " +
            "FROM scheduled_timers WHERE NOT is_fired AND fire_at <= @Now ORDER BY fire_at LIMIT @Limit",
            new { Now = now, Limit = limit });
        return rows.Select(r => r.ToTimer()).ToList();
    }

    public async Task MarkFiredAsync(ScheduledTimer timer, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE scheduled_timers SET is_fired = true WHERE timer_id = @TimerId",
            new { TimerId = timer.TimerId.Value });
    }

    private sealed record TimerRow(
        string timer_id,
        string tenant_id,
        string conversation_id,
        string callback_rule_id,
        DateTimeOffset fire_at,
        bool is_fired,
        DateTimeOffset created_at)
    {
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
