using System.Collections.Concurrent;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.Realtime;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

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
            .RequireAuthorization("AdminOnly");

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
                m.Source == MembershipSource.Manual ? "Manual" : "Skill"));
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
        }, ct);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            await syncService.AddQueueMemberAsync(tenantId.Value, queue.Name,
                agent.AgentId.Value, agent.DisplayName, penalty, ct);
        }

        await audit.RecordAsync(
            tenantId, category: "config", action: "queue.members.added", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agent.AgentId.Value}", targetType: "queue_member",
            metadata: BuildMetadata(context, ("queueId", queueId), ("agentId", agent.AgentId.Value), ("penalty", penalty.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ct: ct);

        var dto = new QueueMemberDto(queueId, agent.AgentId.Value, agent.DisplayName,
            penalty, false, false, null, "Manual");
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

        await membershipStore.DeleteAsync(tenantId, queue.QueueId, agent.AgentId, ct);
        pauseTracker.Clear(tenantId.Value, queueId, agentId);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            await syncService.RemoveQueueMemberAsync(tenantId.Value, queue.Name, agent.AgentId.Value, ct);
        }

        await audit.RecordAsync(
            tenantId, category: "config", action: "queue.members.removed", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agentId}", targetType: "queue_member",
            metadata: BuildMetadata(context, ("queueId", queueId), ("agentId", agentId)),
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

        await membershipStore.SaveAsync(new QueueMembership
        {
            TenantId = tenantId,
            QueueId = queue.QueueId,
            AgentId = agent.AgentId,
            Penalty = newPenalty,
            Source = existing.Source,
            IsExcluded = newExcluded,
            CreatedAt = existing.CreatedAt,
        }, ct);

        if (penaltyChanged)
        {
            var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
            if (syncService is not null)
            {
                // AddQueueMemberAsync upserts — it updates the row in queue_members.
                await syncService.AddQueueMemberAsync(tenantId.Value, queue.Name,
                    agent.AgentId.Value, agent.DisplayName, newPenalty, ct);
            }
            await audit.RecordAsync(
                tenantId, category: "config", action: "queue.members.penalty_changed", severity: "info",
                actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
                targetId: $"{queueId}:{agentId}", targetType: "queue_member",
                changes: new AuditChanges(
                    Before: new { existing.Penalty },
                    After: new { Penalty = newPenalty }),
                metadata: BuildMetadata(context, ("queueId", queueId), ("agentId", agentId)),
                ct: ct);
        }

        var (isPaused, reason) = pauseTracker.Get(tenantId.Value, queueId, agentId);
        var dto = new QueueMemberDto(queueId, agent.AgentId.Value, agent.DisplayName,
            newPenalty, newExcluded, isPaused, reason,
            existing.Source == MembershipSource.Manual ? "Manual" : "Skill");
        return Results.Ok(dto);
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
            var logger = loggerFactory.CreateLogger("Asterisk.Platform.Api.QueueMembers");
            PauseResumeLog.RealtimeUnavailable(logger, paused ? "pause" : "resume",
                tenantId.Value, queueId, agentId);
        }

        var action = paused ? "queue.members.paused" : "queue.members.resumed";
        await audit.RecordAsync(
            tenantId, category: "config", action: action, severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: $"{queueId}:{agentId}", targetType: "queue_member",
            metadata: BuildMetadata(context,
                ("queueId", queueId), ("agentId", agentId),
                ("reason", reason ?? string.Empty),
                ("realtimeSynced", (syncService is not null).ToString().ToLowerInvariant())),
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
public sealed record QueueMemberDto(
    string QueueId,
    string AgentId,
    string DisplayName,
    int Penalty,
    bool IsExcluded,
    bool IsPaused,
    string? PauseReason,
    string Source);

internal sealed record AddMemberBody(string AgentId, int? Penalty = null);
internal sealed record UpdateMemberBody(int? Penalty, bool? IsExcluded);
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
