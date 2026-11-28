using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Conversations;

namespace Verbara.Platform.Api.Tests.Logging;

/// <summary>
/// migration-notices-reach-the-api-log (#701): a migration that removes rows reports how many with <c>RAISE NOTICE</c>,
/// and the Api, which applies the migrations at start-up, writes that notice to its log with the migration's name.
/// Each test gets a fresh database holding every migration before <c>035</c>, recorded in <c>_migrations</c>, and the
/// real host then starts over it, which is what applies <c>035</c>.
/// </summary>
public sealed class MigrationNoticeLoggingLiveDbTests
    : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const string Migration = "035_SurveyResponseOnePerDispatch.sql";
    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";
    private static readonly string RunnerCategory = typeof(DatabaseMigrationService).FullName!;
    private static readonly DateTimeOffset Captured = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public MigrationNoticeLoggingLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) =>
        _postgres = postgres;

    [Fact]
    public async Task Deploy_ShouldLogTheRemovedRatingCountWithTheMigrationName_WhenTheApiApplies035OverRepeatedRatings()
    {
        var connectionString = await DatabaseBeforeTheMigrationAsync();
        // Three ratings of one email dispatch and two of one SMS dispatch: 035 keeps the first of each, removing three.
        await InsertRatingAsync(connectionString, "r-email-1", "conv-email", "email", Captured);
        await InsertRatingAsync(connectionString, "r-email-2", "conv-email", "email", Captured.AddMinutes(1));
        await InsertRatingAsync(connectionString, "r-email-3", "conv-email", "email", Captured.AddMinutes(2));
        await InsertRatingAsync(connectionString, "r-sms-1", "conv-sms", "sms", Captured);
        await InsertRatingAsync(connectionString, "r-sms-2", "conv-sms", "sms", Captured.AddMinutes(1));

        using var host = new MigratingApiHost(connectionString) { CaptureLogs = true };
        using var client = host.CreateClient();

        (await RecordedAsync(connectionString)).Should().BeTrue("the host applied 035 while it started");
        var records = host.CapturedLogs.Records
            .Where(r => r.Category == RunnerCategory && r.Message.Contains(Migration, StringComparison.Ordinal))
            .ToList();
        records.Should().ContainSingle(
            r => r.Level == LogLevel.Information
                && r.Message.Contains("035_SurveyResponseOnePerDispatch: removed 3 later email or SMS rating(s)", StringComparison.Ordinal),
            "the operator reads in the Api log how many ratings the upgrade removed, and from which migration; it logged {0}",
            string.Join(" | ", host.CapturedLogs.Records.Where(r => r.Category == RunnerCategory)));
    }

    [Fact]
    public async Task LogNotices_ShouldLogAWarningAtWarningAndANoticeAtInformation_WhenAMigrationRaisesBoth()
    {
        var connectionString = await EmptyDatabaseAsync();
        var notices = DatabaseMigrationService.ApplyMigrations(connectionString,
            [("900_Raises.sql", "DO $$ BEGIN RAISE WARNING 'check % rows', 2; RAISE NOTICE 'removed % rows', 7; END $$;")]);

        var capture = new LogRecordCapture();
        using (var factory = LoggerFactory.Create(logging => logging.AddProvider(capture)))
            DatabaseMigrationService.LogNotices(factory.CreateLogger(RunnerCategory), notices);

        capture.Records.Select(r => (r.Level, r.Message)).Should().Equal(
            (LogLevel.Warning, "Migration 900_Raises.sql reported (WARNING): check 2 rows"),
            (LogLevel.Information, "Migration 900_Raises.sql reported (NOTICE): removed 7 rows"));
    }

    [Fact]
    public async Task ApplyMigrations_ShouldReturnNoNotice_WhenNothingIsPendingAndItsOwnTableAlreadyExists()
    {
        var connectionString = await EmptyDatabaseAsync();
        DatabaseMigrationService.ApplyMigrations(connectionString, [("900_Raises.sql", "DO $$ BEGIN RAISE NOTICE 'once'; END $$;")])
            .Should().ContainSingle().Which.Should().Be(new Storage.Postgres.MigrationNotice("900_Raises.sql", "NOTICE", "once"));

        // The second run's CREATE TABLE IF NOT EXISTS _migrations raises "already exists, skipping": not a migration's.
        DatabaseMigrationService.ApplyMigrations(connectionString, [("900_Raises.sql", "DO $$ BEGIN RAISE NOTICE 'once'; END $$;")])
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyMigrations_ShouldNameTheNoticesOfTheMigrationsItCommitted_WhenALaterMigrationFails()
    {
        var connectionString = await EmptyDatabaseAsync();

        var act = () => DatabaseMigrationService.ApplyMigrations(connectionString,
        [
            ("900_Committed.sql", "DO $$ BEGIN RAISE NOTICE 'removed % rows', 5; END $$;"),
            ("901_Fails.sql", "DO $$ BEGIN RAISE NOTICE 'rolled back'; END $$; SELECT 1 / 0;"),
        ]);

        var failure = act.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().StartWith("Migration '901_Fails.sql' failed: ");
        failure.Message.Should().Contain("900_Committed.sql (NOTICE): removed 5 rows",
            "the host stops before its log exists, and 900 is not applied again on the next start");
        failure.Message.Should().NotContain("rolled back", "901 is rolled back and raises its notice again on the next start");
    }

    // ─── Harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<string> EmptyDatabaseAsync()
    {
        var name = $"mnl_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_postgres.ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();
        return _postgres.ConnectionStringFor(name);
    }

    /// <summary>A fresh database at the migration before <see cref="Migration"/>, as a deployed older release left it.</summary>
    private async Task<string> DatabaseBeforeTheMigrationAsync()
    {
        var name = $"mnl_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(_postgres.ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();

        var connectionString = _postgres.ConnectionStringFor(name);
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await ExecuteAsync(ds, "CREATE TABLE _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");
        foreach (var (file, sql) in Migrations().Where(m => string.CompareOrdinal(m.File, Migration) < 0))
        {
            await ExecuteAsync(ds, sql);
            await ExecuteAsync(ds, "INSERT INTO _migrations (name) VALUES (@Name)",
                new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = file });
        }

        return connectionString;
    }

    private static async Task InsertRatingAsync(
        string connectionString, string responseId, string conversationId, string channel, DateTimeOffset capturedAt)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await ExecuteAsync(ds,
            "INSERT INTO survey_responses (response_id, survey_id, tenant_id, conversation_id, contact_id, answers, " +
            "submitted_at, channel, queue_name, rating, captured_at) VALUES (@ResponseId, 'srv-1', 't-mnl', " +
            "@ConversationId, @ConversationId, '[]'::jsonb, @CapturedAt, @Channel, 'support', 4, @CapturedAt)",
            new NpgsqlParameter("ResponseId", NpgsqlDbType.Text) { Value = responseId },
            new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId },
            new NpgsqlParameter("Channel", NpgsqlDbType.Text) { Value = channel },
            new NpgsqlParameter("CapturedAt", NpgsqlDbType.TimestampTz) { Value = capturedAt });
    }

    private static async Task<bool> RecordedAsync(string connectionString)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM _migrations WHERE name = @Name");
        cmd.Parameters.Add(new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = Migration });
        return (long)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<(string File, string Sql)> Migrations()
    {
        var assembly = typeof(Storage.Postgres.ServiceCollectionExtensions).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }

    /// <summary>The real host over <paramref name="connectionString"/>, keeping every log record it writes.</summary>
    private sealed class MigratingApiHost(string connectionString) : AuthenticatedPlatformApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        }
    }
}
