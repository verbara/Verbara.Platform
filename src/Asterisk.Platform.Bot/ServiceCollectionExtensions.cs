using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Bot;

/// <summary>
/// DI registration extensions for Platform.Bot services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the virtual agent orchestrator and analytics collector.
    /// Callers must separately register:
    /// <list type="bullet">
    ///   <item><see cref="IBotConfigStore"/> — bot configuration persistence.</item>
    ///   <item><see cref="Asterisk.Platform.Flows.IFlowStore"/> — flow definition persistence.</item>
    ///   <item><see cref="Asterisk.Platform.Flows.IFlowExecutionStore"/> — execution state persistence.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPlatformBot(this IServiceCollection services)
    {
        services.AddSingleton<BotAnalyticsCollector>();
        services.AddSingleton<IVirtualAgent, BotOrchestrator>();
        return services;
    }
}
