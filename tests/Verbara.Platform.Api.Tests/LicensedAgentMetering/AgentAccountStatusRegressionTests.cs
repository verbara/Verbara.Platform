using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.Realtime.Engine;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.1): the regression tests written against the unfixed
/// code. A user's account status governs whether that user's agent can receive work and keep a phone
/// endpoint (agent-account-status-routing), each user owns at most one agent and an agent always names
/// a user that exists (agent-identity-integrity). Each test drives the composed host, so it needs no
/// seam that the fix introduces.
/// </summary>
public sealed class AgentAccountStatusRegressionTests : IClassFixture<AccountStatusApiFactory>
{
    private readonly AccountStatusApiFactory _factory;

    public AgentAccountStatusRegressionTests(AccountStatusApiFactory factory) => _factory = factory;

    private IServiceProvider Services => _factory.Services;

    // ─── (a) routing eligibility ─────────────────────────────────────────────

    [Fact]
    public async Task GetAvailableAgentsAsync_ShouldExcludeAgent_WhenUserSuspended()
    {
        var tenant = NewTenant();
        var queue = await SeedQueueAsync(tenant);
        var suspended = await SeedAgentAsync(tenant, UserStatus.Suspended, AgentState.Available);
        var active = await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Available);

        var available = await Services.GetRequiredService<IAgentPresenceService>()
            .GetAvailableAgentsAsync(tenant, queue.QueueId, ChannelType.WebChat, CancellationToken.None);

        available.Select(a => a.AgentId).Should().Contain(active.AgentId)
            .And.NotContain(suspended.AgentId, because: "an agent whose user is Suspended must not be offered work");
    }

    // ─── (b) sticky routing ──────────────────────────────────────────────────

    [Fact]
    public async Task SelectAgent_ShouldNotStickToAgent_WhenUserDeactivated()
    {
        var tenant = NewTenant();
        var queue = await SeedQueueAsync(tenant);
        var deactivated = await SeedAgentAsync(tenant, UserStatus.Deactivated, AgentState.Available);
        var active = await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Available);
        await SeedMembershipAsync(tenant, queue, deactivated);
        await SeedMembershipAsync(tenant, queue, active);

        var selected = await Services.GetRequiredService<IAgentSelector>()
            .SelectAgentAsync(tenant, queue.QueueId, ChannelType.WebChat, deactivated.AgentId, CancellationToken.None);

        selected.Should().NotBe(deactivated.AgentId,
            because: "sticky routing must not return a contact to an agent whose user is Deactivated");
    }

    // ─── (c) switchboard ownership ───────────────────────────────────────────

    [Fact]
    public async Task TransferToAgentAsync_ShouldFail_WhenTargetUserSuspended()
    {
        var tenant = NewTenant();
        var owner = await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Available);
        var target = await SeedAgentAsync(tenant, UserStatus.Suspended, AgentState.Available);
        var conversation = await SeedActiveConversationAsync(tenant, owner);

        var result = await Services.GetRequiredService<IConversationSwitchboard>()
            .TransferToAgentAsync(conversation.ConversationId, tenant, target.AgentId, CancellationToken.None);

        result.Success.Should().BeFalse(because: "a Suspended user's agent cannot be made the owner");
        result.FailureReason.Should().Be("Target agent not found.");
        var stored = await Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(tenant, conversation.ConversationId, CancellationToken.None);
        stored!.Owner!.OwnerId.Should().Be(owner.AgentId);
    }

    // ─── (d) (e) (f) PJSIP desired state ─────────────────────────────────────

    [Fact]
    public async Task GetExpectedAgentsAsync_ShouldExcludeAgent_WhenUserSuspended()
    {
        var tenant = NewTenant();
        var suspended = await SeedAgentAsync(tenant, UserStatus.Suspended, AgentState.Offline, provisioned: true);
        var active = await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Offline, provisioned: true);

        var expected = await Services.GetRequiredService<IDesiredStateProvider>()
            .GetExpectedAgentsAsync(tenant.Value, CancellationToken.None);

        expected.Select(a => a.AgentId).Should().Contain(active.AgentId.Value)
            .And.NotContain(suspended.AgentId.Value, because: "the reconciler must deprovision a Suspended user's endpoint");
    }

    [Fact]
    public async Task GetExpectedAgentsAsync_ShouldReturnAll_WhenTenantHas1200Agents()
    {
        var tenant = NewTenant();
        for (var i = 0; i < 1200; i++)
            await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Offline, provisioned: true);

        var expected = await Services.GetRequiredService<IDesiredStateProvider>()
            .GetExpectedAgentsAsync(tenant.Value, CancellationToken.None);

        expected.Should().HaveCount(1200, because: "every agent of the tenant is enumerated, with no silent page cap");
    }

    [Fact]
    public async Task GetExpectedQueueMembersAsync_ShouldExcludeMembers_WhenUserSuspended()
    {
        var tenant = NewTenant();
        var queue = await SeedQueueAsync(tenant);
        var suspended = await SeedAgentAsync(tenant, UserStatus.Suspended, AgentState.Offline, provisioned: true);
        var active = await SeedAgentAsync(tenant, UserStatus.Active, AgentState.Offline, provisioned: true);
        await SeedMembershipAsync(tenant, queue, suspended);
        await SeedMembershipAsync(tenant, queue, active);

        var members = await Services.GetRequiredService<IDesiredStateProvider>()
            .GetExpectedQueueMembersAsync(tenant.Value, CancellationToken.None);

        members.Select(m => m.AgentId).Should().Contain(active.AgentId.Value)
            .And.NotContain(suspended.AgentId.Value, because: "a Suspended user's queue_members rows are deprovisioned, not paused");
    }

    [Fact]
    public async Task GetExpectedState_ShouldIncludeAgentAndMembersAgain_WhenUserReactivated()
    {
        var tenant = NewTenant();
        var queue = await SeedQueueAsync(tenant);
        var agent = await SeedAgentAsync(tenant, UserStatus.Suspended, AgentState.Offline, provisioned: true);
        await SeedMembershipAsync(tenant, queue, agent);
        var desired = Services.GetRequiredService<IDesiredStateProvider>();
        (await desired.GetExpectedQueueMembersAsync(tenant.Value, CancellationToken.None))
            .Select(m => m.AgentId).Should().NotContain(agent.AgentId.Value);

        await Services.GetRequiredService<IUserStore>().UpdateAdminFieldsAsync(
            tenant, agent.UserId, new AdminFieldsChange { Status = UserStatus.Active },
            DateTimeOffset.UtcNow, updatedBy: null, CancellationToken.None);

        (await desired.GetExpectedAgentsAsync(tenant.Value, CancellationToken.None))
            .Select(a => a.AgentId).Should().Contain(agent.AgentId.Value,
                because: "the reconciler re-provisions the endpoint once the user is Active again");
        (await desired.GetExpectedQueueMembersAsync(tenant.Value, CancellationToken.None))
            .Select(m => m.AgentId).Should().Contain(agent.AgentId.Value,
                because: "the next reconcile re-creates the agent's queue_members rows after reactivation");
    }

    // ─── (g) (h) agent creation ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAgent_ShouldReturn409_WhenUserAlreadyOwnsAgent()
    {
        var user = await SeedUserAsync(Tenant, UserStatus.Active);
        using var client = AdminClient();

        var first = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "First" });
        var second = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Second" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, because: "a user owns at most one agent per tenant");
        (await AgentsOwnedByAsync(Tenant, user.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateAgent_ShouldReturn404_WhenUserMissing()
    {
        var missingUserId = $"no-such-user-{Guid.NewGuid():N}";
        using var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = missingUserId, displayName = "Ghost" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because: "an agent must name a user that exists");
        (await AgentsOwnedByAsync(Tenant, EntityId.From(missingUserId))).Should().BeEmpty();
    }

    // ─── (i) user deletion ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteUser_ShouldReturn409_WhenUserOwnsAgent()
    {
        var agent = await SeedAgentAsync(Tenant, UserStatus.Active, AgentState.Offline);
        using var client = AdminClient();

        var response = await client.DeleteAsync($"/api/v1/admin/users/{agent.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, because: "the agent must be deleted first");
        (await Services.GetRequiredService<IUserStore>().GetByIdAsync(Tenant, agent.UserId, CancellationToken.None))
            .Should().NotBeNull();
        (await Services.GetRequiredService<IAgentStore>().GetByIdAsync(Tenant, agent.AgentId, CancellationToken.None))
            .Should().NotBeNull();
    }

    // ─── (j) forced Offline on suspension ────────────────────────────────────

    [Fact]
    public async Task StatusChange_ShouldForceAgentOffline_WhenUserSuspended()
    {
        var agent = await SeedAgentAsync(Tenant, UserStatus.Active, AgentState.Available);
        using var client = AdminClient();

        var response = await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Suspended" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await Services.GetRequiredService<IAgentStore>().GetByIdAsync(Tenant, agent.AgentId, CancellationToken.None);
        stored!.State.Should().Be(AgentState.Offline, because: "losing account access forces the agent Offline in the same request");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static readonly TenantId Tenant = new(AccountStatusApiFactory.CustomerTenantId);

    private HttpClient AdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private static TenantId NewTenant() => new($"lam-{Guid.NewGuid():N}");

    private async Task<User> SeedUserAsync(TenantId tenant, UserStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            UserId = EntityId.From($"lam-user-{id}"),
            TenantId = tenant,
            Email = $"lam-{id}@example.test",
            DisplayName = "Licensed Agent Metering",
            Role = UserRole.Agent,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
        return user;
    }

    private async Task<Agent> SeedAgentAsync(TenantId tenant, UserStatus status, AgentState state, bool provisioned = false)
    {
        var user = await SeedUserAsync(tenant, status);
        var id = Guid.NewGuid().ToString("N");
        var agent = new Agent
        {
            AgentId = EntityId.From($"lam-agent-{id}"),
            TenantId = tenant,
            UserId = user.UserId,
            DisplayName = $"Agent {id[..6]}",
            State = state,
            Extension = provisioned ? $"9{id[..5]}" : null,
            SipPassword = provisioned ? "sip-secret" : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    private async Task<Queue> SeedQueueAsync(TenantId tenant)
    {
        var queue = new Queue
        {
            QueueId = EntityId.From($"lam-queue-{Guid.NewGuid():N}"),
            TenantId = tenant,
            Name = $"lam-q-{Guid.NewGuid():N}"[..16],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IQueueStore>().SaveAsync(queue, CancellationToken.None);
        return queue;
    }

    private Task SeedMembershipAsync(TenantId tenant, Queue queue, Agent agent) =>
        Services.GetRequiredService<IQueueMembershipStore>().SaveAsync(new QueueMembership
        {
            TenantId = tenant,
            QueueId = queue.QueueId,
            AgentId = agent.AgentId,
            Source = MembershipSource.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

    private async Task<Conversation> SeedActiveConversationAsync(TenantId tenant, Agent owner)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"lam-conv-{Guid.NewGuid():N}"),
            TenantId = tenant,
            ContactId = EntityId.From($"lam-contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(owner.AgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private async Task<IReadOnlyList<Agent>> AgentsOwnedByAsync(TenantId tenant, EntityId userId)
    {
        var page = await Services.GetRequiredService<IAgentStore>()
            .ListAsync(tenant, new AgentQuery { PageSize = int.MaxValue }, CancellationToken.None);
        return page.Items.Where(a => a.UserId == userId).ToList();
    }
}

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.1 (k)): deleting a tenant that still owns agent rows is
/// refused, because the licensed-agent count does not consult tenant status (agent-identity-integrity).
/// </summary>
public sealed class TenantDeleteAgentGuardRegressionTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public TenantDeleteAgentGuardRegressionTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public async Task DeleteTenant_ShouldReturn409_WhenTenantOwnsAgents()
    {
        using var client = _factory.CreatePlatformAdminClient();
        var tenantId = "lam-cust-" + Guid.NewGuid().ToString("N")[..8];
        (await client.PostAsJsonAsync("/api/management/tenants", new { tenantId, name = "Owns Agents", type = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var agentId = EntityId.From($"lam-agent-{Guid.NewGuid():N}");
        await _factory.Services.GetRequiredService<IAgentStore>().SaveAsync(new Agent
        {
            AgentId = agentId,
            TenantId = new TenantId(tenantId),
            UserId = EntityId.From($"lam-user-{Guid.NewGuid():N}"),
            DisplayName = "Tenant Agent",
            State = AgentState.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await client.DeleteAsync($"/api/management/tenants/{tenantId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, because: "a tenant's agents must be removed before the tenant is deleted");
        var tenant = JsonNode.Parse(await (await client.GetAsync($"/api/management/tenants/{tenantId}")).Content.ReadAsStringAsync())!;
        tenant["status"]!.GetValue<string>().Should().Be("Active");
        (await _factory.Services.GetRequiredService<IAgentStore>().GetByIdAsync(new TenantId(tenantId), agentId, CancellationToken.None))
            .Should().NotBeNull();
    }
}
