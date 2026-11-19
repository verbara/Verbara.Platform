using Verbara.Sdk.Pro.Storage.Common.Retention.DependencyInjection;

namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// Composition-root wiring of the Pro retention purge and its admin surface (Platform/ADR-0052).
/// </summary>
public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pro purge orchestrator in the mode read from <c>Retention:DryRun</c>
    /// (<see cref="RetentionPurgeModeConfiguration.Read"/>: default true, count only; false, delete expired rows),
    /// the read-only <see cref="IRetentionPurgeMode"/>, and the admin surface's tracker and service. The mode is fixed
    /// for the process lifetime; a value that is not a boolean throws here, before the host is built.
    /// </summary>
    public static IServiceCollection AddPlatformProRetention(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var dryRun = RetentionPurgeModeConfiguration.Read(configuration);
        services.AddProRetention(o => o.DryRun = dryRun);
        services.AddSingleton<IRetentionPurgeMode, RetentionPurgeMode>();
        services.AddSingleton<RetentionExecutionTracker>();
        services.AddSingleton<IRetentionAdminService, RetentionAdminService>();
        return services;
    }
}
