using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Automation;

/// <summary>
/// DI registration extensions for Platform.Automation services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the automation rules engine, condition evaluator, action executor, and timer polling service.
    /// </summary>
    public static IServiceCollection AddPlatformAutomation(this IServiceCollection services)
    {
        services.AddSingleton<IConditionEvaluator, DefaultConditionEvaluator>();
        services.AddSingleton<IActionExecutor, DefaultActionExecutor>();
        services.AddSingleton<IAutomationEngine, AutomationEngine>();
        services.AddHostedService<TimerPollingService>();
        services.AddHttpClient("AutomationWebhook");

        return services;
    }
}
