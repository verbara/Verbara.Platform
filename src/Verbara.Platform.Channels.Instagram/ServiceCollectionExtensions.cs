using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Instagram;

/// <summary>
/// DI registration extensions for Platform.Channels.Instagram services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Instagram connector, webhook handler, message transformer, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="InstagramConnector"/>
    /// (circuit 5/60s + retry 2/500ms + timeout 15s).
    /// </summary>
    public static IServiceCollection AddInstagram(
        this IServiceCollection services,
        Action<InstagramOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<InstagramConnector>();
        services.AddSingleton<InstagramWebhookHandler>();
        services.AddSingleton<InstagramMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            InstagramConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build());

        return services;
    }
}
