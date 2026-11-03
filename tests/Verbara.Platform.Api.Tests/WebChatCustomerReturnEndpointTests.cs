using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity task 2.3(e3) (design D8): a visitor returning to a <c>Resolved</c> WebChat
/// conversation, through the real host, over the REST endpoint and over the WebSocket. Each path closes the row once,
/// through <see cref="WebChatInboundRouter"/>: one <c>Resolved → Closed</c> event and one <c>conversation.closed</c>
/// audit entry with the system actor.
/// </summary>
public sealed class WebChatCustomerReturnEndpointTests : IDisposable
{
    private const string TenantIdValue = "tenant-webchat-return";
    private static readonly TenantId Tenant = new(TenantIdValue);
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        public IAuditService Audit { get; } = Substitute.For<IAuditService>();

        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) => services.AddSingleton(Audit));
            var host = base.CreateHost(builder);
            SeedTestCustomerTenant(host.Services, TenantIdValue);
            SeedWebChatChannel(host.Services, TenantIdValue);
            return host;
        }
    }

    [Fact]
    public async Task SendRestMessage_ShouldCloseTheResolvedConversationOnceAndAudit_WhenTheVisitorReturns()
    {
        using var client = _factory.CreateClient();
        var (sessionId, resolvedId) = await OpenResolvedSessionAsync(client);
        var closedEvents = Subscribe(resolvedId);

        using var send = await client.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text = "volví" });
        send.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertReturnedOnceAsync(sessionId, resolvedId, closedEvents);
    }

    [Fact]
    public async Task WebSocketMessage_ShouldCloseTheResolvedConversationOnceAndAudit_WhenTheVisitorReturns()
    {
        using var client = _factory.CreateClient();
        var (sessionId, resolvedId) = await OpenResolvedSessionAsync(client);
        var closedEvents = Subscribe(resolvedId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await _factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        await ReceiveAsync(socket, timeout.Token); // "connected"

        var frame = JsonSerializer.Serialize(new WebChatClientMessage("message", "volví"), WebChatJsonContext.Default.WebChatClientMessage);
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, timeout.Token);

        // The socket's read loop commits the close first, then announces it (event, counter, audit), then opens the
        // new conversation and stores the message: wait for the last of these, not for the close alone, or a loaded
        // run asserts before the announcer's audit write.
        var messages = _factory.Services.GetRequiredService<IMessageStore>();
        var sessions = _factory.Services.GetRequiredService<WebChatSessionManager>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = (await sessions.GetSessionAsync(sessionId))!.ConversationId;
            if (current != resolvedId
                && ClosedAuditCount(resolvedId) == 1
                && (await messages.GetConversationMessagesAsync(Tenant, current, 100, 0, CancellationToken.None)).Count > 0)
                break;
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the socket's read loop has processed the frame
        }

        await AssertReturnedOnceAsync(sessionId, resolvedId, closedEvents);
    }

    private async Task<(string SessionId, EntityId ResolvedId)> OpenResolvedSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = TenantIdValue });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = body.RootElement.GetProperty("sessionId").GetString()!;
        var session = (await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId))!;

        // The session's conversation was served and resolved by an agent.
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = (await store.GetByIdAsync(Tenant, session.ConversationId, CancellationToken.None))!;
        conversation.State = ConversationState.Resolved;
        conversation.Owner = ConversationOwner.ForAgent(EntityId.From("agent-return"));
        await store.SaveAsync(conversation, CancellationToken.None);
        return (sessionId, session.ConversationId);
    }

    private List<ConversationStateChangedEvent> Subscribe(EntityId resolvedId)
    {
        var events = new List<ConversationStateChangedEvent>();
        var subscription = _factory.Services.GetRequiredService<PlatformEventBus>().Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent { NewState: nameof(ConversationState.Closed) } sc && sc.ConversationId == resolvedId.Value)
                lock (events) events.Add(sc);
        });
        _subscriptions.Add(subscription);
        return events;
    }

    private readonly List<IDisposable> _subscriptions = [];

    private int ClosedAuditCount(EntityId resolvedId) =>
        _factory.Audit.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(IAuditService.RecordAsync)
            && c.GetArguments() is { Length: > 5 } args
            && args[2] is "conversation.closed"
            && args[4] is CustomerReturnAnnouncer.ActorId
            && args[6] is string target && target == resolvedId.Value);

    private async Task AssertReturnedOnceAsync(string sessionId, EntityId resolvedId, List<ConversationStateChangedEvent> closedEvents)
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var closed = (await store.GetByIdAsync(Tenant, resolvedId, CancellationToken.None))!;
        closed.State.Should().Be(ConversationState.Closed);
        closed.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.CustomerReturned);

        lock (closedEvents)
        {
            closedEvents.Should().ContainSingle().Which.OldState.Should().Be(nameof(ConversationState.Resolved));
        }

        ClosedAuditCount(resolvedId).Should().Be(1);

        var messages = _factory.Services.GetRequiredService<IMessageStore>();
        (await messages.GetConversationMessagesAsync(Tenant, resolvedId, 100, 0, CancellationToken.None))
            .Should().BeEmpty("the reply opens a new conversation");
        var session = (await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId))!;
        session.ConversationId.Should().NotBe(resolvedId, "the session follows the visitor into the new conversation");
        (await messages.GetConversationMessagesAsync(Tenant, session.ConversationId, 100, 0, CancellationToken.None))
            .Should().ContainSingle(m => m.Direction == MessageDirection.Inbound);

        foreach (var subscription in _subscriptions)
            subscription.Dispose();
    }

    private static async Task<string> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
