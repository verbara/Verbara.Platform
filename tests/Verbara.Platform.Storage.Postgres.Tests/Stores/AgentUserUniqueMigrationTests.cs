using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.4, 2.5; agent-identity-integrity): migration
/// <c>018_AgentUserUnique.sql</c> against a real Postgres — it enforces one agent per (tenant, user) on a
/// clean database, refuses loudly and changes nothing over duplicates, is not blocked by orphans — and
/// the agent store turns a second agent for the same user into <see cref="EntityAlreadyExistsException"/>,
/// race-free.
/// </summary>
public sealed class AgentUserUniqueMigrationTests : IClassFixture<AgentIdentityFixture>
{
    private readonly AgentIdentityFixture _fixture;

    public AgentUserUniqueMigrationTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AgentUserUnique_ShouldEnforceUniqueness_WhenDatabaseIsClean()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: true);
        await AgentIdentityFixture.InsertAgentAsync(ds, "t1", "a1", "u1");
        await AgentIdentityFixture.InsertAgentAsync(ds, "t1", "a2", "u2");
        await AgentIdentityFixture.InsertAgentAsync(ds, "t2", "a3", "u1"); // same user id, other tenant

        await AgentIdentityFixture.ApplyMigrationAsync(ds, AgentIdentityFixture.Migration018);

        (await AgentIdentityFixture.IndexExistsAsync(ds, "ux_agents_tenant_user")).Should().BeTrue();
        (await AgentIdentityFixture.IndexExistsAsync(ds, "idx_agents_user")).Should().BeFalse();
        var duplicate = () => AgentIdentityFixture.InsertAgentAsync(ds, "t1", "a4", "u1");
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ux_agents_tenant_user");
    }

    [Fact]
    public async Task AgentUserUnique_ShouldFailNamingEveryPair_WhenDuplicatesExist()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: true);
        var t0 = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await AgentIdentityFixture.InsertAgentAsync(ds, "tenant-a", "agent-a1", "user-u", extension: "1001", createdAt: t0);
        await AgentIdentityFixture.InsertAgentAsync(ds, "tenant-a", "agent-a2", "user-u", createdAt: t0.AddDays(1));
        await AgentIdentityFixture.InsertAgentAsync(ds, "tenant-b", "agent-b1", "user-v", createdAt: t0);
        await AgentIdentityFixture.InsertAgentAsync(ds, "tenant-b", "agent-b2", "user-v", createdAt: t0);
        await AgentIdentityFixture.InsertAgentAsync(ds, "tenant-b", "agent-b3", "user-w");
        var before = await SnapshotAgentsAsync(ds);

        var migrate = () => AgentIdentityFixture.ApplyMigrationAsync(ds, AgentIdentityFixture.Migration018);

        var error = (await migrate.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        error.MessageText.Should().Contain("2 user(s)")
            .And.Contain("tenant tenant-a, user user-u")
            .And.Contain("agent agent-a1 (created_at 2026-01-02T03:04:05Z, extension 1001)")
            .And.Contain("agent agent-a2")
            .And.Contain("tenant tenant-b, user user-v")
            .And.Contain("agent agent-b1").And.Contain("agent agent-b2")
            .And.Contain("scripts/ops/agents-identity-report.sql")
            .And.Contain("docs/operations/licensed-agent-metering-upgrade.md")
            .And.NotContain("user-w", "a user with one agent is not a duplicate");
        (await SnapshotAgentsAsync(ds)).Should().Equal(before, "the migration never deletes, merges or repoints an agent");
        (await AgentIdentityFixture.IndexExistsAsync(ds, "ux_agents_tenant_user")).Should().BeFalse();
        (await AgentIdentityFixture.IndexExistsAsync(ds, "idx_agents_user")).Should().BeTrue("the schema is left unchanged");
    }

    [Fact]
    public async Task AgentUserUnique_ShouldNotBlock_WhenOnlyOrphanAgentsExist()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: true);
        await AgentIdentityFixture.InsertUserAsync(ds, "t1", "u-present", status: 0);
        await AgentIdentityFixture.InsertAgentAsync(ds, "t1", "a-present", "u-present");
        await AgentIdentityFixture.InsertAgentAsync(ds, "t1", "a-orphan", "u-missing");

        await AgentIdentityFixture.ApplyMigrationAsync(ds, AgentIdentityFixture.Migration018);

        (await AgentIdentityFixture.IndexExistsAsync(ds, "ux_agents_tenant_user")).Should().BeTrue();
        (await AgentIdentityFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM agents")).Should().Be(2);
    }

    [Fact]
    public async Task SaveAsync_ShouldThrowEntityAlreadyExists_WhenUserAlreadyOwnsAgent()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        var store = new PostgresAgentStore(ds);
        var first = NewAgent("u1");
        await store.SaveAsync(first, CancellationToken.None);

        var second = () => store.SaveAsync(NewAgent("u1"), CancellationToken.None);

        (await second.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.EntityKind.Should().Be("agent");
        first.DisplayName = "Renamed";
        await store.SaveAsync(first, CancellationToken.None); // an update of the same agent is not a duplicate
        (await AgentIdentityFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM agents")).Should().Be(1);
        (await store.GetByUserIdAsync(Tenant, EntityId.From("u1"), CancellationToken.None))!.AgentId.Should().Be(first.AgentId);
    }

    [Fact]
    public async Task SaveAsync_ShouldLetExactlyOneSucceed_WhenTwoCreationsForOneUserRace()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        var store = new PostgresAgentStore(ds);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                await store.SaveAsync(NewAgent("racer"), CancellationToken.None);
                return "created";
            }
            catch (EntityAlreadyExistsException)
            {
                return "conflict";
            }
        }));

        outcomes.Should().BeEquivalentTo(["created", "conflict"]);
        (await AgentIdentityFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM agents WHERE user_id = 'racer'")).Should().Be(1);
    }

    private static readonly TenantId Tenant = new("t-store");

    private static Agent NewAgent(string userId) => new()
    {
        AgentId = EntityId.New(),
        TenantId = Tenant,
        UserId = EntityId.From(userId),
        DisplayName = "Agent",
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<List<string>> SnapshotAgentsAsync(NpgsqlDataSource ds)
    {
        var rows = new List<string>();
        await using var cmd = ds.CreateCommand(
            "SELECT tenant_id || '|' || agent_id || '|' || user_id || '|' || COALESCE(extension, '') FROM agents ORDER BY 1");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
