using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Instagram;

/// <summary>
/// DI registration extensions for Platform.Channels.Instagram services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Instagram connector, webhook handler, and message transformer.
    /// </summary>
    public static IServiceCollection AddInstagram(
        this IServiceCollection services,
        Action<InstagramOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<InstagramConnector>();
        services.AddSingleton<InstagramWebhookHandler>();
        services.AddSingleton<InstagramMessageTransformer>();

        return services;
    }
}
