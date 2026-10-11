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
    private static readonly TenantId VisitorTenant = new(VisitorTenantId);
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, VisitorTenantId);
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

    /// <summary>Reads one text frame, or <c>null</c> when none arrives within <paramref name="wait"/>.</summary>
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
    }
}
