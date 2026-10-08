using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// licensed-agent-metering — the one composition-root entry point of the change (design D10: Program.cs
/// spends a single line on it). Slice 1 binds the account-status seam that routing, the switchboard and
/// the PJSIP desired state consult (design D2); later slices add the ledger writer, the daily close and
/// the retention purge here.
/// </summary>
public static class LicensedAgentMeteringExtensions
{
    /// <summary>
    /// Binds <see cref="IAgentAccountStatusLookup"/> to the implementation of the active storage mode:
    /// the Postgres one when <c>AddPostgresStorage</c> registered it, otherwise the in-memory one that
    /// <c>AddInMemoryStorage</c> registered. Resolved at first use, so the order relative to the storage
    /// call does not matter and no database is touched at host start.
    /// </summary>
    public static IServiceCollection AddLicensedAgentMetering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentAccountStatusLookup>(sp =>
            (IAgentAccountStatusLookup?)sp.GetService<PostgresAgentAccountStatusLookup>()
            ?? sp.GetService<InMemoryAgentAccountStatusLookup>()
            ?? throw new InvalidOperationException(
                "AddLicensedAgentMetering needs a storage mode: call AddPostgresStorage or AddInMemoryStorage."));

        // The force-offline teardown shared by the admin action and a status change that leaves access.
        services.TryAddSingleton<AgentForceOfflineService>();

        return services;
    }
}
