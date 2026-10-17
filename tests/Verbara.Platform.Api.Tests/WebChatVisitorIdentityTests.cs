using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// WebChat visitor identity end to end through the real host (whatsapp-works-for-real, design D16,
/// spec <c>webchat-visitor-identity</c>): a session is one contact and one conversation, visitor messages
/// land in that conversation, and an agent reply reaches the visitor's socket on this replica.
/// </summary>
/// <remarks>Each test gets its own host and its own tenant, so row counts start at zero.</remarks>
public sealed class WebChatVisitorIdentityTests : IDisposable
{
    private const string VisitorTenantId = "tenant-webchat-identity";
    private const string InactiveChannelTenantId = "tenant-webchat-inactive";
    private const string MissingChannelTenantId = "tenant-webchat-missing";
    private static readonly TenantId VisitorTenant = new(VisitorTenantId);
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, VisitorTenantId);
            SeedWebChatChannel(host.Services, VisitorTenantId);
            SeedTestCustomerTenant(host.Services, InactiveChannelTenantId);
            SeedWebChatChannel(host.Services, InactiveChannelTenantId, isActive: false);
            SeedTestCustomerTenant(host.Services, MissingChannelTenantId);
            return host;
        }
    }

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = VisitorTenantId });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<WebChatSession> GetSessionAsync(string sessionId)
    {
        var session = await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId);
        session.Should().NotBeNull("the session was just created on this host");
        return session!;
    }

    [Fact]
    public async Task VisitorMessage_ShouldReuseSessionContact_WhenSessionExists()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);

        using var send = await client.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text = "hola" });
        send.StatusCode.Should().Be(HttpStatusCode.OK);

        var (contacts, conversations, _) = await WebhookProbes.CountRowsAsync(_factory.Services, VisitorTenant);
        (contacts, conversations).Should().Be((1, 1),
            "the session created the visitor's contact and conversation; the message must reuse both");

        var messages = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None);
        messages.Should().ContainSingle(m => m.Direction == MessageDirection.Inbound,
            "the visitor message belongs to the conversation the session was created with");
    }

    [Fact]
    public async Task AgentReply_ShouldReachVisitorSocket_WhenConnected()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);

        // An agent owns the session's conversation (as after an offer is accepted).
        var agentId = EntityId.From($"agent-{Guid.NewGuid():N}");
        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = await conversationStore.GetByIdAsync(VisitorTenant, session.ConversationId, CancellationToken.None);
        conversation.Should().NotBeNull();
        conversation!.State = ConversationState.Active;
        conversation.Owner = ConversationOwner.ForAgent(agentId);
        await conversationStore.SaveAsync(conversation, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");

        const string replyText = "hello from the agent";
        var message = await _factory.Services.GetRequiredService<IConversationService>().SendMessageAsync(
            session.ConversationId,
            VisitorTenant,
            new MessageEnvelope([new TextBlock(replyText)]),
            agentId,
            ConversationOwnerKind.Agent,
            CancellationToken.None);

        var received = await ReceiveTextAsync(socket, TimeSpan.FromSeconds(3));
        received.Should().NotBeNull(
            "the visitor's socket is connected on this replica, so the agent reply must reach it (stored delivery status: {0})",
            message.DeliveryStatus);
        received.Should().Contain(replyText);
        message.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
    }

    [Fact]
    public async Task AgentReply_ShouldMarkFailed_WhenSessionClosed()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        var agentId = await GiveConversationToAgentAsync(session.ConversationId);

        // The visitor connects, then leaves: the server marks the session disconnected.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var wsClient = _factory.Server.CreateWebSocketClient();
            using var socket = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
            (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "visitor left", timeout.Token);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (session.IsConnected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the server-side socket loop marks the session disconnected
        session.IsConnected.Should().BeFalse("the server marks the session disconnected when the socket closes");

        var conversations = _factory.Services.GetRequiredService<IConversationService>();
        var send = async () => await conversations.SendMessageAsync(
            session.ConversationId,
            VisitorTenant,
            new MessageEnvelope([new TextBlock("are you still there?")]),
            agentId,
            ConversationOwnerKind.Agent,
            CancellationToken.None);

        var message = (await send.Should().NotThrowAsync("a closed session is a failed send, never an exception")).Subject;
        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        var stored = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None);
        stored.Should().ContainSingle(m => m.MessageId == message.MessageId)
            .Which.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
    }

    [Fact]
    public async Task VisitorMessages_ShouldStayInSessionConversation_WhenVisitorSendsTwice()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);

        using (var first = await client.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text = "hola" }))
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var second = await client.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text = "sigo aqui" }))
            second.StatusCode.Should().Be(HttpStatusCode.OK);

        var (contacts, conversations, _) = await WebhookProbes.CountRowsAsync(_factory.Services, VisitorTenant);
        (contacts, conversations).Should().Be((1, 1));
        var messages = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None);
        messages.Count(m => m.Direction == MessageDirection.Inbound).Should().Be(2);
    }

    [Fact]
    public async Task AgentReply_ShouldMarkFailed_WhenSocketNotYetAttached()
    {
        // The session exists (created over REST) but the visitor's socket never attached: nothing can
        // carry the reply, so it must be recorded as failed, never as sent.
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        var agentId = await GiveConversationToAgentAsync(session.ConversationId);

        var message = await SendAgentReplyAsync(session, agentId, "anyone there?");

        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed,
            "no socket is attached to the session, so the reply reached nobody");
        var stored = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None);
        stored.Should().ContainSingle(m => m.MessageId == message.MessageId)
            .Which.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
    }

    [Fact]
    public async Task AgentReply_ShouldReachVisitorSocket_WhenSocketReattached()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        var agentId = await GiveConversationToAgentAsync(session.ConversationId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClient = _factory.Server.CreateWebSocketClient();

        // First attach, then the visitor drops (network blip, page reload).
        using (var first = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token))
        {
            (await ReceiveTextAsync(first, TimeSpan.FromSeconds(5))).Should().Contain("connected");
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reload", timeout.Token);
        }

        await WaitUntilAsync(() => !session.IsConnected);

        // The widget re-attaches to the same session.
        using var second = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        (await ReceiveTextAsync(second, TimeSpan.FromSeconds(5))).Should().Contain("connected");

        const string replyText = "welcome back";
        var message = await SendAgentReplyAsync(session, agentId, replyText);

        var received = await ReceiveTextAsync(second, TimeSpan.FromSeconds(3));
        received.Should().NotBeNull(
            "the visitor re-attached to the session, so the reply must reach the new socket (stored delivery status: {0})",
            message.DeliveryStatus);
        received.Should().Contain(replyText);
        message.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
    }

    [Fact]
    public async Task AgentReply_ShouldReachRemainingSocket_WhenOtherSocketOfSessionCloses()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        var agentId = await GiveConversationToAgentAsync(session.ConversationId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClient = _factory.Server.CreateWebSocketClient();

        // Two tabs share the session (the widget restores it from localStorage).
        using var stays = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        (await ReceiveTextAsync(stays, TimeSpan.FromSeconds(5))).Should().Contain("connected");
        using (var leaves = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token))
        {
            (await ReceiveTextAsync(leaves, TimeSpan.FromSeconds(5))).Should().Contain("connected");
            await leaves.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "tab closed", timeout.Token);
            // The server acknowledges the close once its read loop and cleanup ran.
            (await ReceiveTextAsync(leaves, TimeSpan.FromSeconds(5))).Should().BeNull();
        }

        const string replyText = "still here?";
        var message = await SendAgentReplyAsync(session, agentId, replyText);

        var received = await ReceiveTextAsync(stays, TimeSpan.FromSeconds(3));
        received.Should().NotBeNull(
            "one socket of the session is still open, so closing the other must not cut delivery (stored delivery status: {0})",
            message.DeliveryStatus);
        received.Should().Contain(replyText);
        message.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
    }

    [Fact]
    public async Task VisitorSocketMessage_ShouldReuseSessionContact_WhenSentOverWebSocket()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");

        var frame = Encoding.UTF8.GetBytes("""{"type":"message","text":"hola por socket"}""");
        await socket.SendAsync(frame, WebSocketMessageType.Text, true, timeout.Token);

        var messageStore = _factory.Services.GetRequiredService<IMessageStore>();
        IReadOnlyList<Message> messages = [];
        await WaitUntilAsync(() =>
        {
            messages = messageStore.GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None)
                .GetAwaiter().GetResult();
            return messages.Any(m => m.Direction == MessageDirection.Inbound);
        });

        messages.Should().ContainSingle(m => m.Direction == MessageDirection.Inbound,
            "the socket message belongs to the conversation the session was created with");
        var (contacts, conversations, _) = await WebhookProbes.CountRowsAsync(_factory.Services, VisitorTenant);
        (contacts, conversations).Should().Be((1, 1),
            "a message over the WebSocket reuses the session's contact and conversation, exactly like the REST route");
    }

    [Fact]
    public async Task VisitorSocketMessage_ShouldBeStoredIntact_WhenMultiByteTextSpansSeveralReads()
    {
        // 4000 characters (the widget's composer cap) of 3- and 2-byte UTF-8, written raw the way a browser's
        // JSON.stringify writes them: 10 000 bytes of text, sent in 1000-byte fragments (cutting characters in half)
        // so the server sees it in several reads. It must assemble the whole message, not parse its first read.
        var text = string.Concat(Enumerable.Repeat("€ñ", 2000));
        text.Length.Should().Be(4000);
        await using var visitor = await VisitorSocket.OpenAsync(this);

        await visitor.SendFragmentedAsync(Encoding.UTF8.GetBytes($$"""{"type":"message","text":"{{text}}"}"""));

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal([text], "a message within the length limit is stored exactly as the visitor typed it");
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocketMessage_ShouldBeStoredIntact_WhenEveryCharacterIsEscaped()
    {
        // The worst case on the wire: a client whose encoder writes every non-ASCII character as a 6-byte \uXXXX
        // escape (System.Text.Json's default encoder does). 4000 characters are then 24 000 bytes of text, and the
        // server's byte bound must still fit them.
        var text = string.Concat(Enumerable.Repeat("€ñ", 2000));
        await using var visitor = await VisitorSocket.OpenAsync(this);
        var frame = JsonSerializer.SerializeToUtf8Bytes(
            new WebChatClientMessage("message", text), WebChatJsonContext.Default.WebChatClientMessage);
        frame.Length.Should().BeGreaterThan(24_000, "every character is escaped");

        await visitor.SendFragmentedAsync(frame);

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal([text]);
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocket_ShouldSkipMalformedFrame_WhenValidFrameFollowsOnSameSocket()
    {
        await using var visitor = await VisitorSocket.OpenAsync(this);

        await visitor.SendRawAsync("""{"type":"message","text":"unterminated""");
        await visitor.SendMessageAsync("after the bad frame");

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal(["after the bad frame"], "a malformed frame is skipped and the socket keeps reading");
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocket_ShouldSkipFrame_WhenTextIsNotValidUtf8()
    {
        await using var visitor = await VisitorSocket.OpenAsync(this);

        byte[] invalid = [.. "{\"type\":\"message\",\"text\":\""u8, 0xC3, 0x28, .. "\"}"u8];
        await visitor.Socket.SendAsync(invalid, WebSocketMessageType.Text, true, visitor.Timeout);
        await visitor.SendMessageAsync("valid");

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal(["valid"], "a frame that is not valid UTF-8 is malformed: skipped, and the socket keeps reading");
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocket_ShouldDropOverLengthMessage_WhenTextExceedsMaxMessageLength()
    {
        await using var visitor = await VisitorSocket.OpenAsync(this);

        await visitor.SendMessageAsync(new string('a', new WebChatOptions().MaxMessageLength + 1));
        await visitor.SendMessageAsync("short one");

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal(["short one"],
            "a message over MaxMessageLength characters is dropped, and the socket stays open for the next one");
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocket_ShouldDropMessage_WhenFrameExceedsByteBound()
    {
        // Far beyond any message the length limit allows: the server stops buffering it, discards the rest of
        // it as it arrives, and goes on reading the next message.
        await using var visitor = await VisitorSocket.OpenAsync(this);

        await visitor.SendRawAsync($$"""{"type":"message","text":"{{new string('x', 64 * 1024)}}"}""");
        await visitor.SendMessageAsync("next");

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal(["next"]);
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task VisitorSocket_ShouldIgnoreBinaryFrame_WhenTextFrameFollows()
    {
        await using var visitor = await VisitorSocket.OpenAsync(this);

        await visitor.Socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"message","text":"binary"}"""),
            WebSocketMessageType.Binary, true, visitor.Timeout);
        await visitor.SendMessageAsync("text");

        var stored = await visitor.WaitForInboundTextsAsync(1);
        stored.Should().Equal(["text"], "the protocol is text frames; a binary frame is ignored, not read as a message");
        visitor.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task RestMessage_ShouldReturnBadRequest_WhenTextExceedsMaxMessageLength()
    {
        using var client = _factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var session = await GetSessionAsync(sessionId);
        var max = new WebChatOptions().MaxMessageLength;

        using (var tooLong = await client.PostAsJsonAsync(
            $"/api/v1/webchat/sessions/{sessionId}/messages", new { text = new string('a', max + 1) }))
            tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the REST route enforces the same limit as the socket");
        using (var atLimit = await client.PostAsJsonAsync(
            $"/api/v1/webchat/sessions/{sessionId}/messages", new { text = new string('a', max) }))
            atLimit.StatusCode.Should().Be(HttpStatusCode.OK, "a message of exactly MaxMessageLength characters is accepted");

        var messages = await _factory.Services.GetRequiredService<IMessageStore>()
            .GetConversationMessagesAsync(VisitorTenant, session.ConversationId, 100, 0, CancellationToken.None);
        messages.Count(m => m.Direction == MessageDirection.Inbound).Should().Be(1);
    }

    /// <summary>A visitor's session with its socket attached and the <c>connected</c> frame consumed.</summary>
    private sealed class VisitorSocket : IAsyncDisposable
    {
        private readonly WebChatVisitorIdentityTests _owner;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
        private readonly HttpClient _client;
        private WebChatSession _session = null!;

        private VisitorSocket(WebChatVisitorIdentityTests owner)
        {
            _owner = owner;
            _client = owner._factory.CreateClient();
        }

        public WebSocket Socket { get; private set; } = null!;

        public CancellationToken Timeout => _timeout.Token;

        public static async Task<VisitorSocket> OpenAsync(WebChatVisitorIdentityTests owner)
        {
            var visitor = new VisitorSocket(owner);
            var sessionId = await CreateSessionAsync(visitor._client);
            visitor._session = await owner.GetSessionAsync(sessionId);
            visitor.Socket = await owner._factory.Server.CreateWebSocketClient()
                .ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), visitor.Timeout);
            (await ReceiveTextAsync(visitor.Socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");
            return visitor;
        }

        public Task SendMessageAsync(string text) => SendRawAsync(JsonSerializer.Serialize(
            new WebChatClientMessage("message", text), WebChatJsonContext.Default.WebChatClientMessage));

        /// <summary>Sends one text message as several 1000-byte WebSocket frames.</summary>
        public async Task SendFragmentedAsync(byte[] message)
        {
            for (var offset = 0; offset < message.Length; offset += 1000)
            {
                var count = Math.Min(1000, message.Length - offset);
                await Socket.SendAsync(message.AsMemory(offset, count), WebSocketMessageType.Text,
                    endOfMessage: offset + count == message.Length, Timeout);
            }
        }

        public Task SendRawAsync(string frame) =>
            Socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, Timeout);

        /// <summary>
        /// Waits until the session's conversation holds <paramref name="expected"/> inbound messages, then gives the
        /// socket's read loop time to store any extra one, and returns the texts of all of them.
        /// </summary>
        public async Task<IReadOnlyList<string>> WaitForInboundTextsAsync(int expected)
        {
            var store = _owner._factory.Services.GetRequiredService<IMessageStore>();
            async Task<IReadOnlyList<string>> ReadAsync() =>
                [.. (await store.GetConversationMessagesAsync(VisitorTenant, _session.ConversationId, 100, 0, CancellationToken.None))
                    .Where(m => m.Direction == MessageDirection.Inbound)
                    .SelectMany(m => m.Content.Blocks.OfType<TextBlock>())
                    .Select(b => b.Text)];
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((await ReadAsync()).Count < expected && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the server-side socket loop stores the message
            await Task.Delay(100); // fence-allow: SETTLE — window in which an extra (wrongly stored) message would show
            return await ReadAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Socket?.Dispose();
            _client.Dispose();
            _timeout.Dispose();
            await Task.CompletedTask;
        }
    }

    private Task<Message> SendAgentReplyAsync(WebChatSession session, EntityId agentId, string text) =>
        _factory.Services.GetRequiredService<IConversationService>().SendMessageAsync(
            session.ConversationId,
            VisitorTenant,
            new MessageEnvelope([new TextBlock(text)]),
            agentId,
            ConversationOwnerKind.Agent,
            CancellationToken.None);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the server-side socket loop catches up
        condition().Should().BeTrue("the server-side socket loop should have caught up within 5 s");
    }

    private async Task<EntityId> GiveConversationToAgentAsync(EntityId conversationId)
    {
        var agentId = EntityId.From($"agent-{Guid.NewGuid():N}");
        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = await conversationStore.GetByIdAsync(VisitorTenant, conversationId, CancellationToken.None);
        conversation.Should().NotBeNull();
        conversation!.State = ConversationState.Active;
        conversation.Owner = ConversationOwner.ForAgent(agentId);
        await conversationStore.SaveAsync(conversation, CancellationToken.None);
        return agentId;
    }

    [Fact]
    public async Task CreateSession_ShouldReturnVisitorIdentity_WhenTenantExists()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = VisitorTenantId });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        root.TryGetProperty("sessionId", out _).Should().BeTrue();
        root.TryGetProperty("wsUrl", out _).Should().BeTrue();
        root.TryGetProperty("visitorAddress", out var visitorAddress).Should().BeTrue("the response gains visitorAddress (D16)");
        root.TryGetProperty("contactId", out var contactId).Should().BeTrue("the response gains contactId (D16)");
        root.TryGetProperty("conversationId", out var conversationId).Should().BeTrue("the response gains conversationId (D16)");

        var contact = await _factory.Services.GetRequiredService<IContactStore>()
            .GetByIdAsync(VisitorTenant, EntityId.From(contactId.GetString()!), CancellationToken.None);
        contact.Should().NotBeNull();
        contact!.FindAddress(ChannelType.WebChat)!.Address.Should().Be(visitorAddress.GetString());
        var conversation = await _factory.Services.GetRequiredService<IConversationStore>()
            .GetByIdAsync(VisitorTenant, EntityId.From(conversationId.GetString()!), CancellationToken.None);
        conversation.Should().NotBeNull();
        conversation!.ContactId.Should().Be(contact.ContactId);
    }

    [Fact]
    public async Task CreateSession_ShouldReturnNotFound_WhenWebChatChannelInactive() =>
        await AssertSessionRefusedLikeAnUnknownTenantAsync(InactiveChannelTenantId);

    [Fact]
    public async Task CreateSession_ShouldReturnNotFound_WhenWebChatChannelMissing() =>
        await AssertSessionRefusedLikeAnUnknownTenantAsync(MissingChannelTenantId);

    /// <summary>
    /// A tenant without an active WebChat channel gets exactly the unknown-tenant answer — same status, same
    /// body — and no contact or conversation is created for it.
    /// </summary>
    private async Task AssertSessionRefusedLikeAnUnknownTenantAsync(string tenantId)
    {
        using var client = _factory.CreateClient();
        using var unknown = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = "no-such-tenant" });
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        using var response = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Be(unknownBody,
            "a tenant without an active WebChat channel answers exactly like an unknown tenant");
        var (contacts, conversations, _) = await WebhookProbes.CountRowsAsync(_factory.Services, new TenantId(tenantId));
        (contacts, conversations).Should().Be((0, 0), "no visitor contact or conversation is created");
    }

    /// <summary>
    /// Reads one text frame, or <c>null</c> when none arrives within <paramref name="wait"/> (or the socket
    /// ends instead).
    /// </summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        var buffer = new byte[8192];
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            return result.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }
}
