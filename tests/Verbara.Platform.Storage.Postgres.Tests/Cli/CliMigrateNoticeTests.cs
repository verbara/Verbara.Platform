using Npgsql;
using NpgsqlTypes;

namespace Verbara.Platform.Storage.Postgres.Tests.Cli;

/// <summary>
/// cli-migrate-prints-migration-notices (#711): an operator who upgrades with the CLI's <c>migrate</c> command instead
/// of letting the Api migrate at start-up reads, in its output, what a migration reported (<c>RAISE NOTICE</c>), with
/// the migration's name — here how many repeated email or SMS ratings <c>035</c> removed.
/// </summary>
public sealed class CliMigrateNoticeTests(CliMigrateFixture postgres) : IClassFixture<CliMigrateFixture>
{
    private const string Migration035 = "035_SurveyResponseOnePerDispatch.sql";
    private static readonly DateTimeOffset Captured = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The tracking table as the Api's runner creates it.</summary>
    private const string ApiTrackingTable =
        "CREATE TABLE _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())";

    /// <summary>The tracking table as the CLI created it before #711: no default on <c>applied_at</c>.</summary>
    private const string CliTrackingTable =
        "CREATE TABLE _migrations (name TEXT NOT NULL PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL)";

    [Theory]
    [InlineData(ApiTrackingTable)]
    [InlineData(CliTrackingTable)]
    public async Task Migrate_ShouldPrintTheRemovedRatingCountWithTheMigrationName_WhenItApplies035OverRepeatedRatings(
        string trackingTable)
    {
        var connectionString = await postgres.DatabaseBeforeAsync(Migration035, trackingTable);
        // Three ratings of one email dispatch and two of one SMS dispatch: 035 keeps the first of each, removing three.
        await InsertRatingAsync(connectionString, "r-email-1", "conv-email", "email", Captured);
        await InsertRatingAsync(connectionString, "r-email-2", "conv-email", "email", Captured.AddMinutes(1));
        await InsertRatingAsync(connectionString, "r-email-3", "conv-email", "email", Captured.AddMinutes(2));
        await InsertRatingAsync(connectionString, "r-sms-1", "conv-sms", "sms", Captured);
        await InsertRatingAsync(connectionString, "r-sms-2", "conv-sms", "sms", Captured.AddMinutes(1));

        var run = await CliMigrateFixture.RunCliAsync("migrate", "--connection", connectionString);

        run.ExitCode.Should().Be(0, "migrate applies 035 over the repeated ratings; {0}", run);
        run.StandardOutput.Should().Contain(
            $"Migration {Migration035} reported (NOTICE): 035_SurveyResponseOnePerDispatch: removed 3 later email or SMS rating(s)",
            "the operator reads in the CLI's output how many ratings the upgrade removed, and from which migration; {0}", run);
        (await CliMigrateFixture.CountAsync(connectionString, "SELECT count(*) FROM _migrations WHERE name = @Name",
                new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = Migration035 }))
            .Should().Be(1, "035 is recorded as applied");
        (await CliMigrateFixture.CountAsync(connectionString, "SELECT count(*) FROM survey_responses"))
            .Should().Be(2, "the first rating of each dispatch is kept");
    }

    [Fact]
    public async Task Migrate_ShouldPrintNoNotice_WhenNothingIsPending()
    {
        var connectionString = await postgres.DatabaseBeforeAsync(Migration035, ApiTrackingTable);
        (await CliMigrateFixture.RunCliAsync("migrate", "--connection", connectionString)).ExitCode.Should().Be(0);

        var second = await CliMigrateFixture.RunCliAsync("migrate", "--connection", connectionString);

        second.ExitCode.Should().Be(0, "{0}", second);
        second.StandardOutput.Should().Contain("Database is up to date.", "{0}", second);
        second.StandardOutput.Should().NotContain(" reported (",
            "nothing was applied, and the runner's own bookkeeping notices are not a migration's; {0}", second);
    }

    [Fact]
    public async Task Migrate_ShouldExitWithOneAndRecordNothing_WhenAMigrationFails()
    {
        var connectionString = await postgres.DatabaseBeforeAsync(Migration035, ApiTrackingTable);
        // 035 reads survey_responses: without it the migration fails on the server.
        await CliMigrateFixture.ExecuteAsync(connectionString, "ALTER TABLE survey_responses RENAME TO survey_responses_moved");

        var run = await CliMigrateFixture.RunCliAsync("migrate", "--connection", connectionString);

        run.ExitCode.Should().Be(1, "a failed migration fails the command; {0}", run);
        run.StandardError.Should().Contain($"{Migration035} ... FAILED (42P01:",
            "the operator reads which migration failed and why; {0}", run);
        run.StandardOutput.Should().NotContain("Database is up to date.", "{0}", run);
        run.StandardOutput.Should().NotContain("Applied ", "{0}", run);
        (await CliMigrateFixture.CountAsync(connectionString, "SELECT count(*) FROM _migrations WHERE name = @Name",
                new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = Migration035 }))
            .Should().Be(0, "the failed migration is not recorded as applied");
    }

    private static Task InsertRatingAsync(
        string connectionString, string responseId, string conversationId, string channel, DateTimeOffset capturedAt) =>
        CliMigrateFixture.ExecuteAsync(connectionString,
            "INSERT INTO survey_responses (response_id, survey_id, tenant_id, conversation_id, contact_id, answers, " +
            "submitted_at, channel, queue_name, rating, captured_at) VALUES (@ResponseId, 'srv-1', 't-cli', " +
            "@ConversationId, @ConversationId, '[]'::jsonb, @CapturedAt, @Channel, 'support', 4, @CapturedAt)",
            new NpgsqlParameter("ResponseId", NpgsqlDbType.Text) { Value = responseId },
            new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId },
            new NpgsqlParameter("Channel", NpgsqlDbType.Text) { Value = channel },
            new NpgsqlParameter("CapturedAt", NpgsqlDbType.TimestampTz) { Value = capturedAt });
}
