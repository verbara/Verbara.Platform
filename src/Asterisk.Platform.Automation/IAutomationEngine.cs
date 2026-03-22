namespace Asterisk.Platform.Automation;

public interface IAutomationEngine
{
    Task ProcessEventAsync(AutomationEvent automationEvent, CancellationToken ct);
}
