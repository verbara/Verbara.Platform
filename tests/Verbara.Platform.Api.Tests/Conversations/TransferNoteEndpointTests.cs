using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Switchboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-console-contract-debt (C1, C2, H2, H11, H13): the note a transfer may carry, over the real pipeline. Refused
/// before anything moves, stored once as a <c>System</c> message of the conversation's own channel after the move,
/// published to the console, and never able to fail a move that has already happened. Each assertion on a wire shape
/// reads the frozen fixture and the raw JSON, because a null after deserializing passes whether a member is omitted or
/// not (H11).
/// </summary>
public sealed class TransferNoteEndpointTests : IClassFixture<TransferNoteApiFactory>
{
    private const string Note = "Customer disputes the October invoice; please do not re-issue it.";

    /// <summary>The event id the failed note write logs under.</summary>
    private const int NoteWriteFailedEventId = 7511;

    private readonly TransferNoteApiFactory _factory;

    public TransferNoteEndpointTests(TransferNoteApiFactory factory) => _factory = factory;

    private static JsonObject Fixture(string name)
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", name);
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static string Route(Conversation conversation, string action) =>
        ConversationOwnershipEndpointTests.Route(conversation, action);

    private static object ToQueue(Queue queue, string? note) =>
        new { targetQueueId = queue.QueueId.Value, targetAgentId = default(string), note };

    private static object ToAgent(Agent agent, string? note) =>
        new { targetQueueId = default(string), targetAgentId = agent.AgentId.Value, note };

    private (User User, Agent Agent, Conversation Conversation) SeedOwned(ChannelType channel = ChannelType.WhatsApp)
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active, channel: channel);
        return (user, agent, conversation);
    }

    private static async Task ShouldBeNotTooLongAsync(HttpResponseMessage response)
    {
        var refusal = Fixture("transfer-refusals.json")["transfer"]!["transferNoteTooLong"]!;
        var body = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(refusal["status"]!.GetValue<int>(), because: $"the response was {body}");
        JsonNode.DeepEquals(JsonNode.Parse(body), refusal["body"]).Should().BeTrue(because: $"the response was {body}");
    }

    private void ShouldHaveWrittenNothing(Conversation conversation)
    {
        _factory.MessagesOf(conversation.ConversationId).Should().BeEmpty();
        _factory.AuditOf(conversation.ConversationId).Should().BeEmpty();
    }

    // ─── "A note over the limit SHALL be refused before anything moves" ───────

    [Fact]
    public async Task Transfer_ShouldReturn400AndMoveNothing_WhenTheNoteIsOverTheLimit()
    {
        var (user, agent, conversation) = SeedOwned();
        var (_, target) = _factory.NewAgent("target");
        _factory.Reserve(agent.AgentId, ChannelType.WhatsApp);
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), ToAgent(target, new string('x', 1001)));

        await ShouldBeNotTooLongAsync(response);
        var stored = _factory.Load(conversation.ConversationId)!;
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agent.AgentId));
        stored.State.Should().Be(ConversationState.Active);
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(1, "a refused transfer releases nothing");
        _factory.LoadOf(target.AgentId, ChannelType.WhatsApp).Should().Be(0);
        ShouldHaveWrittenNothing(conversation);
        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_ShouldStoreTheWholeNote_WhenTheNoteIsExactlyTheLimit()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        var note = new string('x', 1000);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, note));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle()
            .Which.Content.Blocks.Should().ContainSingle()
            .Which.Should().BeOfType<TextBlock>().Which.Text.Should().Be(note);
    }

    [Fact]
    public async Task Transfer_ShouldAcceptAndTrimTheNote_WhenPaddingAloneTakesItOverTheLimit()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        var note = new string('x', 1000);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), ToQueue(queue, $"   {note}  \n"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle()
            .Which.Content.Blocks.Should().ContainSingle()
            .Which.Should().BeOfType<TextBlock>().Which.Text.Should().Be(note);
    }

    [Fact]
    public async Task Transfer_ShouldAnswerNotOwner_WhenSomeoneElseSendsANoteOverTheLimit()
    {
        var (_, owner, conversation) = SeedOwned();
        var (other, _) = _factory.NewAgent("other");
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(other);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), ToQueue(queue, new string('x', 2000)));

        await ConversationOwnershipEndpointTests.ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-owner");
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        ShouldHaveWrittenNothing(conversation);
    }

    [Fact]
    public async Task Transfer_ShouldAnswerTooLong_WhenASupervisorSendsANoteOverTheLimit()
    {
        var (_, owner, conversation) = SeedOwned();
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), ToQueue(queue, new string('x', 1001)));

        await ShouldBeNotTooLongAsync(response);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(owner.AgentId));
        ShouldHaveWrittenNothing(conversation);
    }

    [Fact]
    public async Task Transfer_ShouldAnswerTooLong_WhenTheNoteIsOverTheLimitAndTheTargetAgentDoesNotExist()
    {
        var (user, _, conversation) = SeedOwned();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetAgentId = "ghost-agent", note = new string('x', 2000) });

        await ShouldBeNotTooLongAsync(response);
    }

    [Fact]
    public async Task Transfer_ShouldAnswerTooLong_WhenTheNoteIsOverTheLimitAndTheTargetQueueDoesNotExist()
    {
        var (user, _, conversation) = SeedOwned();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetQueueId = "ghost-queue", note = new string('x', 2000) });

        await ShouldBeNotTooLongAsync(response);
    }

    [Fact]
    public async Task Transfer_ShouldKeepTheExistingRefusals_WhenTheyComeBeforeTheNote()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);

        var noTarget = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { note = new string('x', 2000) });
        var noConversation = await client.PostAsJsonAsync(
            "/api/v1/conversations/no-such-conversation/transfer", ToQueue(queue, new string('x', 2000)));

        var missingTarget = Fixture("transfer-refusals.json")["transfer"]!["missingTarget"]!;
        await ConversationOwnershipEndpointTests.ShouldBeErrorAsync(
            noTarget, HttpStatusCode.BadRequest, missingTarget["body"]!["error"]!.GetValue<string>());
        noConversation.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── "A successful transfer with a note SHALL store exactly one System message" ──

    [Theory]
    [InlineData(true, "Queue", "Queued")]
    [InlineData(false, "Agent", "Active")]
    public async Task Transfer_ShouldStoreTheFixturesMessageAndAnswerTheFixturesBody_WhenItCarriesANote(
        bool toQueue, string ownerKind, string state)
    {
        var (user, _, conversation) = SeedOwned(ChannelType.WhatsApp);
        var queue = _factory.SeedQueue();
        var (_, target) = _factory.NewAgent("target");
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), toQueue ? ToQueue(queue, $"  {Note} ") : ToAgent(target, $"  {Note} "));

        var ok = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: ok.ToJsonString());
        var fixtureBody = Fixture("transfer-request.v1.json")["response"]!["body"]!.AsObject();
        ok.Select(member => member.Key).Should().Equal(
            fixtureBody.Select(member => member.Key).Where(key => !key.StartsWith('_')));
        foreach (var omitted in fixtureBody["_omittedWhileNull"]!.AsArray())
            ok.ContainsKey(omitted!.GetValue<string>()).Should().BeFalse("the API omits a null member (H11)");
        ok["success"]!.GetValue<bool>().Should().BeTrue();
        ok["newOwner"]!["kind"]!.GetValue<string>().Should().Be(ownerKind);
        ok["newOwner"]!["ownerId"]!.GetValue<string>().Should().Be(
            toQueue ? queue.QueueId.Value : target.AgentId.Value);
        ok["newState"]!.GetValue<string>().Should().Be(state);

        var history = await client.GetStringAsync(Route(conversation, "messages"));
        ShouldBeTheFixturesElement(
            JsonNode.Parse(history)!.AsArray().Should().ContainSingle().Subject!.AsObject(),
            conversation, user.UserId.Value);
    }

    [Fact]
    public async Task SupervisorHistory_ShouldReturnTheFixturesMessage_WhenAConversationHoldsANote()
    {
        var (user, _, conversation) = SeedOwned(ChannelType.WhatsApp);
        var queue = _factory.SeedQueue();
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        using var client = _factory.ClientFor(user);
        using var supervisorClient = _factory.ClientFor(supervisor);
        (await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, Note)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await supervisorClient.GetStringAsync(
            $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/messages");

        ShouldBeTheFixturesElement(
            JsonNode.Parse(history)!.AsArray().Should().ContainSingle().Subject!.AsObject(),
            conversation, user.UserId.Value);
    }

    private static void ShouldBeTheFixturesElement(JsonObject element, Conversation conversation, string userId)
    {
        var fixture = Fixture("transfer-note-message.v1.json")["history"]!["element"]!.AsObject();

        element.Select(member => member.Key).Should().Equal(
            fixture.Select(member => member.Key).Where(key => !key.StartsWith('_')),
            "exactly the fixture's members, in its order");
        foreach (var omitted in fixture["_omittedWhileNull"]!.AsArray())
            element.ContainsKey(omitted!.GetValue<string>()).Should().BeFalse("the API omits a null member (H11)");

        element["messageId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        element["conversationId"]!.GetValue<string>().Should().Be(conversation.ConversationId.Value);
        element["tenantId"]!.GetValue<string>().Should().Be(ConversationOwnershipApiFactory.Tenant.Value);
        element["direction"]!.GetValue<string>().Should().Be(fixture["direction"]!.GetValue<string>());
        element["channel"]!.GetValue<string>().Should().Be(fixture["channel"]!.GetValue<string>());
        element["senderId"]!.GetValue<string>().Should().Be(userId);
        element["deliveryStatus"]!.GetValue<string>().Should().Be(fixture["deliveryStatus"]!.GetValue<string>());
        element["createdAt"]!.GetValue<DateTimeOffset>().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        element["createdBy"]!.GetValue<string>().Should().Be($"transfer:{userId}");

        var block = element["content"]!["blocks"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        var fixtureBlock = fixture["content"]!["blocks"]![0]!.AsObject();
        block.Select(member => member.Key).Should().Equal(fixtureBlock.Select(member => member.Key));
        block["$type"]!.GetValue<string>().Should().Be(fixtureBlock["$type"]!.GetValue<string>());
        block["type"]!.GetValue<string>().Should().Be(fixtureBlock["type"]!.GetValue<string>());
        block["text"]!.GetValue<string>().Should().Be(Note);
    }

    [Fact]
    public async Task Transfer_ShouldNameTheSupervisor_WhenASupervisorMovesAConversationItDoesNotOwn()
    {
        var (_, _, conversation) = SeedOwned();
        var supervisor = _factory.NewUser(UserRole.Supervisor, "supervisor");
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(supervisor);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, Note));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var message = _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle().Subject;
        message.SenderId.Should().Be(supervisor.UserId.Value);
        message.CreatedBy.Should().Be($"transfer:{supervisor.UserId.Value}");
    }

    [Theory]
    [InlineData("""{"targetAgentId":"TARGET"}""")]
    [InlineData("""{"targetAgentId":"TARGET","note":null}""")]
    [InlineData("""{"targetAgentId":"TARGET","note":""}""")]
    [InlineData("""{"targetAgentId":"TARGET","note":"  \n\t  \r\n "}""")]
    public async Task Transfer_ShouldMoveAndStoreNothing_WhenThereIsNoNote(string body)
    {
        var (user, _, conversation) = SeedOwned();
        var (_, target) = _factory.NewAgent("target");
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);
        using var content = new StringContent(
            body.Replace("TARGET", target.AgentId.Value, StringComparison.Ordinal),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(Route(conversation, "transfer"), content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(target.AgentId));
        _factory.MessagesOf(conversation.ConversationId).Should().BeEmpty();
        capture.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_ShouldWriteAndPublishNothing_WhenTheSwitchboardRefuses()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var (_, target) = _factory.NewAgent("target");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Closed);
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), ToAgent(target, Note));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ShouldHaveWrittenNothing(conversation);
        capture.Events.Should().BeEmpty();
    }

    // ─── "The message history SHALL return a stored note" ─────────────────────

    [Fact]
    public async Task GetMessages_ShouldReturnTheNoteToALaterOwner_WhenTheConversationWentThroughAQueue()
    {
        var (user, _, conversation) = SeedOwned();
        _factory.SeedMessages(conversation, 3);
        var queue = _factory.SeedQueue();
        var (later, laterOwner) = _factory.NewAgent("later-owner");
        using var client = _factory.ClientFor(user);
        using var laterClient = _factory.ClientFor(later);
        (await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, Note)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);

        using (var scope = _factory.Services.CreateScope())
        {
            var offer = await scope.ServiceProvider.GetRequiredService<IConversationSwitchboard>().OfferToAgentAsync(
                conversation.ConversationId, ConversationOwnershipApiFactory.Tenant, laterOwner.AgentId, CancellationToken.None);
            offer.Success.Should().BeTrue(offer.FailureReason);
        }

        var accepted = await laterClient.PostAsync(Route(conversation, "accept"), content: null);
        var page = JsonNode.Parse(await laterClient.GetStringAsync(Route(conversation, "messages")))!.AsArray();

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(laterOwner.AgentId));
        page.Select(message => message!["direction"]!.GetValue<string>())
            .Should().Equal("Inbound", "Inbound", "Inbound", "System");
        page[3]!["content"]!["blocks"]![0]!["text"]!.GetValue<string>().Should().Be(Note);
    }

    // ─── "The message history SHALL keep paging oldest first" (H13) ───────────

    // This pins today's behaviour, not a wish: the console reads the first 50 messages, so a note written after 50
    // stays out of the page it reads. Gap B14 of the agent console plan, "cargar anteriores", turns these two tests.
    private async Task<(HttpClient Client, Conversation Conversation)> TransferWithANoteAfterFiftyMessagesAsync()
    {
        var (user, _, conversation) = SeedOwned();
        _factory.SeedMessages(conversation, 50);
        var queue = _factory.SeedQueue();
        var client = _factory.ClientFor(user);
        (await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, Note)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (client, conversation);
    }

    [Fact]
    public async Task GetMessages_ShouldAnswerTheFiftyStoredAndNotTheNote_WhenNoQueryIsGiven()
    {
        var (client, conversation) = await TransferWithANoteAfterFiftyMessagesAsync();
        using var owned = client;

        var page = JsonNode.Parse(await client.GetStringAsync(Route(conversation, "messages")))!.AsArray();

        page.Should().HaveCount(50);
        page.Select(message => message!["direction"]!.GetValue<string>()).Should().OnlyContain(d => d == "Inbound");
    }

    [Fact]
    public async Task GetMessages_ShouldAnswerTheNote_WhenTheOffsetReachesIt()
    {
        var (client, conversation) = await TransferWithANoteAfterFiftyMessagesAsync();
        using var owned = client;

        var page = JsonNode.Parse(await client.GetStringAsync($"{Route(conversation, "messages")}?offset=50"))!.AsArray();

        var note = page.Should().ContainSingle().Subject!;
        note["direction"]!.GetValue<string>().Should().Be("System");
        note["content"]!["blocks"]![0]!["text"]!.GetValue<string>().Should().Be(Note);
    }

    // ─── "A stored note SHALL be published to the console on conversation.message" ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_ShouldPublishTheStoredNote_WhenItCarriesOne(bool toQueue)
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        var (_, target) = _factory.NewAgent("target");
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), toQueue ? ToQueue(queue, $" {Note}\n") : ToAgent(target, $" {Note}\n"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle().Subject;
        var published = capture.Events.Should().ContainSingle().Subject;
        published.Sender.Should().Be("System");
        published.Text.Should().Be(Note);
        published.MessageId.Should().Be(stored.MessageId.Value);
        published.TenantId.Should().Be(ConversationOwnershipApiFactory.Tenant.Value);

        // Serialized as SseEndpoints.WriteEventAsync does: the fixture's members plus the two envelope ones it ignores.
        var wire = JsonNode.Parse(
            JsonSerializer.Serialize(published, published.GetType(), ApiJsonContext.Default))!.AsObject();
        var fixture = Fixture("transfer-note-message.v1.json")["sseEvent"]!;
        fixture["event"]!.GetValue<string>().Should().Be(published.Type);
        wire.Select(member => member.Key).Should().BeEquivalentTo(
            fixture["data"]!.AsObject().Select(member => member.Key).Concat(["eventType", "metadata"]));
        wire["sender"]!.GetValue<string>().Should().Be(fixture["data"]!["sender"]!.GetValue<string>());
        wire["type"]!.GetValue<string>().Should().Be(fixture["data"]!["type"]!.GetValue<string>());
        wire["text"]!.GetValue<string>().Should().Be(Note);
    }

    [Fact]
    public async Task Transfer_ShouldPublishNothing_WhenTheNoteIsRefused()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), ToQueue(queue, new string('x', 1001)));

        await ShouldBeNotTooLongAsync(response);
        capture.Events.Should().BeEmpty();
    }

    // ─── "A failed note write SHALL NOT undo or fail the transfer" ────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_ShouldAnswer200AndKeepTheMoveAndLogNoText_WhenTheNoteWriteFails(bool toQueue)
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        var (_, target) = _factory.NewAgent("target");
        var text = $"Case {Guid.NewGuid():N}: the customer's account number is on file.";
        _factory.RefuseWritesTo(conversation.ConversationId);
        using var capture = _factory.CaptureMessages(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), toQueue ? ToQueue(queue, text) : ToAgent(target, text));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ok = await response.Content.ReadFromJsonAsync<JsonObject>();
        ok!["success"]!.GetValue<bool>().Should().BeTrue();
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(
            toQueue ? ConversationOwner.ForQueue(queue.QueueId) : ConversationOwner.ForAgent(target.AgentId));
        capture.Events.Should().BeEmpty();
        _factory.MessagesOf(conversation.ConversationId).Should().BeEmpty();
        _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(entry => entry.Action == "conversation.transferred")
            .Which.Metadata!["note"].Should().Be("failed");

        var warning = _factory.Logs.Records.Should().ContainSingle(record =>
            record.EventId.Id == NoteWriteFailedEventId && record.Contains(conversation.ConversationId.Value)).Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain(ConversationOwnershipApiFactory.Tenant.Value);
        _factory.Logs.RecordsContaining(text).Should().BeEmpty("a note may carry customer data");
    }

    [Fact]
    public async Task Transfer_ShouldAnswer200AndKeepTheStoredNote_WhenPublishingItFails()
    {
        var (user, _, conversation) = SeedOwned();
        var queue = _factory.SeedQueue();
        using var broken = _factory.BreakMessagePublishing(conversation.ConversationId);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation, "transfer"), ToQueue(queue, Note));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle();
        _factory.AuditOf(conversation.ConversationId).Should()
            .ContainSingle(entry => entry.Action == "conversation.transferred")
            .Which.Metadata!["note"].Should().Be("written", "the row is stored, and the event is only a convenience");
        _factory.Logs.RecordsContaining(Note).Should().BeEmpty();
    }
}
