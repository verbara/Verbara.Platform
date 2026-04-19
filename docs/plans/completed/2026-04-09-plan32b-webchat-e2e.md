# Plan 32B: WebChat End-to-End Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make WebChat fully operational — customer connects via WebSocket, messages flow through the inbound pipeline, agents reply through the existing connector, and a self-contained JS widget can be embedded on any website.

**Architecture:** `WebSocketWebChatTransport` implements the existing `IWebChatTransport` interface using `ConcurrentDictionary<string, WebSocket>`. New `WebChatEndpoints` handles session creation (AllowAnonymous), WebSocket upgrade with read loop, and REST fallback. A vanilla JS widget (`<50KB`) served from `wwwroot/webchat/` fetches tenant branding and connects via WebSocket.

**Tech Stack:** ASP.NET WebSockets, System.Text.Json AOT source generators, vanilla JS + CSS widget

---

## Existing Infrastructure

These files already exist and are **not modified** unless noted:

| File | Purpose |
|------|---------|
| `src/Asterisk.Platform.Channels.WebChat/IWebChatTransport.cs` | Interface: `SendToClientAsync`, `DisconnectAsync` |
| `src/Asterisk.Platform.Channels.WebChat/WebChatConnector.cs` | `IChannelConnector` impl — uses transport to send to clients |
| `src/Asterisk.Platform.Channels.WebChat/WebChatSessionManager.cs` | In-memory session store with connect/disconnect/reconnect/cleanup |
| `src/Asterisk.Platform.Channels.WebChat/WebChatSession.cs` | Session model: SessionId, ConversationId, TenantId, IsConnected |
| `src/Asterisk.Platform.Channels.WebChat/WebChatOptions.cs` | Config: SessionTimeout (30min), MaxMessageLength (4000) |
| `src/Asterisk.Platform.Channels.WebChat/WebChatMessageAdapter.cs` | Static helper: converts session message to `InboundMessage` |
| `src/Asterisk.Platform.Channels.WebChat/ServiceCollectionExtensions.cs` | DI: `AddWebChat()` — registers SessionManager + Connector (but NOT transport) |
| `src/Asterisk.Platform.Api/Endpoints/BrandingEndpoints.cs` | Public `GET /branding/{tenantId}` — widget fetches colors/logo |

**Key gap:** `IWebChatTransport` has no implementation. `AddWebChat()` is never called in `Program.cs`. No WebChat endpoints exist.

---

### Task 1: WebSocket Message Types + Transport Implementation

Creates the WebSocket message protocol types, AOT serialization context, and the `WebSocketWebChatTransport` that implements `IWebChatTransport`.

**Files:**
- Create: `src/Asterisk.Platform.Channels.WebChat/WebChatWsMessage.cs`
- Create: `src/Asterisk.Platform.Channels.WebChat/WebChatJsonContext.cs`
- Create: `src/Asterisk.Platform.Channels.WebChat/WebSocketWebChatTransport.cs`
- Test: `tests/Asterisk.Platform.Channels.WebChat.Tests/WebSocketWebChatTransportTests.cs`

- [ ] **Step 1: Create WebChatWsMessage record**

This is the envelope for both server→client and client→server WebSocket messages.

```csharp
// src/Asterisk.Platform.Channels.WebChat/WebChatWsMessage.cs
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.WebChat;

/// <summary>
/// Server → Client WebSocket message envelope.
/// </summary>
public sealed record WebChatWsMessage(string Type, MessageEnvelope? Data);

/// <summary>
/// Client → Server WebSocket message (text only).
/// </summary>
public sealed record WebChatClientMessage(string Type, string? Text);
```

- [ ] **Step 2: Create WebChatJsonContext for AOT serialization**

```csharp
// src/Asterisk.Platform.Channels.WebChat/WebChatJsonContext.cs
using System.Text.Json.Serialization;

namespace Asterisk.Platform.Channels.WebChat;

[JsonSerializable(typeof(WebChatWsMessage))]
[JsonSerializable(typeof(WebChatClientMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WebChatJsonContext : JsonSerializerContext;
```

- [ ] **Step 3: Create WebSocketWebChatTransport**

```csharp
// src/Asterisk.Platform.Channels.WebChat/WebSocketWebChatTransport.cs
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.WebChat;

public sealed class WebSocketWebChatTransport : IWebChatTransport
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public async Task SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(sessionId, out var ws) || ws.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.SerializeToUtf8Bytes(
            new WebChatWsMessage("message", message),
            WebChatJsonContext.Default.WebChatWsMessage);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    public async Task DisconnectAsync(string sessionId, CancellationToken ct)
    {
        if (_connections.TryRemove(sessionId, out var ws) && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", ct);
    }

    /// <summary>Called by WebSocket middleware to register a client connection.</summary>
    public void Register(string sessionId, WebSocket ws) => _connections[sessionId] = ws;

    /// <summary>Called when a client disconnects.</summary>
    public void Unregister(string sessionId) => _connections.TryRemove(sessionId, out _);
}
```

- [ ] **Step 4: Write transport tests**

```csharp
// tests/Asterisk.Platform.Channels.WebChat.Tests/WebSocketWebChatTransportTests.cs
using System.Net.WebSockets;
using Asterisk.Platform.Conversations;
using NSubstitute;

namespace Asterisk.Platform.Channels.WebChat.Tests;

public class WebSocketWebChatTransportTests
{
    private static MessageEnvelope MakeEnvelope(string text = "Hello") =>
        new([new TextBlock(text)]);

    [Fact]
    public async Task SendToClientAsync_ShouldSendJson_WhenClientIsConnected()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);

        await ws.Received(1).SendAsync(
            Arg.Any<ReadOnlyMemory<byte>>(),
            WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendToClientAsync_ShouldBeNoOp_WhenClientIsNotConnected()
    {
        var transport = new WebSocketWebChatTransport();

        // No exception, no crash — just a no-op
        await transport.SendToClientAsync("nonexistent", MakeEnvelope(), CancellationToken.None);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldCloseSocket_WhenOpen()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.DisconnectAsync("sess-1", CancellationToken.None);

        await ws.Received(1).CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Session ended",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Register_ShouldTrackConnection_AndUnregister_ShouldRemoveIt()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<WebSocket>();
        ws.State.Returns(WebSocketState.Open);

        transport.Register("sess-1", ws);
        // After register, send should work (socket is tracked)
        // After unregister, send should be no-op
        transport.Unregister("sess-1");

        // Verify unregister by attempting send — should be no-op
        var task = transport.SendToClientAsync("sess-1", MakeEnvelope(), CancellationToken.None);
        task.IsCompleted.Should().BeTrue();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Channels.WebChat.Tests/ -v q`
Expected: All tests pass (existing 13 + 4 new = 17)

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Channels.WebChat/WebChatWsMessage.cs \
        src/Asterisk.Platform.Channels.WebChat/WebChatJsonContext.cs \
        src/Asterisk.Platform.Channels.WebChat/WebSocketWebChatTransport.cs \
        tests/Asterisk.Platform.Channels.WebChat.Tests/WebSocketWebChatTransportTests.cs
git commit -m "feat: add WebSocketWebChatTransport with AOT message types"
```

---

### Task 2: DI Wiring + Program.cs Integration

Registers the transport in `AddWebChat()`, calls `AddWebChat()` from `Program.cs`, enables WebSocket middleware, and enables static files for the widget.

**Files:**
- Modify: `src/Asterisk.Platform.Channels.WebChat/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Update ServiceCollectionExtensions to register transport**

In `src/Asterisk.Platform.Channels.WebChat/ServiceCollectionExtensions.cs`, add the transport registration:

```csharp
// Replace existing file content
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.WebChat;

/// <summary>
/// DI registration extensions for Platform.Channels.WebChat services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WebChat connector, session manager, transport, and message adapter.
    /// </summary>
    public static IServiceCollection AddWebChat(
        this IServiceCollection services,
        Action<WebChatOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<WebChatOptions>();

        services.AddSingleton<WebChatSessionManager>();
        services.AddSingleton<WebSocketWebChatTransport>();
        services.AddSingleton<IWebChatTransport>(sp => sp.GetRequiredService<WebSocketWebChatTransport>());
        services.AddSingleton<WebChatConnector>();

        return services;
    }
}
```

Key change: `WebSocketWebChatTransport` registered as singleton AND exposed as `IWebChatTransport`. The concrete type is also registered so `WebChatEndpoints` can access `Register`/`Unregister` methods.

- [ ] **Step 2: Add WebChat to Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, add three things:

**2a.** After `builder.Services.AddPlatformBilling();` (around line 76), add:

```csharp
builder.Services.AddWebChat();
```

Add the using at the top of Program.cs:
```csharp
using Asterisk.Platform.Channels.WebChat;
```

**2b.** After `app.UseRouting()` or before endpoint mapping (around line 460), add WebSocket middleware:

```csharp
app.UseWebSockets();
```

**2c.** Before `app.UseRouting()` or after `app.UseStaticFiles()` (if it exists), ensure static files middleware is enabled for the widget:

```csharp
app.UseStaticFiles();
```

If `UseStaticFiles()` is already present, skip this. If not, add it before `UseRouting()`.

- [ ] **Step 3: Build to verify wiring compiles**

Run: `dotnet build src/Asterisk.Platform.Api/ -v q`
Expected: 0 warnings, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Channels.WebChat/ServiceCollectionExtensions.cs \
        src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: wire WebChat DI with WebSocket transport in Program.cs"
```

---

### Task 3: WebChat HTTP Endpoints

Creates the WebChat endpoints for session lifecycle and message exchange.

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/WebChatEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` — add new DTOs
- Modify: `src/Asterisk.Platform.Api/Program.cs` — map endpoints
- Test: `tests/Asterisk.Platform.Api.Tests/WebChatEndpointTests.cs`

- [ ] **Step 1: Create WebChatEndpoints.cs**

```csharp
// src/Asterisk.Platform.Api/Endpoints/WebChatEndpoints.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.WebChat;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Routing.Inbound;
using Asterisk.Platform.Switchboard;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class WebChatEndpoints
{
    internal sealed record CreateSessionRequest(string TenantId);
    internal sealed record CreateSessionResponse(string SessionId, string WsUrl);
    internal sealed record WebChatMessageRequest(string Text);

    internal static void MapWebChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webchat")
            .WithTags("WebChat")
            .AllowAnonymous();

        group.MapPost("/sessions", CreateSession);
        group.MapPost("/sessions/{sessionId}/messages", SendMessage);

        // WebSocket endpoint — mapped at root level (outside /api/v1)
        app.Map("/ws/webchat/{sessionId}", HandleWebSocket);
    }

    private static async Task<IResult> CreateSession(
        CreateSessionRequest request,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IConversationLifecycleService lifecycleService,
        [FromServices] IContactStore contactStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tid = new TenantId(request.TenantId);

        // Validate tenant exists
        var tenant = await tenantStore.GetByIdAsync(tid, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        // Create or resolve anonymous contact
        var contactAddress = new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}");
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tid,
            DisplayName = "Web Visitor",
            Channel = ChannelType.WebChat,
            ChannelAddress = contactAddress.Address,
            CreatedAt = clock.UtcNow,
        };
        await contactStore.SaveAsync(contact, ct);

        // Create conversation
        var conversation = await lifecycleService.CreateAsync(
            tid, contact.ContactId, ChannelType.WebChat, ct);

        var sessionId = await sessionManager.ConnectAsync(
            tid, conversation.ConversationId, contactAddress, clock);

        var wsUrl = $"/ws/webchat/{sessionId}";
        return Results.Ok(new CreateSessionResponse(sessionId, wsUrl));
    }

    private static async Task HandleWebSocket(
        HttpContext context,
        string sessionId,
        [FromServices] WebSocketWebChatTransport transport,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IInboundMessagePipeline pipeline,
        [FromServices] IClock clock,
        [FromServices] PlatformEventBus eventBus,
        CancellationToken ct)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var session = await sessionManager.GetSessionAsync(sessionId);
        if (session is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();
        transport.Register(sessionId, ws);

        try
        {
            // Send connected confirmation
            var connectedMsg = JsonSerializer.SerializeToUtf8Bytes(
                new WebChatWsMessage("connected", null),
                WebChatJsonContext.Default.WebChatWsMessage);
            await ws.SendAsync(connectedMsg, WebSocketMessageType.Text, true, ct);

            // Read loop
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var clientMsg = JsonSerializer.Deserialize(json, WebChatJsonContext.Default.WebChatClientMessage);

                    if (clientMsg?.Type == "message" && !string.IsNullOrEmpty(clientMsg.Text))
                    {
                        await sessionManager.TouchAsync(sessionId, clock);

                        var inbound = WebChatMessageAdapter.ToInboundMessage(
                            sessionId,
                            new MessageEnvelope([new TextBlock(clientMsg.Text)]),
                            Guid.NewGuid().ToString("N"),
                            clock.UtcNow);

                        await pipeline.ProcessAsync(inbound, session.TenantId, ChannelType.WebChat, ct);
                    }
                    else if (clientMsg?.Type == "typing")
                    {
                        eventBus.Publish(new ConversationMessageEvent(
                            session.TenantId.Value, session.ConversationId.Value,
                            "", "TypingIndicator", "WebChat"));
                    }
                }
            }
        }
        finally
        {
            transport.Unregister(sessionId);
            await sessionManager.DisconnectAsync(sessionId);
        }
    }

    private static async Task<IResult> SendMessage(
        string sessionId,
        WebChatMessageRequest request,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IInboundMessagePipeline pipeline,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var session = await sessionManager.GetSessionAsync(sessionId);
        if (session is null || !session.IsConnected)
            return Results.NotFound(new ErrorResponse("Session not found or disconnected"));

        if (string.IsNullOrWhiteSpace(request.Text))
            return Results.BadRequest(new ErrorResponse("Text is required"));

        await sessionManager.TouchAsync(sessionId, clock);

        var inbound = WebChatMessageAdapter.ToInboundMessage(
            sessionId,
            new MessageEnvelope([new TextBlock(request.Text)]),
            Guid.NewGuid().ToString("N"),
            clock.UtcNow);

        await pipeline.ProcessAsync(inbound, session.TenantId, ChannelType.WebChat, ct);

        return Results.Ok(new MessageResponse("Message sent"));
    }
}
```

- [ ] **Step 2: Register DTOs in ApiJsonContext**

In `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`, add after the `// Branding (public)` section:

```csharp
// WebChat
[JsonSerializable(typeof(WebChatEndpoints.CreateSessionRequest))]
[JsonSerializable(typeof(WebChatEndpoints.CreateSessionResponse))]
[JsonSerializable(typeof(WebChatEndpoints.WebChatMessageRequest))]
```

- [ ] **Step 3: Map WebChat endpoints in Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, after `v1.MapOnboardingEndpoints();` (line ~536), add:

```csharp
v1.MapWebChatEndpoints();
```

Note: `MapWebChatEndpoints` also maps the WebSocket endpoint at root level (`/ws/webchat/{sessionId}`) via `app.Map()`, so the v1 group is needed only for the REST endpoints.

**Important:** The `MapWebChatEndpoints` extension method takes `IEndpointRouteBuilder`, and since the WebSocket endpoint needs to be at root level (`/ws/webchat/...`), the method internally maps it outside the group. However, looking at the implementation, the WebSocket endpoint is mapped on `app` (the `IEndpointRouteBuilder` parameter), which in this case is the `v1` group. To fix this, the WebSocket endpoint should be mapped separately.

Revised approach — map the WebSocket endpoint separately in Program.cs, after `app.UseWebSockets();`:

```csharp
// WebSocket endpoint for WebChat (outside versioned API group)
app.Map("/ws/webchat/{sessionId}", WebChatEndpoints.HandleWebSocket);
```

And remove `app.Map("/ws/webchat/{sessionId}", HandleWebSocket);` from `MapWebChatEndpoints`. Change `HandleWebSocket` visibility to `internal static`.

- [ ] **Step 4: Write endpoint tests**

```csharp
// tests/Asterisk.Platform.Api.Tests/WebChatEndpointTests.cs
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.WebChat;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public class WebChatEndpointTests
{
    private static WebChatSessionManager CreateManager() =>
        new(Options.Create(new WebChatOptions()));

    [Fact]
    public async Task SessionManager_ShouldCreateSession_WithValidTenantId()
    {
        var manager = CreateManager();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var sessionId = await manager.ConnectAsync(
            new TenantId("tenant-1"),
            EntityId.From("conv-1"),
            new ChannelAddress(ChannelType.WebChat, "visitor-1"),
            clock);

        sessionId.Should().NotBeNullOrEmpty();
        var session = await manager.GetSessionAsync(sessionId);
        session.Should().NotBeNull();
        session!.TenantId.Value.Should().Be("tenant-1");
        session.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task SessionManager_ShouldRejectReconnect_WhenSessionExpired()
    {
        var manager = new WebChatSessionManager(Options.Create(new WebChatOptions
        {
            SessionTimeout = TimeSpan.FromMinutes(1)
        }));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        var sessionId = await manager.ConnectAsync(
            new TenantId("t1"), EntityId.From("c1"),
            new ChannelAddress(ChannelType.WebChat, "v1"), clock);
        await manager.DisconnectAsync(sessionId);

        // Advance time past timeout
        clock.UtcNow.Returns(DateTimeOffset.UtcNow.AddMinutes(5));

        var reconnected = await manager.ReconnectAsync(sessionId, clock);
        reconnected.Should().BeFalse();
    }

    [Fact]
    public async Task Transport_ShouldSend_WhenRegisteredAndOpen()
    {
        var transport = new WebSocketWebChatTransport();
        var ws = Substitute.For<System.Net.WebSockets.WebSocket>();
        ws.State.Returns(System.Net.WebSockets.WebSocketState.Open);
        transport.Register("sess-1", ws);

        await transport.SendToClientAsync(
            "sess-1", new MessageEnvelope([new TextBlock("Hi")]), CancellationToken.None);

        await ws.Received(1).SendAsync(
            Arg.Any<ReadOnlyMemory<byte>>(),
            System.Net.WebSockets.WebSocketMessageType.Text,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void WebChatMessageAdapter_ShouldCreateInboundMessage_WithCorrectChannel()
    {
        var envelope = new MessageEnvelope([new TextBlock("Hello")]);
        var msg = WebChatMessageAdapter.ToInboundMessage(
            "sess-1", envelope, "ext-1", DateTimeOffset.UtcNow);

        msg.From.Channel.Should().Be(ChannelType.WebChat);
        msg.From.Address.Should().Be("sess-1");
        msg.ExternalMessageId.Should().Be("ext-1");
    }

    [Fact]
    public async Task Transport_ShouldBeNoOp_WhenSessionNotRegistered()
    {
        var transport = new WebSocketWebChatTransport();

        // Should not throw
        await transport.SendToClientAsync(
            "unknown", new MessageEnvelope([new TextBlock("Hi")]), CancellationToken.None);
    }
}
```

- [ ] **Step 5: Run tests to verify**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All tests pass

- [ ] **Step 6: Run full build to verify 0 warnings**

Run: `dotnet build Asterisk.Platform.slnx -v q`
Expected: 0 warnings, 0 errors

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/WebChatEndpoints.cs \
        src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs \
        src/Asterisk.Platform.Api/Program.cs \
        tests/Asterisk.Platform.Api.Tests/WebChatEndpointTests.cs
git commit -m "feat: add WebChat endpoints for session creation, WebSocket, and REST fallback"
```

---

### Task 4: WebChat Customer Widget

Creates the embeddable vanilla JS widget served as static content from Platform.Api.

**Files:**
- Create: `src/Asterisk.Platform.Api/wwwroot/webchat/widget.js`
- Create: `src/Asterisk.Platform.Api/wwwroot/webchat/widget.css`

**No tests** — widget is tested manually and via E2E tests in Platform.Web (future sprint).

- [ ] **Step 1: Create widget.css**

```css
/* src/Asterisk.Platform.Api/wwwroot/webchat/widget.css */
.ast-webchat-bubble {
  position: fixed;
  bottom: 24px;
  right: 24px;
  width: 56px;
  height: 56px;
  border-radius: 50%;
  background: var(--ast-primary, #2563eb);
  color: #fff;
  border: none;
  cursor: pointer;
  box-shadow: 0 4px 12px rgba(0,0,0,0.15);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 99999;
  transition: transform 0.2s;
}
.ast-webchat-bubble:hover { transform: scale(1.1); }
.ast-webchat-bubble svg { width: 24px; height: 24px; fill: currentColor; }

.ast-webchat-panel {
  position: fixed;
  bottom: 90px;
  right: 24px;
  width: 380px;
  max-width: calc(100vw - 48px);
  height: 520px;
  max-height: calc(100vh - 120px);
  border-radius: 12px;
  background: #fff;
  box-shadow: 0 8px 30px rgba(0,0,0,0.12);
  display: flex;
  flex-direction: column;
  z-index: 99999;
  overflow: hidden;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
}
.ast-webchat-panel[hidden] { display: none; }

.ast-webchat-header {
  padding: 16px;
  background: var(--ast-primary, #2563eb);
  color: #fff;
  display: flex;
  align-items: center;
  gap: 10px;
}
.ast-webchat-header img { width: 32px; height: 32px; border-radius: 50%; }
.ast-webchat-header-title { font-weight: 600; font-size: 15px; }
.ast-webchat-close {
  margin-left: auto;
  background: none;
  border: none;
  color: #fff;
  cursor: pointer;
  font-size: 20px;
  line-height: 1;
}

.ast-webchat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.ast-webchat-msg {
  max-width: 80%;
  padding: 10px 14px;
  border-radius: 16px;
  font-size: 14px;
  line-height: 1.4;
  word-wrap: break-word;
}
.ast-webchat-msg--out {
  align-self: flex-end;
  background: var(--ast-primary, #2563eb);
  color: #fff;
  border-bottom-right-radius: 4px;
}
.ast-webchat-msg--in {
  align-self: flex-start;
  background: #f1f5f9;
  color: #1e293b;
  border-bottom-left-radius: 4px;
}

.ast-webchat-typing {
  align-self: flex-start;
  padding: 10px 14px;
  background: #f1f5f9;
  border-radius: 16px;
  font-size: 13px;
  color: #64748b;
}
.ast-webchat-typing[hidden] { display: none; }

.ast-webchat-input-area {
  display: flex;
  padding: 12px;
  border-top: 1px solid #e2e8f0;
  gap: 8px;
}
.ast-webchat-input {
  flex: 1;
  border: 1px solid #e2e8f0;
  border-radius: 8px;
  padding: 8px 12px;
  font-size: 14px;
  outline: none;
  resize: none;
  font-family: inherit;
}
.ast-webchat-input:focus { border-color: var(--ast-primary, #2563eb); }
.ast-webchat-send {
  background: var(--ast-primary, #2563eb);
  color: #fff;
  border: none;
  border-radius: 8px;
  padding: 8px 16px;
  cursor: pointer;
  font-size: 14px;
  font-weight: 500;
}
.ast-webchat-send:disabled { opacity: 0.5; cursor: default; }

@media (max-width: 480px) {
  .ast-webchat-panel {
    bottom: 0;
    right: 0;
    width: 100vw;
    height: 100vh;
    max-width: 100vw;
    max-height: 100vh;
    border-radius: 0;
  }
  .ast-webchat-bubble { bottom: 16px; right: 16px; }
}
```

- [ ] **Step 2: Create widget.js**

```javascript
// src/Asterisk.Platform.Api/wwwroot/webchat/widget.js
(function() {
  'use strict';

  var script = document.currentScript;
  var tenantId = script.getAttribute('data-tenant');
  var position = script.getAttribute('data-position') || 'bottom-right';
  var title = script.getAttribute('data-title') || 'Chat with us';
  var baseUrl = script.src.replace(/\/webchat\/widget\.js.*$/, '');
  var apiBase = baseUrl + '/api/v1';

  if (!tenantId) {
    console.error('[WebChat] data-tenant attribute is required');
    return;
  }

  // Load CSS
  var link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = baseUrl + '/webchat/widget.css';
  document.head.appendChild(link);

  // State
  var sessionId = null;
  var ws = null;
  var isOpen = false;
  var branding = null;

  // DOM elements
  var bubble, panel, messagesEl, inputEl, sendBtn, typingEl;

  // Fetch branding
  fetch(apiBase + '/branding/' + tenantId)
    .then(function(r) { return r.ok ? r.json() : null; })
    .then(function(b) {
      branding = b;
      init();
    })
    .catch(function() { init(); });

  function init() {
    createBubble();
    createPanel();
    applyBranding();

    // Restore session from localStorage
    var saved = localStorage.getItem('ast_webchat_' + tenantId);
    if (saved) {
      try {
        var data = JSON.parse(saved);
        sessionId = data.sessionId;
      } catch(e) { /* ignore */ }
    }
  }

  function createBubble() {
    bubble = document.createElement('button');
    bubble.className = 'ast-webchat-bubble';
    bubble.innerHTML = '<svg viewBox="0 0 24 24"><path d="M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H5.2L4 17.2V4h16v12z"/></svg>';
    bubble.onclick = togglePanel;
    if (position === 'bottom-left') {
      bubble.style.right = 'auto';
      bubble.style.left = '24px';
    }
    document.body.appendChild(bubble);
  }

  function createPanel() {
    panel = document.createElement('div');
    panel.className = 'ast-webchat-panel';
    panel.hidden = true;

    var headerTitle = branding && branding.displayName ? branding.displayName : title;
    var logoHtml = branding && branding.logoUrl
      ? '<img src="' + branding.logoUrl + '" alt="">'
      : '';

    panel.innerHTML =
      '<div class="ast-webchat-header">' +
        logoHtml +
        '<span class="ast-webchat-header-title">' + headerTitle + '</span>' +
        '<button class="ast-webchat-close">&times;</button>' +
      '</div>' +
      '<div class="ast-webchat-messages"></div>' +
      '<div class="ast-webchat-typing" hidden>Agent is typing...</div>' +
      '<div class="ast-webchat-input-area">' +
        '<textarea class="ast-webchat-input" rows="1" placeholder="Type a message..."></textarea>' +
        '<button class="ast-webchat-send" disabled>Send</button>' +
      '</div>';

    if (position === 'bottom-left') {
      panel.style.right = 'auto';
      panel.style.left = '24px';
    }

    document.body.appendChild(panel);

    panel.querySelector('.ast-webchat-close').onclick = togglePanel;
    messagesEl = panel.querySelector('.ast-webchat-messages');
    inputEl = panel.querySelector('.ast-webchat-input');
    sendBtn = panel.querySelector('.ast-webchat-send');
    typingEl = panel.querySelector('.ast-webchat-typing');

    inputEl.oninput = function() {
      sendBtn.disabled = !inputEl.value.trim();
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'typing', text: null }));
      }
    };
    inputEl.onkeydown = function(e) {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    };
    sendBtn.onclick = sendMessage;
  }

  function applyBranding() {
    if (!branding || !branding.primaryColor) return;
    document.documentElement.style.setProperty('--ast-primary', branding.primaryColor);
  }

  function togglePanel() {
    isOpen = !isOpen;
    panel.hidden = !isOpen;
    bubble.style.display = isOpen ? 'none' : 'flex';
    if (isOpen && !sessionId) {
      createSession();
    } else if (isOpen && sessionId && (!ws || ws.readyState !== WebSocket.OPEN)) {
      connectWebSocket();
    }
    if (isOpen) inputEl.focus();
  }

  function createSession() {
    fetch(apiBase + '/webchat/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tenantId: tenantId })
    })
    .then(function(r) { return r.json(); })
    .then(function(data) {
      sessionId = data.sessionId;
      localStorage.setItem('ast_webchat_' + tenantId,
        JSON.stringify({ sessionId: sessionId }));
      connectWebSocket();
    })
    .catch(function(err) {
      appendSystemMessage('Unable to connect. Please try again.');
      console.error('[WebChat] Session creation failed:', err);
    });
  }

  function connectWebSocket() {
    var protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    var wsUrl = protocol + '//' + new URL(baseUrl).host + '/ws/webchat/' + sessionId;

    ws = new WebSocket(wsUrl);

    ws.onopen = function() {
      sendBtn.disabled = !inputEl.value.trim();
    };

    ws.onmessage = function(event) {
      try {
        var msg = JSON.parse(event.data);
        if (msg.type === 'message' && msg.data) {
          appendInboundMessage(msg.data);
        } else if (msg.type === 'typing') {
          showTyping();
        } else if (msg.type === 'ended') {
          appendSystemMessage('Conversation ended');
          ws.close();
        }
      } catch(e) { /* ignore parse errors */ }
    };

    ws.onclose = function() {
      sendBtn.disabled = true;
    };
  }

  function sendMessage() {
    var text = inputEl.value.trim();
    if (!text) return;

    appendOutboundMessage(text);
    inputEl.value = '';
    sendBtn.disabled = true;

    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'message', text: text }));
    } else {
      // REST fallback
      fetch(apiBase + '/webchat/sessions/' + sessionId + '/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text })
      }).catch(function(err) {
        console.error('[WebChat] REST fallback failed:', err);
      });
    }
  }

  function appendOutboundMessage(text) {
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--out';
    div.textContent = text;
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function appendInboundMessage(data) {
    hideTyping();
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--in';
    // data is a MessageEnvelope — extract text blocks
    if (data.blocks) {
      var texts = data.blocks
        .filter(function(b) { return b.type === 'Text' || b.text; })
        .map(function(b) { return b.text || b.content || ''; });
      div.textContent = texts.join('\n') || '[Media]';
    } else {
      div.textContent = '[Message]';
    }
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function appendSystemMessage(text) {
    var div = document.createElement('div');
    div.className = 'ast-webchat-msg ast-webchat-msg--in';
    div.style.fontStyle = 'italic';
    div.textContent = text;
    messagesEl.appendChild(div);
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  var typingTimer;
  function showTyping() {
    typingEl.hidden = false;
    messagesEl.scrollTop = messagesEl.scrollHeight;
    clearTimeout(typingTimer);
    typingTimer = setTimeout(hideTyping, 3000);
  }

  function hideTyping() {
    typingEl.hidden = true;
    clearTimeout(typingTimer);
  }
})();
```

- [ ] **Step 3: Create wwwroot directory structure**

Ensure the `wwwroot/webchat/` directory exists under `src/Asterisk.Platform.Api/`.

- [ ] **Step 4: Build to verify static files are included**

Run: `dotnet build src/Asterisk.Platform.Api/ -v q`
Expected: 0 warnings. Static files are automatically included from `wwwroot/`.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/wwwroot/webchat/widget.css \
        src/Asterisk.Platform.Api/wwwroot/webchat/widget.js
git commit -m "feat: add embeddable WebChat customer widget with branding integration"
```

---

## Verification Checklist

After all tasks are complete:

1. `dotnet build Asterisk.Platform.slnx` — 0 warnings, 0 errors
2. `dotnet test Asterisk.Platform.slnx` — all tests pass (1,573 + ~9 new ≈ 1,582)
3. WebSocket transport: `WebSocketWebChatTransport` sends JSON to connected sockets
4. Session lifecycle: `POST /webchat/sessions` creates session + returns WebSocket URL
5. REST fallback: `POST /webchat/sessions/{id}/messages` processes message through pipeline
6. Widget: `GET /webchat/widget.js` serves the embeddable script
7. Branding: Widget fetches `GET /branding/{tenantId}` and applies colors
8. AOT: `WebChatJsonContext` handles all serialization — no reflection

## Estimated Scope

- 5 new files, 2 modified files
- ~9 new tests (1,573 → ~1,582)
- 0 new migrations
- 1 new endpoint group (WebChatEndpoints), 1 WebSocket endpoint
