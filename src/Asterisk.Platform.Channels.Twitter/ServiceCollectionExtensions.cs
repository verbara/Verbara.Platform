using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Twitter;

/// <summary>
/// DI registration extensions for Platform.Channels.Twitter services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Twitter/X connector, webhook handler, and message transformer.
    /// </summary>
    public static IServiceCollection AddTwitter(
        this IServiceCollection services,
        Action<TwitterOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<TwitterConnector>();
        services.AddSingleton<TwitterWebhookHandler>();
        services.AddSingleton<TwitterMessageTransformer>();

        return services;
    }
}
