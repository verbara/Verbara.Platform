using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.CallAnalytics.Store;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The in-memory analytics stores of a host with no analytics or dialer database, so the analytics routes resolve their
/// stores either way. Moved out of <c>Program.cs</c> unchanged.
/// </summary>
internal static class ProAnalyticsInMemoryFallbackExtensions
{
    /// <summary>
    /// Registers an in-memory completed-session store, interval-snapshot store and call-analytics store, each only when no
    /// store of that type is registered yet. Call after the database-backed registrations.
    /// </summary>
    public static IServiceCollection AddProAnalyticsInMemoryFallbacks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(ICompletedSessionStore)))
            services.AddSingleton<ICompletedSessionStore, InMemoryCompletedSessionStore>();
        if (!services.Any(d => d.ServiceType == typeof(IIntervalSnapshotStore)))
            services.AddSingleton<IIntervalSnapshotStore, InMemoryIntervalSnapshotStore>();
        if (!services.Any(d => d.ServiceType == typeof(ICallAnalyticsStore)))
            services.AddSingleton<ICallAnalyticsStore, InMemoryCallAnalyticsStore>();
        return services;
    }
}
