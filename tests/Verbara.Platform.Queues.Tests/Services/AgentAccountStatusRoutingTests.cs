using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests.Services;

/// <summary>
/// licensed-agent-metering slice 1 (agent-account-status-routing, design D2): the presence choke point
/// offers work only to agents whose user is Active, with one status lookup per decision, and the
/// enumeration helper reads every page.
/// </summary>
public class AgentAccountStatusRoutingTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId QueueId = EntityId.From("q-001");

    private static Agent MakeAgent(string id, AgentState state = AgentState.Available) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = Tenant,
        UserId = EntityId.From($"user-{id}"),
        DisplayName = id,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (InMemoryAgentPresenceService Sut, FakeAgentAccountStatusLookup Status, IAgentCapacityService Capacity) CreateSut(params Agent[] agents)
    {
        var agentStore = Substitute.For<IAgentStore>();
        var queueStore = Substitute.For<IQueueStore>();
        var capacity = Substitute.For<IAgentCapacityService>();
        agentStore.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Agent>(agents, agents.Length, 1, int.MaxValue));
        queueStore.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(new Queue { QueueId = QueueId, TenantId = Tenant, Name = "Q", CreatedAt = DateTimeOffset.UtcNow });
        capacity.HasCapacityAsync(Tenant, Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var status = new FakeAgentAccountStatusLookup();
        return (new InMemoryAgentPresenceService(agentStore, queueStore, capacity, status), status, capacity);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("deactivated")]
    [InlineData("missing")]
    public async Task GetAvailableAgentsAsync_ShouldExcludeAgent_WhenUserIsNotActive(string which)
    {
        var excluded = MakeAgent(which);
        var routable = MakeAgent("routable");
        var (sut, status, capacity) = CreateSut(excluded, routable);
        status.Inactive.Add(excluded.UserId);

        var result = await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        result.Select(a => a.AgentId).Should().Equal(routable.AgentId);
        await capacity.DidNotReceive().HasCapacityAsync(Tenant, excluded.AgentId, Arg.Any<ChannelType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAvailableAgentsAsync_ShouldOfferAgentAgain_WhenUserIsReactivated()
    {
        var agent = MakeAgent("a1");
        var (sut, status, _) = CreateSut(agent);
        status.Inactive.Add(agent.UserId);
        (await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None)).Should().BeEmpty();

        status.Inactive.Remove(agent.UserId);

        (await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None))
            .Select(a => a.AgentId).Should().Equal(agent.AgentId);
    }

    [Fact]
    public async Task GetAvailableAgentsAsync_ShouldLookUpStatusOncePerDecision_WhenManyAgentsAreCandidates()
    {
        var (sut, status, _) = CreateSut(MakeAgent("a1"), MakeAgent("a2"), MakeAgent("a3"), MakeAgent("off", AgentState.Offline));

        await sut.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.Voice, CancellationToken.None);

        status.Calls.Should().Be(1, "one batched lookup per routing decision, not one per agent");
    }

    [Fact]
    public async Task ListAllAsync_ShouldReadEveryPage_WhenTenantHasMoreAgentsThanOnePage()
    {
        const int total = 2 * AgentAccountStatusLookupExtensions.EnumerationPageSize + 7;
        var all = Enumerable.Range(0, total).Select(i => MakeAgent($"a{i:D5}")).ToList();
        var store = Substitute.For<IAgentStore>();
        store.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var q = call.ArgAt<AgentQuery>(1);
            var page = all.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList();
            return Task.FromResult(new PagedResult<Agent>(page, total, q.Page, q.PageSize));
        });

        var result = await store.ListAllAsync(Tenant, CancellationToken.None);

        result.Should().HaveCount(total);
        result.Select(a => a.AgentId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task WhereUserActiveAsync_ShouldNotCallTheLookup_WhenThereAreNoAgents()
    {
        var status = new FakeAgentAccountStatusLookup();

        var result = await status.WhereUserActiveAsync(Tenant, [], CancellationToken.None);

        result.Should().BeEmpty();
        status.Calls.Should().Be(0);
    }
}
