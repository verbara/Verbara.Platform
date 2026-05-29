using System.Collections.Concurrent;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// RESTful queue-member endpoints nested under <c>/queues/{queueId}/members</c>.
/// Replaces the legacy <c>/admin/queue-members</c> endpoints — those remain as 308 redirects
/// for backward compatibility (see <see cref="AdminEndpoints"/>).
/// </summary>
internal static partial class QueueMembersEndpoints
{
    public static void MapQueueMembersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/queues/{queueId}/members")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        group.MapGet("/", ListMembers);
        group.MapPost("/", AddMember);
        group.MapDelete("/{agentId}", RemoveMember);
        group.MapPatch("/{agentId}", UpdateMember);
        group.MapPost("/{agentId}/pause", PauseMember);
        group.MapPost("/{agentId}/resume", ResumeMember);
    }

    // ─── GET /queues/{queueId}/members ────────────────────────────────────────

    private static async Task<IResult> ListMembers(
        string queueId,
        HttpContext context,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] QueueMemberPauseTracker pauseTracker,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        if (queue is null) return Results.NotFound();

        var memberships = await membershipStore.ListByQueueAsync(tenantId, queue.QueueId, ct);
        var result = new List<QueueMemberDto>(memberships.Count);
        foreach (var m in memberships)
        {
            var agent = await agentStore.GetByIdAsync(tenantId, m.AgentId, ct);
            if (agent is null) continue;
            var (isPaused, reason) = pauseTracker.Get(tenantId.Value, queueId, m.AgentId.Value);
            result.Add(new QueueMemberDto(
                queueId,
                m.AgentId.Value,
                agent.DisplayName,
                m.Penalty,
                m.IsExcluded,
                isPaused,
                reason,
                m.Source == MembershipSource.Manual ? "Manual" : "Skill",
                m.AllowedChannels));
        }
        return Results.Ok(result);
    }

    // ─── POST /queues/{queueId}/members ───────────────────────────────────────

    private static async Task<IResult> AddMember(
        string queueId,
        HttpContext context,
        [FromBody] AddMemberBody body,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(body.AgentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        // ADR-0026 Phase A.6 — validate AllowedChannels (null = all channels,
        // populated list restricts to listed channels, empty list = invalid).
        if (ValidateChannels(body.AllowedChannels) is { ok: false, error: var addErr })
            return Results.BadRequest(addErr);

        var penalty = Math.Clamp(body.Penalty ?? 0, 0, 10);
        await membershipStore.SaveAsync(new QueueMembership
        {
            TenantId = tenantId,
            QueueId = queue.QueueId,
            AgentId = agent.AgentId,
            Penalty = penalty,
            Source = MembershipSource.Manual,
            IsExcluded = false,
            CreatedAt = DateTimeOffset.UtcNow,
            AllowedChannels = body.AllowedChannels,
        }, ct);

        // ADR-0026 Phase A.6 — Asterisk sync gate: only sync to queue_members
        // if this membership covers voice (AllowedChannels null = all channels,
        // or list contains "voice" case-insensitively).
        var syncedToAsterisk = false;
        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null && IncludesVoice(body.AllowedChannels))
        {
            await syncService.AddQueueMemberAsync(tenantId.Value, queue.Name,
                agent.AgentId.Value, agent.DisplayName, penalty, ct);
            syncedToAsterisk = true;
        }

        await audit.RecordAsync(
            tenantId, category: "config", action: "queue.members.added", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agent.AgentId.Value}", targetType: "queue_member",
            metadata: BuildMetadata(context,
                ("queueId", queueId),
                ("agentId", agent.AgentId.Value),
                ("penalty", penalty.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("allowedChannels", body.AllowedChannels is null ? "all" : string.Join(",", body.AllowedChannels)),
                ("asteriskSynced", syncedToAsterisk.ToString().ToLowerInvariant())),
            ct: ct);

        var dto = new QueueMemberDto(queueId, agent.AgentId.Value, agent.DisplayName,
            penalty, false, false, null, "Manual", body.AllowedChannels);
        return Results.Created($"/api/v1/queues/{queueId}/members/{agent.AgentId.Value}", dto);
    }

    // ─── DELETE /queues/{queueId}/members/{agentId} ───────────────────────────

    private static async Task<IResult> RemoveMember(
        string queueId,
        string agentId,
        HttpContext context,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] QueueMemberPauseTracker pauseTracker,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(agentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        // ADR-0026 Phase A.6 — only call Asterisk RemoveQueueMember if this
        // membership previously had voice (null=all OR explicit voice). For a
        // digital-only membership the row never existed in queue_members.
        var existing = await membershipStore.GetAsync(tenantId, queue.QueueId, agent.AgentId, ct);
        await membershipStore.DeleteAsync(tenantId, queue.QueueId, agent.AgentId, ct);
        pauseTracker.Clear(tenantId.Value, queueId, agentId);

        var syncedToAsterisk = false;
        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null && IncludesVoice(existing?.AllowedChannels))
        {
            await syncService.RemoveQueueMemberAsync(tenantId.Value, queue.Name, agent.AgentId.Value, ct);
            syncedToAsterisk = true;
        }

        await audit.RecordAsync(
            tenantId, category: "config", action: "queue.members.removed", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agentId}", targetType: "queue_member",
            metadata: BuildMetadata(context,
                ("queueId", queueId),
                ("agentId", agentId),
                ("asteriskSynced", syncedToAsterisk.ToString().ToLowerInvariant())),
            ct: ct);
        return Results.NoContent();
    }

    // ─── PATCH /queues/{queueId}/members/{agentId} ────────────────────────────

    private static async Task<IResult> UpdateMember(
        string queueId,
        string agentId,
        HttpContext context,
        [FromBody] UpdateMemberBody body,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] QueueMemberPauseTracker pauseTracker,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(agentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        var existing = await membershipStore.GetAsync(tenantId, queue.QueueId, agent.AgentId, ct);
        if (existing is null) return Results.NotFound();

        var newPenalty = body.Penalty.HasValue
            ? Math.Clamp(body.Penalty.Value, 0, 10)
            : existing.Penalty;
        var newExcluded = body.IsExcluded ?? existing.IsExcluded;
        var penaltyChanged = body.Penalty.HasValue && newPenalty != existing.Penalty;

        // ADR-0026 Phase A.6 — channel-aware PATCH semantics:
        // - ClearAllowedChannels=true → reset to null (= all channels)
        // - AllowedChannels populated → replace (validated)
        // - both omitted → preserve existing
        IReadOnlyList<string>? newAllowedChannels;
        bool channelsChanged;
        if (body.ClearAllowedChannels == true)
        {
            newAllowedChannels = null;
            channelsChanged = existing.AllowedChannels is not null;
        }
        else if (body.AllowedChannels is not null)
        {
            if (ValidateChannels(body.AllowedChannels) is { ok: false, error: var chErr })
                return Results.BadRequest(chErr);
            newAllowedChannels = body.AllowedChannels;
            channelsChanged = !ChannelListsEqual(existing.AllowedChannels, body.AllowedChannels);
        }
        else
        {
            newAllowedChannels = existing.AllowedChannels;
            channelsChanged = false;
        }

        await membershipStore.SaveAsync(new QueueMembership
        {
            TenantId = tenantId,
            QueueId = queue.QueueId,
            AgentId = agent.AgentId,
            Penalty = newPenalty,
            Source = existing.Source,
            IsExcluded = newExcluded,
            CreatedAt = existing.CreatedAt,
            AllowedChannels = newAllowedChannels,
        }, ct);

        // Asterisk sync diff: voice add/remove based on channel-include diff.
        var existingIncludesVoice = IncludesVoice(existing.AllowedChannels);
        var newIncludesVoice = IncludesVoice(newAllowedChannels);
        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            if (!existingIncludesVoice && newIncludesVoice)
            {
                // Voice added → upsert in queue_members.
                await syncService.AddQueueMemberAsync(tenantId.Value, queue.Name,
                    agent.AgentId.Value, agent.DisplayName, newPenalty, ct);
            }
            else if (existingIncludesVoice && !newIncludesVoice)
            {
                // Voice removed → delete row in queue_members.
                await syncService.RemoveQueueMemberAsync(tenantId.Value, queue.Name, agent.AgentId.Value, ct);
            }
            else if (newIncludesVoice && penaltyChanged)
            {
                // Voice retained, penalty changed → upsert to update penalty.
                await syncService.AddQueueMemberAsync(tenantId.Value, queue.Name,
                    agent.AgentId.Value, agent.DisplayName, newPenalty, ct);
            }
        }

        if (penaltyChanged || channelsChanged || body.IsExcluded.HasValue)
        {
            await audit.RecordAsync(
                tenantId, category: "config", action: "queue.members.updated", severity: "info",
                actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
                targetId: $"{queueId}:{agentId}", targetType: "queue_member",
                changes: new AuditChanges(
                    Before: new QueueMemberUpdateAudit(existing.Penalty, existing.IsExcluded,
                        existing.AllowedChannels is null ? "all" : string.Join(",", existing.AllowedChannels)),
                    After: new QueueMemberUpdateAudit(newPenalty, newExcluded,
                        newAllowedChannels is null ? "all" : string.Join(",", newAllowedChannels))),
                metadata: BuildMetadata(context, ("queueId", queueId), ("agentId", agentId)),
                ct: ct);
        }

        var (isPaused, reason) = pauseTracker.Get(tenantId.Value, queueId, agentId);
        var dto = new QueueMemberDto(queueId, agent.AgentId.Value, agent.DisplayName,
            newPenalty, newExcluded, isPaused, reason,
            existing.Source == MembershipSource.Manual ? "Manual" : "Skill",
            newAllowedChannels);
        return Results.Ok(dto);
    }

    // ─── ADR-0026 Phase A.6 helpers ──────────────────────────────────────────

    private static (bool ok, string? error) ValidateChannels(IReadOnlyList<string>? channels)
    {
        if (channels is null) return (true, null);
        if (channels.Count == 0) return (false,
            "AllowedChannels must be null (= all channels) or contain at least one channel. " +
            "Empty arrays are not allowed; use IsExcluded=true for that semantic.");
        foreach (var ch in channels)
        {
            if (!Enum.TryParse<ChannelType>(ch, ignoreCase: true, out _))
                return (false, $"Unknown channel: '{ch}'. Allowed values match ChannelType enum " +
                    "(Voice, WhatsApp, Sms, WebChat, Email, Messenger, Instagram, Telegram, Twitter, Video, Rcs).");
        }
        return (true, null);
    }

    private static bool IncludesVoice(IReadOnlyList<string>? channels)
        => channels is null || channels.Any(c => c.Equals("voice", StringComparison.OrdinalIgnoreCase));

    private static bool ChannelListsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        var aSet = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return b.All(aSet.Contains);
    }

    // ─── POST /queues/{queueId}/members/{agentId}/pause ───────────────────────

    private static async Task<IResult> PauseMember(
        string queueId,
        string agentId,
        HttpContext context,
        [FromBody] PauseMemberBody? body,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] QueueMemberPauseTracker pauseTracker,
        [FromServices] IAuditService audit,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        return await SetPausedAsync(
            queueId, agentId, paused: true, reason: body?.Reason,
            context, queueStore, agentStore, membershipStore, pauseTracker, audit, loggerFactory, ct);
    }

    // ─── POST /queues/{queueId}/members/{agentId}/resume ──────────────────────

    private static async Task<IResult> ResumeMember(
        string queueId,
        string agentId,
        HttpContext context,
        [FromServices] IQueueStore queueStore,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] QueueMemberPauseTracker pauseTracker,
        [FromServices] IAuditService audit,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        return await SetPausedAsync(
            queueId, agentId, paused: false, reason: null,
            context, queueStore, agentStore, membershipStore, pauseTracker, audit, loggerFactory, ct);
    }

    private static async Task<IResult> SetPausedAsync(
        string queueId, string agentId, bool paused, string? reason,
        HttpContext context,
        IQueueStore queueStore, IAgentStore agentStore,
        IQueueMembershipStore membershipStore, QueueMemberPauseTracker pauseTracker,
        IAuditService audit, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(agentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        var existing = await membershipStore.GetAsync(tenantId, queue.QueueId, agent.AgentId, ct);
        if (existing is null) return Results.NotFound();

        pauseTracker.Set(tenantId.Value, queueId, agentId, paused, reason);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            await syncService.SyncAgentPausedAsync(tenantId.Value, agent.AgentId.Value, paused, ct);
        }
        else
        {
            var logger = loggerFactory.CreateLogger("Verbara.Platform.Api.QueueMembers");
            PauseResumeLog.RealtimeUnavailable(logger, paused ? "pause" : "resume",
                tenantId.Value, queueId, agentId);
        }

        var action = paused ? "queue.members.paused" : "queue.members.resumed";
        // Resume never carries a reason — omit the key entirely so audit metadata stays
        // meaningful. Pauses include the reason when supplied.
        var extras = paused && reason is not null
            ? new (string, string)[]
              {
                  ("queueId", queueId), ("agentId", agentId),
                  ("reason", reason),
                  ("realtimeSynced", (syncService is not null).ToString().ToLowerInvariant()),
              }
            : new (string, string)[]
              {
                  ("queueId", queueId), ("agentId", agentId),
                  ("realtimeSynced", (syncService is not null).ToString().ToLowerInvariant()),
              };
        await audit.RecordAsync(
            tenantId, category: "config", action: action, severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agentId}", targetType: "queue_member",
            metadata: BuildMetadata(context, extras),
            ct: ct);

        return Results.Ok(new PauseResultDto(queueId, agentId, paused, reason,
            RealtimeSynced: syncService is not null));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static Dictionary<string, string> BuildMetadata(
        HttpContext context, params (string Key, string Value)[] extras)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ["endpoint"] = context.Request.Path.Value ?? "",
        };
        foreach (var (k, v) in extras)
        {
            dict[k] = v;
        }
        return dict;
    }

    private static partial class PauseResumeLog
    {
        [LoggerMessage(
            EventId = 2700,
            Level = LogLevel.Warning,
            Message = "[queue.members] Realtime sync unavailable — {Action} persisted to DB only for tenant={TenantId} queue={QueueId} agent={AgentId}")]
        public static partial void RealtimeUnavailable(
            ILogger logger, string action, string tenantId, string queueId, string agentId);
    }
}

// ─── DTOs ────────────────────────────────────────────────────────────────────

/// <summary>Projected queue-member row exposed by <see cref="QueueMembersEndpoints"/>.</summary>
/// <param name="QueueId">Queue identifier.</param>
/// <param name="AgentId">Agent identifier.</param>
/// <param name="DisplayName">Agent display name projected for the UI.</param>
/// <param name="Penalty">Queue penalty (0-10); higher = lower priority.</param>
/// <param name="IsExcluded">True when the agent is excluded from this queue.</param>
/// <param name="IsPaused">
/// Per-queue pause state for the UI badge. BEST-EFFORT: maintained by an in-process
/// <see cref="QueueMemberPauseTracker"/>, not persisted to the queue_memberships table.
/// Authoritative pause state lives in Asterisk Realtime's <c>queue_members.paused</c> column.
/// Multi-instance deploys may see stale values on replicas that didn't originate the
/// pause. Scheduled for promotion to Postgres/Redis in R2 / R5.2.
/// </param>
/// <param name="PauseReason">Optional reason string associated with an active pause.</param>
/// <param name="Source">Membership source — <c>Manual</c> or <c>Skill</c>.</param>
/// <param name="AllowedChannels">
/// ADR-0026 Phase A.6 channel-aware membership. <c>null</c> means the agent
/// is a member for all channels the queue accepts (pre-v2.6.0 implicit
/// behavior). A populated list restricts membership to the listed channels
/// only and gates Asterisk sync (voice in list ⇒ sync; voice out ⇒ no sync).
/// </param>
public sealed record QueueMemberDto(
    string QueueId,
    string AgentId,
    string DisplayName,
    int Penalty,
    bool IsExcluded,
    bool IsPaused,
    string? PauseReason,
    string Source,
    IReadOnlyList<string>? AllowedChannels = null);

internal sealed record AddMemberBody(
    string AgentId,
    int? Penalty = null,
    IReadOnlyList<string>? AllowedChannels = null);

/// <summary>
/// PATCH body for queue-member updates. PATCH semantics for AllowedChannels:
/// <c>ClearAllowedChannels=true</c> resets to NULL (= all channels);
/// <c>AllowedChannels</c> populated replaces existing list; both omitted
/// preserves existing value.
/// </summary>
internal sealed record UpdateMemberBody(
    int? Penalty,
    bool? IsExcluded,
    IReadOnlyList<string>? AllowedChannels = null,
    bool? ClearAllowedChannels = null);

internal sealed record QueueMemberUpdateAudit(int Penalty, bool IsExcluded, string AllowedChannels);

internal sealed record PauseMemberBody(string? Reason);
internal sealed record PauseResultDto(string QueueId, string AgentId, bool IsPaused, string? Reason, bool RealtimeSynced);

// ─── Pause tracker (ephemeral, per-instance) ─────────────────────────────────

/// <summary>
/// Ephemeral tracker for per-queue-member pause state. Pause state is NOT persisted
/// — it's best-effort UI-facing data so clients can render "Paused" badges. The
/// authoritative pause state lives in Asterisk Realtime (<c>queue_members.paused</c>)
/// and is mutated by <see cref="IRealtimeSyncService.SyncAgentPausedAsync(string, string, bool, CancellationToken)"/>.
/// </summary>
internal sealed class QueueMemberPauseTracker
{
    private readonly ConcurrentDictionary<string, (bool Paused, string? Reason)> _state = new(StringComparer.Ordinal);

    public (bool IsPaused, string? Reason) Get(string tenantId, string queueId, string agentId)
    {
        var key = Key(tenantId, queueId, agentId);
        if (_state.TryGetValue(key, out var value))
            return (value.Paused, value.Reason);
        return (false, null);
    }

    public void Set(string tenantId, string queueId, string agentId, bool paused, string? reason)
    {
        var key = Key(tenantId, queueId, agentId);
        _state[key] = (paused, paused ? reason : null);
    }

    public void Clear(string tenantId, string queueId, string agentId)
        => _state.TryRemove(Key(tenantId, queueId, agentId), out _);

    private static string Key(string tenantId, string queueId, string agentId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{tenantId}:{queueId}:{agentId}");
}
