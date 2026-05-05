using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Seeds;

public static class RbacSeederOrchestrator
{
    public static async Task SeedRbacAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        // Order matters: permissions first, then templates, then migration
        await PermissionSeeder.SeedAsync(dataSource, ct);
        await RoleTemplateSeeder.SeedAsync(dataSource, ct);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, ct);
    }
}
