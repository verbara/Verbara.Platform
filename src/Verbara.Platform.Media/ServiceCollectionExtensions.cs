using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Media;

/// <summary>
/// DI registration extensions for Platform.Media services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMediaService"/> → <see cref="DefaultMediaService"/> and
    /// <see cref="IMediaStorage"/> → <see cref="FileSystemMediaStorage"/>.
    /// An <see cref="IMediaStore"/> implementation must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformMedia(
        this IServiceCollection services,
        Action<MediaOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<MediaOptions>(_ => { });

        services.AddSingleton<IMediaStorage, FileSystemMediaStorage>();
        services.AddSingleton<IMediaService, DefaultMediaService>();
        return services;
    }
}
