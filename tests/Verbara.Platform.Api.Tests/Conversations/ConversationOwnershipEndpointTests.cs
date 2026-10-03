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

    private static string Route(Conversation conversation, string action) =>
        $"/api/v1/conversations/{conversation.ConversationId.Value}/{action}";

    private static object TypifyBody() => new
    {
        selectedNodePath = new[]
        {
            ConversationOwnershipApiFactory.TypificationRootNodeId,
            ConversationOwnershipApiFactory.TypificationLeafNodeId,
        },
        fieldValues = new Dictionary<string, string>(),
        notes = "done",
    };

    private static async Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
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
