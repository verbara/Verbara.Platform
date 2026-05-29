using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Routing.Inbound.Services;

namespace Verbara.Platform.Routing.Inbound.Tests;

// ADR-0026 Phase B — the selector now consumes IRoutingEligibilityService
// (membership-aware) instead of IAgentPresenceService directly. These tests
// retain their original intent (round-robin rotation, single-agent, null
// when empty, preferred-agent honoring) but stub IRoutingEligibilityService
// directly. Membership-gating semantics get dedicated coverage in
// MembershipGateRoutingTests.
public class RoundRobinAgentSelectorTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId QueueId = EntityId.From("q-1");
    private static readonly string[] AgentIds12 = ["agent-1", "agent-2"];

    private static Agent MakeAgent(string id) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = Tenant,
        UserId = EntityId.New(),
        DisplayName = $"Agent {id}",
        State = AgentState.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static RoutableAgent[] AsPool(params Agent[] agents)
        => agents.Select(a => new RoutableAgent(a, Penalty: 0)).ToArray();

    private static RoundRobinAgentSelector Create(
        IReadOnlyList<RoutableAgent> pool,
        IAgentPresenceService? presence = null,
        IQueueMembershipStore? membershipStore = null)
    {
        var eligibility = Substitute.For<IRoutingEligibilityService>();
        eligibility.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns(pool);
        membershipStore ??= Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByAgentAsync(Tenant, Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<QueueMembership>());
        presence ??= Substitute.For<IAgentPresenceService>();
        return new RoundRobinAgentSelector(eligibility, membershipStore, presence);
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnNull_WhenNoAgentsAvailable()
    {
        var selector = Create(pool: []);

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnPreferredAgent_WhenPreferredIsInEligiblePool()
    {
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var selector = Create(pool: AsPool(agent1, agent2));

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, agent2.AgentId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-2");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldFallBackToRoundRobin_WhenPreferredIsNotEligibleAndHasNoMemberships()
    {
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var notEligibleId = EntityId.From("agent-999");
        // Default substitute returns empty memberships → no sticky bypass.
        var selector = Create(pool: AsPool(agent1, agent2));

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, notEligibleId, CancellationToken.None);

        result.Should().NotBeNull();
        AgentIds12.Should().Contain(result!.Value.Value);
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldRotateAgents_WhenCalledMultipleTimes()
    {
        var agent1 = MakeAgent("agent-1");
        var agent2 = MakeAgent("agent-2");
        var agent3 = MakeAgent("agent-3");
        var selector = Create(pool: AsPool(agent1, agent2, agent3));

        var results = new HashSet<string>();
        for (var i = 0; i < 9; i++)
        {
            var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);
            results.Add(result!.Value.Value);
        }

        results.Should().Contain("agent-1");
        results.Should().Contain("agent-2");
        results.Should().Contain("agent-3");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnSingleAgent_WhenOnlyOneEligible()
    {
        var agent1 = MakeAgent("agent-solo");
        var selector = Create(pool: AsPool(agent1));

        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-solo");
    }
}
