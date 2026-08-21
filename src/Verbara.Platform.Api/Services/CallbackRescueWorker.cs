using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// W5b — leader-gated background sweep that ORIGINATES a rescue callback to a customer whose
/// answered voice call dropped because the AGENT'S leg died mid-call (power/network loss). On
/// such a hangup the voice bridge (task A1) parks the conversation in <see cref="ConversationState.WrapUp"/>
/// and stamps <c>Metadata["pendingCallbackEval"] == "true"</c> plus the call context
/// (<c>agentLegAbnormal</c>, <c>callbackEvalSince</c>, <c>callbackNumber</c>, <c>originQueueId</c>).
/// This worker drains that set after the per-tenant grace and, for each call that is WORTHY of
/// rescue, dials the customer back into their ORIGINAL queue at the FRONT via
/// <see cref="ICallbackOriginator.OriginateCallbackAsync"/> (which pre-creates the new tracked
/// rescue conversation), then resolves the orphaned original.
///
/// <para><b>Grace + cancel-on-return:</b> the per-tenant
/// <see cref="TenantAuthConfig.VoiceCallbackGraceSeconds"/> (default 25) bounds how long after the
/// drop we wait before calling back. <c>&lt;= 0</c> disables voice rescue for the tenant (the
/// pending marker is simply cleared). The grace also gives a transient blip time to self-heal:
/// only after it elapses does the worthiness check run.</para>
///
/// <para><b>Worthiness:</b> a callback fires only when the agent leg ended ABNORMALLY
/// (<c>agentLegAbnormal == "true"</c>) OR — as a backstop for a "clean" hangup whose cause was
/// mis-classified — the owning agent is confirmed GONE (W3 liveness <see cref="IAgentLivenessStore"/>
/// reports not-alive, or the agent is <see cref="AgentState.Offline"/>). A clean end with the agent
/// still present is treated as a normal wrap-up (false positive) and the marker is cleared with no
/// callback.</para>
///
/// <para><b>Bounded retries (anti-loop):</b> <c>Metadata["callbackAttempts"]</c> is incremented and
/// persisted BEFORE each originate (so a crash mid-sweep can never loop forever). Past
/// <see cref="MaxCallbackAttempts"/> the conversation is marked <c>callbackStuck</c> and escalated
/// (a <c>warning</c> audit) instead of dialed again.</para>
///
/// <para><b>Leader-gated (ADR-0022 Phase A.5 pattern):</b> the sweep runs on every voice-capable pod
/// but only the elected leader for <see cref="CallbackRescueLeaderResources.Sweep"/> acts — a single
/// pod originates cluster-wide, honouring the AMI single-writer contract. Unlike the W3/W4/W5 digital
/// sweeps, voice is cluster-only (the whole voice/AMI stack registers solely inside the
/// cluster-connection branch), so this worker + its lease only exist on a clustered voice-AMI pod and
/// are always Postgres-lease-gated — there is no single-node path and no <c>AlwaysLeader</c> stub.</para>
///
/// <para><b>Idempotent + restart-tolerant:</b> each candidate is re-loaded and re-checked (still in
/// WrapUp, still pending, not already stuck) before acting, so a raced state change or another pod is
/// a no-op.</para>
/// </summary>
internal sealed partial class CallbackRescueWorker : BackgroundService
{
    /// <summary>Default sweep cadence — every 5 s.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>Max callback attempts before a conversation is escalated instead of re-dialed.</summary>
    private const int MaxCallbackAttempts = 3;

    private readonly IConversationStore _conversations;
    private readonly ICallbackOriginator _originator;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly IAgentLivenessStore _liveness;
    private readonly IAgentStore _agents;
    private readonly IAuditService _audit;
    private readonly PlatformEventBus _eventBus;
    private readonly IClusterLeader _leader;
    private readonly ILogger<CallbackRescueWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _sweepInterval;

    public CallbackRescueWorker(
        IConversationStore conversations,
        ICallbackOriginator originator,
        ITenantAuthConfigStore authConfigStore,
        IAgentLivenessStore liveness,
        IAgentStore agents,
        IAuditService audit,
        PlatformEventBus eventBus,
        [FromKeyedServices(CallbackRescueLeaderResources.Sweep)] IClusterLeader leader,
        ILogger<CallbackRescueWorker> logger,
        TimeProvider? clock = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(originator);
        ArgumentNullException.ThrowIfNull(authConfigStore);
        ArgumentNullException.ThrowIfNull(liveness);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(logger);

        _conversations = conversations;
        _originator = originator;
        _authConfigStore = authConfigStore;
        _liveness = liveness;
        _agents = agents;
        _audit = audit;
        _eventBus = eventBus;
        _leader = leader;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_sweepInterval, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogSweepFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(CallbackRescueWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    /// <summary>
    /// Single sweep pass. Public so tests can drive the loop deterministically without
    /// depending on <see cref="PeriodicTimer"/> ticks. Leader-gated: followers no-op on
    /// the first line.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        if (!_leader.IsLeader)
            return;                                   // leader-gated; followers no-op

        var now = _clock.GetUtcNow();
        var graceCache = new Dictionary<string, int>(StringComparer.Ordinal);   // tenantId -> grace seconds, per-sweep

        foreach (var conv in await _conversations.ListPendingCallbackEvalAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateAsync(conv.TenantId, conv.ConversationId, now, graceCache, ct).ConfigureAwait(false);
        }
    }

    private async Task EvaluateAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, Dictionary<string, int> graceCache, CancellationToken ct)
    {
        var tenantKey = tenantId.Value;
        if (!graceCache.TryGetValue(tenantKey, out var grace))
        {
            var cfg = await _authConfigStore.GetAsync(tenantKey, ct).ConfigureAwait(false);
            grace = cfg?.VoiceCallbackGraceSeconds ?? 25;
            graceCache[tenantKey] = grace;
        }

        // Re-load + re-check (idempotent): a raced state change or another pod must not double-act.
        var conv = await _conversations.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conv is null)
            return;
        if (conv.State != ConversationState.WrapUp)
            return;
        if (!conv.Metadata.TryGetValue("pendingCallbackEval", out var pending) || pending != "true")
            return;
        if (conv.Metadata.ContainsKey("callbackStuck"))
            return;                                   // already escalated

        if (grace <= 0)
        {
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);   // tenant disabled voice rescue
            return;
        }

        // Grace window (also gives cancel-on-return / transient-blip time).
        if (!conv.Metadata.TryGetValue("callbackEvalSince", out var sinceRaw)
            || !DateTimeOffset.TryParse(sinceRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var evalSince))
        {
            // Missing/corrupt timestamp (the bridge always writes "O") — we can't bound the grace, so
            // clear the marker to self-bound the pending set rather than re-listing it every sweep.
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);
            return;
        }
        if (now - evalSince.ToUtcInstant() < TimeSpan.FromSeconds(grace))
            return;                                   // not ready yet

        var attempts = conv.Metadata.TryGetValue("callbackAttempts", out var a) && int.TryParse(
            a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        if (attempts >= MaxCallbackAttempts)
        {
            await MarkStuckAsync(conv, ct).ConfigureAwait(false);
            await AuditEscalatedAsync(conv, attempts, "max_attempts", ct).ConfigureAwait(false);
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);
            LogCallbackEscalated(conv.ConversationId.Value, tenantKey, attempts);
            return;
        }

        // Worthiness: abnormal agent-leg cause OR the owning agent is confirmed gone (liveness backstop).
        var worthy = conv.Metadata.TryGetValue("agentLegAbnormal", out var abnormal) && abnormal == "true";
        if (!worthy && conv.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } agentId })
        {
            var alive = await _liveness.IsAliveAsync(tenantId, agentId, ct).ConfigureAwait(false);
            if (!alive)
            {
                worthy = true;
            }
            else
            {
                var agent = await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
                if (agent?.State == AgentState.Offline)
                    worthy = true;
            }
        }

        if (!worthy)
        {
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);   // clean end + agent present → no callback
            return;
        }

        var number = conv.Metadata.TryGetValue("callbackNumber", out var num) ? num : null;
        var originQueueId = conv.Metadata.TryGetValue("originQueueId", out var oq) ? oq : null;
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(originQueueId))
        {
            await MarkStuckAsync(conv, ct).ConfigureAwait(false);
            await AuditEscalatedAsync(conv, attempts, "no_number_or_queue", ct).ConfigureAwait(false);
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);
            LogCallbackEscalated(conv.ConversationId.Value, tenantKey, attempts);
            return;
        }

        // Ordering invariant: persist the incremented attempt BEFORE originating (a crash can't loop forever).
        var nextAttempt = attempts + 1;
        conv.SetMetadata("callbackAttempts", nextAttempt.ToString(CultureInfo.InvariantCulture));
        await _conversations.SaveAsync(conv, ct).ConfigureAwait(false);

        var ok = await _originator.OriginateCallbackAsync(
            tenantId, number, EntityId.From(originQueueId), conv.ConversationId, nextAttempt, ct).ConfigureAwait(false);
        if (ok)
        {
            await ClearPendingAsync(conv, ct, alsoSetEnqueued: true).ConfigureAwait(false);
            await ResolveOriginalAsync(conv, ct).ConfigureAwait(false);
            await AuditEnqueuedAsync(conv, nextAttempt, ct).ConfigureAwait(false);
            LogCallbackEnqueued(conv.ConversationId.Value, tenantKey, number, nextAttempt);
        }
        else if (nextAttempt >= MaxCallbackAttempts)
        {
            // Exhausted on this failed dial → escalate instead of leaving it to retry.
            await MarkStuckAsync(conv, ct).ConfigureAwait(false);
            await AuditEscalatedAsync(conv, nextAttempt, "originate_failed", ct).ConfigureAwait(false);
            await ClearPendingAsync(conv, ct).ConfigureAwait(false);
            LogCallbackEscalated(conv.ConversationId.Value, tenantKey, nextAttempt);
        }
        else
        {
            // Leave pendingCallbackEval for retry next sweep (attempts already counted).
            await AuditFailedAsync(conv, nextAttempt, ct).ConfigureAwait(false);
        }
    }

    private async Task ClearPendingAsync(Conversation conv, CancellationToken ct, bool alsoSetEnqueued = false)
    {
        conv.RemoveMetadata("pendingCallbackEval");
        if (alsoSetEnqueued)
            conv.SetMetadata("callbackEnqueued", "true");
        await _conversations.SaveAsync(conv, ct).ConfigureAwait(false);
    }

    private async Task MarkStuckAsync(Conversation conv, CancellationToken ct)
    {
        conv.SetMetadata("callbackStuck", "true");
        await _conversations.SaveAsync(conv, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The original WrapUp call is orphaned (its agent is gone) and the customer is now handled by the
    /// new rescue callback, so move it to a terminal state via the legal WrapUp edge (Resolved preferred,
    /// else Closed) and publish the existing <see cref="ConversationStateChangedEvent"/>. If neither edge
    /// is legal the <c>callbackEnqueued</c> marker already set is enough — leave it in WrapUp. Best-effort:
    /// never throws out of the sweep.
    /// </summary>
    private async Task ResolveOriginalAsync(Conversation conv, CancellationToken ct)
    {
        try
        {
            var target = ConversationStateMachine.CanTransition(ConversationState.WrapUp, ConversationState.Resolved)
                ? ConversationState.Resolved
                : ConversationStateMachine.CanTransition(ConversationState.WrapUp, ConversationState.Closed)
                    ? ConversationState.Closed
                    : (ConversationState?)null;
            if (target is not { } terminal)
                return;

            var oldState = conv.State;
            conv.TransitionTo(terminal, _clock.GetUtcNow());
            conv.UpdatedAt = _clock.GetUtcNow();
            await _conversations.SaveAsync(conv, ct).ConfigureAwait(false);
            _eventBus.Publish(new ConversationStateChangedEvent(
                conv.TenantId.Value, conv.ConversationId.Value, oldState.ToString(), conv.State.ToString()));
        }
        catch (Exception ex)
        {
            LogResolveOriginalFailed(conv.ConversationId.Value, ex);
        }
    }

    private async Task AuditEnqueuedAsync(Conversation conv, int attempt, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(
                conv.TenantId,
                category: "conversations",
                action: "conversation.callback.enqueued",
                severity: "info",
                actorId: "system",
                actorType: "system",
                targetId: conv.ConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["former_agent"] = conv.Owner?.OwnerId?.Value ?? "",
                    ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditWriteFailed(conv.ConversationId.Value, ex);
        }
    }

    private async Task AuditEscalatedAsync(Conversation conv, int attempts, string reason, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(
                conv.TenantId,
                category: "conversations",
                action: "conversation.callback.escalated",
                severity: "warning",
                actorId: "system",
                actorType: "system",
                targetId: conv.ConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["former_agent"] = conv.Owner?.OwnerId?.Value ?? "",
                    ["attempts"] = attempts.ToString(CultureInfo.InvariantCulture),
                    ["reason"] = reason,
                    ["state"] = conv.State.ToString(),
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditWriteFailed(conv.ConversationId.Value, ex);
        }
    }

    private async Task AuditFailedAsync(Conversation conv, int attempt, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(
                conv.TenantId,
                category: "conversations",
                action: "conversation.callback.failed",
                severity: "warning",
                actorId: "system",
                actorType: "system",
                targetId: conv.ConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["former_agent"] = conv.Owner?.OwnerId?.Value ?? "",
                    ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditWriteFailed(conv.ConversationId.Value, ex);
        }
    }

    [LoggerMessage(EventId = 9140, Level = LogLevel.Error,
        Message = "Callback-rescue sweep failed.")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(EventId = 9141, Level = LogLevel.Warning,
        Message = "Callback-rescue acted but audit emission failed for conversation {ConversationId}.")]
    partial void LogAuditWriteFailed(string conversationId, Exception ex);

    [LoggerMessage(EventId = 9142, Level = LogLevel.Information,
        Message = "Conversation {ConversationId} (tenant {TenantId}) rescue callback to {Number} enqueued by callback-rescue sweep (attempt {Attempt}).")]
    partial void LogCallbackEnqueued(string conversationId, string tenantId, string number, int attempt);

    [LoggerMessage(EventId = 9143, Level = LogLevel.Warning,
        Message = "Conversation {ConversationId} (tenant {TenantId}) ESCALATED (no callback) by callback-rescue sweep after {Attempts} attempts.")]
    partial void LogCallbackEscalated(string conversationId, string tenantId, int attempts);

    [LoggerMessage(EventId = 9144, Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    partial void LogWorkerCrash(string workerName, string reason, Exception ex);

    [LoggerMessage(EventId = 9145, Level = LogLevel.Warning,
        Message = "Callback-rescue could not resolve the orphaned original conversation {ConversationId} (callback already enqueued).")]
    partial void LogResolveOriginalFailed(string conversationId, Exception ex);
}
