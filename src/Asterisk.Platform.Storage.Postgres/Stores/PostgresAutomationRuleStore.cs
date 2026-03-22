using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAutomationRuleStore : IAutomationRuleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAutomationRuleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AutomationRule>> GetActiveByTriggerAsync(
        TenantId tenantId, AutomationTrigger trigger, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RuleRow>(
            "SELECT rule_id, tenant_id, name, trigger, conditions, actions, is_active, priority, " +
            "max_executions_per_conversation, created_at " +
            "FROM automation_rules " +
            "WHERE tenant_id = @TenantId AND trigger = @Trigger AND is_active = true " +
            "ORDER BY priority",
            new { TenantId = tenantId.Value, Trigger = (int)trigger });
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<AutomationRule?> GetByIdAsync(TenantId tenantId, EntityId ruleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RuleRow>(
            "SELECT rule_id, tenant_id, name, trigger, conditions, actions, is_active, priority, " +
            "max_executions_per_conversation, created_at " +
            "FROM automation_rules WHERE tenant_id = @TenantId AND rule_id = @RuleId",
            new { TenantId = tenantId.Value, RuleId = ruleId.Value });
        return row?.ToRule();
    }

    public async Task SaveAsync(AutomationRule rule, CancellationToken ct)
    {
        var conditionsJson = JsonSerializer.Serialize(rule.Conditions, PostgresJson.Ctx.IReadOnlyListAutomationCondition);
        var actionsJson = JsonSerializer.Serialize(rule.Actions, PostgresJson.Ctx.IReadOnlyListAutomationAction);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO automation_rules (rule_id, tenant_id, name, trigger, conditions, actions, is_active, " +
            "priority, max_executions_per_conversation, created_at) " +
            "VALUES (@RuleId, @TenantId, @Name, @Trigger, @Conditions::jsonb, @Actions::jsonb, @IsActive, " +
            "@Priority, @MaxExecutionsPerConversation, @CreatedAt) " +
            "ON CONFLICT (tenant_id, rule_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, trigger = EXCLUDED.trigger, conditions = EXCLUDED.conditions, " +
            "  actions = EXCLUDED.actions, is_active = EXCLUDED.is_active, priority = EXCLUDED.priority, " +
            "  max_executions_per_conversation = EXCLUDED.max_executions_per_conversation",
            new
            {
                RuleId = rule.RuleId.Value,
                TenantId = rule.TenantId.Value,
                rule.Name,
                Trigger = (int)rule.Trigger,
                Conditions = conditionsJson,
                Actions = actionsJson,
                rule.IsActive,
                rule.Priority,
                MaxExecutionsPerConversation = rule.MaxExecutionsPerConversation,
                rule.CreatedAt,
            });
    }

    private sealed record RuleRow(
        string rule_id,
        string tenant_id,
        string name,
        int trigger,
        string conditions,
        string actions,
        bool is_active,
        int priority,
        int max_executions_per_conversation,
        DateTimeOffset created_at)
    {
        public AutomationRule ToRule()
        {
            var condList = JsonSerializer.Deserialize(conditions, PostgresJson.Ctx.IReadOnlyListAutomationCondition)
                           ?? (IReadOnlyList<AutomationCondition>)[];
            var actList = JsonSerializer.Deserialize(actions, PostgresJson.Ctx.IReadOnlyListAutomationAction)
                          ?? (IReadOnlyList<AutomationAction>)[];
            return new AutomationRule
            {
                RuleId = EntityId.From(rule_id),
                TenantId = new TenantId(tenant_id),
                Name = name,
                Trigger = (AutomationTrigger)trigger,
                Conditions = condList,
                Actions = actList,
                IsActive = is_active,
                Priority = priority,
                MaxExecutionsPerConversation = max_executions_per_conversation,
                CreatedAt = created_at,
            };
        }
    }
}
