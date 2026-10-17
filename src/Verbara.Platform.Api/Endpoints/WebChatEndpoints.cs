using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class WebChatEndpoints
{
    internal sealed record CreateSessionRequest(string TenantId);
    /// <summary>
    /// A created WebChat session: its transport handle (<c>sessionId</c>, <c>wsUrl</c>) and the visitor's
    /// identity — its <c>webchat-</c> visitor address, the contact carrying it and that contact's
    /// conversation (spec <c>webchat-visitor-identity</c>; the last three are additive in 2.28.0).
    /// </summary>
    internal sealed record CreateSessionResponse(
        string SessionId,
        string WsUrl,
        string VisitorAddress,
        string ContactId,
        string ConversationId);
    internal sealed record WebChatMessageRequest(string Text);

    internal static RouteGroupBuilder MapWebChatEndpoints(this RouteGroupBuilder group)
    {
        var webchat = group.MapGroup("/webchat")
            .WithTags("WebChat")
            .AllowAnonymous();

        webchat.MapPost("/sessions", CreateSession)
            .RequireWebChatRateLimit(WebChatRateLimitPolicy.SessionsPolicy, WebChatRateLimitPolicy.SessionsLimitKey,
                WebChatRateLimitPolicy.DefaultSessionsPerMinutePerIp, ResolveSessionCreateTenantAsync);
        webchat.MapPost("/sessions/{sessionId}/messages", SendRestMessage)
            .RequireWebChatRateLimit(WebChatRateLimitPolicy.MessagesPolicy, WebChatRateLimitPolicy.MessagesLimitKey,
                WebChatRateLimitPolicy.DefaultMessagesPerMinutePerIp, ResolveSessionTenantAsync);

        return group;
    }

    /// <summary>The rate-limit tenant of a session create: the requested tenant, when it exists.</summary>
    private static async ValueTask<string?> ResolveSessionCreateTenantAsync(EndpointFilterInvocationContext invocation)
    {
        var request = invocation.Arguments.OfType<CreateSessionRequest>().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(request?.TenantId))
            return null;

        var tenantStore = invocation.HttpContext.RequestServices.GetRequiredService<ITenantStore>();
        return await tenantStore.GetAsync(request.TenantId, invocation.HttpContext.RequestAborted) is null
            ? null
            : request.TenantId;
    }

    /// <summary>The rate-limit tenant of a message: the tenant of the session it names, when that exists.</summary>
    private static async ValueTask<string?> ResolveSessionTenantAsync(EndpointFilterInvocationContext invocation)
    {
        if (invocation.HttpContext.Request.RouteValues["sessionId"] is not string sessionId)
            return null;

        var sessionManager = invocation.HttpContext.RequestServices.GetRequiredService<WebChatSessionManager>();
        return (await sessionManager.GetSessionAsync(sessionId))?.TenantId.Value;
    }

    /// <summary>Maps the WebSocket endpoint at root level (outside versioned API group).</summary>
    internal static void MapWebChatWebSocket(this WebApplication app)
    {
        app.Map("/ws/webchat/{sessionId}", HandleWebSocket)
            .ExcludeFromDescription();
    }

    private static async Task<Results<Ok<CreateSessionResponse>, NotFound<ErrorResponse>>> CreateSession(
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
            return TypedResults.NotFound(new ErrorResponse("Tenant not found"));

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

        return TypedResults.Ok(new CreateSessionResponse(
            sessionId,
            $"/ws/webchat/{sessionId}",
            visitorAddress.Address,
            contact.ContactId.Value,
            conversation.ConversationId.Value));
    }

    internal static async Task HandleWebSocket(
        HttpContext context,
        string sessionId,
        [FromServices] WebSocketWebChatTransport transport,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IInboundMessagePipeline pipeline,
        [FromServices] WebChatInboundRouter inboundRouter,
        [FromServices] IClock clock,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IOptions<WebChatOptions> options,
        [FromServices] ILoggerFactory loggerFactory)
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

        // Attaching (re)connects the session — a reload or a network blip re-attaches to the same one — and
        // it stays connected until its LAST socket detaches (spec webchat-visitor-identity).
        if (!await sessionManager.AttachSocketAsync(sessionId, clock))
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session expired", ct);
            return;
        }

        try
        {
            // The confirmation goes out before the socket is registered, so it never races an agent reply
            // for the socket's single outstanding send.
            var connectedMsg = JsonSerializer.SerializeToUtf8Bytes(
                new WebChatWsMessage("connected", null),
                WebChatJsonContext.Default.WebChatWsMessage);
            await ws.SendAsync(connectedMsg, WebSocketMessageType.Text, true, ct);
            transport.Register(sessionId, ws);

            // Read loop. Each message is read whole (it can span several reads) into a buffer bounded by
            // MaxMessageLength; a message the server cannot use is skipped and the socket stays open, so one bad
            // message never cuts the visitor off from agent replies (spec webchat-visitor-identity).
            var maxLength = options.Value.MaxMessageLength;
            var logger = loggerFactory.CreateLogger(LoggerCategory);
            var reader = new WebChatMessageReader(WebChatMessageReader.MaxMessageBytesFor(maxLength));
            while (ws.State == WebSocketState.Open)
            {
                WebChatInboundFrame frame;
                try
                {
                    frame = await reader.ReadAsync(ws, ct);
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (frame.Kind == WebChatFrameKind.Close)
                    break;

                if (frame.Kind == WebChatFrameKind.Ignored)
                {
                    LogNonTextFrameIgnored(logger, session.TenantId.Value, sessionId);
                    continue;
                }

                if (frame.Kind == WebChatFrameKind.Oversized)
                {
                    LogMessageTooLarge(logger, session.TenantId.Value, sessionId, maxLength);
                    continue;
                }

                WebChatClientMessage? clientMsg;
                try
                {
                    clientMsg = JsonSerializer.Deserialize(frame.Payload.Span, WebChatJsonContext.Default.WebChatClientMessage);
                }
                catch (JsonException ex)
                {
                    LogMalformedFrame(logger, session.TenantId.Value, sessionId, ex);
                    continue;
                }

                if (clientMsg?.Type == "message" && !string.IsNullOrEmpty(clientMsg.Text))
                {
                    if (clientMsg.Text.Length > maxLength)
                    {
                        LogMessageTooLong(logger, session.TenantId.Value, sessionId, clientMsg.Text.Length, maxLength);
                        continue;
                    }

                    await sessionManager.TouchAsync(sessionId, clock);
                    var inbound = WebChatMessageAdapter.ToInboundMessage(
                        session,
                        new MessageEnvelope([new TextBlock(clientMsg.Text)]),
                        Guid.NewGuid().ToString("N"),
                        clock.UtcNow);

                    var pipelineResult = await pipeline.ProcessAsync(inbound, session.TenantId, ChannelType.WebChat, ct);
                    await inboundRouter.RouteFirstInboundAsync(
                        sessionId, session.TenantId, pipelineResult, inbound.Content, ct);
                }
                else if (clientMsg?.Type == "typing")
                {
                    eventBus.Publish(new ConversationMessageEvent(
                        session.TenantId.Value, session.ConversationId.Value,
                        "", "TypingIndicator", "WebChat"));
                }
            }
        }
        finally
        {
            transport.Unregister(sessionId, ws);
            await sessionManager.DetachSocketAsync(sessionId);
        }
    }

    private static async Task<Results<Ok<MessageResponse>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> SendRestMessage(
        string sessionId,
        WebChatMessageRequest request,
        [FromServices] WebChatSessionManager sessionManager,
        [FromServices] IInboundMessagePipeline pipeline,
        [FromServices] WebChatInboundRouter inboundRouter,
        [FromServices] IClock clock,
        [FromServices] IOptions<WebChatOptions> options,
        CancellationToken ct)
    {
        var session = await sessionManager.GetSessionAsync(sessionId);
        if (session is null || !session.IsConnected)
            return TypedResults.NotFound(new ErrorResponse("Session not found or disconnected"));

        if (string.IsNullOrWhiteSpace(request.Text))
            return TypedResults.BadRequest(new ErrorResponse("Text is required"));

        var maxLength = options.Value.MaxMessageLength;
        if (request.Text.Length > maxLength)
            return TypedResults.BadRequest(new ErrorResponse($"Text exceeds {maxLength} characters"));

        await sessionManager.TouchAsync(sessionId, clock);

        var inbound = WebChatMessageAdapter.ToInboundMessage(
            session,
            new MessageEnvelope([new TextBlock(request.Text)]),
            Guid.NewGuid().ToString("N"),
            clock.UtcNow);

        var pipelineResult = await pipeline.ProcessAsync(inbound, session.TenantId, ChannelType.WebChat, ct);
        await inboundRouter.RouteFirstInboundAsync(
            sessionId, session.TenantId, pipelineResult, inbound.Content, ct);

        return TypedResults.Ok(new MessageResponse("Message sent"));
    }

    private const string LoggerCategory = "Verbara.Platform.Api.Endpoints.WebChat";

    [LoggerMessage(EventId = 7540, Level = LogLevel.Warning,
        Message = "WebChat socket message skipped: not valid JSON (tenant={TenantId}, session={SessionId})")]
    private static partial void LogMalformedFrame(ILogger logger, string tenantId, string sessionId, Exception ex);

    [LoggerMessage(EventId = 7541, Level = LogLevel.Warning,
        Message = "WebChat socket message skipped: {Length} characters, over the {MaxLength}-character limit (tenant={TenantId}, session={SessionId})")]
    private static partial void LogMessageTooLong(ILogger logger, string tenantId, string sessionId, int length, int maxLength);

    [LoggerMessage(EventId = 7542, Level = LogLevel.Warning,
        Message = "WebChat socket message skipped: larger than any message of {MaxLength} characters can be (tenant={TenantId}, session={SessionId})")]
    private static partial void LogMessageTooLarge(ILogger logger, string tenantId, string sessionId, int maxLength);

    [LoggerMessage(EventId = 7543, Level = LogLevel.Debug,
        Message = "WebChat socket message skipped: not a text message (tenant={TenantId}, session={SessionId})")]
    private static partial void LogNonTextFrameIgnored(ILogger logger, string tenantId, string sessionId);
}
