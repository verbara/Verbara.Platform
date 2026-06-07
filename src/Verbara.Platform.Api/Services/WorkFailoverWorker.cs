using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// W5 — leader-gated background sweep that RE-QUEUES the orphaned digital work of an
/// agent who has gone Offline past the per-tenant grace. When the W3 liveness reaper
/// (or a user/idle teardown) forces an agent Offline, any conversation the agent was
/// actively handling ({Active, OnHold, Consulting} — <see cref="ConversationStateMachine.FailoverWorkStates"/>)
/// is stranded: the live customer is waiting on someone who is gone. This worker waits
/// out the grace window, then returns each such conversation to its ORIGINAL queue at
/// the FRONT via <see cref="IConversationSwitchboard.RequeueToFrontAsync"/> (which
/// releases the offline agent's capacity and bridges OnHold/Consulting→Active→Escalated→Queued).
///
/// <para><b>Grace + cancel-on-return:</b> the per-tenant
/// <see cref="TenantAuthConfig.WorkFailoverGraceSeconds"/> (default 30) bounds how long
/// the agent may be Offline before failover acts. <c>&lt;= 0</c> disables failover for
/// the tenant. An agent who comes back is no longer in
/// <see cref="IAgentStore.StreamOfflineAgentsAsync"/>, so a return inside the grace
/// silently cancels the failover (no explicit cancel needed).</para>
///
/// <para><b>Origin queue:</b> the target queue is read from
/// <c>Metadata["originQueueId"]</c>, stamped by <see cref="QueueDistributionWorker"/>
/// at offer time (the owner is still the Queue then). If it is missing the conversation
/// cannot be safely re-queued, so it is ESCALATED instead (marked <c>failoverStuck</c> +
/// a <c>warning</c> audit entry) so a supervisor can intervene.</para>
///
/// <para><b>Bounded retries:</b> <c>Metadata["failoverAttempts"]</c> is incremented and
/// persisted BEFORE each re-queue (so a crash mid-sweep can never loop forever). Past
/// <see cref="MaxFailoverAttempts"/> the conversation is escalated instead of re-queued.</para>
///
/// <para><b>Leader-gated (ADR-0022 Phase A.5 pattern):</b> the sweep runs on every pod
/// but only the elected leader for <see cref="WorkFailoverLeaderResources.Sweep"/> acts
/// (a single pod re-queues cluster-wide). On single-node deployments with no cluster
/// connection string an <see cref="AlwaysLeader"/> stub is registered so the worker
/// still runs.</para>
///
/// <para><b>Idempotent + restart-tolerant:</b> each candidate conversation is re-loaded
/// and re-checked (still owned by the offline agent, still in a failover-work state, not
/// already stuck) before acting, so a normal accept/transfer or the agent returning that
/// raced the sweep is a no-op.</para>
/// </summary>
public sealed partial class WorkFailoverWorker : BackgroundService
{
    /// <summary>Default sweep cadence — every 5 s.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>Max re-queue attempts before a conversation is escalated instead of re-queued.</summary>
    private const int MaxFailoverAttempts = 3;

    private readonly IAgentStore _agentStore;
    private readonly IConversationStore _conversationStore;
    private readonly IConversationSwitchboard _switchboard;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly IAuditService _audit;
    private readonly IClusterLeader _leader;
    private readonly ILogger<WorkFailoverWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _sweepInterval;

    public WorkFailoverWorker(
        IAgentStore agentStore,
        IConversationStore conversationStore,
        IConversationSwitchboard switchboard,
        ITenantAuthConfigStore authConfigStore,
        IAuditService audit,
        [FromKeyedServices(WorkFailoverLeaderResources.Sweep)] IClusterLeader leader,
        ILogger<WorkFailoverWorker> logger,
        TimeProvider? clock = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(agentStore);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(switchboard);
        ArgumentNullException.ThrowIfNull(authConfigStore);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(logger);

        _agentStore = agentStore;
        _conversationStore = conversationStore;
        _switchboard = switchboard;
        _authConfigStore = authConfigStore;
        _audit = audit;
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
            LogWorkerCrash(nameof(WorkFailoverWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    /// <summary>
    /// Single sweep pass. Public so tests can drive the loop deterministically
    /// without depending on <see cref="PeriodicTimer"/> ticks. Leader-gated:
    /// followers no-op on the first line.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        if (!_leader.IsLeader)
            return;                                   // leader-gated; followers no-op

        var now = _clock.GetUtcNow();
        var graceCache = new Dictionary<string, int>(StringComparer.Ordinal);   // tenantId -> grace seconds, per-sweep

        await foreach (var offlineAgent in _agentStore.StreamOfflineAgentsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var tenantKey = offlineAgent.TenantId.Value;

            if (!graceCache.TryGetValue(tenantKey, out var grace))
            {
                var cfg = await _authConfigStore.GetAsync(tenantKey, ct).ConfigureAwait(false);
                grace = cfg?.WorkFailoverGraceSeconds ?? 30;
                graceCache[tenantKey] = grace;
            }
            if (grace <= 0)
                continue;                             // tenant disabled failover

            // Grace + cancel-on-return: a returned agent is no longer in StreamOfflineAgentsAsync,
            // so we only need to confirm the still-offline agent has been gone long enough.
            if (offlineAgent.OfflineSince is not { } since || now - since < TimeSpan.FromSeconds(grace))
                continue;

            foreach (var staleConv in await _conversationStore.ListFailoverWorkByOwnerAsync(
                         offlineAgent.TenantId, offlineAgent.AgentId, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await FailoverConversationAsync(offlineAgent, staleConv.ConversationId, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task FailoverConversationAsync(Agent offlineAgent, EntityId conversationId, CancellationToken ct)
    {
        var tenantKey = offlineAgent.TenantId.Value;

        // Re-load + re-check (idempotent: a normal accept/transfer or the agent returning
        // may have changed it since the stream snapshot).
        var conv = await _conversationStore.GetByIdAsync(offlineAgent.TenantId, conversationId, ct).ConfigureAwait(false);
        if (conv is null)
            return;
        if (conv.Owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId } || ownerId != offlineAgent.AgentId)
            return;                                   // no longer owned by the offline agent
        if (!ConversationStateMachine.IsFailoverWork(conv.State))
            return;                                   // no longer engaged
        if (conv.Metadata.ContainsKey("failoverStuck"))
            return;                                   // already escalated

        var attempts = conv.Metadata.TryGetValue("failoverAttempts", out var a) && int.TryParse(a, out var n) ? n : 0;
        var hasOrigin = conv.Metadata.TryGetValue("originQueueId", out var originId) && !string.IsNullOrWhiteSpace(originId);

        // Escalate (no re-queue) if we've exhausted attempts OR we don't know the origin queue.
        if (attempts >= MaxFailoverAttempts || !hasOrigin)
        {
            await MarkStuckAsync(conv, ct).ConfigureAwait(false);
            await AuditEscalatedAsync(conv, offlineAgent, attempts, hasOrigin ? "max_attempts" : "no_origin_queue", ct).ConfigureAwait(false);
            LogFailoverEscalated(conv.ConversationId.Value, tenantKey, attempts);
            return;
        }

        // Persist the incremented attempt counter BEFORE re-queueing (so a crash can't loop forever).
        conv.SetMetadata("failoverAttempts", (attempts + 1).ToString(CultureInfo.InvariantCulture));
        await _conversationStore.SaveAsync(conv, ct).ConfigureAwait(false);

        var result = await _switchboard.RequeueToFrontAsync(
            conv.ConversationId, offlineAgent.TenantId, EntityId.From(originId!), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            // Defensive (the OnHold/Consulting bridge makes this unlikely): mark stuck + escalate.
            await MarkStuckAsync(conv, ct).ConfigureAwait(false);
            await AuditEscalatedAsync(conv, offlineAgent, attempts + 1, "requeue_failed", ct).ConfigureAwait(false);
            LogFailoverEscalated(conv.ConversationId.Value, tenantKey, attempts + 1);
            return;
        }

        await AuditRequeuedAsync(conv, offlineAgent, EntityId.From(originId!), attempts + 1, ct).ConfigureAwait(false);
        LogConversationRequeued(conv.ConversationId.Value, tenantKey, originId!);
    }

    private async Task MarkStuckAsync(Conversation conv, CancellationToken ct)
    {
        conv.SetMetadata("failoverStuck", "true");
        await _conversationStore.SaveAsync(conv, ct).ConfigureAwait(false);
    }

    private async Task AuditRequeuedAsync(Conversation conv, Agent offlineAgent, EntityId originQueue, int attempt, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(
                conv.TenantId,
                category: "conversations",
                action: "conversation.failover.requeued",
                severity: "info",
                actorId: "system",
                actorType: "system",
                targetId: conv.ConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["former_agent"] = offlineAgent.AgentId.Value,
                    ["origin_queue"] = originQueue.Value,
                    ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditWriteFailed(conv.ConversationId.Value, ex);
        }
    }

    private async Task AuditEscalatedAsync(Conversation conv, Agent offlineAgent, int attempts, string reason, CancellationToken ct)
    {
        try
        {
            await _audit.RecordAsync(
                conv.TenantId,
                category: "conversations",
                action: "conversation.failover.escalated",
                severity: "warning",
                actorId: "system",
                actorType: "system",
                targetId: conv.ConversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["former_agent"] = offlineAgent.AgentId.Value,
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

    [LoggerMessage(EventId = 9130, Level = LogLevel.Error,
        Message = "Work-failover sweep failed.")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(EventId = 9131, Level = LogLevel.Warning,
        Message = "Work-failover acted but audit emission failed for conversation {ConversationId}.")]
    partial void LogAuditWriteFailed(string conversationId, Exception ex);

    [LoggerMessage(EventId = 9132, Level = LogLevel.Information,
        Message = "Conversation {ConversationId} (tenant {TenantId}) re-queued to origin queue {OriginQueueId} by work-failover sweep.")]
    partial void LogConversationRequeued(string conversationId, string tenantId, string originQueueId);

    [LoggerMessage(EventId = 9133, Level = LogLevel.Warning,
        Message = "Conversation {ConversationId} (tenant {TenantId}) ESCALATED (not re-queued) by work-failover sweep after {Attempts} attempts.")]
    partial void LogFailoverEscalated(string conversationId, string tenantId, int attempts);

    [LoggerMessage(EventId = 9134, Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
