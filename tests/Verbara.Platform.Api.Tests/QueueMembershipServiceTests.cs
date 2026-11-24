using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class QueueMembershipServiceTests
{
    private const string TenantId = "t1";
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly IAgentStore _agentStore = Substitute.For<IAgentStore>();
    private readonly ISkillCatalogStore _skillCatalog = Substitute.For<ISkillCatalogStore>();
    private readonly List<AgentSkillProficiency> _proficiencies = [];
    private readonly IQueueMembershipStore _membershipStore = Substitute.For<IQueueMembershipStore>();
    private readonly QueueMembershipService _sut;

    public QueueMembershipServiceTests()
    {
        _sut = new QueueMembershipService(
            _queueStore,
            _agentStore,
            _skillCatalog,
            _membershipStore,
            NullLogger<QueueMembershipService>.Instance);

        // Default: no stored memberships
        _membershipStore.ListByTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<QueueMembership>());

        // The tenant's proficiencies: every SetupAgentSkills call adds to the one list the store returns.
        _skillCatalog.ListProficienciesAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<AgentSkillProficiency>>([.. _proficiencies]));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Queue MakeQueue(string id, string name, params string[] skills) => new()
    {
        QueueId = EntityId.From(id),
        TenantId = new TenantId(TenantId),
        Name = name,
        IsActive = true,
        RequiredSkills = skills,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Agent MakeAgent(
        string id, string displayName, string? extension = "100", string? sipPassword = "secret",
        params string[] skills) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = new TenantId(TenantId),
        UserId = EntityId.From($"user-{id}"),
        DisplayName = displayName,
        State = AgentState.Available,
        Extension = extension,
        SipPassword = sipPassword,
        Skills = skills,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private void SetupQueues(params Queue[] queues)
    {
        _queueStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Queue>(queues, queues.Length, 1, 1000));
    }

    private void SetupAgents(params Agent[] agents)
    {
        _agentStore.ListPageAfterAsync(Arg.Any<TenantId>(), Arg.Any<EntityId?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Agent>>(agents));
    }

    private void SetupAgentSkills(string agentId, params (string SkillName, int Proficiency)[] skills)
    {
        _proficiencies.AddRange(skills.Select(s => new AgentSkillProficiency(EntityId.From(agentId), s.SkillName, s.Proficiency)));
    }

    private void SetupStoredMemberships(params QueueMembership[] memberships)
    {
        _membershipStore.ListByTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(memberships);
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldDeriveFromSkillMatch()
    {
        var queue = MakeQueue("q1", "Spanish Support", "spanish");
        var agent = MakeAgent("a1", "Alice", skills: ["spanish"]);

        SetupQueues(queue);
        SetupAgents(agent);
        SetupAgentSkills("a1", ("spanish", 8));

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QueueId = "q1",
                QueueName = "Spanish Support",
                AgentId = "a1",
                DisplayName = "Alice",
                Source = MembershipSource.Skill,
            });
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldSkipUnprovisionedAgents()
    {
        var queue = MakeQueue("q1", "Support", "billing");
        var agent = MakeAgent("a1", "Bob", extension: null, sipPassword: null, skills: ["billing"]);

        SetupQueues(queue);
        SetupAgents(agent);

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldSkipAgentsWithNoMatchingSkills()
    {
        var queue = MakeQueue("q1", "Spanish Support", "spanish");
        var agent = MakeAgent("a1", "Charlie", skills: ["billing"]);

        SetupQueues(queue);
        SetupAgents(agent);

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldIncludeManualOverrides()
    {
        var queue = MakeQueue("q1", "VIP Queue", "vip");
        var agent = MakeAgent("a1", "Diana", skills: ["billing"]); // No VIP skill

        SetupQueues(queue);
        SetupAgents(agent);
        SetupStoredMemberships(new QueueMembership
        {
            TenantId = new TenantId(TenantId),
            QueueId = EntityId.From("q1"),
            AgentId = EntityId.From("a1"),
            Penalty = 3,
            Source = MembershipSource.Manual,
            IsExcluded = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                QueueId = "q1",
                AgentId = "a1",
                Penalty = 3,
                Source = MembershipSource.Manual,
            });
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldExcludeExcludedAgents()
    {
        var queue = MakeQueue("q1", "Support", "spanish");
        var agent = MakeAgent("a1", "Eve", skills: ["spanish"]);

        SetupQueues(queue);
        SetupAgents(agent);
        SetupAgentSkills("a1", ("spanish", 7));
        SetupStoredMemberships(new QueueMembership
        {
            TenantId = new TenantId(TenantId),
            QueueId = EntityId.From("q1"),
            AgentId = EntityId.From("a1"),
            Penalty = 0,
            Source = MembershipSource.Manual,
            IsExcluded = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldComputePenaltyFromProficiency()
    {
        var queue = MakeQueue("q1", "Support", "skill-a");
        var agentHigh = MakeAgent("a1", "Expert", skills: ["skill-a"]);
        var agentLow = MakeAgent("a2", "Novice", skills: ["skill-a"]);

        SetupQueues(queue);
        SetupAgents(agentHigh, agentLow);
        SetupAgentSkills("a1", ("skill-a", 10));
        SetupAgentSkills("a2", ("skill-a", 1));

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.First(m => m.AgentId == "a1").Penalty.Should().Be(0);  // 10 - round(10) = 0
        result.First(m => m.AgentId == "a2").Penalty.Should().Be(9);  // 10 - round(1)  = 9
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ManualOverrideTakesPrecedence()
    {
        var queue = MakeQueue("q1", "Support", "spanish");
        var agent = MakeAgent("a1", "Frank", skills: ["spanish"]);

        SetupQueues(queue);
        SetupAgents(agent);
        SetupAgentSkills("a1", ("spanish", 8));
        SetupStoredMemberships(new QueueMembership
        {
            TenantId = new TenantId(TenantId),
            QueueId = EntityId.From("q1"),
            AgentId = EntityId.From("a1"),
            Penalty = 5,
            Source = MembershipSource.Manual,
            IsExcluded = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        var member = result.Should().ContainSingle().Which;
        member.Penalty.Should().Be(5);           // Manual penalty, not skill-derived (would be 2)
        member.Source.Should().Be(MembershipSource.Manual);
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldReadTheTenantsProficienciesOnce_WhenSeveralQueuesAndAgentsMatch()
    {
        var spanish = MakeQueue("q1", "Spanish Support", "spanish");
        var billing = MakeQueue("q2", "Billing", "billing", "spanish");
        var alice = MakeAgent("a1", "Alice", skills: ["spanish", "billing"]);
        var bob = MakeAgent("a2", "Bob", skills: ["spanish"]);

        SetupQueues(spanish, billing);
        SetupAgents(alice, bob);
        SetupAgentSkills("a1", ("spanish", 8), ("billing", 6));
        SetupAgentSkills("a2", ("spanish", 3));

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().HaveCount(4, "both agents match both queues");
        await _skillCatalog.Received(1).ListProficienciesAsync(new TenantId(TenantId), Arg.Any<CancellationToken>());
        _skillCatalog.ReceivedCalls().Should().ContainSingle(
            "the computation reads the tenant's proficiencies once and looks each (queue, agent) up in memory");
        result.Single(m => m.QueueId == "q1" && m.AgentId == "a1").Penalty.Should().Be(2);  // 10 - round(8) = 2
        result.Single(m => m.QueueId == "q2" && m.AgentId == "a1").Penalty.Should().Be(3);  // 10 - round(7) = 3
        result.Single(m => m.QueueId == "q2" && m.AgentId == "a2").Penalty.Should().Be(7);  // 10 - round(3) = 7
    }

    [Fact]
    public async Task ComputeEffectiveMembers_ShouldSkipQueuesWithNoRequiredSkills()
    {
        var queue = MakeQueue("q1", "General Queue"); // No required skills
        var agent = MakeAgent("a1", "Grace", skills: ["spanish", "billing"]);

        SetupQueues(queue);
        SetupAgents(agent);

        var result = await _sut.ComputeEffectiveMembersAsync(TenantId, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
