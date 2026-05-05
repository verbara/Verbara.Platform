using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Messenger;

/// <summary>
/// DI registration extensions for Platform.Channels.Messenger services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Messenger connector, webhook handler, message transformer, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="MessengerConnector"/>
    /// (circuit 5/60s + retry 2/500ms + timeout 15s).
    /// </summary>
    public static IServiceCollection AddMessenger(
        this IServiceCollection services,
        Action<MessengerOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<MessengerConnector>();
        services.AddSingleton<MessengerWebhookHandler>();
        services.AddSingleton<MessengerMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            MessengerConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build());

        return services;
    }
}
