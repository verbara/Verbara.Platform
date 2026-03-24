using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.AgentAssist.Engine;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/supervisor").RequireAuthorization("SupervisorPlus");
        group.MapGet("/sessions/active", GetActiveSessions);
        group.MapPost("/sessions/{sessionId}/whisper", PostWhisper);
        group.MapPost("/sessions/{sessionId}/listen", PostListen);
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
        WhisperRequest body,
        IServiceProvider services)
    {
        var supervisor = services.GetService<AgentAssistSupervisor>();
        if (supervisor is null || !supervisor.ActiveSessions.TryGetValue(sessionId, out var session))
            return Results.NotFound();

        var enqueued = session.TryEnqueueSupervisorWhisper(body.Text);
        if (!enqueued)
            return Results.BadRequest(new { error = "Whisper delivery is not enabled for this session." });

        return Results.Ok();
    }

    private static IResult PostListen(
        string sessionId,
        ListenRequest body,
        IServiceProvider services)
    {
        var supervisor = services.GetService<AgentAssistSupervisor>();
        if (supervisor is null || !supervisor.ActiveSessions.ContainsKey(sessionId))
            return Results.NotFound();

        var entry = new ListenEntry(body.SupervisorId, sessionId, DateTimeOffset.UtcNow);
        SupervisorListenStore.Sessions[sessionId + ":" + body.SupervisorId] = entry;

        return Results.Ok(entry);
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
