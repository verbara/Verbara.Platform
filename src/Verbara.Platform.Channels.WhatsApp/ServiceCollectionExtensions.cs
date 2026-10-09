using Verbara.Platform.Channels.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// DI registration extensions for Platform.Channels.WhatsApp services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WhatsApp connector, webhook handler (also exposed as <see cref="IWebhookHandler"/> /
    /// <see cref="IChannelConnector"/> for the channel registry), message transformer, and the
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

        // Expose the channel to the registry (whatsapp-works-for-real D1): registering this module is the
        // only act that makes WhatsApp reachable for inbound webhooks and outbound sends.
        services.AddSingleton<IWebhookHandler>(sp => sp.GetRequiredService<WhatsAppWebhookHandler>());
        services.AddTransient<IChannelConnector>(sp => sp.GetRequiredService<WhatsAppConnector>());

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
