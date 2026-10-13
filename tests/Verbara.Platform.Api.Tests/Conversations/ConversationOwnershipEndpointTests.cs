using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// Who may act on a conversation, over the real <c>Program.cs</c> pipeline with access tokens the host
/// mints: the owner is the caller's Agent profile (never its user id); an offer is accepted or rejected
/// only by the agent it was made to; a transfer needs <c>contacts:conversation:transfer</c>, a target that
/// exists, and the owner or a supervisor; close and typify need the owner or a supervisor. Every test
/// checks the stored conversation afterwards, not only the status code.
/// </summary>
public sealed class ConversationOwnershipEndpointTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private readonly ConversationOwnershipApiFactory _factory;

    public ConversationOwnershipEndpointTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    // ─── transfer ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ShouldReturn403AndKeepOwner_WhenCallerIsNotTheOwner()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(other);

        // Transferring to its own user id was how another agent took a conversation over.
        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetAgentId = other.UserId.Value });

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Transfer_ShouldReturn403AndKeepOwner_WhenTheOwnerLacksTransferPermission()
    {
        // The Api role template does not grant contacts:conversation:transfer. This user owns the
        // conversation, so nothing but the permission stands between it and the transfer.
        var (apiUser, apiAgent) = _factory.NewAgent("integration", UserRole.Api);
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(apiAgent.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(apiUser);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(apiAgent.AgentId));
    }

    [Fact]
    public async Task Transfer_ShouldReturn403AndKeepOwner_WhenCallerIsAnApiKeyBoundToNoUser()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.UnboundApiKeyClient();

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
    }

    [Fact]
    public async Task Transfer_ShouldReturn400AndKeepOwnerAndCapacity_WhenTargetAgentDoesNotExist()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetAgentId = "ghost-agent" });

        await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "target-agent-not-found");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        stored.State.Should().Be(ConversationState.Active);
        _factory.LoadOf(owner.AgentId).Should().Be(1, "a refused transfer releases nothing");
    }

    [Fact]
    public async Task Transfer_ShouldReturn400AndKeepOwner_WhenTargetQueueDoesNotExist()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = "ghost-queue" });

        await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "target-queue-not-found");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        stored.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task Transfer_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var (user, _) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations/no-such-conversation/transfer", new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Transfer_ShouldReturn409AndReleaseNothing_WhenTheConversationCannotMove()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var (_, target) = _factory.NewAgent("target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Closed);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        stored.State.Should().Be(ConversationState.Closed);
        _factory.LoadOf(owner.AgentId).Should().Be(1, "capacity is released only once the transfer can happen");
        _factory.LoadOf(target.AgentId).Should().Be(0);
    }

    [Fact]
    public async Task Transfer_ShouldMoveOwnerAndCapacityAndWriteAudit_WhenOwnerTransfersToAnotherAgent()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var (_, target) = _factory.NewAgent("target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetAgentId = target.AgentId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(target.AgentId));
        stored.State.Should().Be(ConversationState.Active);
        _factory.LoadOf(owner.AgentId).Should().Be(0);
        _factory.LoadOf(target.AgentId).Should().Be(1);

        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.transferred").Subject;
        entry.ActorId.Should().Be(user.UserId.Value);
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["target_agent"].Should().Be(target.AgentId.Value);
        entry.Metadata["from_owner_id"].Should().Be(owner.AgentId.Value);
        entry.Metadata.Should().NotContainKey("by_supervisor");
    }

    [Fact]
    public async Task Transfer_ShouldQueueTheConversation_WhenOwnerTransfersToAnExistingQueue()
    {
        var (user, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        stored.State.Should().Be(ConversationState.Queued);
    }

    [Fact]
    public async Task Transfer_ShouldSucceedAndAuditBySupervisor_WhenSupervisorTransfersAnotherAgentsConversation()
    {
        // A supervisor needs no Agent profile to move someone else's conversation.
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.transferred").Subject;
        entry.ActorId.Should().Be(supervisor.UserId.Value);
        entry.Metadata!["by_supervisor"].Should().Be(supervisor.UserId.Value);
        entry.Metadata["target_queue"].Should().Be(queue.QueueId.Value);
    }

    // ─── accept / reject ──────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_ShouldMakeCallersAgentTheOwner_WhenOfferedToCaller()
    {
        var (user, agent) = _factory.NewAgent("offered");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(agent, queue);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "accept"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agent.AgentId));
        _factory.LoadOf(agent.AgentId).Should().Be(1);
    }

    [Fact]
    public async Task Accept_ShouldReturn403AndKeepOffer_WhenOfferedToAnotherAgent()
    {
        var (_, offered) = _factory.NewAgent("offered");
        var (sniper, sniperAgent) = _factory.NewAgent("sniper");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(offered, queue);
        using var client = _factory.ClientFor(sniper);

        var response = await client.PostAsync(Route(conversation, "accept"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-offered-to-you");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Offered);
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        _factory.LoadOf(sniperAgent.AgentId).Should().Be(0);
    }

    [Fact]
    public async Task Accept_ShouldReturn403AndKeepOffer_WhenCallerHasNoAgentProfile()
    {
        var (_, offered) = _factory.NewAgent("offered");
        var supervisor = _factory.NewUser(UserRole.Supervisor, "no-profile");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(offered, queue);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Route(conversation, "accept"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-an-agent");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task Accept_ShouldReturn403AndKeepOffer_WhenCallerIsAnApiKeyBoundToNoUser()
    {
        var (_, offered) = _factory.NewAgent("offered");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(offered, queue);
        using var client = _factory.UnboundApiKeyClient();

        var response = await client.PostAsync(Route(conversation, "accept"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task Accept_ShouldReturn409AndKeepOffer_WhenAgentHasNoCapacity()
    {
        var (user, agent) = _factory.NewAgent("full", capacity: new ChannelCapacityOverride { MaxChat = 0 });
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(agent, queue);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "accept"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Offered);
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
    }

    [Fact]
    public async Task Accept_ShouldReturn404_WhenConversationDoesNotExist()
    {
        var (user, _) = _factory.NewAgent("agent");
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync("/api/v1/conversations/no-such-conversation/accept", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reject_ShouldReturn403AndKeepOffer_WhenOfferedToAnotherAgent()
    {
        var (_, offered) = _factory.NewAgent("offered");
        var (other, _) = _factory.NewAgent("other");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(offered, queue);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsync(Route(conversation, "reject"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-offered-to-you");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task Reject_ShouldReturn403AndKeepOffer_WhenCallerIsAnApiKeyBoundToNoUser()
    {
        var (_, offered) = _factory.NewAgent("offered");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(offered, queue);
        using var client = _factory.UnboundApiKeyClient();

        var response = await client.PostAsync(Route(conversation, "reject"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task Reject_ShouldRequeue_WhenOfferedAgentRejects()
    {
        var (user, agent) = _factory.NewAgent("offered");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedOfferTo(agent, queue);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "reject"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Queued);
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
    }

    // ─── messages, hold, unhold: the owner is an Agent profile ────────────────

    [Fact]
    public async Task SendMessage_ShouldSucceed_WhenCallerOwnsConversationByAgentId()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "messages"), new { text = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessagesOfAsync(conversation)).Should().ContainSingle(m => m.SenderId == agent.AgentId.Value);
    }

    [Fact]
    public async Task SendMessage_ShouldReturn403AndSendNothing_WhenCallerIsNotTheOwner()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsJsonAsync(Route(conversation, "messages"), new { text = "not mine" });

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        (await MessagesOfAsync(conversation)).Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_ShouldReturn403AndSendNothing_WhenTheOwnerIsTheCallersUserId()
    {
        // A conversation recorded with a user id as its owner (what a transfer to a user id produced)
        // is owned by no agent: the user id is not an owner identity.
        var (user, _) = _factory.NewAgent("phantom");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(user.UserId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "messages"), new { text = "hijacked" });

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        (await MessagesOfAsync(conversation)).Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_ShouldReturn403_WhenCallerHasNoAgentProfile()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var admin = _factory.NewUser(UserRole.Admin, "admin");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(admin);

        var response = await client.PostAsJsonAsync(Route(conversation, "messages"), new { text = "hello" });

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-an-agent");
        (await MessagesOfAsync(conversation)).Should().BeEmpty();
    }

    [Fact]
    public async Task Hold_ShouldSucceed_WhenCallerOwnsConversationByAgentId()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "hold"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.OnHold);
    }

    [Fact]
    public async Task Hold_ShouldReturn403AndKeepState_WhenTheOwnerIsTheCallersUserId()
    {
        var (user, _) = _factory.NewAgent("phantom");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(user.UserId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "hold"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task Hold_ShouldReturn403AndKeepState_WhenCallerIsNotTheOwner()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsync(Route(conversation, "hold"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task Unhold_ShouldSucceed_WhenCallerOwnsConversationByAgentId()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.OnHold);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "unhold"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Active);
    }

    [Fact]
    public async Task Unhold_ShouldReturn403AndKeepState_WhenTheOwnerIsTheCallersUserId()
    {
        var (user, _) = _factory.NewAgent("phantom");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(user.UserId), ConversationState.OnHold);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "unhold"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.OnHold);
    }

    [Fact]
    public async Task Unhold_ShouldReturn403AndKeepState_WhenCallerIsNotTheOwner()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.OnHold);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsync(Route(conversation, "unhold"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.OnHold);
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn403AndCreateNothing_WhenACallerWithoutAnAgentProfileSendsAnInitialMessage()
    {
        var admin = _factory.NewUser(UserRole.Admin, "admin");
        var contactId = await SeedContactAsync();
        using var client = _factory.ClientFor(admin);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", new
        {
            contactId = contactId.Value,
            channel = "WebChat",
            initialMessage = "hello",
        });

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-an-agent");
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IConversationStore>().FindActiveByContactAsync(
            ConversationOwnershipApiFactory.Tenant, contactId, ChannelType.WebChat, CancellationToken.None))
            .Should().BeNull(because: "the request is refused before a conversation is created");
    }

    // ─── close / typify ───────────────────────────────────────────────────────

    [Fact]
    public async Task Close_ShouldReturn403AndKeepState_WhenCallerIsNotOwnerOrSupervisor()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.WaitingForCustomer);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.WaitingForCustomer);
    }

    [Fact]
    public async Task Close_ShouldReturn403AndKeepState_WhenCallerIsAnApiKeyBoundToNoUser()
    {
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.WaitingForCustomer);
        using var client = _factory.UnboundApiKeyClient();

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.WaitingForCustomer);
    }

    [Fact]
    public async Task Close_ShouldClose_WhenOwnerCloses()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agent.AgentId), ConversationState.WaitingForCustomer);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        _factory.AuditOf(conversation.ConversationId).Should().NotContain(e => e.Action == "conversation.closed");
    }

    [Fact]
    public async Task Close_ShouldCloseAndAuditBySupervisor_WhenSupervisorClosesAnotherAgentsConversation()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(owner.AgentId), ConversationState.WaitingForCustomer);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.closed").Subject;
        entry.ActorId.Should().Be(supervisor.UserId.Value);
        entry.Metadata!["by_supervisor"].Should().Be(supervisor.UserId.Value);
    }

    [Fact]
    public async Task Close_ShouldReturn409TypificationRequiredAndKeepState_WhenSchemaIsBound()
    {
        // Typification is demanded (a published schema is bound): /close refuses with a stable code
        // so the client opens the wrap-up sheet, and mutates nothing (design D5). The schema-free
        // close cases live in ConversationCloseWithoutSchemaEndpointTests: this class's host binds a
        // tenant-wide schema the first time any typify test runs, and that binding cannot be undone.
        _factory.EnsureTypificationSchema();
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        _factory.Reserve(agent.AgentId);
        using var events = StateChangeRecorder.For(_factory, conversation);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "typification-required");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agent.AgentId));
        _factory.LoadOf(agent.AgentId).Should().Be(1, "a refused close releases nothing");
        events.Changes.Should().BeEmpty();
        (await SubmissionOfAsync(conversation)).Should().BeNull();
    }

    [Fact]
    public async Task Typify_ShouldReturn403AndKeepState_WhenCallerIsNotOwnerOrSupervisor()
    {
        _factory.EnsureTypificationSchema();
        var (_, owner) = _factory.NewAgent("owner");
        var (other, _) = _factory.NewAgent("other");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsJsonAsync(Route(conversation, "typify"), TypifyBody());

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Active);
        (await SubmissionOfAsync(conversation)).Should().BeNull();
    }

    [Fact]
    public async Task Typify_ShouldWrapUpAndAttributeToTheUser_WhenOwnerTypifies()
    {
        _factory.EnsureTypificationSchema();
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "typify"), TypifyBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.WrapUp);
        (await SubmissionOfAsync(conversation))!.AgentId.Should().Be(user.UserId);
        _factory.AuditOf(conversation.ConversationId).Should().NotContain(e => e.Action == "conversation.typified");
    }

    [Fact]
    public async Task Typify_ShouldWrapUpAndAuditBySupervisor_WhenSupervisorTypifiesAnotherAgentsConversation()
    {
        _factory.EnsureTypificationSchema();
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Route(conversation, "typify"), TypifyBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.WrapUp);
        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.typified").Subject;
        entry.Metadata!["by_supervisor"].Should().Be(supervisor.UserId.Value);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    internal static string Route(Conversation conversation, string action) =>
        $"/api/v1/conversations/{conversation.ConversationId.Value}/{action}";

    internal static object TypifyBody() => new
    {
        selectedNodePath = new[]
        {
            ConversationOwnershipApiFactory.TypificationRootNodeId,
            ConversationOwnershipApiFactory.TypificationLeafNodeId,
        },
        fieldValues = new Dictionary<string, string>(),
        notes = "done",
    };

    internal static async Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, because: $"the response was {body}");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be(error);
    }

    private async Task<EntityId> SeedContactAsync()
    {
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);
        return contact.ContactId;
    }

    private async Task<IReadOnlyList<Message>> MessagesOfAsync(Conversation conversation)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageStore>().GetConversationMessagesAsync(
            ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, limit: 50, offset: 0, CancellationToken.None);
    }

    private async Task<Verbara.Platform.Typification.TypificationSubmission?> SubmissionOfAsync(Conversation conversation)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>()
            .GetByConversationIdAsync(ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, CancellationToken.None);
    }
}

/// <summary>
/// <c>POST /conversations/{id}/close</c> on a deployment where no typification schema is bound — what an
/// unlicensed tenant always is (close-chat-without-typification-licence, design D1-D10). An agent-held
/// chat closes through <c>Active → WrapUp → Closed</c>, its owner's capacity unit is released exactly
/// once, both state changes are published, and a refusal carries a stable 409 code.
/// </summary>
/// <remarks>
/// A class of its own so it gets a host of its own: <see cref="ConversationOwnershipEndpointTests"/>
/// binds a tenant-wide schema (<see cref="ConversationOwnershipApiFactory.EnsureTypificationSchema"/>)
/// the first time a typify test runs, and these tests need that binding never to exist. Never call
/// <c>EnsureTypificationSchema</c> here.
/// </remarks>
public sealed class ConversationCloseWithoutSchemaEndpointTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private readonly ConversationOwnershipApiFactory _factory;

    public ConversationCloseWithoutSchemaEndpointTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    private static string Route(Conversation conversation, string action) =>
        ConversationOwnershipEndpointTests.Route(conversation, action);

    private static Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error) =>
        ConversationOwnershipEndpointTests.ShouldBeErrorAsync(response, status, error);

    [Fact]
    public async Task Close_ShouldCloseViaWrapUpAndReleaseCapacityOnce_WhenOwnerClosesActiveChatWithoutSchema()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        // Two chats held: a release that ran twice would show as 0, one that never ran as 2.
        _factory.Reserve(agent.AgentId);
        _factory.Reserve(agent.AgentId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Closed);
        stored.ClosedAt.Should().NotBeNull();
        _factory.LoadOf(agent.AgentId).Should().Be(1, "closing one chat releases exactly its one unit");
    }

    [Fact]
    public async Task Close_ShouldCloseViaWrapUp_WhenOwnerClosesOnHoldChatWithoutSchema()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.OnHold);
        _factory.Reserve(agent.AgentId);
        _factory.Reserve(agent.AgentId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        _factory.LoadOf(agent.AgentId).Should().Be(1);
    }

    [Fact]
    public async Task Close_ShouldReturn409NotClosable_WhenConversationIsQueued()
    {
        // Queue-owned, so only a supervisor passes the actor check and reaches the state check.
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var queue = _factory.SeedQueue();
        var conversation = _factory.SeedConversation(ConversationOwner.ForQueue(queue.QueueId), ConversationState.Queued);
        using var events = StateChangeRecorder.For(_factory, conversation);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-not-closable");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Queued);
        stored.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        events.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_ShouldReturn409NotClosable_WhenVoiceConversationIsActive()
    {
        // The voice bridge owns a live call's wrap-up edge and its capacity release (design D8).
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active, channel: ChannelType.Voice);
        _factory.Reserve(agent.AgentId, ChannelType.Voice);
        using var events = StateChangeRecorder.For(_factory, conversation);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-not-closable");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Active);
        _factory.LoadOf(agent.AgentId, ChannelType.Voice).Should().Be(1);
        events.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_ShouldNotReleaseCapacity_WhenClosingFromWrapUp()
    {
        // The unit was released when the chat entered WrapUp (design D2); the close releases nothing.
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.WrapUp);
        _factory.Reserve(agent.AgentId); // another chat this agent still holds
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        _factory.LoadOf(agent.AgentId).Should().Be(1, "nothing at or after WrapUp → Closed releases again");
    }

    [Fact]
    public async Task Close_ShouldReleaseOwnersCapacityAndAudit_WhenSupervisorClosesActiveChat()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var (_, owner) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(owner.AgentId), ConversationState.Active);
        _factory.Reserve(owner.AgentId);
        _factory.Reserve(owner.AgentId);
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        _factory.LoadOf(owner.AgentId).Should().Be(1, "a supervisor close releases the owner's unit");
        var entry = _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(e => e.Action == "conversation.closed").Subject;
        entry.ActorId.Should().Be(supervisor.UserId.Value);
        entry.Metadata!["by_supervisor"].Should().Be(supervisor.UserId.Value);
        entry.Metadata["owner_kind"].Should().Be(nameof(ConversationOwnerKind.Agent));
        entry.Metadata["owner_id"].Should().Be(owner.AgentId.Value);
        entry.Metadata["typification"].Should().Be("skipped-no-schema");
    }

    [Fact]
    public async Task Close_ShouldPublishWrapUpThenClosedStateChanges_WhenClosingFromActive()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        _factory.Reserve(agent.AgentId);
        using var events = StateChangeRecorder.For(_factory, conversation);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsync(Route(conversation, "close"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        events.Changes.Should().Equal(
            (nameof(ConversationState.Active), nameof(ConversationState.WrapUp)),
            (nameof(ConversationState.WrapUp), nameof(ConversationState.Closed)));
    }

    [Fact]
    public async Task Close_ShouldReleaseOnce_WhenTwoClosesRace()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active);
        _factory.Reserve(agent.AgentId);
        _factory.Reserve(agent.AgentId);
        using var first = _factory.ClientFor(user);
        using var second = _factory.ClientFor(user);

        var responses = await Task.WhenAll(
            first.PostAsync(Route(conversation, "close"), content: null),
            second.PostAsync(Route(conversation, "close"), content: null));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        var bodies = string.Join(" | ", await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync())));
        // tasks.md 1.1 names "one 200, one 409". Design D4 also allows a loser that read WrapUp after the
        // winner's wrap-up to finish the close itself (200), so the invariant pinned here is: at least one
        // 200, nothing but 200/409, the conversation Closed, and the owner's load down by exactly one.
        statuses.Should().Contain(HttpStatusCode.OK, because: $"the responses were {bodies}");
        statuses.Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict,
            because: $"the responses were {bodies}");
        _factory.Load(conversation.ConversationId)!.State.Should().Be(ConversationState.Closed);
        _factory.LoadOf(agent.AgentId).Should().Be(1, "two racing closes release the one unit exactly once");
        foreach (var response in responses)
            response.Dispose();
    }
}

/// <summary>
/// Records the <see cref="ConversationStateChangedEvent"/>s the host publishes for one conversation, as
/// (old, new) pairs in publish order.
/// </summary>
internal sealed class StateChangeRecorder : IDisposable
{
    private readonly List<(string Old, string New)> _changes = [];
    private readonly IDisposable _subscription;

    private StateChangeRecorder(PlatformEventBus bus, string conversationId) =>
        _subscription = bus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent changed && changed.ConversationId == conversationId)
                lock (_changes)
                    _changes.Add((changed.OldState, changed.NewState));
        });

    public static StateChangeRecorder For(ConversationOwnershipApiFactory factory, Conversation conversation) =>
        new(factory.Services.GetRequiredService<PlatformEventBus>(), conversation.ConversationId.Value);

    public IReadOnlyList<(string Old, string New)> Changes
    {
        get
        {
            lock (_changes)
                return [.. _changes];
        }
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>
/// The ownership host with a seam between a close's (or typify's) actor check and its wrap-up: the
/// typification resolver both routes call after the actor check runs <see cref="BeforeResolve"/> once,
/// so a test can land a transfer exactly there (close-chat-without-typification-licence D3).
/// </summary>
public sealed class TransferDuringWrapUpApiFactory : ConversationOwnershipApiFactory
{
    private Func<Task>? _beforeResolve;

    /// <summary>Runs once, at the next typification resolve, before the real resolver.</summary>
    public Func<Task>? BeforeResolve
    {
        set => Volatile.Write(ref _beforeResolve, value);
    }

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        var real = services.Last(d => d.ServiceType == typeof(Verbara.Platform.Typification.Resolution.ITypificationResolver));
        services.RemoveAll<Verbara.Platform.Typification.Resolution.ITypificationResolver>();
        services.AddSingleton<Verbara.Platform.Typification.Resolution.ITypificationResolver>(sp => new HookedResolver(
            (Verbara.Platform.Typification.Resolution.ITypificationResolver)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!),
            this));
    }

    /// <summary>
    /// What a transfer from <paramref name="from"/> to <paramref name="to"/> commits: the stored owner
    /// swapped (state kept <c>Active</c>), the old owner's unit released and the new owner's reserved.
    /// </summary>
    public async Task TransferAsync(Conversation conversation, Agent from, Agent to)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var stored = (await store.GetByIdAsync(Tenant, conversation.ConversationId, CancellationToken.None))!;
        stored.Owner = ConversationOwner.ForAgent(to.AgentId);
        await store.SaveAsync(stored, CancellationToken.None);
        var capacity = Services.GetRequiredService<Verbara.Platform.Queues.Services.IAgentCapacityService>();
        await capacity.ReleaseAsync(Tenant, from.AgentId, conversation.Channel, CancellationToken.None);
        await capacity.ReserveAsync(Tenant, to.AgentId, conversation.Channel, CancellationToken.None);
    }

    private sealed class HookedResolver(
        Verbara.Platform.Typification.Resolution.ITypificationResolver inner,
        TransferDuringWrapUpApiFactory factory) : Verbara.Platform.Typification.Resolution.ITypificationResolver
    {
        public async Task<Verbara.Platform.Typification.Resolution.ResolvedTypification?> ResolveForConversationAsync(
            Conversation conversation, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref factory._beforeResolve, null) is { } hook)
                await hook();
            return await inner.ResolveForConversationAsync(conversation, ct);
        }
    }
}

/// <summary>
/// A transfer that commits after <c>/close</c> authorized the old owner but before it wrapped up must
/// not let the old owner's close wrap up and release the new owner's chat (design D3).
/// </summary>
public sealed class CloseDuringTransferEndpointTests : IClassFixture<TransferDuringWrapUpApiFactory>
{
    private readonly TransferDuringWrapUpApiFactory _factory;

    public CloseDuringTransferEndpointTests(TransferDuringWrapUpApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Close_ShouldReturn409AndKeepNewOwnersCapacity_WhenTransferCommitsAfterActorCheck()
    {
        var (userA, agentA) = _factory.NewAgent("owner-a");
        var (_, agentB) = _factory.NewAgent("owner-b");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        _factory.Reserve(agentA.AgentId);
        _factory.Reserve(agentB.AgentId); // another chat agent B already holds
        _factory.BeforeResolve = () => _factory.TransferAsync(conversation, agentA, agentB);
        using var events = StateChangeRecorder.For(_factory, conversation);
        using var client = _factory.ClientFor(userA);

        var response = await client.PostAsync(ConversationOwnershipEndpointTests.Route(conversation, "close"), content: null);

        await ConversationOwnershipEndpointTests.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-not-closable");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agentB.AgentId));
        _factory.LoadOf(agentB.AgentId).Should().Be(2, "agent B's units are untouched by agent A's refused close");
        _factory.LoadOf(agentA.AgentId).Should().Be(0, "only the transfer released agent A");
        events.Changes.Should().BeEmpty();
    }
}

/// <summary>
/// The same interleaving on <c>/typify</c>: the submission would be attributed to the old owner while the
/// new owner's chat wrapped up, so it is refused with 409 and nothing is persisted (design D3/D6).
/// </summary>
public sealed class TypifyDuringTransferEndpointTests : IClassFixture<TransferDuringWrapUpApiFactory>
{
    private readonly TransferDuringWrapUpApiFactory _factory;

    public TypifyDuringTransferEndpointTests(TransferDuringWrapUpApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Typify_ShouldReturn409AndKeepNewOwnersCapacity_WhenTransferCommitsAfterActorCheck()
    {
        _factory.EnsureTypificationSchema();
        var (userA, agentA) = _factory.NewAgent("owner-a");
        var (_, agentB) = _factory.NewAgent("owner-b");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        _factory.Reserve(agentA.AgentId);
        _factory.Reserve(agentB.AgentId);
        _factory.BeforeResolve = () => _factory.TransferAsync(conversation, agentA, agentB);
        using var client = _factory.ClientFor(userA);

        var response = await client.PostAsJsonAsync(
            ConversationOwnershipEndpointTests.Route(conversation, "typify"), ConversationOwnershipEndpointTests.TypifyBody());

        await ConversationOwnershipEndpointTests.ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-not-closable");
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agentB.AgentId));
        _factory.LoadOf(agentB.AgentId).Should().Be(2);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>()
            .GetByConversationIdAsync(ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, CancellationToken.None))
            .Should().BeNull();
    }
}
