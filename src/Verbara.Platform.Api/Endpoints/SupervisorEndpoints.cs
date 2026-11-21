using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/supervisor").RequireAuthorization("SupervisorPlus").RequireOperationalTenant();

        // Voice sessions
        group.MapGet("/sessions/active", GetActiveSessions)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapPost("/sessions/{sessionId}/whisper", PostWhisper)
            .WithSummary("Sends a supervisor's text whisper to the session's agent")
            .WithDescription(
                "The text whisper: delivers the text to the Agent Assist session's bound agent as an agentassist.suggestion " +
                "with source Supervisor and priority Critical, from any Api replica, and answers 204 No Content. The " +
                "session is looked up in the caller's tenant only: another tenant's, an ended or an unknown session " +
                "answers 404. 400 whisper-text-invalid: the text is empty or longer than 500 characters after trimming. " +
                "409 whisper-session-not-bound: the session's agent or conversation is not resolved yet; nothing was " +
                "published. The Realtime hub's WhisperToAgentAsync, StartSupervisionAsync and StopSupervisingAsync stay " +
                "refused; this route is the only whisper.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        group.MapPost("/sessions/{sessionId}/listen", PostListen)
            .Produces(StatusCodes.Status403Forbidden);

        // Digital conversations
        group.MapGet("/conversations", ListDigitalConversations)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapGet("/conversations/{id}/messages", GetConversationMessages)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{id}/takeover", TakeoverConversation);
        group.MapPost("/conversations/{id}/close", ForceCloseConversation);
        group.MapPost("/conversations/{id}/note", SendCoachingNote);

        // W5 (A7) — stuck-work visibility + manual reassign
        group.MapGet("/conversations/stuck", GetStuckConversations)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapPost("/conversations/{id}/reassign", ReassignConversation);

        // W5b — voice-appropriate counterpart to reassign: re-arm the rescue of a
        // callback-stuck WrapUp voice conversation (the live call is gone, so it can't
        // be transferred to a queue).
        group.MapPost("/conversations/{id}/retry-callback", RetryCallback);

        // supervisor-silent-listen (design D12, D13) — the silent AUDIO listen, keyed by conversation id; distinct
        // from the Agent Assist transcript routes above, which are keyed by the Agent Assist session id.
        group.MapSupervisorListenRoutes();
    }

    // ─── Handlers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// design D2: the tenant-wide supervisor reads (the conversation list, stuck work and active sessions) show other
    /// agents' work, so they answer only a caller that reads every conversation of the tenant (the conversation read
    /// rule's <see cref="ConversationReaderClass.ReadsAll"/>). Returns the 403 <c>not-readable</c> refusal otherwise,
    /// or <see langword="null"/> when the caller may read. Called before any store or session read.
    /// </summary>
    private static async Task<JsonHttpResult<ErrorResponse>?> RefuseUnlessReadsAllAsync(
        HttpContext context, TenantId tenantId, IAgentStore agents, CancellationToken ct)
    {
        var readerClass = await ConversationReaderClass.ResolveAsync(context, tenantId, agents, ct);
        return readerClass.ReadsAll ? null : ConversationReadRule.Refusal();
    }

    private static async Task<Results<Ok<ActiveSessionDto[]>, JsonHttpResult<ErrorResponse>>> GetActiveSessions(
        HttpContext context,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        [FromServices] IAgentAssistLiveSessionStore liveSessionStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await RefuseUnlessReadsAllAsync(context, tenantId, agents, ct) is { } refusal)
            return refusal;

        // agent-assist-works-end-to-end (design D12): the records filed under the caller's tenant, shared by every replica
        // when Redis is configured; this replica's own sessions otherwise.
        var records = await liveSessionStore.ListAsync(tenantId.Value, ct);
        return TypedResults.Ok(await ToActiveSessionsAsync(records, tenantId, conversations, agents, ct));
    }

    /// <summary>
    /// design D1, D3: the live Agent Assist sessions owned by <paramref name="callerTenant"/> (ordinal). A session whose
    /// owner tenant is unresolved or conflicted owns nothing and is kept for no caller. Every live-session route reads
    /// through this before anything else, so another tenant's session answers exactly as an unknown one.
    /// </summary>
    private static List<LiveAssistSession> LiveSessionsOf(IAgentAssistLiveSessions liveSessions, TenantId callerTenant) =>
        [.. liveSessions.Snapshot().Where(s => string.Equals(s.OwnerTenant, callerTenant.Value, StringComparison.Ordinal))];

    private static bool IsLiveSessionOf(IAgentAssistLiveSessions liveSessions, TenantId callerTenant, string sessionId) =>
        LiveSessionsOf(liveSessions, callerTenant).Exists(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));

    /// <summary>
    /// supervisor-silent-listen (boundary gap 1, design D13): the active-session items, each with the id of the tracked
    /// voice conversation of its call in <paramref name="callerTenant"/> — resolved by the call's linked id, the same
    /// lookup <c>AgentAssistBridge</c> runs — or null when none resolves there. agent-assist-works-end-to-end (design
    /// D13): each also names its agent's display name in <paramref name="callerTenant"/>, or null when the record has no
    /// agent id or no agent row resolves there.
    /// </summary>
    internal static async Task<ActiveSessionDto[]> ToActiveSessionsAsync(
        IReadOnlyList<LiveSessionRecord> sessions,
        TenantId callerTenant,
        IConversationStore conversations,
        IAgentStore agents,
        CancellationToken ct)
    {
        var agentNames = await AgentNamesAsync(sessions, callerTenant, agents, ct);
        var dtos = new ActiveSessionDto[sessions.Count];
        for (var i = 0; i < sessions.Count; i++)
        {
            var record = sessions[i];
            string? conversationId = null;
            if (!string.IsNullOrEmpty(record.LinkedId))
                conversationId = (await conversations.FindByVoiceLinkedIdAsync(callerTenant, record.LinkedId, ct))?.ConversationId.Value;

            dtos[i] = new ActiveSessionDto(
                SessionId: record.SessionId,
                AgentId: record.AgentId,
                AgentName: record.AgentId is { } agentId ? agentNames.GetValueOrDefault(agentId) : null,
                QueueName: record.QueueName,
                CallerIdNum: record.CallerIdNum,
                ConnectedAt: record.ConnectedAt,
                ConversationId: conversationId,
                Sentiment: record.Sentiment);
        }

        return dtos;
    }

    /// <summary>The display name of each agent the records name, read in <paramref name="callerTenant"/> in one query.</summary>
    private static async Task<Dictionary<string, string>> AgentNamesAsync(
        IReadOnlyList<LiveSessionRecord> sessions, TenantId callerTenant, IAgentStore agents, CancellationToken ct)
    {
        var ids = sessions
            .Select(s => s.AgentId)
            .Where(EntityId.IsValid)
            .Distinct(StringComparer.Ordinal)
            .Select(id => EntityId.From(id!))
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var rows = await agents.GetByIdsAsync(callerTenant, ids, ct);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agent in rows.Where(a => a.TenantId == callerTenant))
            names[agent.AgentId.Value] = agent.DisplayName;
        return names;
    }

    /// <summary>
    /// supervisor-whisper-reaches-the-agent (design D2-D7): the text whisper. In this order: the session is looked up in
    /// the shared live-session store under the caller's tenant (404, no body, when not found, before the text is read);
    /// the text is validated (400 <c>whisper-text-invalid</c>); the session's agent, linked id and conversation must be
    /// resolved (409 <c>whisper-session-not-bound</c>); then one <c>agentassist.suggestion</c> is published, the spoken
    /// whisper is attempted best effort, and the answer is 204 No Content. Every refusal publishes nothing. A publish that
    /// throws is not caught (500), and nothing is retried: a retry would hand a second copy to the subscribers that ran.
    /// The spoken attempt runs only for a session this replica runs and the caller's tenant owns as it is now (the owner
    /// rule of <see cref="IsLiveSessionOf"/>), so a record still filed under a former owner reaches no audio; a spoken
    /// attempt that throws is logged and ignored, so 204 always means published.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> PostWhisper(
        HttpContext context,
        string sessionId,
        [FromBody] WhisperRequest body,
        [FromServices] IAgentAssistLiveSessionStore liveSessionStore,
        [FromServices] IConversationStore conversations,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IAgentAssistLiveSessions liveSessions,
        [FromServices] ILoggerFactory loggers,
        CancellationToken ct)
    {
        var callerTenant = GetTenantId(context);
        if (await liveSessionStore.FindAsync(callerTenant.Value, sessionId, ct) is not { } record)
            return TypedResults.NotFound();

        var text = body.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > SupervisorWhisper.MaxTextLength)
            return TypedResults.BadRequest(new ErrorResponse(WhisperTextInvalid));

        if (string.IsNullOrEmpty(record.AgentId) || string.IsNullOrEmpty(record.LinkedId))
            return TypedResults.Conflict(new ErrorResponse(WhisperSessionNotBound));
        var conversation = await conversations.FindByVoiceLinkedIdAsync(callerTenant, record.LinkedId, ct);
        if (conversation is null || string.IsNullOrEmpty(conversation.ConversationId.Value))
            return TypedResults.Conflict(new ErrorResponse(WhisperSessionNotBound));

        eventBus.Publish(SupervisorWhisper.BuildEvent(
            record, conversation.ConversationId.Value, text, SupervisorWhisper.NewSuggestionId()));

        // design D6: the spoken whisper succeeds only on the engine's replica with Pro's spoken whisper enabled.
        if (IsLiveSessionOf(liveSessions, callerTenant, record.SessionId))
        {
            try
            {
                _ = liveSessions.TryWhisper(record.SessionId, text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSpokenWhisperFailed(loggers.CreateLogger(typeof(SupervisorEndpoints).FullName!), ex, record.SessionId, callerTenant.Value);
            }
        }

        return TypedResults.NoContent();
    }

    [LoggerMessage(EventId = 7610, Level = LogLevel.Warning,
        Message = "The spoken whisper for session {SessionId} in tenant {TenantId} failed; the text whisper was published")]
    private static partial void LogSpokenWhisperFailed(ILogger logger, Exception exception, string sessionId, string tenantId);

    private const string WhisperTextInvalid = "whisper-text-invalid";
    private const string WhisperSessionNotBound = "whisper-session-not-bound";

    /// <summary>
    /// design D5: records the signed-in caller as the listening supervisor. The body is optional and its
    /// <c>SupervisorId</c> is ignored; credentials with no user id are refused with a bodyless 403 before anything is
    /// recorded. Entries are keyed by tenant, session and user, so each tenant's listens stay apart.
    /// </summary>
    private static Results<Ok<ListenEntry>, NotFound, StatusCodeHttpResult> PostListen(
        HttpContext context,
        string sessionId,
        [FromBody] ListenRequest? body,
        [FromServices] IAgentAssistLiveSessions liveSessions)
    {
        _ = body;
        if (AgentAssistCaller.CallerUserIdOf(context.User) is not { } userId)
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);

        var tenantId = GetTenantId(context);
        if (!IsLiveSessionOf(liveSessions, tenantId, sessionId))
            return TypedResults.NotFound();

        var entry = new ListenEntry(userId, sessionId, DateTimeOffset.UtcNow);
        SupervisorListenStore.Sessions[$"{tenantId.Value}:{sessionId}:{userId}"] = entry;

        return TypedResults.Ok(entry);
    }

    // ─── Digital Conversation Monitoring ─────────────────────────────────────────

    private static async Task<Results<Ok<PagedResult<Conversation>>, JsonHttpResult<ErrorResponse>>> ListDigitalConversations(
        HttpContext context,
        [FromServices] IConversationStore store,
        [FromServices] IAgentStore agents,
        [FromQuery] string? queue,
        [FromQuery] string? agent,
        [FromQuery] string? channel,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        if (await RefuseUnlessReadsAllAsync(context, tenantId, agents, ct) is { } refusal)
            return refusal;

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
        return TypedResults.Ok(result);
    }

    // design D1: the same transcript read as GET /conversations/{id}/messages — 404 for an id missing from the
    // tenant, then the conversation read rule (403 not-readable), then the page, oldest first.
    private static Task<Results<Ok<IReadOnlyList<Message>>, NotFound, JsonHttpResult<ErrorResponse>>> GetConversationMessages(
        string id,
        HttpContext context,
        [FromServices] IMessageStore messageStore,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        [FromServices] IContactStore contacts,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ConversationEndpoints.ReadTranscriptAsync(id, context, messageStore, conversations, agents, contacts, limit, offset, ct);

    // The supervisor's own Agent profile becomes the owner, as for any owner: a supervisor without one
    // cannot own a conversation. The switchboard announces the assignment; the takeover is audited.
    private static async Task<Results<Ok<OwnershipResult>, NotFound, JsonHttpResult<ErrorResponse>>> TakeoverConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent_id"] = actor.Agent.AgentId.Value,
        };
        ConversationActor.AddOwner(metadata, "previous_owner", conversation.Owner);

        // licensed-agent-metering (design D5) — the owner change commits with its conversation_taken_over row.
        var result = await switchboard.TransferToAgentAsync(
            conversation.ConversationId, tenantId, actor.Agent.AgentId,
            new OwnershipChange(OwnershipChangeKind.TakenOver, CallerIdentity.ResolveUserIdOrSystem(context.User)), ct);
        if (!result.Success)
            return ConversationActor.Conflict(result.FailureReason ?? "Takeover failed");

        await ConversationAudit.TryRecordAsync(
            context, audit, tenantId, "conversation.taken_over", conversation.ConversationId, metadata, ct);

        return TypedResults.Ok(result);
    }

    private static async Task<Ok<MessageResponse>> ForceCloseConversation(
        string id,
        [FromBody] SupervisorCloseRequest body,
        HttpContext context,
        [FromServices] IConversationLifecycleService lifecycleService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        await lifecycleService.CloseAsync(tenantId, EntityId.From(id), ct);
        return TypedResults.Ok(new MessageResponse($"Conversation {id} closed"));
    }

    private static async Task<Results<Ok<MessageResponse>, BadRequest<ErrorResponse>>> SendCoachingNote(
        string id,
        [FromBody] CoachingNoteRequest body,
        HttpContext context,
        [FromServices] IMessageStore messageStore,
        [FromServices] IConversationStore conversations,
        [FromServices] IClock clock,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] ILoggerFactory loggers,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var supervisorId = context.User.FindFirst("sub")?.Value ?? "supervisor";

        if (string.IsNullOrWhiteSpace(body.Text))
            return TypedResults.BadRequest(new ErrorResponse("Text is required"));

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

        // Publish SSE event for the conversation's agent: its owner or open-offer agent as it is now, read after the note
        // is stored (contact-history-opens-by-assignment, design D17). An unknown id keeps this route's behaviour and
        // publishes with an empty audience (readers only).
        var audience = await ConversationAudienceRead.CurrentAsync(
            conversations, loggers, tenantId, note.ConversationId, CancellationToken.None);
        eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, id, note.MessageId.Value, "System", "Coaching") { Audience = audience });

        return TypedResults.Ok(new MessageResponse("Coaching note sent"));
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
    private static async Task<Results<Ok<IReadOnlyList<StuckConversationDto>>, JsonHttpResult<ErrorResponse>>> GetStuckConversations(
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IConversationStore conversationStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await RefuseUnlessReadsAllAsync(context, tenantId, agentStore, ct) is { } refusal)
            return refusal;

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
        return TypedResults.Ok(result);
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
        [FromServices] IAgentStore agentStore,
        [FromServices] IAgentAccountStatusLookup accountStatus,
        [FromServices] IQueueStore queueStore,
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

        // The target must exist in this tenant, checked before anything is changed: a target that
        // names nothing would strand the conversation with no one able to work it.
        if (hasQueue && await queueStore.GetByIdAsync(tenantId, EntityId.From(body.TargetQueueId!), ct) is null)
            return Results.BadRequest(new ErrorResponse(ConversationActor.TargetQueueNotFound));
        if (hasAgent && !await ConversationActor.IsOwnableTargetAsync(
                agentStore, accountStatus, tenantId, EntityId.From(body.TargetAgentId!), ct))
            return Results.BadRequest(new ErrorResponse(ConversationActor.TargetAgentNotFound));

        // Clear failover markers BEFORE the transfer so the transfer's re-load+save carries
        // the cleared state (setting them "false" is not enough — ContainsKey must go false).
        conv.RemoveMetadata("failoverAttempts");
        conv.RemoveMetadata("failoverStuck");
        await conversationStore.SaveAsync(conv, ct);

        OwnershipResult result;
        if (hasQueue)
            result = await switchboard.TransferToQueueAsync(convId, tenantId, EntityId.From(body.TargetQueueId!), ct);
        else
            result = await switchboard.TransferToAgentAsync(
                convId, tenantId, EntityId.From(body.TargetAgentId!),
                new OwnershipChange(OwnershipChangeKind.Reassigned, supervisorId.Value), ct);

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

        // 204 (not Ok()) so the empty body doesn't trip customFetch's response.json() — matches
        // the proven-safe ReassignConversation sibling the Web client already consumes.
        return Results.NoContent();
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

// agent-assist-works-end-to-end (fixtures/active-session.v2.json, design D13): every member is always written.
// agentId is the agent the session's live events name, agentName its display name in the caller's tenant,
// conversationId the tracked voice conversation of the session's call in the caller's tenant and sentiment the label of
// the session's latest caller sentiment reading; each is null when unknown.
internal sealed record ActiveSessionDto(
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AgentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AgentName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? QueueName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CallerIdNum,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ConnectedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConversationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sentiment);

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
