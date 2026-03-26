using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Dialer.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class PlatformDesiredStateProviderTests
{
    private readonly IConfiguration _configuration;
    private readonly IAgentStore _agentStore = Substitute.For<IAgentStore>();
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly TrunkStoreBase _trunkStore = Substitute.For<TrunkStoreBase>();
    private readonly QueueMembershipService _membershipService;
    private readonly IQueueMembershipStore _membershipStore = Substitute.For<IQueueMembershipStore>();

    private readonly Dictionary<string, string?> _configData = new();

    public PlatformDesiredStateProviderTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(_configData!)
            .Build();

        var skillCatalog = new Asterisk.Sdk.Pro.Routing.Skills.InMemorySkillCatalog();
        _membershipService = new QueueMembershipService(
            _queueStore, _agentStore, skillCatalog, _membershipStore,
            NullLogger<QueueMembershipService>.Instance);
    }

    private PlatformDesiredStateProvider CreateSut() =>
        new(_configuration, _agentStore, _queueStore, _trunkStore, _membershipService);

    // ─── GetActiveTenantIds ─────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveTenantIds_ShouldReadFromConfiguration()
    {
        _configData["Realtime:TenantIds:0"] = "acme";
        _configData["Realtime:TenantIds:1"] = "globex";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(_configData!)
            .Build();
        var sut = new PlatformDesiredStateProvider(
            config, _agentStore, _queueStore, _trunkStore, _membershipService);

        var result = await sut.GetActiveTenantIdsAsync();

        result.Should().BeEquivalentTo(["acme", "globex"]);
    }

    [Fact]
    public async Task GetActiveTenantIds_ShouldDefaultToDemo_WhenNotConfigured()
    {
        var config = new ConfigurationBuilder().Build();
        var sut = new PlatformDesiredStateProvider(
            config, _agentStore, _queueStore, _trunkStore, _membershipService);

        var result = await sut.GetActiveTenantIdsAsync();

        result.Should().BeEquivalentTo(["demo"]);
    }

    // ─── GetExpectedAgents ──────────────────────────────────────────────────

    [Fact]
    public async Task GetExpectedAgents_ShouldReturnOnlyProvisionedAgents()
    {
        var tid = new TenantId("t1");
        var agents = new[]
        {
            MakeAgent("a1", "Agent 1", "1001", "pass1"),
            MakeAgent("a2", "Agent 2", null, null),       // not provisioned
            MakeAgent("a3", "Agent 3", "1003", ""),        // empty password
            MakeAgent("a4", "Agent 4", "1004", "pass4"),
        };
        _agentStore.ListAsync(tid, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Agent>(agents, agents.Length, 1, 1000)));

        var sut = CreateSut();
        var result = await sut.GetExpectedAgentsAsync("t1");

        result.Should().HaveCount(2);
        result.Select(a => a.AgentId).Should().BeEquivalentTo(["a1", "a4"]);
    }

    [Fact]
    public async Task GetExpectedAgents_ShouldMapAllFields()
    {
        var tid = new TenantId("t1");
        var agents = new[] { MakeAgent("agent-99", "Jane Doe", "2099", "secret99") };
        _agentStore.ListAsync(tid, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Agent>(agents, 1, 1, 1000)));

        var sut = CreateSut();
        var result = await sut.GetExpectedAgentsAsync("t1");

        var agent = result.Should().ContainSingle().Subject;
        agent.AgentId.Should().Be("agent-99");
        agent.DisplayName.Should().Be("Jane Doe");
        agent.Extension.Should().Be("2099");
        agent.SipPassword.Should().Be("secret99");
    }

    // ─── GetExpectedQueues ──────────────────────────────────────────────────

    [Fact]
    public async Task GetExpectedQueues_ShouldReturnOnlyActiveQueues()
    {
        var tid = new TenantId("t1");
        var queues = new[]
        {
            MakeQueue("support", true),
            MakeQueue("inactive", false),
            MakeQueue("sales", true),
        };
        _queueStore.ListAsync(tid, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Queue>(queues, queues.Length, 1, 1000)));

        var sut = CreateSut();
        var result = await sut.GetExpectedQueuesAsync("t1");

        result.Should().HaveCount(2);
        result.Select(q => q.QueueName).Should().BeEquivalentTo(["support", "sales"]);
    }

    [Fact]
    public async Task GetExpectedQueues_ShouldMapWrapUpAndSlaToOptions()
    {
        var tid = new TenantId("t1");
        var queue = MakeQueue("vip", true);
        queue.WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 45 };
        queue.SlaTargets = new SlaPolicyTarget { AnswerWithinSeconds = 10 };
        queue.MaxWaiting = 5;

        _queueStore.ListAsync(tid, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Queue>([queue], 1, 1, 1000)));

        var sut = CreateSut();
        var result = await sut.GetExpectedQueuesAsync("t1");

        var q = result.Should().ContainSingle().Subject;
        q.QueueName.Should().Be("vip");
        q.Options.Wrapuptime.Should().Be(45);
        q.Options.Servicelevel.Should().Be(10);
        q.Options.Maxlen.Should().Be(5);
    }

    // ─── GetExpectedQueueMembers ────────────────────────────────────────────

    [Fact]
    public async Task GetExpectedQueueMembers_ShouldDelegateToMembershipService()
    {
        // Set up the stores that QueueMembershipService reads from
        var tid = new TenantId("t1");
        var agent = MakeAgent("a1", "Agent One", "1001", "pass1");
        var queue = MakeQueue("support", true);
        queue.RequiredSkills = [];

        _agentStore.ListAsync(tid, Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Agent>([agent], 1, 1, 1000)));
        _queueStore.ListAsync(tid, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PagedResult<Queue>([queue], 1, 1, 1000)));

        // Manual override membership
        _membershipStore.ListByTenantAsync(tid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QueueMembership>>(new[]
            {
                new QueueMembership
                {
                    TenantId = tid,
                    QueueId = queue.QueueId,
                    AgentId = agent.AgentId,
                    Penalty = 3,
                    Source = MembershipSource.Manual,
                    IsExcluded = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            }));

        var sut = CreateSut();
        var result = await sut.GetExpectedQueueMembersAsync("t1");

        var member = result.Should().ContainSingle().Subject;
        member.QueueName.Should().Be("support");
        member.AgentId.Should().Be("a1");
        member.Penalty.Should().Be(3);
    }

    // ─── GetExpectedTrunks ──────────────────────────────────────────────────

    [Fact]
    public async Task GetExpectedTrunks_ShouldReturnOnlyTrunksWithAuthUsername()
    {
        var trunks = new[]
        {
            new Trunk { Id = 1, Name = "trunk-sip", AuthUsername = "user1", IsActive = true },
            new Trunk { Id = 2, Name = "trunk-noauth", AuthUsername = null, IsActive = true },
            new Trunk { Id = 3, Name = "trunk-empty", AuthUsername = "", IsActive = true },
            new Trunk { Id = 4, Name = "trunk-ok", AuthUsername = "user4", IsActive = true },
        };
        _trunkStore.ListActiveAsync("t1", Arg.Any<CancellationToken>())
            .Returns(trunks);

        var sut = CreateSut();
        var result = await sut.GetExpectedTrunksAsync("t1");

        result.Should().HaveCount(2);
        result.Select(t => t.TrunkName).Should().BeEquivalentTo(["trunk-sip", "trunk-ok"]);
        result[0].Trunk.Should().BeSameAs(trunks[0]);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static Agent MakeAgent(string id, string name, string? ext, string? pw) => new()
    {
        AgentId = EntityId.From(id),
        TenantId = new TenantId("t1"),
        UserId = EntityId.From("u-" + id),
        DisplayName = name,
        Extension = ext,
        SipPassword = pw,
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Queue MakeQueue(string name, bool active) => new()
    {
        QueueId = EntityId.New(),
        TenantId = new TenantId("t1"),
        Name = name,
        IsActive = active,
        WrapUp = new WrapUpConfig(),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
