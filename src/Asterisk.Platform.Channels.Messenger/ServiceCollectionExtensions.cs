using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Messenger;

/// <summary>
/// DI registration extensions for Platform.Channels.Messenger services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Messenger connector, webhook handler, and message transformer.
    /// </summary>
    public static IServiceCollection AddMessenger(
        this IServiceCollection services,
        Action<MessengerOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<MessengerConnector>();
        services.AddSingleton<MessengerWebhookHandler>();
        services.AddSingleton<MessengerMessageTransformer>();

        return services;
    }
}
