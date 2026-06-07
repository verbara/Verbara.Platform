using Verbara.Platform.Queues.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Queues;

/// <summary>
/// DI registration extensions for Platform.Queues services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers queue services and configures <see cref="QueueOptions"/>.
    /// </summary>
    public static IServiceCollection AddPlatformQueues(
        this IServiceCollection services,
        Action<QueueOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<QueueOptions>();

        // W6 — InMemoryAgentCapacityService(IAgentCapacityResolver): the resolver supplies
        // EFFECTIVE per-channel + MaxTotal capacity AND owns the single agent read (null ==
        // phantom agent → fail closed). Resolved by constructor injection. (The Api host
        // overrides this with PersistentAgentCapacityService when a Postgres connection exists.)
        services.AddSingleton<IAgentCapacityService, InMemoryAgentCapacityService>();
        services.AddSingleton<IAgentPresenceService, InMemoryAgentPresenceService>();

        // W6 — effective-capacity resolver (tenant default + sparse per-agent override, merged at
        // read time). Stateless over Singleton stores; ICapacityDefaultsProvider is supplied by the
        // Api layer so Queues stays free of an Identity dependency.
        services.AddSingleton<IAgentCapacityResolver, AgentCapacityResolver>();

        return services;
    }
}
