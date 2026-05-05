using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Identity;

/// <summary>
/// DI registration extensions for Platform.Identity services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers identity services and configures <see cref="IdentityOptions"/>.
    /// </summary>
    public static IServiceCollection AddPlatformIdentity(
        this IServiceCollection services,
        Action<IdentityOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<IdentityOptions>();

        return services;
    }
}
