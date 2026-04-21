using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Telegram;

/// <summary>
/// DI registration extensions for Platform.Channels.Telegram services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Telegram connector, webhook handler, message transformer, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="TelegramConnector"/>
    /// (circuit 5/45s + retry 3/300ms + timeout 10s).
    /// </summary>
    public static IServiceCollection AddTelegram(
        this IServiceCollection services,
        Action<TelegramOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<TelegramConnector>();
        services.AddSingleton<TelegramWebhookHandler>();
        services.AddSingleton<TelegramMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            TelegramConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(45))
                .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(300))
                .WithTimeout(TimeSpan.FromSeconds(10))
                .Build());

        return services;
    }
}
