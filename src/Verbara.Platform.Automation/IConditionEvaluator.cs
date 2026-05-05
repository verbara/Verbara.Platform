namespace Verbara.Platform.Automation;

public interface IConditionEvaluator
{
    Task<bool> EvaluateAsync(AutomationCondition condition, AutomationEvent automationEvent, CancellationToken ct);
}
