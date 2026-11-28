using Npgsql;
using Verbara.Platform.Storage.Postgres;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();
string? connectionString = ParseConnectionString(args);

if (connectionString is null)
{
    Console.Error.WriteLine("ERROR: --connection <connection-string> is required.");
    PrintUsage();
    return 1;
}

return command switch
{
    "migrate" => RunMigrate(connectionString),
    "doctor"  => await RunDoctorAsync(connectionString),
    _         => UnknownCommand(command),
};

// ---------------------------------------------------------------------------
// migrate
// ---------------------------------------------------------------------------

static int RunMigrate(string connectionString)
{
    Console.WriteLine("Verbara.Platform — Database Migration");
    Console.WriteLine("======================================");

    // The Api's own runner (#711): the same transactions, tracking table and embedded migrations, and every notice
    // a migration raises is printed under it with its migration's name, as the Api logs it at start-up (#701).
    var progress = new ConsoleMigrationProgress();
    try
    {
        PlatformMigrationRunner.Apply(connectionString, progress);
    }
    catch (InvalidOperationException) when (progress.FailedMigration is not null)
    {
        // Already printed by the progress: the failed migration and its error.
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine(progress.AppliedCount == 0
        ? "Database is up to date."
        : $"Applied {progress.AppliedCount} migration(s).");
    return 0;
}

// ---------------------------------------------------------------------------
// doctor
// ---------------------------------------------------------------------------

static async Task<int> RunDoctorAsync(string connectionString)
{
    Console.WriteLine("Verbara.Platform — Database Health Check");
    Console.WriteLine("=========================================");

    var expectedTables = new[]
    {
        // Identity
        "users",
        "api_keys",
        // Auth
        "refresh_tokens",
        "auth_events",
        "tenant_auth_config",
        // RBAC
        "permissions",
        "role_templates",
        "role_template_permissions",
        "tenant_roles",
        "tenant_role_permissions",
        "user_roles",
        // Conversations
        "conversations",
        "messages",
        "contacts",
        // Queues
        "queue_configs",
        "agents",
        "queue_memberships",
        // Channels
        "tenant_channel_configs",
        // Flows
        "flow_definitions",
        "flow_executions",
        // Bot
        "bot_configurations",
        // Automation
        "automation_rules",
        "scheduled_timers",
        "automation_execution_logs",
        // KnowledgeBase
        "articles",
        // Teams & Cases
        "teams",
        "cases",
        "dispositions",
        "wrap_up_records",
        "service_accounts",
        // Surveys
        "surveys",
        "survey_responses",
        // Audit
        "audit_entries",
        // Media
        "media_files",
        // Migrations tracking
        "_migrations",
        // Pro package schema tracking (created by EnsureSchemaAsync)
        "_schema_dialer",
        "_schema_realtime",
        "_schema_eventstore",
        "_schema_analytics",
        "_schema_callanalytics",
        "_schema_agentassist",
    };

    bool allOk = true;

    try
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var conn = await dataSource.OpenConnectionAsync();

        Console.WriteLine("  Connection ... OK");

        foreach (string table in expectedTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = $1";
            cmd.Parameters.AddWithValue(table);
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            bool exists = count > 0;
            if (exists)
            {
                Console.WriteLine($"  Table {table,-35} OK");
            }
            else
            {
                Console.WriteLine($"  Table {table,-35} MISSING");
                allOk = false;
            }
        }

        // Report applied migrations
        try
        {
            await using var migCmd = conn.CreateCommand();
            migCmd.CommandText = "SELECT name, applied_at FROM _migrations ORDER BY applied_at";
            await using var reader = await migCmd.ExecuteReaderAsync();
            Console.WriteLine();
            Console.WriteLine("  Applied migrations:");
            bool any = false;
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"    {reader.GetString(0)}  ({reader.GetDateTime(1):u})");
                any = true;
            }
            if (!any)
                Console.WriteLine("    (none)");
        }
        catch
        {
            // _migrations table may not exist yet
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Connection ... FAILED ({ex.Message})");
        return 1;
    }

    Console.WriteLine();
    if (allOk)
    {
        Console.WriteLine("Health: OK");
        return 0;
    }
    else
    {
        Console.WriteLine("Health: DEGRADED — run 'migrate' to apply missing schema.");
        return 1;
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string? ParseConnectionString(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--connection" or "-c")
            return args[i + 1];
    }
    return null;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Verbara.Platform CLI

        Usage:
          verbara-platform <command> --connection <connection-string>

        Commands:
          migrate    Apply pending database migrations
          doctor     Check database connectivity and schema health

        Examples:
          dotnet run --project tools/Verbara.Platform.Cli -- migrate --connection "Host=localhost;Database=platform"
          dotnet run --project tools/Verbara.Platform.Cli -- doctor  --connection "Host=localhost;Database=platform"
        """);
}

/// <summary>Prints the runner's progress the way <c>migrate</c> always has, plus every notice a migration raised.</summary>
sealed class ConsoleMigrationProgress : IMigrationProgress
{
    public int AppliedCount { get; private set; }

    public string? FailedMigration { get; private set; }

    public void Skipped(string migration) => Console.WriteLine($"  {migration} ... already applied");

    public void Applied(string migration, IReadOnlyList<MigrationNotice> notices)
    {
        AppliedCount++;
        Console.WriteLine($"  {migration} ... applied");
        foreach (var notice in notices)
            Console.WriteLine($"    {notice}");
    }

    public void Failed(string migration, Exception exception)
    {
        FailedMigration = migration;
        Console.Error.WriteLine($"  {migration} ... FAILED ({exception.Message})");
    }
}
