using Verbara.Platform.Core;

namespace Verbara.Platform.Automation;

public sealed class AutomationRule
{
    public required EntityId RuleId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }
    public required AutomationTrigger Trigger { get; init; }
    public IReadOnlyList<AutomationCondition> Conditions { get; init; } = [];
    public IReadOnlyList<AutomationAction> Actions { get; init; } = [];
    public bool IsActive { get; set; } = true;
    public int Priority { get; init; } = 100;
    public int MaxExecutionsPerConversation { get; init; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
}
