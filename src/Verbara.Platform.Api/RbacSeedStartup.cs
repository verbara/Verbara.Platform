using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Api;

/// <summary>
/// The startup RBAC seed (permissions, role templates, and the per-tenant role migration), run once from
/// <c>Program.cs</c> outside the <c>Testing</c> environment. A host without Postgres has nothing to seed.
/// </summary>
internal static class RbacSeedStartup
{
    public static Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct) =>
        RunAsync(services, logger, RbacSeederOrchestrator.SeedRbacAsync, ct);

    /// <summary>Runs <paramref name="seed"/> against the host's data source; the seam the tests drive.</summary>
    internal static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        Func<NpgsqlDataSource, ILoggerFactory, CancellationToken, Task> seed,
        CancellationToken ct)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
            return;

        try
        {
            // The role migration logs each tenant it fails or skips under its own category (harden-rbac-migration-seeder).
            await seed(dataSource, services.GetRequiredService<ILoggerFactory>(), ct);
            RbacSeedLog.SeedCompleted(logger);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // ADR-0037 — loud, but never fatal. A silent Console.WriteLine here is what let a
            // fully broken role model (5 of 11 templates seeded) go unnoticed for weeks. The
            // boot deliberately continues: a transient database fault must not brick the host.
            // A cancelled boot is not a failed seed, so cancellation is left to propagate.
            RbacSeedLog.SeedFailed(logger, ex);
        }
    }
}
