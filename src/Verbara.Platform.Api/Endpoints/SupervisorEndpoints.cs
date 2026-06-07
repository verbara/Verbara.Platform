using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/supervisor").RequireAuthorization("SupervisorPlus").RequireOperationalTenant();

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

        // W5 (A7) — stuck-work visibility + manual reassign
        group.MapGet("/conversations/stuck", GetStuckConversations);
        group.MapPost("/conversations/{id}/reassign", ReassignConversation);

        // W5b — voice-appropriate counterpart to reassign: re-arm the rescue of a
        // callback-stuck WrapUp voice conversation (the live call is gone, so it can't
        // be transferred to a queue).
        group.MapPost("/conversations/{id}/retry-callback", RetryCallback);
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

    // ─── W5 (A7) Stuck Work + Manual Reassign ────────────────────────────────────

    /// <summary>
    /// W5 (A7) — "stuck work" = conversations actively OWNED by an Offline agent in a
    /// failover-work state ({Active, OnHold, Consulting}). This includes the
    /// failover-escalated ones (still owned by the offline agent, marked
    /// <c>failoverStuck</c>) the automatic sweep could not re-queue. Reuses the existing
    /// offline-agent + failover-work-by-owner queries (N+1 over a tenant's offline agents
    /// is fine for a supervisor-initiated query).
    /// </summary>
    private static async Task<IResult> GetStuckConversations(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // Page through ALL offline agents (large page so a single call covers them).
        var offline = await agentStore.ListAsync(
            tenantId,
            new AgentQuery { State = AgentState.Offline, Page = 1, PageSize = 10_000 },
            ct);

        var stuck = new List<StuckConversationDto>();
        foreach (var agent in offline.Items)
        {
            var work = await conversationStore.ListFailoverWorkByOwnerAsync(tenantId, agent.AgentId, ct);
            foreach (var conv in work)
            {
                var attempts = conv.Metadata.TryGetValue("failoverAttempts", out var a)
                    && int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : 0;

                stuck.Add(new StuckConversationDto(
                    ConversationId: conv.ConversationId.Value,
                    Channel: conv.Channel.ToString(),
                    State: conv.State.ToString(),
                    OwnerAgentId: agent.AgentId.Value,
                    OwnerAgentName: agent.DisplayName,
                    OwnerOfflineSince: agent.OfflineSince,
                    FailoverAttempts: attempts,
                    Escalated: conv.Metadata.ContainsKey("failoverStuck")));
            }
        }

        // W5b — also surface voice conversations whose rescue callbacks exhausted the
        // retry cap. These live in WrapUp (NOT a failover-work state) so the loop above
        // never returns them: the two sets are DISJOINT (FailoverWorkStates vs WrapUp),
        // no dedup needed. The conv's Owner is the (now-dead) agent; resolve it best-
        // effort (the agent may have reconnected — OwnerOfflineSince stays nullable).
        foreach (var conv in await conversationStore.ListCallbackStuckAsync(tenantId, ct))
        {
            var attempts = conv.Metadata.TryGetValue("callbackAttempts", out var a)
                && int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : 0;

            string ownerAgentId = string.Empty;
            string ownerAgentName = string.Empty;
            DateTimeOffset? ownerOfflineSince = null;
            if (conv.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId })
            {
                ownerAgentId = ownerId.Value;
                var owner = await agentStore.GetByIdAsync(tenantId, ownerId, ct);
                if (owner is not null)
                {
                    ownerAgentName = owner.DisplayName;
                    ownerOfflineSince = owner.OfflineSince;
                }
            }

            stuck.Add(new StuckConversationDto(
                ConversationId: conv.ConversationId.Value,
                Channel: conv.Channel.ToString(),
                State: conv.State.ToString(),
                OwnerAgentId: ownerAgentId,
                OwnerAgentName: ownerAgentName,
                OwnerOfflineSince: ownerOfflineSince,
                FailoverAttempts: attempts,
                Escalated: true));
        }

        IReadOnlyList<StuckConversationDto> result = stuck;
        return Results.Ok(result);
    }

    /// <summary>
    /// W5 (A7) — manual reassign of a stuck conversation to a queue OR an agent. Clears the
    /// W5 failover markers (<c>failoverAttempts</c>, <c>failoverStuck</c>) BEFORE the transfer
    /// so the transfer's own re-load+save carries the cleared state and a reassigned-then-
    /// re-orphaned conversation gets fresh failover treatment (the worker skips when
    /// <c>failoverStuck</c> is present, so it must be GONE, not "false").
    /// </summary>
    private static async Task<IResult> ReassignConversation(
        string id,
        [FromBody] ReassignConversationRequest body,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IConversationSwitchboard switchboard,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var supervisorId = GetCurrentUserId(context);

        var hasQueue = !string.IsNullOrWhiteSpace(body.TargetQueueId);
        var hasAgent = !string.IsNullOrWhiteSpace(body.TargetAgentId);
        if (hasQueue == hasAgent)   // both or neither
            return Results.BadRequest(new ErrorResponse(
                "Exactly one of targetQueueId or targetAgentId must be provided."));

        var convId = EntityId.From(id);
        var conv = await conversationStore.GetByIdAsync(tenantId, convId, ct);
        if (conv is null)
            return Results.NotFound();

        // Clear failover markers BEFORE the transfer so the transfer's re-load+save carries
        // the cleared state (setting them "false" is not enough — ContainsKey must go false).
        conv.RemoveMetadata("failoverAttempts");
        conv.RemoveMetadata("failoverStuck");
        await conversationStore.SaveAsync(conv, ct);

        OwnershipResult result;
        if (hasQueue)
            result = await switchboard.TransferToQueueAsync(convId, tenantId, EntityId.From(body.TargetQueueId!), ct);
        else
            result = await switchboard.TransferToAgentAsync(convId, tenantId, EntityId.From(body.TargetAgentId!), ct);

        if (!result.Success)
            return Results.BadRequest(new ErrorResponse(result.FailureReason ?? "Reassign failed"));

        // Best-effort audit (mirrors the automatic failover sweep's audit shape; a failed
        // audit write must not fail the supervisor's reassign).
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "conversations",
                action: "conversation.reassigned",
                severity: "info",
                actorId: supervisorId.Value,
                actorType: "user",
                targetId: convId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [hasQueue ? "target_queue" : "target_agent"] =
                        (hasQueue ? body.TargetQueueId : body.TargetAgentId)!,
                    ["by_supervisor"] = supervisorId.Value,
                },
                ct: ct);
        }
        catch
        {
            // Swallow — the reassign already succeeded; audit is advisory.
        }

        return Results.NoContent();
    }

    /// <summary>
    /// W5b — voice-appropriate counterpart to <see cref="ReassignConversation"/>. A WrapUp voice
    /// conversation whose rescue callbacks exhausted the retry cap (marked <c>callbackStuck</c>)
    /// can't be transferred to a queue — the live call is already gone — so the supervisor re-arms
    /// the AUTOMATIC rescue instead: clear <c>callbackStuck</c>, reset <c>callbackAttempts</c> to a
    /// fresh cap, and re-stamp <c>pendingCallbackEval</c>/<c>callbackEvalSince</c> so the
    /// CallbackRescueWorker re-evaluates after the grace and originates a fresh callback.
    /// </summary>
    private static async Task<IResult> RetryCallback(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAuditService audit,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var supervisorId = GetCurrentUserId(context);

        var convId = EntityId.From(id);
        var conv = await conversationStore.GetByIdAsync(tenantId, convId, ct);
        if (conv is null)
            return Results.NotFound();

        if (conv.Channel != ChannelType.Voice
            || conv.State != ConversationState.WrapUp
            || !conv.Metadata.ContainsKey("callbackStuck"))
        {
            return Results.BadRequest(new ErrorResponse("Conversation is not a stuck voice rescue."));
        }

        // Re-arm the automatic rescue: drop the stuck marker, reset the cap, and re-stamp the
        // pending-eval markers so the CallbackRescueWorker picks it up after the grace.
        conv.RemoveMetadata("callbackStuck");
        conv.SetMetadata("callbackAttempts", "0");
        conv.SetMetadata("pendingCallbackEval", "true");
        conv.SetMetadata("callbackEvalSince", clock.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await conversationStore.SaveAsync(conv, ct);

        // Best-effort audit (mirrors ReassignConversation's shape; a failed audit write must not
        // fail the supervisor's re-arm).
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "conversations",
                action: "conversation.callback.retry_requested",
                severity: "info",
                actorId: supervisorId.Value,
                actorType: "user",
                targetId: id,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["by_supervisor"] = supervisorId.Value,
                },
                ct: ct);
        }
        catch
        {
            // Swallow — the re-arm already succeeded; audit is advisory.
        }

        return Results.Ok();
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }

    /// <summary>
    /// Resolves the calling user's id. Mirrors AdminEndpoints.GetCurrentUserId: prefer the
    /// JWT <c>sub</c>, then the <c>user_id</c> claim (set by ApiKeyAuthenticationHandler when
    /// the key has a linked user), then NameIdentifier.
    /// </summary>
    private static EntityId GetCurrentUserId(HttpContext context)
    {
        var nameId = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
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

// W5 (A7) — stuck-work list item: a conversation actively owned by an offline agent.
internal sealed record StuckConversationDto(
    string ConversationId,
    string Channel,
    string State,
    string OwnerAgentId,
    string OwnerAgentName,
    DateTimeOffset? OwnerOfflineSince,
    int FailoverAttempts,
    bool Escalated);

// W5 (A7) — manual reassign body: EXACTLY ONE of the two targets must be set.
internal sealed record ReassignConversationRequest(string? TargetQueueId, string? TargetAgentId);
