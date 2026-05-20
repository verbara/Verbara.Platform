using System.Text.Json;
using Npgsql;
using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAutomationRuleStore : IAutomationRuleStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAutomationRuleStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<AutomationRule>> GetActiveByTriggerAsync(
        TenantId tenantId, AutomationTrigger trigger, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT rule_id, tenant_id, name, trigger, conditions, actions, is_active, priority, " +
            "max_executions_per_conversation, created_at " +
            "FROM automation_rules " +
            "WHERE tenant_id = @TenantId AND trigger = @Trigger AND is_active = true " +
            "ORDER BY priority",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Trigger", (int)trigger)); },
            RuleRow.Map, ct);
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<AutomationRule?> GetByIdAsync(TenantId tenantId, EntityId ruleId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT rule_id, tenant_id, name, trigger, conditions, actions, is_active, priority, " +
            "max_executions_per_conversation, created_at " +
            "FROM automation_rules WHERE tenant_id = @TenantId AND rule_id = @RuleId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("RuleId", ruleId.Value)); },
            RuleRow.Map, ct);
        return row?.ToRule();
    }

    public async Task SaveAsync(AutomationRule rule, CancellationToken ct)
    {
        var conditionsJson = JsonSerializer.Serialize(rule.Conditions, PostgresJson.Ctx.IReadOnlyListAutomationCondition);
        var actionsJson = JsonSerializer.Serialize(rule.Actions, PostgresJson.Ctx.IReadOnlyListAutomationAction);

        await _dataSource.ExecuteAsync(
            "INSERT INTO automation_rules (rule_id, tenant_id, name, trigger, conditions, actions, is_active, " +
            "priority, max_executions_per_conversation, created_at) " +
            "VALUES (@RuleId, @TenantId, @Name, @Trigger, @Conditions::jsonb, @Actions::jsonb, @IsActive, " +
            "@Priority, @MaxExecutionsPerConversation, @CreatedAt) " +
            "ON CONFLICT (tenant_id, rule_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, trigger = EXCLUDED.trigger, conditions = EXCLUDED.conditions, " +
            "  actions = EXCLUDED.actions, is_active = EXCLUDED.is_active, priority = EXCLUDED.priority, " +
            "  max_executions_per_conversation = EXCLUDED.max_executions_per_conversation",
            p =>
            {
                p.Add(new NpgsqlParameter("RuleId", rule.RuleId.Value));
                p.Add(new NpgsqlParameter("TenantId", rule.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", rule.Name));
                p.Add(new NpgsqlParameter("Trigger", (int)rule.Trigger));
                p.Add(new NpgsqlParameter("Conditions", conditionsJson));
                p.Add(new NpgsqlParameter("Actions", actionsJson));
                p.Add(new NpgsqlParameter("IsActive", rule.IsActive));
                p.Add(new NpgsqlParameter("Priority", rule.Priority));
                p.Add(new NpgsqlParameter("MaxExecutionsPerConversation", rule.MaxExecutionsPerConversation));
                p.Add(new NpgsqlParameter("CreatedAt", rule.CreatedAt));
            },
            ct);
    }

    private sealed class RuleRow
    {
        public string rule_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int trigger { get; init; }
        public string conditions { get; init; } = null!;
        public string actions { get; init; } = null!;
        public bool is_active { get; init; }
        public int priority { get; init; }
        public int max_executions_per_conversation { get; init; }
        public DateTime created_at { get; init; }

        public static RuleRow Map(NpgsqlDataReader r) => new()
        {
            rule_id = r.GetString("rule_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            trigger = r.GetInt32("trigger"),
            conditions = r.GetString("conditions"),
            actions = r.GetString("actions"),
            is_active = r.GetBoolean("is_active"),
            priority = r.GetInt32("priority"),
            max_executions_per_conversation = r.GetInt32("max_executions_per_conversation"),
            created_at = r.GetDateTime("created_at"),
        };

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
