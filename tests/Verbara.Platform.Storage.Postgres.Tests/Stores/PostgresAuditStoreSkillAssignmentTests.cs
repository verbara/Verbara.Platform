using FluentAssertions.Execution;
using Npgsql;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// routing-skills-stay-in-their-tenant, design D5: the Api records an agent skill assignment as a JSON string written
/// through its source-generated context, and <see cref="PostgresAuditStore"/> stores that string as a JSON object in
/// <c>before_json</c>/<c>after_json</c>. An object passed instead would be serialized by reflection, which the Native
/// AOT image does not have, so only the string form is proven to keep the fields there. The literals are the exact
/// text the Api writes (asserted on the Api side by <c>SkillAuditFromWriteTests</c>; this project does not reference
/// the Api). Runs on the pinned PostgreSQL 18 image with every embedded migration applied.
/// </summary>
public sealed class PostgresAuditStoreSkillAssignmentTests : IClassFixture<PinnedPostgres18MigrationsFixture>
{
    private const string ReplacedJson = """{"agentId":"a-1","skillName":"Billing","proficiency":5}""";
    private const string StoredJson = """{"agentId":"a-1","skillName":"billing","proficiency":9}""";

    private readonly PinnedPostgres18MigrationsFixture _fixture;

    public PostgresAuditStoreSkillAssignmentTests(PinnedPostgres18MigrationsFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreBothAssignmentsAsJsonObjects_WhenTheChangesAreJsonStrings()
    {
        var store = new PostgresAuditStore(_fixture.DataSource);
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = new TenantId("t-skills"),
            Action = "skill.assigned",
            Category = "config",
            Severity = "info",
            ActorId = "admin-1",
            ActorType = "user",
            TargetType = "agent",
            TargetId = "a-1",
            Changes = new AuditChanges(Before: ReplacedJson, After: StoredJson),
            OccurredAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(entry, CancellationToken.None);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT jsonb_typeof(before_json), before_json->>'agentId', before_json->>'skillName',
                   (before_json->>'proficiency')::int,
                   jsonb_typeof(after_json), after_json->>'agentId', after_json->>'skillName',
                   (after_json->>'proficiency')::int
            FROM audit_entries WHERE entry_id = @EntryId
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("EntryId", entry.EntryId.Value));
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the entry was saved");

        using (new AssertionScope())
        {
            reader.GetString(0).Should().Be("object", "the replaced assignment is stored as a JSON object");
            reader.GetString(1).Should().Be("a-1");
            reader.GetString(2).Should().Be("Billing");
            reader.GetInt32(3).Should().Be(5);
            reader.GetString(4).Should().Be("object", "the stored assignment is stored as a JSON object");
            reader.GetString(5).Should().Be("a-1");
            reader.GetString(6).Should().Be("billing");
            reader.GetInt32(7).Should().Be(9);
        }
    }
}
