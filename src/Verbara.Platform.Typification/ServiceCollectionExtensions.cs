using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Typification;

/// <summary>DI registration extensions for Platform.Typification services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Platform.Typification services. Currently a no-op stub — the
    /// schema validator and binding resolver are registered here in later tasks
    /// (D1/D2). Callers must separately register implementations of the store
    /// interfaces in <c>Verbara.Platform.Typification.Stores</c>.
    /// </summary>
    public static IServiceCollection AddPlatformTypification(this IServiceCollection services)
    {
        return services;
    }
}
