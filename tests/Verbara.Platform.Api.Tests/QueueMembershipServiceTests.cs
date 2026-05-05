using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Routing.Models;
using Verbara.Sdk.Pro.Routing.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class QueueMembershipServiceTests
{
    private const string TenantId = "t1";
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly IAgentStore _agentStore = Substitute.For<IAgentStore>();
    private readonly SkillCatalogBase _skillCatalog = Substitute.For<SkillCatalogBase>();
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
        _agentStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Agent>(agents, agents.Length, 1, 1000));
    }

    private void SetupAgentSkills(string agentId, params AgentSkill[] skills)
    {
        _skillCatalog.GetAgentSkillsAsync(agentId, Arg.Any<CancellationToken>())
            .Returns(skills);
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
        SetupAgentSkills("a1", new AgentSkill { AgentId = "a1", SkillName = "spanish", Proficiency = 8 });

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
        SetupAgentSkills("a1", new AgentSkill { AgentId = "a1", SkillName = "spanish", Proficiency = 7 });
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
        SetupAgentSkills("a1", new AgentSkill { AgentId = "a1", SkillName = "skill-a", Proficiency = 10 });
        SetupAgentSkills("a2", new AgentSkill { AgentId = "a2", SkillName = "skill-a", Proficiency = 1 });

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
        SetupAgentSkills("a1", new AgentSkill { AgentId = "a1", SkillName = "spanish", Proficiency = 8 });
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
