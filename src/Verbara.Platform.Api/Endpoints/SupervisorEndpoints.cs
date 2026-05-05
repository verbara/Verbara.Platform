using System.Collections.Concurrent;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/supervisor").RequireAuthorization("SupervisorPlus");

        // Voice sessions
        group.MapGet("/sessions/active", GetActiveSessions);
        group.MapPost("/sessions/{sessionId}/whisper", PostWhisper);
        group.MapPost("/sessions/{sessionId}/listen", PostListen);

        // Digital conversations
        group.MapGet("/conversations", ListDigitalConversations);
        group.MapGet("/conversations/{id}/messages", GetConversationMessages);
        group.MapPost("/conversations/{id}/takeover", TakeoverConversation);
        group.MapPost("/conversations/{id}/close", ForceCloseConversation);
        group.MapPost("/conversations/{id}/note", SendCoachingNote);
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static IResult GetActiveSessions(
        IServiceProvider services)
    {
        var supervisor = services.GetService<AgentAssistSupervisor>();
        if (supervisor is null)
            return Results.Ok(Array.Empty<ActiveSessionDto>());

        var dtos = supervisor.ActiveSessions.Values
            .Select(s =>
            {
                var cs = s.CallSession;
                return new ActiveSessionDto(
                    SessionId: s.SessionId,
                    AgentId: cs?.AgentId,
                    QueueName: cs?.QueueName,
                    CallerIdNum: cs?.CallerIdNum,
                    ConnectedAt: cs?.ConnectedAt);
            })
            .ToArray();

        return Results.Ok(dtos);
    }

    private static IResult PostWhisper(
        string sessionId,
        [FromBody] WhisperRequest body,
        IServiceProvider services)
    {
        var supervisor = services.GetService<AgentAssistSupervisor>();
        if (supervisor is null || !supervisor.ActiveSessions.TryGetValue(sessionId, out var session))
            return Results.NotFound();

        var enqueued = session.TryEnqueueSupervisorWhisper(body.Text);
        if (!enqueued)
            return Results.BadRequest(new ErrorResponse("Whisper delivery is not enabled for this session."));

        return Results.Ok();
    }

    private static IResult PostListen(
        string sessionId,
        [FromBody] ListenRequest body,
        IServiceProvider services)
    {
        var supervisor = services.GetService<AgentAssistSupervisor>();
        if (supervisor is null || !supervisor.ActiveSessions.ContainsKey(sessionId))
            return Results.NotFound();

        var entry = new ListenEntry(body.SupervisorId, sessionId, DateTimeOffset.UtcNow);
        SupervisorListenStore.Sessions[sessionId + ":" + body.SupervisorId] = entry;

        return Results.Ok(entry);
    }

    // ─── Digital Conversation Monitoring ─────────────────────────────────────────

    private static async Task<IResult> ListDigitalConversations(
        HttpContext context,
        [FromServices] IConversationStore store,
        [FromQuery] string? queue,
        [FromQuery] string? agent,
        [FromQuery] string? channel,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);

        ConversationState? parsedState = null;
        if (state is not null && Enum.TryParse<ConversationState>(state, ignoreCase: true, out var s))
            parsedState = s;

        ChannelType? parsedChannel = null;
        if (channel is not null && Enum.TryParse<ChannelType>(channel, ignoreCase: true, out var ch))
            parsedChannel = ch;

        var query = new ConversationQuery
        {
            State = parsedState,
            AssignedAgentId = agent is not null ? EntityId.From(agent) : null,
            Channel = parsedChannel,
            Page = page,
            PageSize = pageSize,
        };

        var result = await store.ListAsync(tenantId, query, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetConversationMessages(
        string id,
        HttpContext context,
        [FromServices] IMessageStore messageStore,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var messages = await messageStore.GetConversationMessagesAsync(
            tenantId, EntityId.From(id), limit, offset, ct);
        return Results.Ok(messages);
    }

    private static async Task<IResult> TakeoverConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationSwitchboard switchboard,
        [FromServices] PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var supervisorId = context.User.FindFirst("sub")?.Value;
        if (supervisorId is null)
            return Results.Unauthorized();

        var result = await switchboard.TransferToAgentAsync(
            EntityId.From(id), tenantId, EntityId.From(supervisorId), ct);

        if (!result.Success)
            return Results.BadRequest(new ErrorResponse(result.FailureReason ?? "Takeover failed"));

        eventBus.Publish(new ConversationAssignedEvent(
            tenantId.Value, id, supervisorId, "", "", ""));

        return Results.Ok(result);
    }

    private static async Task<IResult> ForceCloseConversation(
        string id,
        [FromBody] SupervisorCloseRequest body,
        HttpContext context,
        [FromServices] IConversationLifecycleService lifecycleService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        await lifecycleService.CloseAsync(tenantId, EntityId.From(id), wrapUp: null, ct);
        return Results.Ok(new MessageResponse($"Conversation {id} closed"));
    }

    private static async Task<IResult> SendCoachingNote(
        string id,
        [FromBody] CoachingNoteRequest body,
        HttpContext context,
        [FromServices] IMessageStore messageStore,
        [FromServices] IClock clock,
        [FromServices] PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var supervisorId = context.User.FindFirst("sub")?.Value ?? "supervisor";

        if (string.IsNullOrWhiteSpace(body.Text))
            return Results.BadRequest(new ErrorResponse("Text is required"));

        // Create a system message visible only to the agent (Direction=System marks it as internal)
        var note = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = EntityId.From(id),
            TenantId = tenantId,
            Direction = MessageDirection.System,
            Channel = ChannelType.WebChat,
            SenderId = supervisorId,
            Content = new MessageEnvelope([new TextBlock(body.Text)]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = clock.UtcNow,
            CreatedBy = $"supervisor:{supervisorId}",
        };

        await messageStore.SaveAsync(note, ct);

        // Publish SSE event for the agent
        eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, id, note.MessageId.Value, "System", "Coaching"));

        return Results.Ok(new MessageResponse("Coaching note sent"));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── In-Memory Listen Store ────────────────────────────────────────────────────

internal static class SupervisorListenStore
{
    public static readonly ConcurrentDictionary<string, ListenEntry> Sessions = new();
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record ActiveSessionDto(
    string SessionId,
    string? AgentId,
    string? QueueName,
    string? CallerIdNum,
    DateTimeOffset? ConnectedAt);

internal sealed record WhisperRequest(string Text);

internal sealed record ListenRequest(string SupervisorId);

internal sealed record ListenEntry(
    string SupervisorId,
    string SessionId,
    DateTimeOffset StartedAt);

internal sealed record SupervisorCloseRequest(string? Reason);

internal sealed record CoachingNoteRequest(string Text);
