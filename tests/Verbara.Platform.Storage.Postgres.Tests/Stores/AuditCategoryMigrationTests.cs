using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// audit-categories-accepted (#694): a database built from the real migrations accepts an audit entry, written through
/// the audit service and the Postgres store, for every category the code writes; keeps every category it accepted
/// before; and refuses an unknown one. Migration 033 also runs on a database that already holds audit rows, and a
/// second run leaves the same check. Live-DB (Testcontainers).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditCategoryMigrationTests : IClassFixture<ConversationClockFixture>
{
    private const string Migration033 = "033_AuditCsatCategory.sql";

    /// <summary>
    /// Every category the code under <c>src/</c> writes. The governance test
    /// <c>AuditCategoryGuardTests.LiveDbTest_ShouldWriteEveryCategoryTheCodeWrites_WhenListingThem</c> fails when this
    /// list and the code disagree.
    /// </summary>
    private static readonly string[] CategoriesTheCodeWrites =
    [
        "admin", "auth", "billing", "config", "contact", "conversations", "csat", "data_access", "license",
        "operational", "queues", "rbac", "reports",
    ];

    private static readonly string[] CategoriesBefore033 =
    [
        "auth", "billing", "config", "tenant", "security", "impersonation", "retention", "data", "rbac",
        "data_access", "admin", "conversations", "queues", "reports", "operational", "license", "contact",
    ];

    private readonly ConversationClockFixture _fixture;

    public AuditCategoryMigrationTests(ConversationClockFixture fixture) => _fixture = fixture;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    }

    private static Task RecordAsync(NpgsqlDataSource ds, string category) =>
        new DefaultAuditService(new PostgresAuditStore(ds), new FixedClock()).RecordAsync(
            new TenantId("t-audit"),
            category: category,
            action: $"{category}.probe",
            severity: "info",
            actorId: "system",
            actorType: "system",
            targetId: "target-1",
            targetType: "probe");

    private static Task InsertAsync(NpgsqlDataSource ds, string entryId, string category) =>
        ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, occurred_at, " +
            "category, severity, actor_type) VALUES (@EntryId, 't-audit', 'probe', 'probe', 'p-1', now(), " +
            "@Category, 'info', 'system')",
            new NpgsqlParameter("EntryId", entryId),
            new NpgsqlParameter("Category", category));

    private static async Task<long> CountAsync(NpgsqlDataSource ds, string category) =>
        (long)(await ConversationClockFixture.ScalarAsync(ds,
            "SELECT COUNT(*) FROM audit_entries WHERE category = @Category",
            new NpgsqlParameter("Category", category)))!;

    [Fact]
    public async Task AuditService_ShouldSaveAnEntryForEveryCategoryTheCodeWrites_WhenEveryMigrationIsApplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();

        foreach (var category in CategoriesTheCodeWrites)
        {
            var act = () => RecordAsync(ds, category);
            await act.Should().NotThrowAsync("the code writes audit entries with the category `{0}`", category);
            (await CountAsync(ds, category)).Should().Be(1);
        }
    }

    [Fact]
    public async Task AuditEntries_ShouldKeepEveryEarlierCategoryAndRefuseAnUnknownOne_WhenEveryMigrationIsApplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();

        foreach (var category in CategoriesBefore033)
            await InsertAsync(ds, $"e-{category}", category);

        var unknown = () => InsertAsync(ds, "e-unknown", "users");
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Migration033_ShouldAcceptCsatAndKeepExistingRows_WhenRunOnADatabaseThatAlreadyHasAuditRows()
    {
        var ds = await _fixture.CreateDatabaseBeforeAsync(Migration033);
        foreach (var category in CategoriesBefore033)
            await InsertAsync(ds, $"old-{category}", category);
        var refusedBefore = () => InsertAsync(ds, "e-csat-before", "csat");
        (await refusedBefore.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");

        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration033);

        await RecordAsync(ds, "csat");
        (await CountAsync(ds, "csat")).Should().Be(1);
        foreach (var category in CategoriesBefore033)
            (await CountAsync(ds, category)).Should().Be(1, "the migration keeps the row of category `{0}`", category);
    }

    [Fact]
    public async Task Migration033_ShouldLeaveTheSameCheck_WhenReapplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await RecordAsync(ds, "csat");

        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration033);

        await RecordAsync(ds, "csat");
        (await CountAsync(ds, "csat")).Should().Be(2);
        var unknown = () => InsertAsync(ds, "e-unknown", "users");
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }
}
