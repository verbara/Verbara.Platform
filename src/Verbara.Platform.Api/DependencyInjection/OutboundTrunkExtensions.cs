using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services.OutboundTrunk;
using Verbara.Sdk.Pro.Dialer.Routing;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>outbound-calls-reach-the-trunk (design D1): the one registration of the shared dialable trunk resolver.</summary>
public static class OutboundTrunkExtensions
{
    /// <summary>
    /// Registers <see cref="DialableTrunkResolver"/> once. The outbound route store is optional (only the dialer
    /// storage registers it), so a host without it always takes the configured default trunk.
    /// </summary>
    public static IServiceCollection AddDialableTrunkResolver(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(sp => new DialableTrunkResolver(
            sp.GetService<OutboundRouteStoreBase>(),
            sp.GetRequiredService<TrunkStoreBase>(),
            configuration["Asterisk:Outbound:DefaultTrunk"],
            sp.GetRequiredService<ILogger<DialableTrunkResolver>>()));
        return services;
    }
}
