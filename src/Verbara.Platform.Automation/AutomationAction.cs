namespace Verbara.Platform.Automation;

public sealed class AutomationAction
{
    public required AutomationActionType Type { get; init; }
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();
}
