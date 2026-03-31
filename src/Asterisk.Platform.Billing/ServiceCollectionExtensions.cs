using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Billing;

/// <summary>
/// DI registration extensions for Platform.Billing services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMeteringService"/> and <see cref="IQuotaEnforcementService"/>.
    /// Store implementations (<see cref="IUsageRecordStore"/>, <see cref="ITenantQuotaStore"/>) must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformBilling(this IServiceCollection services)
    {
        services.AddSingleton<IMeteringService, DefaultMeteringService>();
        services.AddSingleton<IQuotaEnforcementService, DefaultQuotaEnforcementService>();
        return services;
    }
}
