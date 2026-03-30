using System.Reflection;
using Npgsql;

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
    "migrate" => await RunMigrateAsync(connectionString),
    "doctor"  => await RunDoctorAsync(connectionString),
    _         => UnknownCommand(command),
};

// ---------------------------------------------------------------------------
// migrate
// ---------------------------------------------------------------------------

static async Task<int> RunMigrateAsync(string connectionString)
{
    Console.WriteLine("Asterisk.Platform — Database Migration");
    Console.WriteLine("======================================");

    await using var dataSource = NpgsqlDataSource.Create(connectionString);

    // Ensure _migrations tracking table exists
    await EnsureMigrationsTableAsync(dataSource);

    // Load applied migrations
    var applied = await GetAppliedMigrationsAsync(dataSource);

    // Discover embedded SQL migrations sorted by filename
    var migrations = GetEmbeddedMigrations();
    if (migrations.Count == 0)
    {
        Console.WriteLine("No migration files found.");
        return 0;
    }

    int ran = 0;
    foreach (var (name, sql) in migrations)
    {
        if (applied.Contains(name))
        {
            Console.WriteLine($"  SKIP  {name} (already applied)");
            continue;
        }

        Console.Write($"  RUN   {name} ... ");
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();

            await using var trackCmd = conn.CreateCommand();
            trackCmd.Transaction = tx;
            trackCmd.CommandText = "INSERT INTO _migrations (name, applied_at) VALUES ($1, now())";
            trackCmd.Parameters.AddWithValue(name);
            await trackCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            Console.WriteLine("OK");
            ran++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED");
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    if (ran == 0)
        Console.WriteLine("Database is already up to date.");
    else
        Console.WriteLine($"\nApplied {ran} migration(s) successfully.");

    return 0;
}

// ---------------------------------------------------------------------------
// doctor
// ---------------------------------------------------------------------------

static async Task<int> RunDoctorAsync(string connectionString)
{
    Console.WriteLine("Asterisk.Platform — Database Health Check");
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

static async Task EnsureMigrationsTableAsync(NpgsqlDataSource dataSource)
{
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
        "CREATE TABLE IF NOT EXISTS _migrations (" +
        "  name TEXT NOT NULL PRIMARY KEY," +
        "  applied_at TIMESTAMPTZ NOT NULL" +
        ")";
    await cmd.ExecuteNonQueryAsync();
}

static async Task<HashSet<string>> GetAppliedMigrationsAsync(NpgsqlDataSource dataSource)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name FROM _migrations";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result.Add(reader.GetString(0));
    return result;
}

static List<(string Name, string Sql)> GetEmbeddedMigrations()
{
    var assembly = Assembly.GetExecutingAssembly();
    var names = assembly.GetManifestResourceNames();

    var migrations = new List<(string, string)>();
    foreach (var resourceName in names)
    {
        // Resources are embedded as "Migrations\001_InitialSchema.sql" (backslash on Windows) or forward slash
        if (!resourceName.Contains("Migrations") || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            continue;

        // Extract the bare filename for tracking
        string fileName = resourceName
            .Replace('\\', '/')
            .Split('/')
            [^1];

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        string sql = reader.ReadToEnd();
        migrations.Add((fileName, sql));
    }

    migrations.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.Ordinal));
    return migrations;
}

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
        Asterisk.Platform CLI

        Usage:
          asterisk-platform <command> --connection <connection-string>

        Commands:
          migrate    Apply pending database migrations
          doctor     Check database connectivity and schema health

        Examples:
          dotnet run --project tools/Asterisk.Platform.Cli -- migrate --connection "Host=localhost;Database=platform"
          dotnet run --project tools/Asterisk.Platform.Cli -- doctor  --connection "Host=localhost;Database=platform"
        """);
}
