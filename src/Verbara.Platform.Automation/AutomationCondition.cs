namespace Verbara.Platform.Automation;

public sealed class AutomationCondition
{
    public required string Field { get; init; }
    public required ConditionOperator Operator { get; init; }
    public string? Value { get; init; }
}
