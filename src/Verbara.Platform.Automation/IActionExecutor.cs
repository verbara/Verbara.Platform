namespace Verbara.Platform.Automation;

public interface IActionExecutor
{
    Task<bool> ExecuteAsync(AutomationAction action, AutomationEvent automationEvent, CancellationToken ct);
}
