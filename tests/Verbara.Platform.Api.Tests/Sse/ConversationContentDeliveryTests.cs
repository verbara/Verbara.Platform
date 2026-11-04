using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// contact-history-opens-by-assignment task 1.9 (design D17, operator answer Q2; spec <c>conversation-read-scope</c>
/// "The live stream MUST deliver conversation content only to its readers" and "Each message publisher MUST read the
/// conversation's current audience"; fixture <c>conversation-message-event.v1.json</c> recipients). Every publisher is
/// driven through its real path and every subscriber reads its own <c>GET /api/v1/events/stream</c>.
/// <list type="bullet">
/// <item><b>Written first, red on the unfixed code:</b> the stream was a tenant broadcast, so an unrelated agent
/// received every message; and a WebChat message after the one that routed the session was never published. They turn
/// green with task 2.8.</item>
/// <item><b>Guards, green before and after:</b> the owner and the offered agent keep their messages, and
/// <c>conversation.offered</c> / <c>conversation.state_changed</c> still reach every agent of the tenant.</item>
/// </list>
/// </summary>
public sealed class ConversationContentDeliveryTests : IClassFixture<ContentDeliveryApiFactory>
{
    private readonly ContentDeliveryApiFactory _factory;

    public ConversationContentDeliveryTests(ContentDeliveryApiFactory factory) => _factory = factory;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    private static string NewPhone() => $"1555{Random.Shared.Next(1_000_000, 9_999_999)}";

    /// <summary>Opens a WhatsApp conversation through the webhook and returns its id.</summary>
    private async Task<(EntityId ConversationId, string Phone)> WhatsAppConversationAsync()
    {
        var phone = NewPhone();
        using var capture = new MessageEventCapture(_factory.Bus);
        (await _factory.PostWhatsAppAsync(phone, "first message")).StatusCode.Should().Be(HttpStatusCode.OK);
        var opened = await capture.WaitForAsync(e => e.TenantId == Tenant && e.Sender == "Inbound", $"the first message of {phone}");
        return (EntityId.From(opened.ConversationId), phone);
    }

    /// <summary>Posts one webhook message of <paramref name="phone"/> and returns the event its publisher wrote.</summary>
    private async Task<ConversationMessageEvent> InboundAsync(EntityId conversationId, string phone, string text)
    {
        using var capture = new MessageEventCapture(_factory.Bus);
        (await _factory.PostWhatsAppAsync(phone, text)).StatusCode.Should().Be(HttpStatusCode.OK);
        return await capture.WaitForAsync(e => e.ConversationId == conversationId.Value && e.Sender == "Inbound", text);
    }

    private Task SyncAsync(params LiveStreamProbe[] probes) => LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, probes);

    // ═══ The live stream is filtered (red on the unfixed code) ═════════════════

    [Fact]
    public async Task StreamEvents_ShouldNotDeliverTheMessage_WhenTheSubscriberHoldsNothingOfTheConversation()
    {
        var (userA, agentA) = _factory.NewAgent("stream-owner");
        var (userB, _) = _factory.NewAgent("stream-stranger");
        var (conversation, phone) = await WhatsAppConversationAsync();
        await _factory.SetHolderAsync(conversation, owner: agentA);
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);
        await using var streamB = await _factory.OpenStreamAsync("stranger B", userB);

        var published = await InboundAsync(conversation, phone, "my address is 12 Elm Street");
        await SyncAsync(streamA, streamB);

        streamB.Received(published.MessageId).Should().BeFalse(
            "agent B holds nothing of A's conversation, so its stream must not receive what the customer wrote");
    }

    // ═══ Guards: green before and after ═════════════════════════════════════════

    [Fact]
    public async Task StreamEvents_ShouldDeliverTheMessage_WhenTheSubscriberOwnsTheConversation()
    {
        var (userA, agentA) = _factory.NewAgent("stream-owner");
        var (conversation, phone) = await WhatsAppConversationAsync();
        await _factory.SetHolderAsync(conversation, owner: agentA);
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);

        var published = await InboundAsync(conversation, phone, "are you there?");
        await SyncAsync(streamA);

        streamA.Received(published.MessageId).Should().BeTrue("the owner agent receives its conversation's messages");
    }

    [Fact]
    public async Task StreamEvents_ShouldDeliverTheMessage_WhenTheSubscriberIsOfferedTheConversation()
    {
        var (userO, agentO) = _factory.NewAgent("stream-offered");
        var (conversation, phone) = await WhatsAppConversationAsync();
        await _factory.SetHolderAsync(conversation, offeredTo: agentO);
        await using var streamO = await _factory.OpenStreamAsync("offered O", userO);

        var published = await InboundAsync(conversation, phone, "still waiting");
        await SyncAsync(streamO);

        streamO.Received(published.MessageId).Should().BeTrue("the offered card still receives its live messages");
    }

    [Fact]
    public async Task StreamEvents_ShouldDeliverOffersAndStateChangesToEveryAgent_WhenTheyHoldNothingOfTheConversation()
    {
        var (userA, _) = _factory.NewAgent("inbox-a");
        var (userB, agentB) = _factory.NewAgent("inbox-b");
        await using var streamA = await _factory.OpenStreamAsync("agent A", userA);
        await using var streamB = await _factory.OpenStreamAsync("agent B", userB);
        var conversationId = $"conv-inbox-{Guid.NewGuid():N}";

        _factory.Bus.Publish(new ConversationOfferedEvent(Tenant, conversationId, agentB.AgentId.Value, "queue-inbox", "WhatsApp"));
        _factory.Bus.Publish(new ConversationStateChangedEvent(Tenant, conversationId, "Queued", "Offered"));
        await SyncAsync(streamA, streamB);

        foreach (var stream in new[] { streamA, streamB })
        {
            stream.Frames.Should().Contain(f => f.Event == "conversation.offered" && f.Member("conversationId") == conversationId,
                $"{stream.Name}: conversation.offered is not a content event and stays a tenant broadcast");
            stream.Frames.Should().Contain(f => f.Event == "conversation.state_changed" && f.Member("conversationId") == conversationId,
                $"{stream.Name}: conversation.state_changed stays a tenant broadcast");
        }
    }

    // ═══ Each publisher stamps the conversation's current audience (red on the unfixed code) ═══

    [Fact]
    public async Task SendCoachingNote_ShouldReachOnlyTheConversationsAgent_WhenTheConversationIsOwnedOrOffered()
    {
        var (userA, agentA) = _factory.NewAgent("coached-owner");
        var (userO, agentO) = _factory.NewAgent("coached-offered");
        var (userU, _) = _factory.NewAgent("coached-unrelated");
        var owned = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active);
        var offered = _factory.SeedOfferTo(agentO, _factory.SeedQueue());
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);
        await using var streamO = await _factory.OpenStreamAsync("offered O", userO);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        using var supervisor = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "coach"));
        using var capture = new MessageEventCapture(_factory.Bus);

        foreach (var conversation in new[] { owned, offered })
        {
            (await supervisor.PostAsJsonAsync(
                    $"/api/v1/supervisor/conversations/{conversation.ConversationId.Value}/note", new { text = "slow down" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var noteOfOwned = await capture.WaitForAsync(e => e.ConversationId == owned.ConversationId.Value, "the owned note");
        var noteOfOffered = await capture.WaitForAsync(e => e.ConversationId == offered.ConversationId.Value, "the offered note");
        await SyncAsync(streamA, streamO, streamU);

        streamA.Received(noteOfOwned.MessageId).Should().BeTrue("the owner receives the note of its conversation");
        streamO.Received(noteOfOffered.MessageId).Should().BeTrue("the offered agent receives the note of its offer");
        streamU.Received(noteOfOwned.MessageId).Should().BeFalse("the unrelated agent receives neither note");
        streamU.Received(noteOfOffered.MessageId).Should().BeFalse("the unrelated agent receives neither note");
    }

    [Fact]
    public async Task WebChatTyping_ShouldReachTheConversationsCurrentAgentOnly_WhenTheOwnerAndThenAnOfferChange()
    {
        _factory.ActivateWebChat();
        var (userA, agentA) = _factory.NewAgent("typing-owner");
        var (userO, agentO) = _factory.NewAgent("typing-offered");
        var (userU, _) = _factory.NewAgent("typing-unrelated");
        _factory.SeedOfferTo(agentO, _factory.SeedQueue()); // O's open offer on another WebChat conversation
        var (sessionId, conversation) = await OpenWebChatSessionAsync();
        await _factory.SetHolderAsync(conversation, owner: agentA);
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);
        await using var streamO = await _factory.OpenStreamAsync("offered O", userO);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await _factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);

        await SendTypingAsync(socket, conversation, timeout.Token);
        await SyncAsync(streamA, streamO, streamU);

        Typing(streamA, conversation).Should().Be(1, "the owner receives the typing frame of its conversation");
        Typing(streamO, conversation).Should().Be(0, "an offer of another conversation does not open this one");
        Typing(streamU, conversation).Should().Be(0, "the unrelated agent never receives it");

        await _factory.SetHolderAsync(conversation, offeredTo: agentO);
        await SendTypingAsync(socket, conversation, timeout.Token);
        await SyncAsync(streamA, streamO, streamU);

        Typing(streamO, conversation).Should().Be(1, "once offered to O, the next typing frame reaches O");
        Typing(streamA, conversation).Should().Be(1, "the previous owner does not receive the frame after the change");
        Typing(streamU, conversation).Should().Be(0, "the unrelated agent never receives it");
    }

    [Fact]
    public async Task WebhookInbound_ShouldReachOnlyTheConversationsAgent_AndOnlyReadersWhenTheConversationCannotBeLoaded()
    {
        var (userA, agentA) = _factory.NewAgent("webhook-owner");
        var (userO, agentO) = _factory.NewAgent("webhook-offered");
        var (userU, _) = _factory.NewAgent("webhook-unrelated");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "webhook-reader");
        var (ownedId, ownedPhone) = await WhatsAppConversationAsync();
        var (offeredId, offeredPhone) = await WhatsAppConversationAsync();
        var (lostId, lostPhone) = await WhatsAppConversationAsync();
        await _factory.SetHolderAsync(ownedId, owner: agentA);
        await _factory.SetHolderAsync(offeredId, offeredTo: agentO);
        await _factory.SetHolderAsync(lostId, owner: agentA);
        _factory.ConversationProbe.Hide(lostId);
        await using var streamA = await _factory.OpenStreamAsync("owner A", userA);
        await using var streamO = await _factory.OpenStreamAsync("offered O", userO);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);

        var toOwned = await InboundAsync(ownedId, ownedPhone, "owned");
        var toOffered = await InboundAsync(offeredId, offeredPhone, "offered");
        var toLost = await InboundAsync(lostId, lostPhone, "lost");
        await SyncAsync(streamA, streamO, streamU, streamS);

        streamA.Received(toOwned.MessageId).Should().BeTrue("the owner receives its conversation's message");
        streamO.Received(toOffered.MessageId).Should().BeTrue("the offered agent receives its offer's message");
        streamA.Received(toOffered.MessageId).Should().BeFalse("each message reaches only its own agent");
        streamO.Received(toOwned.MessageId).Should().BeFalse("each message reaches only its own agent");
        foreach (var message in new[] { toOwned, toOffered, toLost })
            streamU.Received(message.MessageId).Should().BeFalse($"the unrelated agent receives none ({message.Text})");
        streamA.Received(toLost.MessageId).Should().BeFalse(
            "a message whose conversation cannot be loaded has an empty audience (fail closed)");
        streamS.Received(toLost.MessageId).Should().BeTrue("an empty audience still reaches the readers of everything");
    }

    [Fact]
    public async Task WebChatMessages_ShouldReachTheOfferedThenAcceptingAgent_WhenTheyFollowTheMessageThatRoutedTheSession()
    {
        // Spec audit F3: WebChatInboundRouter returned before its publish for every later message of an Offered or
        // Active WebChat conversation, so the offered agent never received them live.
        _factory.ActivateWebChat();
        var (userO, agentO) = _factory.NewAgent("webchat-offered");
        var (userU, _) = _factory.NewAgent("webchat-unrelated");
        await using var streamO = await _factory.OpenStreamAsync("offered O", userO);
        await using var streamU = await _factory.OpenStreamAsync("unrelated U", userU);
        using var visitor = _factory.CreateClient();
        var (sessionId, conversation) = await OpenWebChatSessionAsync(visitor);

        await SendWebChatAsync(visitor, sessionId, "first: routes the session");
        await _factory.SetHolderAsync(conversation, offeredTo: agentO);
        var second = await SendWebChatAsync(visitor, sessionId, "second: while offered");
        using (var clientO = _factory.ClientFor(userO))
        {
            (await clientO.PostAsync($"/api/v1/conversations/{conversation.Value}/accept", content: null))
                .StatusCode.Should().Be(HttpStatusCode.OK, "O accepts its offer");
        }

        var third = await SendWebChatAsync(visitor, sessionId, "third: after the accept");
        await SyncAsync(streamO, streamU);

        streamO.Received(second.Value).Should().BeTrue("the offered agent receives a message sent while it is offered");
        streamO.Received(third.Value).Should().BeTrue("the accepting agent receives a message sent after the accept");
        streamU.Received(second.Value).Should().BeFalse("the unrelated agent receives neither");
        streamU.Received(third.Value).Should().BeFalse("the unrelated agent receives neither");
    }

    // ─── WebChat helpers ──────────────────────────────────────────────────────

    private async Task<(string SessionId, EntityId ConversationId)> OpenWebChatSessionAsync(HttpClient? client = null)
    {
        using var owned = client is null ? _factory.CreateClient() : null;
        var http = client ?? owned!;
        using var response = await http.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = Tenant });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = body.RootElement.GetProperty("sessionId").GetString()!;
        var session = (await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId))!;
        return (sessionId, session.ConversationId);
    }

    /// <summary>Sends one visitor message over REST and returns the id of the message it stored.</summary>
    private async Task<EntityId> SendWebChatAsync(HttpClient visitor, string sessionId, string text)
    {
        using var send = await visitor.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text });
        send.StatusCode.Should().Be(HttpStatusCode.OK, await send.Content.ReadAsStringAsync());
        var session = (await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId))!;
        var messages = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(ConversationOwnershipApiFactory.Tenant, session.ConversationId, 100, 0, CancellationToken.None);
        return messages.Should().Contain(m => m.Content.Blocks.OfType<TextBlock>().Any(b => b.Text == text)).Subject.MessageId;
    }

    private async Task SendTypingAsync(WebSocket socket, EntityId conversation, CancellationToken ct)
    {
        using var capture = new MessageEventCapture(_factory.Bus);
        var frame = JsonSerializer.Serialize(new WebChatClientMessage("typing", ""), WebChatJsonContext.Default.WebChatClientMessage);
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct);
        await capture.WaitForAsync(
            e => e.ConversationId == conversation.Value && e.Sender == "TypingIndicator", "the typing frame");
    }

    private static int Typing(LiveStreamProbe stream, EntityId conversation) =>
        stream.MessagesOf(conversation.Value).Count(f => f.Member("sender") == "TypingIndicator");
}

/// <summary>
/// contact-history-opens-by-assignment task 1.9, the transfer note on a store that returns detached rows (spec audit
/// F2): the handler's pre-transfer copy still names the former owner, so an audience stamped from it would send the
/// note to the agent who just gave the conversation away. Red on the unfixed code (the note was a tenant broadcast);
/// green with task 2.8, where a mutation that stamps the pre-transfer audience must turn it red again.
/// </summary>
public sealed class TransferNoteDeliveryTests : IClassFixture<DetachedContentDeliveryApiFactory>
{
    private const string Note = "Customer disputes the October invoice; please do not re-issue it.";

    private readonly DetachedContentDeliveryApiFactory _factory;

    public TransferNoteDeliveryTests(DetachedContentDeliveryApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Transfer_ShouldDeliverTheNoteToTheNewOwnerAndReadersButNotTheFormerOwner_WhenTheStoreReturnsDetachedRows()
    {
        _factory.ConversationProbe.Detached.Should().BeTrue("the store must not hand out the stored reference");
        var (userA, agentA) = _factory.NewAgent("transfer-from");
        var (userB, agentB) = _factory.NewAgent("transfer-to");
        var supervisorUser = _factory.NewUser(UserRole.Supervisor, "transfer-reader");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active, channel: ChannelType.WhatsApp);
        await using var streamA = await _factory.OpenStreamAsync("former owner A", userA);
        await using var streamB = await _factory.OpenStreamAsync("new owner B", userB);
        await using var streamS = await _factory.OpenStreamAsync("supervisor", supervisorUser);
        using var capture = new MessageEventCapture(_factory.Bus);
        using var clientA = _factory.ClientFor(userA);

        var response = await clientA.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer",
            new { targetQueueId = default(string), targetAgentId = agentB.AgentId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _factory.Load(conversation.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(agentB.AgentId));
        var note = await capture.WaitForAsync(
            e => e.ConversationId == conversation.ConversationId.Value && e.Sender == "System", "the transfer note");
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.Tenant.Value, streamA, streamB, streamS);

        streamB.Received(note.MessageId).Should().BeTrue("the new owner receives the note written for it");
        streamS.Received(note.MessageId).Should().BeTrue("a monitor receives every message");
        streamA.Received(note.MessageId).Should().BeFalse("the former owner, whose transfer wrote it, does not");
    }

    [Fact]
    public async Task Transfer_ShouldDeliverTheNoteToTheFormerOwner_WhenItReadsEveryConversation()
    {
        var (userA, agentA) = _factory.NewAgent("transfer-from-reader", UserRole.Supervisor);
        var (userB, agentB) = _factory.NewAgent("transfer-to-from-reader");
        var (userC, _) = _factory.NewAgent("transfer-unrelated");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active, channel: ChannelType.WhatsApp);
        await using var streamA = await _factory.OpenStreamAsync("former owner A, a reader of every conversation", userA);
        await using var streamB = await _factory.OpenStreamAsync("new owner B", userB);
        await using var streamC = await _factory.OpenStreamAsync("unrelated agent C", userC);
        using var capture = new MessageEventCapture(_factory.Bus);
        using var clientA = _factory.ClientFor(userA);

        var response = await clientA.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.ConversationId.Value}/transfer",
            new { targetQueueId = default(string), targetAgentId = agentB.AgentId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var note = await capture.WaitForAsync(
            e => e.ConversationId == conversation.ConversationId.Value && e.Sender == "System", "the transfer note");
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.Tenant.Value, streamA, streamB, streamC);

        streamA.Received(note.MessageId).Should().BeTrue("it receives the note as a reader of every conversation, not as the former owner");
        streamB.Received(note.MessageId).Should().BeTrue();
        streamC.Received(note.MessageId).Should().BeFalse();
    }
}
