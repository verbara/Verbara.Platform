using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Video;

/// <summary>
/// DI registration extensions for Platform.Channels.Video services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Video connector, session manager, and webhook handler.
    /// Requires a registered <see cref="IVideoTransport"/> implementation.
    /// </summary>
    public static IServiceCollection AddVideo(
        this IServiceCollection services,
        Action<VideoOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<VideoOptions>();

        services.AddSingleton<VideoSessionManager>();
        services.AddSingleton<VideoConnector>();
        services.AddSingleton<VideoWebhookHandler>();

        return services;
    }
}
