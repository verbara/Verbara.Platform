using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound.Services;

namespace Verbara.Platform.Routing.Inbound.Tests;

/// <summary>
/// conversations-return-to-last-agent task 2.4 (design D5, scout G8): the sticky bypass reads the real presence service
/// for the ROUTED queue, so that queue's required skills apply to a preferred agent who is a member of another queue
/// only. Positive control: the same agent with the skill is returned, so the skill is what refuses the other.
/// </summary>
public sealed class RoundRobinAgentSelectorStickySkillTests
{
    private static readonly TenantId Tenant = new("t-sticky");
    private static readonly EntityId Q1 = EntityId.From("q1-requires-s");
    private static readonly EntityId Q2 = EntityId.From("q2-other");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static Agent MakeAgent(string id, params string[] skills) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = Tenant,
        UserId = EntityId.From("user-" + id),
        DisplayName = id,
        State = AgentState.Available,
        Skills = skills,
        CreatedAt = Now,
    };

    private static RoundRobinAgentSelector Selector(Agent preferred, Agent inPool)
    {
        var agents = Substitute.For<IAgentStore>();
        agents.ListAsync(Tenant, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Agent>([preferred, inPool], 2, 1, int.MaxValue));
        var queues = Substitute.For<IQueueStore>();
        queues.GetByIdAsync(Tenant, Q1, Arg.Any<CancellationToken>()).Returns(new Queue
        {
            QueueId = Q1, TenantId = Tenant, Name = "Q1", RequiredSkills = ["S"], CreatedAt = Now,
        });
        var capacity = Substitute.For<IAgentCapacityService>();
        capacity.HasCapacityAsync(Tenant, Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>()).Returns(true);
        var accounts = Substitute.For<IAgentAccountStatusLookup>();
        accounts.GetActiveUserIdsAsync(Tenant, Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<EntityId>>(new HashSet<EntityId>(call.ArgAt<IReadOnlyCollection<EntityId>>(1))));
        var presence = new InMemoryAgentPresenceService(agents, queues, capacity, accounts);

        // Q1's eligible pool holds only the other agent; the preferred agent is a member of Q2 only.
        var eligibility = Substitute.For<IRoutingEligibilityService>();
        eligibility.GetEligibleAgentsAsync(Tenant, Q1, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns([new RoutableAgent(inPool, Penalty: 0)]);
        var memberships = Substitute.For<IQueueMembershipStore>();
        memberships.ListByAgentAsync(Tenant, preferred.AgentId, Arg.Any<CancellationToken>()).Returns([
            new QueueMembership
            {
                TenantId = Tenant, QueueId = Q2, AgentId = preferred.AgentId, Source = MembershipSource.Manual, CreatedAt = Now,
            },
        ]);
        return new RoundRobinAgentSelector(eligibility, memberships, presence);
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldNotReturnThePreferredAgent_WhenTheyAreOutsideThePoolAndLackTheRoutedQueuesSkill()
    {
        var a = MakeAgent("agent-a", "T");
        var b = MakeAgent("agent-b", "S");

        var chosen = await Selector(a, b).SelectAgentAsync(Tenant, Q1, ChannelType.WhatsApp, a.AgentId, CancellationToken.None);

        chosen.Should().Be(b.AgentId);
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldReturnThePreferredAgent_WhenTheyAreOutsideThePoolAndHaveTheRoutedQueuesSkill()
    {
        var a = MakeAgent("agent-a", "S");
        var b = MakeAgent("agent-b", "S");

        var chosen = await Selector(a, b).SelectAgentAsync(Tenant, Q1, ChannelType.WhatsApp, a.AgentId, CancellationToken.None);

        chosen.Should().Be(a.AgentId);
    }
}
