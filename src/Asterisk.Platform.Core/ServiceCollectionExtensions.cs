using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Platform.Core;

/// <summary>
/// DI registration extensions for Platform.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core platform services including the system clock.
    /// </summary>
    public static IServiceCollection AddPlatformCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<PlatformEventBus>();
        services.TryAddSingleton<IFeatureRegistry, DefaultFeatureRegistry>();
        return services;
    }
}
