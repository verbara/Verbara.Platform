using System.Text.Json;
using Npgsql;
using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAutomationLogStore : IAutomationExecutionLogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAutomationLogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AutomationExecutionLog log, CancellationToken ct)
    {
        var actionsJson = JsonSerializer.Serialize(
            log.ActionsExecuted.Select(a => (int)a).ToList(),
            PostgresJson.Ctx.ListInt32);

        await _dataSource.ExecuteAsync(
            "INSERT INTO automation_execution_logs (log_id, rule_id, tenant_id, conversation_id, " +
            "trigger, conditions_matched, actions_executed, error, executed_at) " +
            "VALUES (@LogId, @RuleId, @TenantId, @ConversationId, " +
            "@Trigger, @ConditionsMatched, @ActionsExecuted::jsonb, @Error, @ExecutedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("LogId", log.LogId.Value));
                p.Add(new NpgsqlParameter("RuleId", log.RuleId.Value));
                p.Add(new NpgsqlParameter("TenantId", log.TenantId.Value));
                p.Add(new NpgsqlParameter("ConversationId", log.ConversationId.Value));
                p.Add(new NpgsqlParameter("Trigger", (int)log.Trigger));
                p.Add(new NpgsqlParameter("ConditionsMatched", log.ConditionsMatched));
                p.Add(new NpgsqlParameter("ActionsExecuted", actionsJson));
                p.Add(new NpgsqlParameter("Error", (object?)log.Error ?? DBNull.Value));
                p.Add(new NpgsqlParameter("ExecutedAt", log.ExecutedAt));
            },
            ct);
    }

    public async Task<IReadOnlyList<AutomationExecutionLog>> GetByConversationAsync(
        TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT log_id, rule_id, tenant_id, conversation_id, trigger, conditions_matched, " +
            "actions_executed, error, executed_at " +
            "FROM automation_execution_logs " +
            "WHERE tenant_id = @TenantId AND conversation_id = @ConversationId " +
            "ORDER BY executed_at DESC",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("ConversationId", conversationId.Value)); },
            LogRow.Map, ct);
        return rows.Select(r => r.ToLog()).ToList();
    }

    private sealed class LogRow
    {
        public string log_id { get; init; } = null!;
        public string rule_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string conversation_id { get; init; } = null!;
        public int trigger { get; init; }
        public bool conditions_matched { get; init; }
        public string actions_executed { get; init; } = null!;
        public string? error { get; init; }
        public DateTime executed_at { get; init; }

        public static LogRow Map(NpgsqlDataReader r) => new()
        {
            log_id = r.GetString("log_id"),
            rule_id = r.GetString("rule_id"),
            tenant_id = r.GetString("tenant_id"),
            conversation_id = r.GetString("conversation_id"),
            trigger = r.GetInt32("trigger"),
            conditions_matched = r.GetBoolean("conditions_matched"),
            actions_executed = r.GetString("actions_executed"),
            error = r.GetStringOrNull("error"),
            executed_at = r.GetDateTime("executed_at"),
        };

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
