using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.2; design D2): the Postgres account-status seam answers a
/// whole batch with one query — only users that exist in the tenant with status Active (0) come back.
/// </summary>
public sealed class PostgresAgentAccountStatusLookupTests : IClassFixture<AgentIdentityFixture>
{
    private readonly AgentIdentityFixture _fixture;

    public PostgresAgentAccountStatusLookupTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AgentAccountStatusLookup_ShouldReturnOnlyActiveUsersOfTheTenant_WhenAskedInBatch()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertUserAsync(ds, "t1", "active", status: 0);
        await AgentIdentityFixture.InsertUserAsync(ds, "t1", "suspended", status: 1);
        await AgentIdentityFixture.InsertUserAsync(ds, "t1", "deactivated", status: 2);
        await AgentIdentityFixture.InsertUserAsync(ds, "t2", "other-tenant", status: 0);
        var sut = new PostgresAgentAccountStatusLookup(ds);

        var active = await sut.GetActiveUserIdsAsync(
            new TenantId("t1"),
            [EntityId.From("active"), EntityId.From("suspended"), EntityId.From("deactivated"),
             EntityId.From("missing"), EntityId.From("other-tenant"), EntityId.From("active")],
            CancellationToken.None);

        active.Should().BeEquivalentTo([EntityId.From("active")]);
    }

    [Fact]
    public async Task AgentAccountStatusLookup_ShouldSeeAStatusWrite_OnTheNextCall()
    {
        var ds = await _fixture.CreateDatabaseAsync(before018: false);
        await AgentIdentityFixture.InsertUserAsync(ds, "t1", "u1", status: 0);
        var sut = new PostgresAgentAccountStatusLookup(ds);
        var tenant = new TenantId("t1");
        (await ((Queues.Services.IAgentAccountStatusLookup)sut).IsActiveAsync(tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeTrue();

        await AgentIdentityFixture.ExecuteAsync(ds, "UPDATE users SET status = 1 WHERE user_id = 'u1'");

        (await ((Queues.Services.IAgentAccountStatusLookup)sut).IsActiveAsync(tenant, EntityId.From("u1"), CancellationToken.None))
            .Should().BeFalse("there is no cache: a status write takes effect on the next decision");
    }

    [Fact]
    public async Task AgentAccountStatusLookup_ShouldReturnEmpty_WhenNoIdsAreGiven()
    {
        var sut = new PostgresAgentAccountStatusLookup(await _fixture.CreateDatabaseAsync(before018: false));

        (await sut.GetActiveUserIdsAsync(new TenantId("t1"), [], CancellationToken.None)).Should().BeEmpty();
    }
}
