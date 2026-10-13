using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.8 (design D14): the listen endpoint's id carries <c>-agent-</c>
/// (<c>{tenant}-agent-sup-{userId}</c>), and nothing reads it as an agent. One test per reader of the D14 table;
/// (a) is the hardening, the others pin what was already true on <c>main</c>.
/// </summary>
public sealed class ListenEndpointIsNotAnAgentTests
{
    private const string Tenant = "acme";
    private const string User = "7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d";
    private static readonly TenantId TenantId = new(Tenant);

    // ─── (a) the interface parser ───────────────────────────────────────────────

    [Theory]
    [InlineData($"PJSIP/{Tenant}-agent-sup-{User}")]
    [InlineData($"PJSIP/{Tenant}-agent-sup-")]
    [InlineData($"PJSIP/{Tenant}-agent-sup-x")]
    public void AgentInterfaceParser_ShouldNotReadAListenSlotAsAnAgent(string agentInterface)
    {
        AgentInterfaceParser.ExtractAgentId(Tenant, agentInterface).Should().BeNull();
    }

    [Fact]
    public void AgentAssistBridge_ShouldNotDeriveTheListenSlotAsAnAgentId()
    {
        AgentAssistBridge.DeriveAgentId(Tenant, $"PJSIP/{Tenant}-agent-sup-{User}", fallbackAgentId: null)
            .Should().NotBe($"sup-{User}").And.BeEmpty();
    }

    // ─── (c) queue membership ───────────────────────────────────────────────────

    private static async Task<InMemorySupervisorListenProfileStore> ProvisionedProfilesAsync()
    {
        var profiles = new InMemorySupervisorListenProfileStore();
        await profiles.SaveAsync(new SupervisorListenProfile
        {
            TenantId = TenantId, UserId = EntityId.From(User), SipExtension = "sl0a1b2c3d", SipPassword = "pw",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        return profiles;
    }

    [Fact]
    public async Task QueueMembers_ShouldNeverIncludeTheListenSlot()
    {
        var agentStore = new InMemoryAgentStore();
        var agent = new Agent
        {
            AgentId = EntityId.From("0651f84b6f284a1c9b8d658330fa8310"), TenantId = TenantId, UserId = EntityId.New(),
            DisplayName = "A", State = AgentState.Available, Extension = "1001", SipPassword = "x", CreatedAt = DateTimeOffset.UnixEpoch,
        };
        await agentStore.SaveAsync(agent, CancellationToken.None);
        var queueStore = new InMemoryQueueStore();
        var queue = new Queue { QueueId = EntityId.New(), TenantId = TenantId, Name = "support", CreatedAt = DateTimeOffset.UnixEpoch };
        await queueStore.SaveAsync(queue, CancellationToken.None);
        var memberships = new InMemoryQueueMembershipStore();
        await memberships.SaveAsync(new QueueMembership
        {
            TenantId = TenantId, QueueId = queue.QueueId, AgentId = agent.AgentId, Penalty = 0, Source = MembershipSource.Manual, CreatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var membership = new QueueMembershipService(queueStore, agentStore, new Verbara.Sdk.Pro.Routing.Skills.InMemorySkillCatalog(),
            memberships, NullLogger<QueueMembershipService>.Instance);
        var endpointProfiles = Substitute.For<EndpointProfileStoreBase>();
        IReadOnlyList<Verbara.Sdk.Pro.Realtime.Models.EndpointProfile> listenProfiles = [new() { Id = 7, TenantId = Tenant, Name = "supervisor-listen" }];
        endpointProfiles.ListAsync(Tenant, Arg.Any<CancellationToken>()).Returns(listenProfiles);
        var provider = new PlatformDesiredStateProvider(
            new ConfigurationBuilder().Build(), agentStore, queueStore, Substitute.For<TrunkStoreBase>(), membership,
            new FakeAgentAccountStatusLookup(), await ProvisionedProfilesAsync(), endpointProfiles);

        var effective = await membership.ComputeEffectiveMembersAsync(Tenant, CancellationToken.None);
        var expected = await provider.GetExpectedQueueMembersAsync(Tenant);
        var agents = await provider.GetExpectedAgentsAsync(Tenant);

        agents.Select(a => a.AgentId).Should().Contain($"sup-{User}", "the endpoint IS in the desired state");
        effective.Select(m => m.AgentId).Should().NotContain(id => id.StartsWith("sup-", StringComparison.Ordinal));
        expected.Select(m => m.AgentId).Should().Equal(agent.AgentId.Value);
    }

    [Fact]
    public async Task ReconciliationPass_ShouldAddNoQueueMemberForTheListenSlot()
    {
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Tenant { TenantId = Tenant, Name = "Acme", Status = TenantStatus.Active } });
        var memberships = Substitute.For<IQueueMembershipStore>();
        memberships.ListByTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QueueMembership>>([]));
        var sync = Substitute.For<IRealtimeSyncService>();
        var services = new ServiceCollection();
        services.AddSingleton(tenants);
        services.AddSingleton(memberships);
        services.AddSingleton(Substitute.For<IQueueStore>());
        services.AddSingleton(Substitute.For<IAgentStore>());
        services.AddSingleton(sync);
        services.AddSingleton<Verbara.Platform.Queues.Services.IAgentAccountStatusLookup>(new FakeAgentAccountStatusLookup());
        services.AddSingleton<ISupervisorListenProfileStore>(await ProvisionedProfilesAsync());
        var reconciler = new RealtimeReconciliationService(services.BuildServiceProvider(), new ServiceHeartbeat(),
            Options.Create(new RealtimeOptions { ReconcilerIntervalSeconds = 60 }), new AgentPauseCoordinator(),
            NullLogger<RealtimeReconciliationService>.Instance);

        await reconciler.ReconcileAsync(CancellationToken.None);

        sync.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IRealtimeSyncService.AddQueueMemberAsync))
            .SelectMany(c => c.GetArguments().OfType<string>())
            .Should().NotContain(a => a.Contains("sup-", StringComparison.Ordinal));
    }

    // ─── (d) transfers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VoiceTransfer_ToTheListenSlot_ShouldAnswerAgentNotFound()
    {
        var conversations = Substitute.For<IConversationStore>();
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(), TenantId = TenantId, ContactId = EntityId.New(), Channel = ChannelType.Voice,
            State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        conversation.SetMetadata("customerChannel", "PJSIP/pstn-00000001");
        conversations.GetByIdAsync(TenantId, conversation.ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        await using var pool = new VerbaraServerPool(Substitute.For<Verbara.Sdk.Ami.Connection.IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer("primary", new VerbaraServer(Substitute.For<Verbara.Sdk.IAmiConnection>(), NullLogger<VerbaraServer>.Instance));
        var service = new VoiceCallControlService(conversations, Substitute.For<IQueueStore>(), new InMemoryAgentStore(), pool, null,
            Substitute.For<ITenantStore>(), new AlwaysLeader(VoiceLeaderResources.AmiOwner), "pstn", NullLogger<VoiceCallControlService>.Instance);

        var outcome = await service.BlindTransferAsync(TenantId, conversation.ConversationId,
            new VoiceTransferTarget(VoiceTransferKind.Agent, $"sup-{User}"), CancellationToken.None);

        outcome.Error.Should().Be("agent-not-found", "the listen endpoint is not an agents row");
    }
}

/// <summary>supervisor-silent-listen 2.8 (b): the Api host runs no agent-presence tracking.</summary>
public sealed class ListenEndpointPresenceTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly AuthenticatedPlatformApiFactory _factory;

    public ListenEndpointPresenceTests(AuthenticatedPlatformApiFactory factory) => _factory = factory;

    [Fact]
    public void ApiHost_ShouldResolveNoAgentStateBridge_AndNoPresenceTracker()
    {
        _factory.Services.GetService<IAgentPresenceTracker>().Should().BeNull(
            "EnableAgentPresenceTracking = false: nothing parses the listen endpoint's device state into a presence");
        var bridge = typeof(IAgentPresenceTracker).Assembly.GetType("Verbara.Sdk.Pro.Realtime.Engine.AgentStateBridge", throwOnError: true)!;
        _factory.Services.GetService(bridge).Should().BeNull("Pro registers AgentStateBridge only with presence tracking on");
    }
}

/// <summary>supervisor-silent-listen 2.8 (f, g): a supervisor with an endpoint is not an agent, and a listen lists nothing.</summary>
public sealed class ListenEndpointAgentSurfaceTests : IClassFixture<SupervisorListenVoiceApiFactory>
{
    private readonly SupervisorListenVoiceApiFactory _factory;

    public ListenEndpointAgentSurfaceTests(SupervisorListenVoiceApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AgentsMe_ShouldStay404_ForASupervisorWithAnEndpointAndNoAgentProfile()
    {
        using var client = _factory.CreateSupervisorClient();
        (await client.PostAsync("/api/v1/supervisor/listen-endpoint", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await client.GetAsync("/api/v1/agents/me");

        me.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SupervisorLists_ShouldGainNoEntry_AcrossAListen()
    {
        using var client = _factory.CreateSupervisorClient();
        await client.PostAsync("/api/v1/supervisor/listen-endpoint", null);
        var call = _factory.Harness.SeedVoiceCall();
        var conversationsBefore = await Count(client, "/api/v1/supervisor/conversations");
        var sessionsBefore = (await client.GetFromJsonAsync<JsonArray>("/api/v1/supervisor/sessions/active"))!.Count;

        await client.PostAsJsonAsync($"/api/v1/supervisor/conversations/{call.ConversationId.Value}/listen", new { destination = "endpoint" });
        var conversationsDuring = await Count(client, "/api/v1/supervisor/conversations");
        var sessionsDuring = (await client.GetFromJsonAsync<JsonArray>("/api/v1/supervisor/sessions/active"))!.Count;
        await client.PostAsync($"/api/v1/supervisor/conversations/{call.ConversationId.Value}/listen/stop", null);

        conversationsDuring.Should().Be(conversationsBefore);
        sessionsDuring.Should().Be(sessionsBefore);
    }

    private static async Task<int> Count(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<JsonObject>(path))!["totalCount"]!.GetValue<int>();
}
