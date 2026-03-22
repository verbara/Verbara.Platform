using Asterisk.Platform.Core;

namespace Asterisk.Platform.Automation;

public sealed class AutomationExecutionLog
{
    public required EntityId LogId { get; init; }
    public required EntityId RuleId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required AutomationTrigger Trigger { get; init; }
    public required bool ConditionsMatched { get; init; }
    public required IReadOnlyList<AutomationActionType> ActionsExecuted { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
}
