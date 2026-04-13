using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Delivery;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asterisk.Platform.Core;

/// <summary>
/// DI registration extensions for Platform.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core platform services including the system clock and the hybrid
    /// <see cref="PlatformEventBus"/> facade. Callers must invoke
    /// <c>services.AddAsteriskPush()</c> before this method so the
    /// <see cref="IPushEventBus"/> dependency resolves and Sdk.Push forwarding is active.
    /// </summary>
    public static IServiceCollection AddPlatformCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        // Explicit factory to bind PlatformEventBus to the IPushEventBus-aware constructor;
        // avoids any DI ctor-selection ambiguity with the parameterless ctor that exists
        // for test fixtures.
        services.AddSingleton<PlatformEventBus>(sp =>
            new PlatformEventBus(sp.GetRequiredService<IPushEventBus>()));

        // Override the Sdk.Push DefaultDeliveryFilter with the Platform-specific one.
        // AddAsteriskPush() registers the default; this Replace ensures the Platform rules win
        // regardless of registration order.
        services.Replace(ServiceDescriptor.Singleton<IEventDeliveryFilter, PlatformDeliveryFilter>());

        services.TryAddSingleton<IFeatureRegistry, DefaultFeatureRegistry>();
        return services;
    }
}
