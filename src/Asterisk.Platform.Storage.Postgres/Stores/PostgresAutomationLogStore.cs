using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAutomationLogStore : IAutomationExecutionLogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAutomationLogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AutomationExecutionLog log, CancellationToken ct)
    {
        var actionsJson = JsonSerializer.Serialize(
            log.ActionsExecuted.Select(a => (int)a).ToList(),
            PostgresJson.Ctx.ListInt32);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO automation_execution_logs (log_id, rule_id, tenant_id, conversation_id, " +
            "trigger, conditions_matched, actions_executed, error, executed_at) " +
            "VALUES (@LogId, @RuleId, @TenantId, @ConversationId, " +
            "@Trigger, @ConditionsMatched, @ActionsExecuted::jsonb, @Error, @ExecutedAt)",
            new
            {
                LogId = log.LogId.Value,
                RuleId = log.RuleId.Value,
                TenantId = log.TenantId.Value,
                ConversationId = log.ConversationId.Value,
                Trigger = (int)log.Trigger,
                log.ConditionsMatched,
                ActionsExecuted = actionsJson,
                log.Error,
                log.ExecutedAt,
            });
    }

    public async Task<IReadOnlyList<AutomationExecutionLog>> GetByConversationAsync(
        TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<LogRow>(
            "SELECT log_id, rule_id, tenant_id, conversation_id, trigger, conditions_matched, " +
            "actions_executed, error, executed_at " +
            "FROM automation_execution_logs " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY executed_at DESC",
            new { TenantId = tenantId.Value, ConversationId = conversationId.Value });
        return rows.Select(r => r.ToLog()).ToList();
    }

    private sealed record LogRow(
        string log_id,
        string rule_id,
        string tenant_id,
        string conversation_id,
        int trigger,
        bool conditions_matched,
        string actions_executed,
        string? error,
        DateTimeOffset executed_at)
    {
        public AutomationExecutionLog ToLog()
        {
            var actionInts = JsonSerializer.Deserialize(actions_executed, PostgresJson.Ctx.ListInt32)
                             ?? [];
            IReadOnlyList<AutomationActionType> actions = actionInts.Select(i => (AutomationActionType)i).ToList();

            return new AutomationExecutionLog
            {
                LogId = EntityId.From(log_id),
                RuleId = EntityId.From(rule_id),
                TenantId = new TenantId(tenant_id),
                ConversationId = EntityId.From(conversation_id),
                Trigger = (AutomationTrigger)trigger,
                ConditionsMatched = conditions_matched,
                ActionsExecuted = actions,
                Error = error,
                ExecutedAt = executed_at,
            };
        }
    }
}
