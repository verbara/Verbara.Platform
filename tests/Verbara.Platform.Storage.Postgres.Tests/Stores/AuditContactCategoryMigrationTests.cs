using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// contacts-writes-follow-their-permissions, task 2.6 (design D17): every contact audit record carries the category
/// <c>contact</c>, so a database built from the real migrations must accept it in <c>audit_entries</c>, keep accepting
/// every category it accepted before, and still refuse an unknown one. Live-DB (Testcontainers).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditContactCategoryMigrationTests : IClassFixture<ConversationClockFixture>
{
    private static readonly string[] EarlierCategories =
    [
        "auth", "billing", "config", "tenant", "security", "impersonation", "retention", "data", "rbac",
        "data_access", "admin", "conversations", "queues", "reports", "operational", "license",
    ];

    private readonly ConversationClockFixture _fixture;

    public AuditContactCategoryMigrationTests(ConversationClockFixture fixture) => _fixture = fixture;

    private static Task InsertAsync(NpgsqlDataSource ds, string entryId, string category) =>
        ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO audit_entries (entry_id, tenant_id, action, entity_type, entity_id, occurred_at, " +
            "category, severity, actor_type) VALUES (@EntryId, 't-audit', 'contact.opted_out', 'contact', 'c-1', now(), " +
            "@Category, 'info', 'user')",
            new NpgsqlParameter("EntryId", entryId),
            new NpgsqlParameter("Category", category));

    [Fact]
    public async Task AuditEntries_ShouldAcceptTheContactCategory_WhenEveryMigrationIsApplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();

        var act = () => InsertAsync(ds, "e-contact", "contact");

        await act.Should().NotThrowAsync("a contact audit record is written with the category `contact`");
    }

    [Fact]
    public async Task AuditEntries_ShouldKeepEveryEarlierCategoryAndRefuseAnUnknownOne_WhenEveryMigrationIsApplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();

        foreach (var category in EarlierCategories)
            await InsertAsync(ds, $"e-{category}", category);

        var unknown = () => InsertAsync(ds, "e-unknown", "contacts");
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Migration029_ShouldLeaveTheSameCheck_WhenReapplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await InsertAsync(ds, "e-contact", "contact");

        await ConversationClockFixture.ApplyMigrationAsync(ds, ContactStoreFixture.AuditContactCategoryMigration);

        await InsertAsync(ds, "e-contact-2", "contact");
        var unknown = () => InsertAsync(ds, "e-unknown", "contacts");
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }
}
