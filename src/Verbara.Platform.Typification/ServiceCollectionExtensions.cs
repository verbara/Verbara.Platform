using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Validation;

namespace Verbara.Platform.Typification;

/// <summary>DI registration extensions for Platform.Typification services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Platform.Typification services: the server-authoritative schema
    /// validator (D1) and the most-specific-wins binding resolver (D2). Callers
    /// must separately register implementations of the store interfaces in
    /// <c>Verbara.Platform.Typification.Stores</c>.
    /// </summary>
    public static IServiceCollection AddPlatformTypification(this IServiceCollection services)
    {
        services.AddSingleton<ITypificationValidator, DefaultTypificationValidator>();
        services.AddSingleton<ITypificationResolver, DefaultTypificationResolver>();
        return services;
    }
}
