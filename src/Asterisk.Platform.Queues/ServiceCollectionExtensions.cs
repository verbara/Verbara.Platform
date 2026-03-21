using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Queues;

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

        return services;
    }
}
