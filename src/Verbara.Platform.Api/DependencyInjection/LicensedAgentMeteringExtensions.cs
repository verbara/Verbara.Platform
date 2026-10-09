using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// licensed-agent-metering — the one composition-root entry point of the change (design D10: Program.cs
/// spends a single line on it). Slice 1 binds the account-status seam that routing, the switchboard and
/// the PJSIP desired state consult (design D2); slice 2 adds the licence-id seam of the ledger, the in-memory
/// licensed-agent writer (the Postgres one is registered by <c>AddPostgresStorage</c>), and the worker that
/// runs the daily close and the 15-month purge (design D5-D8); slice 3 adds the read side of the peaks report
/// and the export (licensed-agent-reporting).
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

        // Slice 2 — the licence id each ledger append is stamped with (read once per append, design D9).
        services.TryAddSingleton<ILicenseIdSource>(sp => new ProLicenseIdSource(sp.GetRequiredService<ILicenseStatus>()));

        // The licensed-agent writer: AddPostgresStorage registers the Postgres one; otherwise the in-memory twin,
        // built over the UNDECORATED inner stores so the realtime side effects run once, after the commit.
        services.TryAddSingleton(sp => new InMemoryLicensedAgentChangeWriter(
            sp.GetKeyedService<IAgentStore>(RealtimeSyncingStoresExtensions.AgentStoreInner) ?? sp.GetRequiredService<IAgentStore>(),
            sp.GetKeyedService<IUserStore>(AuthHotpathCacheKeys.UserStoreInner) ?? sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<InMemoryLicenseAgentLedger>()));
        services.TryAddSingleton<ILicensedAgentChangeWriter>(sp => sp.GetRequiredService<InMemoryLicensedAgentChangeWriter>());
        services.TryAddSingleton<ILicensedUserChangeWriter>(sp => sp.GetRequiredService<InMemoryLicensedAgentChangeWriter>());
        // The switchboard depends on the ownership half only.
        services.TryAddSingleton<ILicensedAgentOwnershipWriter>(sp => sp.GetRequiredService<ILicensedAgentChangeWriter>());

        // Slice 3 — the read side of GET /management/licensing/agents and its export: AddPostgresStorage registers
        // the Postgres reader; otherwise the in-memory one over the in-memory ledger (no daily close runs there).
        services.TryAddSingleton<ILicensedAgentReportReader>(sp => new InMemoryLicensedAgentReportReader(
            sp.GetRequiredService<InMemoryLicenseAgentLedger>(), sp.GetRequiredService<ITenantStore>()));

        // The daily close and the fixed 15-month purge. Resolves its storage at the first tick, never at host
        // start; without Postgres it returns at once.
        services.AddHostedService<LicenseAgentDailyCloseWorker>();

        return services;
    }
}
