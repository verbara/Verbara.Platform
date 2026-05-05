using Verbara.Platform.Routing.Inbound.Middlewares;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// DI registration extensions for Platform.Routing.Inbound services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the inbound router, all built-in middlewares, and the round-robin agent selector.
    /// </summary>
    public static IServiceCollection AddInboundRouting(
        this IServiceCollection services,
        Action<InboundRoutingOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<InboundRoutingOptions>();

        services.AddSingleton<ChannelQueueMappingMiddleware>();
        services.AddSingleton<LastAgentMiddleware>();
        services.AddSingleton<PriorityEscalationMiddleware>();
        services.AddSingleton<OverflowMiddleware>();
        services.AddSingleton<BusinessHoursMiddleware>();

        services.AddSingleton<IReadOnlyList<InboundRoutingMiddlewareBase>>(sp =>
        [
            sp.GetRequiredService<ChannelQueueMappingMiddleware>(),
            sp.GetRequiredService<LastAgentMiddleware>(),
            sp.GetRequiredService<PriorityEscalationMiddleware>(),
            sp.GetRequiredService<OverflowMiddleware>(),
            sp.GetRequiredService<BusinessHoursMiddleware>(),
        ]);

        services.AddSingleton<RoundRobinAgentSelector>();
        services.AddSingleton<IAgentSelector>(sp => sp.GetRequiredService<RoundRobinAgentSelector>());

        services.AddSingleton<IInboundRouter, InboundRouter>();

        return services;
    }
}
