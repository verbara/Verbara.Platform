using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Automation;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryAutomationRuleStore : IAutomationRuleStore
{
    private readonly ConcurrentDictionary<(TenantId, string), AutomationRule> _items = new();

    public Task<IReadOnlyList<AutomationRule>> GetActiveByTriggerAsync(TenantId tenantId, AutomationTrigger trigger, CancellationToken ct)
    {
        IReadOnlyList<AutomationRule> result = _items.Values
            .Where(r => r.TenantId == tenantId && r.IsActive && r.Trigger == trigger)
            .OrderBy(r => r.Priority)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<AutomationRule?> GetByIdAsync(TenantId tenantId, EntityId ruleId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, ruleId.Value), out var item);
        return Task.FromResult(item);
    }

    public Task SaveAsync(AutomationRule rule, CancellationToken ct)
    {
        _items[(rule.TenantId, rule.RuleId.Value)] = rule;
        return Task.CompletedTask;
    }
}
