using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Audit;

/// <summary>
/// DI registration extensions for Platform.Audit services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAuditService"/> → <see cref="DefaultAuditService"/>.
    /// An <see cref="IAuditStore"/> implementation must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformAudit(this IServiceCollection services)
    {
        services.AddSingleton<IAuditService, DefaultAuditService>();
        return services;
    }
}
