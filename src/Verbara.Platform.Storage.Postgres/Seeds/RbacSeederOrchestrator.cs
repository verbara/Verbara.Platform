using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Seeds;

public static class RbacSeederOrchestrator
{
    public static async Task SeedRbacAsync(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Order matters: permissions first, then templates, then migration
        await PermissionSeeder.SeedAsync(dataSource, ct);
        await RoleTemplateSeeder.SeedAsync(dataSource, ct);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(
            dataSource, loggerFactory.CreateLogger(RbacMigrationLog.Category), ct);
    }
}
