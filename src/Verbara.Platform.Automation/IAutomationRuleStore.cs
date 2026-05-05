using Verbara.Platform.Core;

namespace Verbara.Platform.Automation;

public interface IAutomationRuleStore
{
    Task<IReadOnlyList<AutomationRule>> GetActiveByTriggerAsync(TenantId tenantId, AutomationTrigger trigger, CancellationToken ct);
    Task<AutomationRule?> GetByIdAsync(TenantId tenantId, EntityId ruleId, CancellationToken ct);
    Task SaveAsync(AutomationRule rule, CancellationToken ct);
}
