using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 1, Phase B (tasks.md 2.4-2.6): the behaviour around the regression
/// tests on a host with real in-memory stores — reactivation, the pause written before the status
/// change returns, the delete order, the agent audit trail and the GDPR purge order.
/// </summary>
public sealed class AgentLifecycleBehaviourTests : IClassFixture<AccountStatusApiFactory>
{
    private static readonly TenantId Tenant = new(AccountStatusApiFactory.CustomerTenantId);

    private readonly AccountStatusApiFactory _factory;

    public AgentLifecycleBehaviourTests(AccountStatusApiFactory factory) => _factory = factory;

    private IServiceProvider Services => _factory.Services;

    [Fact]
    public async Task StatusChange_ShouldPauseQueueMembersBeforeReturning_WhenUserSuspended()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Available);
        var sync = Services.GetRequiredService<IRealtimeSyncService>();
        using var client = AdminClient();

        (await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Suspended" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await sync.Received().SyncAgentPausedAsync(Tenant.Value, agent.AgentId.Value, true, Arg.Any<CancellationToken>());
        var entry = (await AuditEntriesAsync("user.status_changed")).Single(e => e.TargetId == agent.UserId.Value);
        entry.Metadata!["agent_forced_offline"].Should().Be(agent.AgentId.Value);
    }

    [Fact]
    public async Task StatusChange_ShouldLeaveAgentOffline_WhenUserReactivated()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Available);
        using var client = AdminClient();
        await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Suspended" });

        (await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Active" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await AgentAsync(agent.AgentId))!.State.Should().Be(AgentState.Offline,
            because: "reactivation does not set the agent routable; the agent signs in again");
    }

    [Fact]
    public async Task StatusChange_ShouldNotTouchAgent_WhenRoleChangesOnly()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Available);
        using var client = AdminClient();

        (await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { role = "Supervisor" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await AgentAsync(agent.AgentId))!.State.Should().Be(AgentState.Available);
    }

    [Fact]
    public async Task DeleteUser_ShouldSucceed_WhenAgentDeletedFirst()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Offline);
        using var client = AdminClient();
        (await client.DeleteAsync($"/api/v1/admin/users/{agent.UserId.Value}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.DeleteAsync($"/api/v1/admin/agents/{agent.AgentId.Value}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var response = await client.DeleteAsync($"/api/v1/admin/users/{agent.UserId.Value}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Services.GetRequiredService<IUserStore>().GetByIdAsync(Tenant, agent.UserId, CancellationToken.None)).Should().BeNull();
        (await AuditEntriesAsync("user.deleted")).Should().Contain(e => e.TargetId == agent.UserId.Value);
    }

    [Fact]
    public async Task DeleteUser_ShouldProblemPointAtTheAgent_WhenRefused()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Offline);
        using var client = AdminClient();

        var response = await client.DeleteAsync($"/api/v1/admin/users/{agent.UserId.Value}");

        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        problem["type"]!.GetValue<string>().Should().Be("https://verbara.platform/errors/user-owns-agent");
        problem["detail"]!.GetValue<string>().Should().Contain(agent.AgentId.Value);
        (await AuditEntriesAsync("user.deleted")).Should().NotContain(e => e.TargetId == agent.UserId.Value);
    }

    [Fact]
    public async Task CreateAgent_ShouldAuditAgentCreated_WhenCreated()
    {
        var user = await SeedUserAsync(UserStatus.Active);
        using var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Audited" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var agentId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();
        var entry = (await AuditEntriesAsync("agent.created")).Should().ContainSingle(e => e.TargetId == agentId).Subject;
        entry.ActorId.Should().Be(AccountStatusApiFactory.CustomerAdminUserId);
        entry.TargetType.Should().Be("Agent");
        entry.Metadata!["agent_id"].Should().Be(agentId);
        entry.Metadata["user_id"].Should().Be(user.UserId.Value);
    }

    [Fact]
    public async Task DeleteAgent_ShouldAuditAgentDeleted_WhenDeleted()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Offline);
        using var client = AdminClient();

        (await client.DeleteAsync($"/api/v1/admin/agents/{agent.AgentId.Value}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var entry = (await AuditEntriesAsync("agent.deleted")).Should().ContainSingle(e => e.TargetId == agent.AgentId.Value).Subject;
        entry.ActorId.Should().Be(AccountStatusApiFactory.CustomerAdminUserId);
        entry.Metadata!["agent_id"].Should().Be(agent.AgentId.Value);
        entry.Metadata["user_id"].Should().Be(agent.UserId.Value);
        entry.Metadata["source"].Should().Be("admin");
    }

    [Fact]
    public async Task CreateAgent_ShouldYieldOneCreatedAndOneConflict_WhenTwoCreationsForOneUserRace()
    {
        var user = await SeedUserAsync(UserStatus.Active);
        using var a = AdminClient();
        using var b = AdminClient();

        var responses = await Task.WhenAll(
            a.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Racer A" }),
            b.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Racer B" }));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await Services.GetRequiredService<IAgentStore>().GetByUserIdAsync(Tenant, user.UserId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task PurgeUserData_ShouldDeleteAgentThenUser_WhenUserOwnsAgent()
    {
        var agent = await SeedAgentAsync(UserStatus.Active, AgentState.Offline);

        var result = await Services.GetRequiredService<IGdprPurgeService>().PurgeUserDataAsync(
            Tenant.Value, agent.UserId.Value, "dpo-user", "Art. 17 request", CancellationToken.None);

        result.EntitiesDeleted.Should().ContainKey("agent").And.ContainKey("user");
        (await AgentAsync(agent.AgentId)).Should().BeNull();
        (await Services.GetRequiredService<IAgentStore>().GetByUserIdAsync(Tenant, agent.UserId, CancellationToken.None)).Should().BeNull();
        (await Services.GetRequiredService<IUserStore>().GetByIdAsync(Tenant, agent.UserId, CancellationToken.None)).Should().BeNull();
        var entry = (await AuditEntriesAsync("agent.deleted")).Should().ContainSingle(e => e.TargetId == agent.AgentId.Value).Subject;
        entry.ActorId.Should().Be("dpo-user");
        entry.Metadata!["source"].Should().Be("gdpr_purge");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private HttpClient AdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<User> SeedUserAsync(UserStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            UserId = EntityId.From($"lam-user-{id}"),
            TenantId = Tenant,
            Email = $"lam-{id}@example.test",
            DisplayName = "Licensed Agent Metering",
            Role = UserRole.Agent,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
        return user;
    }

    private async Task<Agent> SeedAgentAsync(UserStatus status, AgentState state)
    {
        var user = await SeedUserAsync(status);
        var agent = new Agent
        {
            AgentId = EntityId.From($"lam-agent-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            UserId = user.UserId,
            DisplayName = "Lifecycle Agent",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    private Task<Agent?> AgentAsync(EntityId agentId) =>
        Services.GetRequiredService<IAgentStore>().GetByIdAsync(Tenant, agentId, CancellationToken.None);

    private async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync(string action)
    {
        using var scope = Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            Tenant, new AuditQuery(Action: action, Page: 1, PageSize: 200), CancellationToken.None);
        return page.Items;
    }
}

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 2.2): a transfer or reassign to an agent whose user is not
/// Active gets the response an unknown target agent gets, and the conversation is unchanged.
/// </summary>
public sealed class OwnershipTargetStatusTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId Tenant = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public OwnershipTargetStatusTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task ReassignConversation_ShouldReturn400TargetAgentNotFound_WhenTargetUserDeactivated()
    {
        var owner = await SeedAgentAsync(UserStatus.Active);
        var target = await SeedAgentAsync(UserStatus.Deactivated);
        var conversation = await SeedConversationAsync(owner);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/reassign",
            new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target-agent-not-found");
        (await OwnerOfAsync(conversation)).Should().Be(owner.AgentId);
    }

    [Fact]
    public async Task TransferConversation_ShouldReturn400TargetAgentNotFound_WhenTargetUserSuspended()
    {
        var owner = await SeedAgentAsync(UserStatus.Active);
        var target = await SeedAgentAsync(UserStatus.Suspended);
        var conversation = await SeedConversationAsync(owner);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer",
            new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target-agent-not-found");
        (await OwnerOfAsync(conversation)).Should().Be(owner.AgentId);
    }

    [Fact]
    public async Task ReassignConversation_ShouldTransfer_WhenTargetUserActive()
    {
        var owner = await SeedAgentAsync(UserStatus.Active);
        var target = await SeedAgentAsync(UserStatus.Active);
        var conversation = await SeedConversationAsync(owner);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/reassign",
            new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await OwnerOfAsync(conversation)).Should().Be(target.AgentId);
    }

    private async Task<Agent> SeedAgentAsync(UserStatus status)
    {
        var userId = $"lam-user-{Guid.NewGuid():N}";
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = Tenant,
            Email = $"{userId}@example.test",
            DisplayName = userId,
            Role = UserRole.Agent,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var agent = new Agent
        {
            AgentId = EntityId.From($"lam-agent-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            UserId = EntityId.From(userId),
            DisplayName = "Ownership Agent",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    private async Task<Conversation> SeedConversationAsync(Agent owner)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(owner.AgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private async Task<EntityId?> OwnerOfAsync(Conversation conversation) =>
        (await _factory.Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!.Owner?.OwnerId;
}

/// <summary>licensed-agent-metering slice 1 (tasks.md 2.7): a tenant with no agents is still deleted and audited.</summary>
public sealed class TenantDeleteWithoutAgentsTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public TenantDeleteWithoutAgentsTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public async Task DeleteTenant_ShouldDeleteAndAudit_WhenTenantOwnsNoAgents()
    {
        using var client = _factory.CreatePlatformAdminClient();
        var tenantId = "lam-empty-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/management/tenants", new { tenantId, name = "No Agents", type = 2 });

        var response = await client.DeleteAsync($"/api/management/tenants/{tenantId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var tenant = JsonNode.Parse(await (await client.GetAsync($"/api/management/tenants/{tenantId}")).Content.ReadAsStringAsync())!;
        tenant["status"]!.GetValue<string>().Should().Be("Deleted");
        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(tenantId), new AuditQuery(Action: "tenant.deleted", Page: 1, PageSize: 10), CancellationToken.None);
        audit.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteTenant_ShouldSucceed_AfterItsAgentsAreDeleted()
    {
        using var client = _factory.CreatePlatformAdminClient();
        var tenantId = "lam-clean-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/management/tenants", new { tenantId, name = "Cleaned", type = 2 });
        var agents = _factory.Services.GetRequiredService<IAgentStore>();
        var agentId = EntityId.New();
        await agents.SaveAsync(new Agent
        {
            AgentId = agentId,
            TenantId = new TenantId(tenantId),
            UserId = EntityId.New(),
            DisplayName = "Gone",
            State = AgentState.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        (await client.DeleteAsync($"/api/management/tenants/{tenantId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await agents.DeleteAsync(new TenantId(tenantId), agentId, CancellationToken.None);

        (await client.DeleteAsync($"/api/management/tenants/{tenantId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
