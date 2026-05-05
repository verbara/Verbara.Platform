using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Video;

/// <summary>
/// DI registration extensions for Platform.Channels.Video services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Video connector, session manager, webhook handler, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="VideoSessionManager"/>
    /// to wrap <see cref="IVideoTransport"/> calls (circuit 3/60s + retry 2/1s + timeout 30s).
    /// Requires a registered <see cref="IVideoTransport"/> implementation.
    /// </summary>
    public static IServiceCollection AddVideo(
        this IServiceCollection services,
        Action<VideoOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<VideoOptions>();

        services.AddSingleton<VideoSessionManager>();
        services.AddSingleton<VideoConnector>();
        services.AddSingleton<VideoWebhookHandler>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            VideoConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromSeconds(1))
                .WithTimeout(TimeSpan.FromSeconds(30))
                .Build());

        return services;
    }
}
