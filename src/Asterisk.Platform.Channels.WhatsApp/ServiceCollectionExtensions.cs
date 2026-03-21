using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.WhatsApp;

/// <summary>
/// DI registration extensions for Platform.Channels.WhatsApp services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WhatsApp connector, webhook handler, and message transformer.
    /// </summary>
    public static IServiceCollection AddWhatsApp(
        this IServiceCollection services,
        Action<WhatsAppOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<WhatsAppConnector>();
        services.AddSingleton<WhatsAppWebhookHandler>();
        services.AddSingleton<WhatsAppMessageTransformer>();

        return services;
    }
}
