namespace Verbara.Platform.Storage.Postgres.Tests.Cli;

/// <summary>
/// cli-migrate-prints-migration-notices (#711): the one migration runner the Api and the CLI share reports, per
/// migration, the notices it raised — and none of a migration that failed and was rolled back.
/// </summary>
public sealed class PlatformMigrationRunnerTests(CliMigrateFixture postgres) : IClassFixture<CliMigrateFixture>
{
    [Fact]
    public async Task Apply_ShouldReportEachMigrationWithOnlyItsOwnNotices_WhenTwoMigrationsRaise()
    {
        var connectionString = await postgres.EmptyDatabaseAsync();
        var progress = new RecordingProgress();

        var notices = PlatformMigrationRunner.Apply(connectionString,
        [
            ("901_Second.sql", "DO $$ BEGIN RAISE NOTICE 'removed % rows', 7; END $$;"),
            ("900_First.sql", "DO $$ BEGIN RAISE WARNING 'check % rows', 2; END $$;"),
        ], progress);

        progress.Events.Should().Equal(
            "applied 900_First.sql: Migration 900_First.sql reported (WARNING): check 2 rows",
            "applied 901_Second.sql: Migration 901_Second.sql reported (NOTICE): removed 7 rows");
        notices.Select(n => n.IsWarning).Should().Equal(true, false);
    }

    [Fact]
    public async Task Apply_ShouldReportTheMigrationAsSkipped_WhenItIsAlreadyRecorded()
    {
        var connectionString = await postgres.EmptyDatabaseAsync();
        PlatformMigrationRunner.Apply(connectionString, [("900_Once.sql", "DO $$ BEGIN RAISE NOTICE 'once'; END $$;")]);
        var progress = new RecordingProgress();

        PlatformMigrationRunner.Apply(connectionString, [("900_Once.sql", "DO $$ BEGIN RAISE NOTICE 'once'; END $$;")], progress)
            .Should().BeEmpty();

        progress.Events.Should().Equal("skipped 900_Once.sql");
    }

    [Fact]
    public async Task Apply_ShouldNotReportTheNoticesOfAFailedMigration_WhenItIsRolledBack()
    {
        var connectionString = await postgres.EmptyDatabaseAsync();
        var progress = new RecordingProgress();

        var act = () => PlatformMigrationRunner.Apply(connectionString,
        [
            ("900_Committed.sql", "DO $$ BEGIN RAISE NOTICE 'removed % rows', 5; END $$;"),
            ("901_Fails.sql", "DO $$ BEGIN RAISE NOTICE 'rolled back'; END $$; SELECT 1 / 0;"),
            ("902_Never.sql", "DO $$ BEGIN RAISE NOTICE 'never run'; END $$;"),
        ], progress);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().StartWith("Migration '901_Fails.sql' failed: ");
        progress.Events.Should().HaveCount(2);
        progress.Events[0].Should().Be("applied 900_Committed.sql: Migration 900_Committed.sql reported (NOTICE): removed 5 rows");
        progress.Events[1].Should().StartWith("failed 901_Fails.sql: ").And.NotContain("rolled back");
    }

    [Fact]
    public async Task Apply_ShouldRecordTheMigration_WhenTheTrackingTableHasNoDefaultOnAppliedAt()
    {
        // The tracking table as the CLI created it before #711.
        var connectionString = await postgres.EmptyDatabaseAsync();
        await CliMigrateFixture.ExecuteAsync(connectionString,
            "CREATE TABLE _migrations (name TEXT NOT NULL PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL)");

        PlatformMigrationRunner.Apply(connectionString, [("900_Any.sql", "SELECT 1;")]);

        (await CliMigrateFixture.CountAsync(connectionString, "SELECT count(*) FROM _migrations WHERE name = '900_Any.sql'"))
            .Should().Be(1);
    }

    [Fact]
    public void EmbeddedMigrations_ShouldListEveryMigrationFileByName_InOrdinalOrder()
    {
        var names = PlatformMigrationRunner.EmbeddedMigrations().Select(m => m.Name).ToList();

        names.Should().Contain(["001_Baseline.sql", "024_UserRolesUserForeignKey.sql", "035_SurveyResponseOnePerDispatch.sql"]);
        names.Should().BeInAscendingOrder(StringComparer.Ordinal);
        names.Should().OnlyHaveUniqueItems();
    }

    private sealed class RecordingProgress : IMigrationProgress
    {
        public List<string> Events { get; } = [];

        public void Skipped(string migration) => Events.Add($"skipped {migration}");

        public void Applied(string migration, IReadOnlyList<MigrationNotice> notices) =>
            Events.Add($"applied {migration}: {string.Join(" | ", notices)}");

        public void Failed(string migration, Exception exception) => Events.Add($"failed {migration}: {exception.Message}");
    }
}
