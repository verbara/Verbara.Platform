using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.WhatsApp;

/// <summary>
/// DI registration extensions for Platform.Channels.WhatsApp services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WhatsApp connector, webhook handler, message transformer, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="WhatsAppConnector"/>
    /// (circuit 5/60s + retry 2/500ms + timeout 15s).
    /// </summary>
    public static IServiceCollection AddWhatsApp(
        this IServiceCollection services,
        Action<WhatsAppOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<WhatsAppConnector>();
        services.AddSingleton<WhatsAppWebhookHandler>();
        services.AddSingleton<WhatsAppMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            WhatsAppConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build());

        return services;
    }
}
