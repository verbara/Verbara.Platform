// R5.2 PC.3 (B.7) — RBAC re-seed CLI tool. Closes the existing-tenant migration
// gap when the canonical permission catalog grows: fresh tenants pick up new
// permissions automatically via RoleTemplateSeeder + RbacMigrationSeeder, but
// existing tenants need this tool to re-align their tenant_role_permissions.
using Verbara.Platform.Storage.Postgres.Seeds;
using Npgsql;

namespace Verbara.Platform.Tools.RbacReseed;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string? connectionString = ParseConnectionString(args)
            ?? Environment.GetEnvironmentVariable("CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "ERROR: connection string required via --connection / -c argument or CONNECTION_STRING env var.");
            PrintUsage();
            return 1;
        }

        IReadOnlyList<string> tenantIds;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        if (HasFlag(args, "--all"))
        {
            tenantIds = await ListActiveTenantsAsync(conn);
            Console.WriteLine($"Discovered {tenantIds.Count} active tenant(s).");
        }
        else
        {
            string? csv = GetArgValue(args, "--tenants");
            if (string.IsNullOrWhiteSpace(csv))
            {
                Console.Error.WriteLine(
                    "ERROR: provide either --all or --tenants tenant-a,tenant-b.");
                PrintUsage();
                return 1;
            }
            tenantIds = csv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (tenantIds.Count == 0)
        {
            Console.WriteLine("No tenants to process. Exiting.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Verbara.Platform — RBAC platform_admin re-seed");
        Console.WriteLine("===============================================");
        Console.WriteLine($"Canonical permission count: {RoleTemplateSeeder.GetCanonicalPermissions().Count}");
        Console.WriteLine();

        var report = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            tenantIds, conn, CancellationToken.None);

        int totalAdded = 0, totalRemoved = 0, alignedCount = 0;
        foreach (var (tenantId, result) in report)
        {
            if (result.PermissionsAdded == 0 && result.PermissionsRemoved == 0)
            {
                // Either tenant lacks a platform_admin role (skipped) or already aligned.
                Console.WriteLine($"  {tenantId,-40} aligned (no changes)");
                alignedCount++;
                continue;
            }
            Console.WriteLine(
                $"  {tenantId,-40} +{result.PermissionsAdded} added, -{result.PermissionsRemoved} removed");
            totalAdded += result.PermissionsAdded;
            totalRemoved += result.PermissionsRemoved;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Done. Tenants processed: {report.Count} ({alignedCount} aligned). " +
            $"Total: +{totalAdded} added, -{totalRemoved} removed.");
        return 0;
    }

    private static async Task<IReadOnlyList<string>> ListActiveTenantsAsync(NpgsqlConnection conn)
    {
        // status = 0 → Active (matches Verbara.Sdk.Pro.MultiTenant.TenantStatus.Active)
        const string sql = "SELECT tenant_id FROM tenants WHERE status = 0 ORDER BY tenant_id";
        var ids = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static string? ParseConnectionString(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--connection" or "-c")
                return args[i + 1];
        }
        return null;
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Verbara.Platform — RBAC re-seed CLI

            Re-aligns the per-tenant `platform_admin` role permissions with the
            current canonical permission catalog declared in
            RoleTemplateSeeder.AllPermissions(). Required after deploying a
            release that adds new permission keys (e.g. R5.2 added 7 keys for
            security/audit/retention administration).

            Usage:
              dotnet run --project tools/RbacReseed -- --tenants tenant-a,tenant-b [--connection <conn-string>]
              dotnet run --project tools/RbacReseed -- --all                       [--connection <conn-string>]

            Connection string sources (first match wins):
              1. --connection <conn-string> argument
              2. -c <conn-string>            argument
              3. CONNECTION_STRING           env var

            Examples:
              CONNECTION_STRING='Host=localhost;Database=asterisk_platform;Username=postgres;Password=postgres' \
                dotnet run --project tools/RbacReseed -- --all

              dotnet run --project tools/RbacReseed -- --tenants acme,globex \
                --connection 'Host=localhost;Database=asterisk_platform;Username=postgres;Password=postgres'

            Behavior:
              * For each provided tenant, removes role_permission rows that are
                no longer in the canonical catalog and inserts rows for any
                canonical permission currently missing on the platform_admin role.
              * Operates inside a per-tenant transaction (failure on tenant N
                does not corrupt tenants 1..N-1).
              * Idempotent — re-running prints `aligned (no changes)`.
              * Tenants without a platform_admin role are skipped (run the
                RbacSeederOrchestrator first).
            """);
    }
}
