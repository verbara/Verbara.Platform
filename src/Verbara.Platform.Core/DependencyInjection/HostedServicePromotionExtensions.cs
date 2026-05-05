using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Core.DependencyInjection;

/// <summary>
/// Promotes <see cref="IHostedService"/> registrations to also be resolvable
/// as the concrete type. Required when an <c>IHealthCheck</c> consults the
/// hosted service's heartbeat — DI must return the same singleton instance
/// to both <see cref="IHostedService"/> and the concrete type.
/// </summary>
public static class HostedServicePromotionExtensions
{
    /// <summary>
    /// Removes the <see cref="IHostedService"/>-only registration of
    /// <typeparamref name="T"/> (added by
    /// <see cref="ServiceCollectionHostedServiceExtensions.AddHostedService{THostedService}(IServiceCollection)"/>),
    /// re-registers <typeparamref name="T"/> as a concrete singleton, and
    /// forwards <see cref="IHostedService"/> via factory so the host and
    /// health checks resolve the same instance.
    /// </summary>
    /// <remarks>
    /// Idempotent — repeated calls for the same <typeparamref name="T"/> are no-ops.
    /// Tracked internally via a private marker type registered on first promotion.
    /// </remarks>
    public static IServiceCollection PromoteHostedServiceToSingleton<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        this IServiceCollection services)
        where T : class, IHostedService
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(PromotionMarker<T>)))
            return services;

        var existing = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(T));
        if (existing is not null)
            services.Remove(existing);

        services.TryAddSingleton<T>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<T>());
        services.AddSingleton<PromotionMarker<T>>();
        return services;
    }

    private sealed class PromotionMarker<T>
        where T : class, IHostedService
    {
    }
}
