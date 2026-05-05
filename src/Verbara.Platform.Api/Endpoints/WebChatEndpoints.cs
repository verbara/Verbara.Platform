using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class WebChatEndpoints
{
    internal sealed record CreateSessionRequest(string TenantId);
    internal sealed record CreateSessionResponse(string SessionId, string WsUrl);
    internal sealed record WebChatMessageRequest(string Text);

    internal static RouteGroupBuilder MapWebChatEndpoints(this RouteGroupBuilder group)
    {
        var webchat = group.MapGroup("/webchat")
            .WithTags("WebChat")
            .AllowAnonymous();

        webchat.MapPost("/sessions", CreateSession);
        webchat.MapPost("/sessions/{sessionId}/messages", SendRestMessage);

        return group;
    }

    /// <summary>Maps the WebSocket endpoint at root level (outside versioned API group).</summary>
    internal static void MapWebChatWebSocket(this WebApplication app)
    {
        app.Map("/ws/webchat/{sessionId}", HandleWebSocket)
            .ExcludeFromDescription();
    }

    private static async Task<IResult> CreateSession(
        CreateSessionRequest request,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IConversationLifecycleService lifecycleService,
        [FromServices] IContactStore contactStore,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tid = new TenantId(request.TenantId);

        var tenant = await tenantStore.GetAsync(request.TenantId, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        // Create anonymous contact for this WebChat visitor
        var visitorAddress = new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}");
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tid,
            CreatedAt = clock.UtcNow,
        };
        contact.AddAddress(visitorAddress);
        await contactStore.SaveAsync(contact, ct);

        // Create conversation
        var conversation = await lifecycleService.CreateAsync(
            tid, contact.ContactId, ChannelType.WebChat, ct);

        var sessionId = await sessionManager.ConnectAsync(
            tid, conversation.ConversationId, visitorAddress, clock);

        return Results.Ok(new CreateSessionResponse(sessionId, $"/ws/webchat/{sessionId}"));
    }

    internal static async Task HandleWebSocket(
        HttpContext context,
        string sessionId,
        [FromServices] WebSocketWebChatTransport transport,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IInboundMessagePipeline pipeline,
        [FromServices] IClock clock,
        [FromServices] PlatformEventBus eventBus)
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

        var ct = context.RequestAborted;
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
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                }
                catch (WebSocketException)
                {
                    break;
                }

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

    private static async Task<IResult> SendRestMessage(
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
