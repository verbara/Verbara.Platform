using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering-follow-ups (design D2) — <see cref="PostgresAgentStore.ListPageAfterAsync"/> against a
/// real Postgres: a keyset page on the primary key <c>(tenant_id, agent_id)</c>, ascending, with an exclusive
/// <c>after</c> bound, at most <c>limit</c> rows, and only the named tenant's agents.
/// </summary>
public class PostgresAgentStoreListPageAfterTests : IClassFixture<AgentStoreAutoAnswerFixture>, IAsyncLifetime
{
    private readonly AgentStoreAutoAnswerFixture _fixture;
    private readonly PostgresAgentStore _sut;
    private static readonly TenantId Tenant = new("acme");
    private static readonly TenantId OtherTenant = new("globex");

    // Lower-case ASCII ids: their database-collation order and their ordinal order agree.
    private static readonly string[] TenantIdsAscending = ["agent-a", "agent-b", "agent-c", "agent-d", "agent-e"];

    public PostgresAgentStoreListPageAfterTests(AgentStoreAutoAnswerFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresAgentStore(_fixture.DataSource);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Agent NewAgent(TenantId tenant, string agentId) => new()
    {
        AgentId = EntityId.From(agentId),
        TenantId = tenant,
        UserId = EntityId.New(),
        DisplayName = "Agent " + agentId,
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedAsync()
    {
        // Inserted out of order, so the page order can only come from ORDER BY agent_id.
        foreach (var id in new[] { "agent-d", "agent-a", "agent-e", "agent-c", "agent-b" })
            await AgentInsert.InsertAsync(_fixture.DataSource, NewAgent(Tenant, id));

        // The other tenant's ids interleave with this tenant's, so a missing tenant filter would show.
        foreach (var id in new[] { "agent-0", "agent-bb", "agent-cc", "agent-z" })
            await AgentInsert.InsertAsync(_fixture.DataSource, NewAgent(OtherTenant, id));
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnThisTenantsAgentsAscending_WhenAfterIsNull()
    {
        await SeedAsync();

        var page = await _sut.ListPageAfterAsync(Tenant, null, 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal(TenantIdsAscending);
        page.Should().OnlyContain(a => a.TenantId == Tenant);
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldExcludeTheAfterBound_WhenAfterIsAnExistingAgent()
    {
        await SeedAsync();

        var page = await _sut.ListPageAfterAsync(Tenant, EntityId.From("agent-b"), 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal("agent-c", "agent-d", "agent-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldStartAfterTheBound_WhenAfterIsNotAnAgent()
    {
        await SeedAsync();

        var page = await _sut.ListPageAfterAsync(Tenant, EntityId.From("agent-bb"), 100, CancellationToken.None);

        page.Select(a => a.AgentId.Value).Should().Equal("agent-c", "agent-d", "agent-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnAtMostLimit_WhenMoreAgentsFollow()
    {
        await SeedAsync();

        var first = await _sut.ListPageAfterAsync(Tenant, null, 2, CancellationToken.None);
        var second = await _sut.ListPageAfterAsync(Tenant, first[^1].AgentId, 2, CancellationToken.None);
        var third = await _sut.ListPageAfterAsync(Tenant, second[^1].AgentId, 2, CancellationToken.None);

        first.Select(a => a.AgentId.Value).Should().Equal("agent-a", "agent-b");
        second.Select(a => a.AgentId.Value).Should().Equal("agent-c", "agent-d");
        third.Select(a => a.AgentId.Value).Should().Equal("agent-e");
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnEmpty_WhenAfterIsTheLastAgent()
    {
        await SeedAsync();

        var page = await _sut.ListPageAfterAsync(Tenant, EntityId.From("agent-e"), 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldReturnNoOtherTenantsAgent_WhenTheTenantHasNone()
    {
        await SeedAsync();

        var page = await _sut.ListPageAfterAsync(new TenantId("initech"), null, 100, CancellationToken.None);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task ListPageAfterAsync_ShouldMapTheFullRow_WhenAnAgentIsReturned()
    {
        var agent = NewAgent(Tenant, "agent-a");
        agent.Extension = "1001";
        agent.SipPassword = "s3cr3t";
        await AgentInsert.InsertAsync(_fixture.DataSource, agent);

        var page = await _sut.ListPageAfterAsync(Tenant, null, 10, CancellationToken.None);

        page.Should().ContainSingle();
        page[0].UserId.Should().Be(agent.UserId);
        page[0].DisplayName.Should().Be("Agent agent-a");
        page[0].Extension.Should().Be("1001");
        page[0].SipPassword.Should().Be("s3cr3t");
    }
}
