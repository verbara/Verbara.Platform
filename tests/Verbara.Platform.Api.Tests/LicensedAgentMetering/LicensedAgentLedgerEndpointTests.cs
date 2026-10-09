using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Api.Tests.LicensedAgentMetering;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.3; licensed-agent-ledger): the admin endpoints and the GDPR
/// purge write exactly one ledger row per counted change, with <c>counted</c> set correctly, and none for a
/// user who owns no agent. The host runs in-memory, so the rows land in the in-memory ledger.
/// </summary>
public sealed class LicensedAgentLedgerAdminTests : IClassFixture<AccountStatusApiFactory>
{
    private static readonly TenantId Tenant = new(AccountStatusApiFactory.CustomerTenantId);

    private readonly AccountStatusApiFactory _factory;

    public LicensedAgentLedgerAdminTests(AccountStatusApiFactory factory) => _factory = factory;

    private IServiceProvider Services => _factory.Services;

    [Fact]
    public async Task CreateAgent_ShouldWriteOneCountedAgentCreatedRow_AttributedToTheAdmin()
    {
        var user = await SeedUserAsync(UserStatus.Active);
        using var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Ledgered" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var agentId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();
        var row = RowsFor(agentId).Should().ContainSingle().Subject;
        (row.Kind, row.UserId, row.ActorUserId, row.Counted)
            .Should().Be((LicenseAgentEventKinds.AgentCreated, user.UserId.Value, AccountStatusApiFactory.CustomerAdminUserId, (bool?)true));
    }

    [Fact]
    public async Task CreateAgent_ShouldWriteNoRow_WhenTheCreationIsRefused()
    {
        var agent = await CreateAgentAsync(UserStatus.Active);
        using var client = AdminClient();

        var response = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = agent.UserId.Value, displayName = "Second" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Ledger().Events(Tenant.Value).Count(r => r.UserId == agent.UserId.Value).Should().Be(1, "only the first creation is recorded");
    }

    [Fact]
    public async Task StatusChange_ShouldWriteOneRowPerChange_WithTheCountedStateAfterIt()
    {
        var agent = await CreateAgentAsync(UserStatus.Active);
        using var client = AdminClient();

        await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Suspended" });
        await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { role = "Supervisor" });
        await client.PutAsJsonAsync($"/api/v1/admin/users/{agent.UserId.Value}", new { status = "Active" });

        RowsFor(agent.AgentId.Value).Where(r => r.Kind == LicenseAgentEventKinds.UserStatusChanged)
            .Select(r => (r.UserStatus, r.Counted, r.ActorUserId))
            .Should().Equal(
                ("Suspended", (bool?)false, AccountStatusApiFactory.CustomerAdminUserId),
                ("Active", true, AccountStatusApiFactory.CustomerAdminUserId));
    }

    [Fact]
    public async Task StatusChange_ShouldWriteNoRow_WhenTheUserOwnsNoAgent()
    {
        var user = await SeedUserAsync(UserStatus.Active);
        using var client = AdminClient();

        (await client.PutAsJsonAsync($"/api/v1/admin/users/{user.UserId.Value}", new { status = "Suspended" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Ledger().Events(Tenant.Value).Should().NotContain(r => r.UserId == user.UserId.Value);
    }

    [Fact]
    public async Task DeleteAgent_ShouldWriteOneAgentDeletedRow_NotCounted()
    {
        var agent = await CreateAgentAsync(UserStatus.Active);
        using var client = AdminClient();

        (await client.DeleteAsync($"/api/v1/admin/agents/{agent.AgentId.Value}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        RowsFor(agent.AgentId.Value).Select(r => (r.Kind, r.Counted)).Should().Equal(
            (LicenseAgentEventKinds.AgentCreated, (bool?)true),
            (LicenseAgentEventKinds.AgentDeleted, false));
    }

    [Fact]
    public async Task DeleteUser_ShouldWriteNoRow_ForAUserWithoutAnAgent()
    {
        var user = await SeedUserAsync(UserStatus.Active);
        using var client = AdminClient();

        (await client.DeleteAsync($"/api/v1/admin/users/{user.UserId.Value}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Services.GetRequiredService<IUserStore>().GetByIdAsync(Tenant, user.UserId, CancellationToken.None)).Should().BeNull();
        Ledger().Events(Tenant.Value).Should().NotContain(r => r.UserId == user.UserId.Value);
    }

    [Fact]
    public async Task PurgeUserData_ShouldWriteAgentDeletedThenUserDeleted_WhenTheUserOwnsAnAgent()
    {
        var agent = await CreateAgentAsync(UserStatus.Active);

        await Services.GetRequiredService<IGdprPurgeService>().PurgeUserDataAsync(
            Tenant.Value, agent.UserId.Value, "dpo-user", "Art. 17 request", CancellationToken.None);

        RowsFor(agent.AgentId.Value).Skip(1).Select(r => (r.Kind, r.UserStatus, r.Counted, r.ActorUserId)).Should().Equal(
            (LicenseAgentEventKinds.AgentDeleted, null, (bool?)false, "dpo-user"),
            (LicenseAgentEventKinds.UserDeleted, "Active", false, "dpo-user"));
        (await Services.GetRequiredService<IAgentStore>().GetByIdAsync(Tenant, agent.AgentId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Writer_ShouldBeTheInMemoryTwin_OverTheUndecoratedStores()
    {
        Services.GetRequiredService<ILicensedAgentChangeWriter>().Should().BeOfType<InMemoryLicensedAgentChangeWriter>();
        Services.GetRequiredService<ILicensedUserChangeWriter>().Should().BeSameAs(Services.GetRequiredService<ILicensedAgentChangeWriter>());
        Services.GetRequiredService<ILicensedAgentOwnershipWriter>().Should().BeSameAs(Services.GetRequiredService<ILicensedAgentChangeWriter>());
    }

    private InMemoryLicenseAgentLedger Ledger() => Services.GetRequiredService<InMemoryLicenseAgentLedger>();

    private List<LicenseAgentEvent> RowsFor(string agentId) =>
        Ledger().Events(Tenant.Value).Where(r => r.AgentId == agentId && r.Kind != LicenseAgentEventKinds.AgentBaseline).ToList();

    private HttpClient AdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, AccountStatusApiFactory.CustomerTenantId)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<Agent> CreateAgentAsync(UserStatus status)
    {
        var user = await SeedUserAsync(status);
        using var client = AdminClient();
        var response = await client.PostAsJsonAsync("/api/v1/admin/agents", new { userId = user.UserId.Value, displayName = "Ledgered" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var agentId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["agentId"]!.GetValue<string>();
        return (await Services.GetRequiredService<IAgentStore>().GetByIdAsync(Tenant, EntityId.From(agentId), CancellationToken.None))!;
    }

    private async Task<User> SeedUserAsync(UserStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            UserId = EntityId.From($"lam2-user-{id}"),
            TenantId = Tenant,
            Email = $"lam2-{id}@example.test",
            DisplayName = "Licensed Agent Ledger",
            Role = UserRole.Agent,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await Services.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None);
        return user;
    }
}

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.4): at each of the three ownership call sites — supervisor
/// takeover, agent transfer, supervisor reassign — the new owner and its ledger row are committed together.
/// </summary>
public sealed class LicensedAgentLedgerOwnershipTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private readonly ConversationOwnershipApiFactory _factory;

    public LicensedAgentLedgerOwnershipTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Takeover_ShouldCommitTheOwnerWithAConversationTakenOverRow()
    {
        var (supervisor, supervisorAgent) = _factory.NewAgent("lam-supervisor", UserRole.Supervisor);
        var (_, owner) = _factory.NewAgent("lam-owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync($"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/takeover", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveCommitted(conversation, supervisorAgent, supervisor.UserId.Value, LicenseAgentEventKinds.ConversationTakenOver);
    }

    [Fact]
    public async Task Transfer_ShouldCommitTheOwnerWithAConversationTransferredRow()
    {
        var (owner, ownerAgent) = _factory.NewAgent("lam-owner");
        var (_, target) = _factory.NewAgent("lam-target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(ownerAgent.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(owner);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer", new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveCommitted(conversation, target, owner.UserId.Value, LicenseAgentEventKinds.ConversationTransferred);
    }

    [Fact]
    public async Task Reassign_ShouldCommitTheOwnerWithAConversationReassignedRow()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "lam-supervisor");
        var (_, owner) = _factory.NewAgent("lam-owner");
        var (_, target) = _factory.NewAgent("lam-target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/reassign", new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ShouldHaveCommitted(conversation, target, supervisor.UserId.Value, LicenseAgentEventKinds.ConversationReassigned);
    }

    private void ShouldHaveCommitted(Conversation conversation, Agent receiver, string actorUserId, string kind)
    {
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(receiver.AgentId));
        var row = _factory.Services.GetRequiredService<InMemoryLicenseAgentLedger>()
            .Events(ConversationOwnershipApiFactory.Tenant.Value)
            .Should().ContainSingle(r => r.ConversationId == conversation.ConversationId.Value).Subject;
        (row.Kind, row.AgentId, row.UserId, row.ActorUserId, row.Counted)
            .Should().Be((kind, receiver.AgentId.Value, receiver.UserId.Value, actorUserId, (bool?)true));
    }
}
