using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Twitter;

/// <summary>
/// DI registration extensions for Platform.Channels.Twitter services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Twitter/X connector, webhook handler, message transformer, and
    /// the keyed <see cref="ResiliencePolicy"/> consumed by <see cref="TwitterConnector"/>
    /// (circuit 5/60s + retry 2/500ms + timeout 15s).
    /// </summary>
    public static IServiceCollection AddTwitter(
        this IServiceCollection services,
        Action<TwitterOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<TwitterConnector>();
        services.AddSingleton<TwitterWebhookHandler>();
        services.AddSingleton<TwitterMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            TwitterConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build());

        return services;
    }
}
