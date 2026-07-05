using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Billing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMeteringService"/>, <see cref="IQuotaEnforcementService"/>, and <see cref="IInvoiceGenerationService"/>.
    /// Store implementations (<see cref="IUsageRecordStore"/>, <see cref="ITenantQuotaStore"/>,
    /// <see cref="IRateCardStore"/>, <see cref="IInvoiceStore"/>) must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformBilling(this IServiceCollection services)
    {
        services.AddSingleton<IMeteringService, DefaultMeteringService>();
        services.AddSingleton<IQuotaEnforcementService, DefaultQuotaEnforcementService>();
        services.AddSingleton<IInvoiceGenerationService, DefaultInvoiceGenerationService>();
        // credit-grant-lazy-mint-rollover: shared by the enforcement path (DefaultQuotaEnforcementService) and
        // the credits-readout endpoints (CreditLedgerEndpoints), both resolved from DI.
        services.AddSingleton<CreditGrantLazyMinter>();
        return services;
    }
}
