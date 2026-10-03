using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The conversation routes under impersonation, with impersonation tokens this host mints: the transfer
/// permission gate passes only on a permission minted into the token; a full session then acts with the
/// supervisor override its Admin role grants, audited with the impersonation context; a read-only session
/// writes nothing; and a takeover needs an Agent profile in the tenant, which the impersonator does not
/// hold to begin with.
/// </summary>
/// <remarks>
/// The missing profile is not a limit on a full session. Its Admin role passes the AdminOnly
/// <c>POST /admin/agents</c>, which binds a profile to whatever user id it is given, the impersonator's
/// included; with that profile the session takes over and sends like any agent. Role-gated routes do not
/// read the permissions minted into the token. Only a read-only session is stopped, by the guard.
/// </remarks>
public sealed class ConversationImpersonationTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private const string TransferPermission = "contacts:conversation:transfer";
    private const string ReadOnlyBody = "Operation not allowed in read-only impersonation mode";

    private readonly ConversationOwnershipApiFactory _factory;

    public ConversationImpersonationTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Transfer_ShouldSucceedAndAuditTheImpersonator_WhenAFullSessionCarriesTheTransferPermission()
    {
        var admin = _factory.NewPlatformAdmin();
        var (_, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, [TransferPermission], readOnly: false));

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.transferred").Subject;
        entry.ActorId.Should().Be(admin.UserId.Value);
        entry.Metadata!["by_supervisor"].Should().Be(admin.UserId.Value);
        entry.Metadata["impersonator_id"].Should().Be(admin.UserId.Value);
        entry.Metadata["impersonator_tenant"].Should().Be(admin.TenantId.Value);
        entry.Metadata.Should().ContainKey("impersonation_session_id");
    }

    [Fact]
    public async Task Transfer_ShouldReturn403AndKeepOwner_WhenAFullSessionLacksTheTransferPermission()
    {
        var admin = _factory.NewPlatformAdmin();
        var (_, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, ["contacts:contact:view"], readOnly: false));

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Transfer_ShouldReturn403AndKeepOwner_WhenTheSessionIsReadOnly()
    {
        var admin = _factory.NewPlatformAdmin();
        var (_, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, [TransferPermission], readOnly: true));

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ReadOnlyBody);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Takeover_ShouldReturn403AndKeepOwner_WhenTheSessionIsReadOnly()
    {
        var admin = _factory.NewPlatformAdmin();
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, [TransferPermission], readOnly: true));

        var response = await client.PostAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/takeover", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ReadOnlyBody);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Takeover_ShouldReturn403AndKeepOwner_WhenAFullSessionsImpersonatorHasNoAgentProfileInTheTenant()
    {
        // The refusal is the one any caller without a profile gets; see the class remarks for why it
        // does not hold a full session back.
        var admin = _factory.NewPlatformAdmin();
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.CreateBearerClient(
            _factory.MintImpersonationToken(admin, [TransferPermission], readOnly: false));

        var response = await client.PostAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/takeover", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not-an-agent");
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    private static string Route(Conversation conversation, string action) =>
        $"/api/v1/conversations/{conversation.ConversationId.Value}/{action}";
}
