using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Routing.Inbound.Services;

namespace Verbara.Platform.Routing.Inbound.Tests;

// ADR-0026 Phase B — membership-aware executive gate.
//
// These tests exercise BOTH halves of the gate:
//   1. MembershipAwareRoutingEligibilityService — builds the eligible pool by
//      intersecting presence (routable + capacity + skill match) with
//      queue_memberships (member + !IsExcluded + AllowedChannels permits
//      channel), then sorts ASC by penalty.
//   2. RoundRobinAgentSelector — penalty-grouped round-robin + sticky bypass
//      when a preferred agent has membership in any queue of the tenant.
//
// Coverage matrix (plan §B.4.1):
//   - Exclude agent when no membership exists
//   - Exclude agent when channel not in AllowedChannels
//   - Include agent when AllowedChannels contains the conversation channel
//   - Include agent when AllowedChannels is null (all-channels default)
//   - Sort ASC by penalty when multiple eligible agents
//   - Honor preferred agent when they have membership in any queue (sticky)
//   - Exclude preferred agent when zero memberships across tenant (no sticky)
//   - Respect IsExcluded=true (skip even with full skill + channel match)
public class MembershipGateRoutingTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId QueueId = EntityId.From("q-1");
    private static readonly EntityId OtherQueueId = EntityId.From("q-2");

    private static Agent MakeAgent(string id) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = Tenant,
        UserId = EntityId.New(),
        DisplayName = $"Agent {id}",
        State = AgentState.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static QueueMembership MakeMembership(
        Agent agent,
        EntityId? queueId = null,
        int penalty = 0,
        bool isExcluded = false,
        IReadOnlyList<string>? allowedChannels = null) => new()
    {
        TenantId = Tenant,
        QueueId = queueId ?? QueueId,
        AgentId = agent.AgentId,
        Penalty = penalty,
        Source = MembershipSource.Manual,
        IsExcluded = isExcluded,
        CreatedAt = DateTimeOffset.UtcNow,
        AllowedChannels = allowedChannels,
    };

    // ── Eligibility service (the gate proper) ─────────────────────────────

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldExcludeAgent_WhenNoMembership()
    {
        var agent = MakeAgent("agent-1");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([agent]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<QueueMembership>());

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldExcludeAgent_WhenChannelNotInAllowedChannels()
    {
        var agent = MakeAgent("agent-voice-only");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([agent]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(agent, allowedChannels: ["Voice"])]);

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldIncludeAgent_WhenAllowedChannelsContainsConversationChannel()
    {
        var agent = MakeAgent("agent-multichannel");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([agent]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(agent, allowedChannels: ["WebChat", "Email"])]);

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().HaveCount(1);
        pool[0].Agent.AgentId.Value.Should().Be("agent-multichannel");
    }

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldIncludeAgent_WhenAllowedChannelsIsNull()
    {
        var agent = MakeAgent("agent-all-channels");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([agent]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(agent, allowedChannels: null)]);

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().HaveCount(1);
        pool[0].Agent.AgentId.Value.Should().Be("agent-all-channels");
    }

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldSortByPenaltyAsc_WhenMultipleEligibleAgents()
    {
        var fast = MakeAgent("agent-fast");
        var slow = MakeAgent("agent-slow");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([slow, fast]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns([
                MakeMembership(slow, penalty: 5),
                MakeMembership(fast, penalty: 0),
            ]);

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().HaveCount(2);
        pool[0].Agent.AgentId.Value.Should().Be("agent-fast");
        pool[0].Penalty.Should().Be(0);
        pool[1].Agent.AgentId.Value.Should().Be("agent-slow");
        pool[1].Penalty.Should().Be(5);
    }

    [Fact]
    public async Task GetEligibleAgentsAsync_ShouldExcludeAgent_WhenIsExcludedTrue()
    {
        var agent = MakeAgent("agent-excluded");
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([agent]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByQueueAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(agent, isExcluded: true)]);

        var sut = new MembershipAwareRoutingEligibilityService(presence, membershipStore);
        var pool = await sut.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, CancellationToken.None);

        pool.Should().BeEmpty();
    }

    // ── Sticky / last-agent bypass (selector layer) ───────────────────────

    [Fact]
    public async Task SelectAgentAsync_ShouldHonorPreferredAgent_WhenAgentHasMembershipInOtherQueue()
    {
        // The preferred agent isn't in the current queue's eligible pool but
        // they have a membership in OTHER queues + are reachable. CSAT wins.
        var inPool = MakeAgent("agent-other");
        var preferred = MakeAgent("agent-preferred");
        var preferredId = preferred.AgentId;

        var eligibility = Substitute.For<IRoutingEligibilityService>();
        eligibility.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([new RoutableAgent(inPool, Penalty: 0)]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByAgentAsync(Tenant, preferredId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(preferred, queueId: OtherQueueId)]);
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([preferred, inPool]);

        var selector = new RoundRobinAgentSelector(eligibility, membershipStore, presence);
        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, preferredId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-preferred");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldNotHonorPreferredAgent_WhenAgentHasNoMembershipsAtAll()
    {
        // Preferred agent has zero memberships across the tenant — sticky
        // bypass MUST NOT trigger; we fall back to the regular eligible pool.
        var inPool = MakeAgent("agent-fallback");
        var preferredId = EntityId.From("agent-orphan");

        var eligibility = Substitute.For<IRoutingEligibilityService>();
        eligibility.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([new RoutableAgent(inPool, Penalty: 0)]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByAgentAsync(Tenant, preferredId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<QueueMembership>());
        var presence = Substitute.For<IAgentPresenceService>();

        var selector = new RoundRobinAgentSelector(eligibility, membershipStore, presence);
        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, preferredId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-fallback");
    }

    [Fact]
    public async Task SelectAgentAsync_ShouldNotHonorPreferredAgent_WhenAllMembershipsExcluded()
    {
        // Preferred agent has memberships but ALL are IsExcluded=true — no
        // active sticky pivot exists, behave like zero memberships.
        var inPool = MakeAgent("agent-fallback");
        var preferred = MakeAgent("agent-only-excluded");
        var preferredId = preferred.AgentId;

        var eligibility = Substitute.For<IRoutingEligibilityService>();
        eligibility.GetEligibleAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([new RoutableAgent(inPool, Penalty: 0)]);
        var membershipStore = Substitute.For<IQueueMembershipStore>();
        membershipStore.ListByAgentAsync(Tenant, preferredId, Arg.Any<CancellationToken>())
            .Returns([MakeMembership(preferred, queueId: OtherQueueId, isExcluded: true)]);
        var presence = Substitute.For<IAgentPresenceService>();
        presence.GetAvailableAgentsAsync(Tenant, QueueId, ChannelType.WebChat, Arg.Any<CancellationToken>())
            .Returns([preferred, inPool]);

        var selector = new RoundRobinAgentSelector(eligibility, membershipStore, presence);
        var result = await selector.SelectAgentAsync(Tenant, QueueId, ChannelType.WebChat, preferredId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be("agent-fallback");
    }
}
