using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment, the conversation read rule over the real host (design D2-D5, D12, D13, D18;
/// spec <c>conversation-read-scope</c>; fixture <c>conversation-read-refusals.v1.json</c>).
/// <list type="bullet">
/// <item><b>Regression tests, written first against the unfixed code (tasks 1.2, 1.7).</b> Every read was scoped to
/// the tenant only, so each of them answered 200 where the rule answers 403 <c>not-readable</c>. They turn green with
/// tasks 2.1-2.3.</item>
/// <item><b>Guard tests (task 1.3)</b>, green before and after the change: the console's three current reads (inbox
/// list, offered preview, owner re-read), the monitor and Admin reads, and the 404 of an id outside the tenant.</item>
/// </list>
/// </summary>
public sealed class ConversationReadScopeTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private const string NotReadable = "not-readable";

    private readonly ConversationOwnershipApiFactory _factory;

    public ConversationReadScopeTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    private static string ConversationRoute(Conversation conversation) =>
        $"/api/v1/conversations/{conversation.ConversationId.Value}";

    private static string MessagesRoute(Conversation conversation) => $"{ConversationRoute(conversation)}/messages";

    private static string ContactRoute(Contact contact) => $"/api/v1/contacts/{contact.ContactId.Value}/conversations";

    private static async Task ShouldBeNotReadableAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"the response was {body}");
        JsonNode.DeepEquals(JsonNode.Parse(body), new JsonObject { ["error"] = NotReadable })
            .Should().BeTrue(because: $"the refusal body is {{\"error\":\"{NotReadable}\"}}, and it was {body}");
    }

    private static async Task ShouldBeOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
    }

    /// <summary>Contact K with a <c>Closed</c> conversation owned by agent A.</summary>
    private (Contact Contact, User UserA, Conversation ClosedOfA) ContactWithAClosedConversation()
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));
        var (userA, ownerA) = _factory.NewAgent("history-owner");
        var closed = _factory.SeedConversation(
            ConversationOwner.ForAgent(ownerA.AgentId), ConversationState.Closed, contactId: contact.ContactId);
        return (contact, userA, closed);
    }

    private void SeedMessage(Conversation conversation, string text)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.From($"m-{Guid.NewGuid():N}"),
            ConversationId = conversation.ConversationId,
            TenantId = conversation.TenantId,
            Direction = MessageDirection.Inbound,
            Channel = conversation.Channel,
            SenderId = "customer",
            Content = new MessageEnvelope([new TextBlock(text)]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<string>> ConversationIdsAsync(HttpResponseMessage response)
    {
        await ShouldBeOkAsync(response);
        var page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return [.. page["items"]!.AsArray().Select(item => item!["conversationId"]!.GetValue<string>())];
    }

    // ═══ Task 1.2: regression tests, red on the unfixed code ═══════════════════

    [Fact]
    public async Task GetConversation_ShouldRefuseNotReadable_WhenTheAgentHoldsNothingOfTheContact()
    {
        var (_, _, closedOfA) = ContactWithAClosedConversation();
        var (userB, _) = _factory.NewAgent("stranger");
        using var clientB = _factory.ClientFor(userB);

        await ShouldBeNotReadableAsync(await clientB.GetAsync(ConversationRoute(closedOfA)));
    }

    [Fact]
    public async Task GetMessages_ShouldRefuseNotReadable_WhenTheAgentHoldsNothingOfTheContact()
    {
        var (_, _, closedOfA) = ContactWithAClosedConversation();
        SeedMessage(closedOfA, "my card number is on the invoice");
        var (userB, _) = _factory.NewAgent("stranger");
        using var clientB = _factory.ClientFor(userB);

        await ShouldBeNotReadableAsync(await clientB.GetAsync($"{MessagesRoute(closedOfA)}?limit=50&offset=0"));
    }

    [Fact]
    public async Task GetContactConversations_ShouldRefuseNotReadable_WhenTheAgentHoldsNothingOfTheContact()
    {
        var (contact, _, _) = ContactWithAClosedConversation();
        var (userB, _) = _factory.NewAgent("stranger");
        using var clientB = _factory.ClientFor(userB);

        await ShouldBeNotReadableAsync(await clientB.GetAsync(ContactRoute(contact)));
    }

    [Fact]
    public async Task ConversationReads_ShouldRefuseNotReadable_WhenTheCallerIsATenantApiKeyBoundToNoUser()
    {
        var (contact, _, closedOfA) = ContactWithAClosedConversation();
        using var unbound = _factory.UnboundApiKeyClient();

        await ShouldBeNotReadableAsync(await unbound.GetAsync(ConversationRoute(closedOfA)));
        await ShouldBeNotReadableAsync(await unbound.GetAsync(MessagesRoute(closedOfA)));
        await ShouldBeNotReadableAsync(await unbound.GetAsync(ContactRoute(contact)));
    }

    [Fact]
    public async Task ContactReads_ShouldRefuseNotReadable_WhenTheAgentOnlyHasAnOpenOfferOnOneConversationOfTheContact()
    {
        // Q3: an offer grants the offered conversation only, never the contact's list or its other conversations.
        var (contact, _, closedOfA) = ContactWithAClosedConversation();
        var (userB, agentB) = _factory.NewAgent("offered");
        _factory.SeedOfferTo(agentB, _factory.SeedQueue(), contactId: contact.ContactId);
        using var clientB = _factory.ClientFor(userB);

        await ShouldBeNotReadableAsync(await clientB.GetAsync(ContactRoute(contact)));
        await ShouldBeNotReadableAsync(await clientB.GetAsync(ConversationRoute(closedOfA)));
    }

    [Fact]
    public async Task ListConversations_ShouldReturnOnlyTheAgentsOwnAndOfferedConversations_WhenNoFilterIsSent()
    {
        var (userA, agentA) = _factory.NewAgent("lister");
        var ownedActive = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        var ownedClosed = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Closed);
        var offered = _factory.SeedOfferTo(agentA, _factory.SeedQueue());
        var (_, other) = _factory.NewAgent("other");
        _factory.SeedConversation(ConversationOwner.ForAgent(other.AgentId), ConversationState.Active);
        _factory.SeedConversation(ConversationOwner.ForQueue(_factory.SeedQueue().QueueId), ConversationState.Queued);
        using var clientA = _factory.ClientFor(userA);

        var ids = await ConversationIdsAsync(await clientA.GetAsync("/api/v1/conversations?pageSize=100"));

        ids.Should().BeEquivalentTo(
            [ownedActive.ConversationId.Value, ownedClosed.ConversationId.Value, offered.ConversationId.Value],
            "an agent without monitor or read lists only what it owns or is offered, never the tenant (design D4)");
    }

    // ═══ Task 1.7: the shared-contact leak (design D18), red on the unfixed code ═══

    /// <summary>The tenant's anonymous voice contact, a live call of it owned by B and a closed call of it owned by A.</summary>
    private (Contact Anonymous, User UserB, Conversation CallOfB, Conversation CallOfA) TwoAnonymousCalls()
    {
        var anonymous = _factory.SeedContact(new ChannelAddress(ChannelType.Voice, "anonymous"));
        var (_, agentA) = _factory.NewAgent("anonymous-a");
        var (userB, agentB) = _factory.NewAgent("anonymous-b");
        var callOfA = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Closed,
            channel: ChannelType.Voice, contactId: anonymous.ContactId);
        var callOfB = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentB.AgentId), ConversationState.Active,
            channel: ChannelType.Voice, contactId: anonymous.ContactId);
        return (anonymous, userB, callOfB, callOfA);
    }

    [Fact]
    public async Task GetConversation_ShouldRefuseNotReadable_WhenTheAgentHoldsAnotherCallOfTheSharedAnonymousContact()
    {
        var (_, userB, _, callOfA) = TwoAnonymousCalls();
        using var clientB = _factory.ClientFor(userB);

        await ShouldBeNotReadableAsync(await clientB.GetAsync(ConversationRoute(callOfA)));
    }

    [Fact]
    public async Task GetContactConversations_ShouldListOnlyTheAgentsOwnCall_WhenTheContactIsTheSharedAnonymousContact()
    {
        var (anonymous, userB, callOfB, _) = TwoAnonymousCalls();
        using var clientB = _factory.ClientFor(userB);

        var response = await clientB.GetAsync(ContactRoute(anonymous));

        await ShouldBeOkAsync(response);
        var page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        page["totalCount"]!.GetValue<int>().Should().Be(1, "the list of a shared contact counts only the caller's own calls");
        page["items"]!.AsArray().Select(item => item!["id"]?.GetValue<string>())
            .Should().Equal([callOfB.ConversationId.Value], "items[].id (fixture contact-conversation-history.v1.json) is B's call only");
    }

    // ═══ Task 1.3: guard tests, green before and after the change ═════════════

    [Fact]
    public async Task ListConversations_ShouldReturnTheOwnedConversationsOnly_WhenTheInboxSendsItsOwnAgentId()
    {
        // Design D4: agentId = self keeps today's meaning (owner kind Agent and id A); the offer reaches the console
        // through conversation.offered, not through this list.
        var (userA, agentA) = _factory.NewAgent("inbox");
        var ownedActive = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        var ownedClosed = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Closed);
        _factory.SeedOfferTo(agentA, _factory.SeedQueue());
        var (_, other) = _factory.NewAgent("other");
        _factory.SeedConversation(ConversationOwner.ForAgent(other.AgentId), ConversationState.Active);
        using var clientA = _factory.ClientFor(userA);

        var ids = await ConversationIdsAsync(
            await clientA.GetAsync($"/api/v1/conversations?agentId={agentA.AgentId.Value}&pageSize=100"));

        ids.Should().BeEquivalentTo([ownedActive.ConversationId.Value, ownedClosed.ConversationId.Value]);
    }

    [Fact]
    public async Task GetMessages_ShouldAnswerTheTranscript_WhenTheOfferedAgentPreviewsItsOffer()
    {
        var (userB, agentB) = _factory.NewAgent("previewer");
        var offered = _factory.SeedOfferTo(agentB, _factory.SeedQueue());
        SeedMessage(offered, "hello, is anyone there?");
        using var clientB = _factory.ClientFor(userB);

        var response = await clientB.GetAsync(MessagesRoute(offered));

        await ShouldBeOkAsync(response);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task GetConversation_ShouldAnswerTheConversation_WhenTheOwnerReReadsItAfterTheClose()
    {
        var (_, userA, closedOfA) = ContactWithAClosedConversation();
        using var clientA = _factory.ClientFor(userA);

        var response = await clientA.GetAsync(ConversationRoute(closedOfA));

        await ShouldBeOkAsync(response);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["state"]!.GetValue<string>().Should().Be("Closed");
    }

    public static TheoryData<string> Monitors => new() { "supervisor", "manager", "admin" };

    [Theory]
    [MemberData(nameof(Monitors))]
    public async Task ConversationReads_ShouldAnswer200_WhenTheCallerIsASupervisorManagerOrAdmin(string who)
    {
        var (contact, _, closedOfA) = ContactWithAClosedConversation();
        var reader = who switch
        {
            "supervisor" => _factory.NewUser(UserRole.Supervisor, "monitor-supervisor"),
            "admin" => _factory.NewUser(UserRole.Admin, "monitor-admin"),
            // The Manager template grants contacts:conversation:monitor, but its users are not in SupervisorPlus.
            _ => _factory.NewUser(UserRole.Agent, "monitor-manager"),
        };
        if (who == "manager")
            _factory.GrantTemplate(reader, "manager");
        using var client = _factory.ClientFor(reader);

        await ShouldBeOkAsync(await client.GetAsync(ConversationRoute(closedOfA)));
        await ShouldBeOkAsync(await client.GetAsync(MessagesRoute(closedOfA)));
        await ShouldBeOkAsync(await client.GetAsync(ContactRoute(contact)));
    }

    [Fact]
    public async Task GetConversation_ShouldAnswer404_WhenTheIdNamesAConversationOfAnotherTenant()
    {
        var foreign = new Conversation
        {
            ConversationId = EntityId.From($"conv-foreign-{Guid.NewGuid():N}"),
            TenantId = new TenantId(ConversationOwnershipApiFactory.PlatformTenantId),
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(foreign, CancellationToken.None);
        using var supervisor = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "foreign-reader"));

        var response = await supervisor.GetAsync(ConversationRoute(foreign));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("a 404 keeps its empty body");
    }
}
