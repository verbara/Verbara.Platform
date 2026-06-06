using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// W4 — leader-gated background sweep that APPLIES a deferred ("pause-when-free")
/// request once the agent's active work drains. A pending pause records the target
/// aux state on <see cref="Agent.PendingState"/> while State keeps reflecting the
/// still-working state (so the agent's in-flight call/chat is never cut off). This
/// worker watches every pending agent and, when <see cref="IConversationStore.CountActiveWorkAsync"/>
/// reaches zero, flips State to the target via <see cref="Agent.ApplyPendingState"/>
/// and publishes <see cref="AgentStateChangedEvent"/> so the existing
/// <c>RealtimeStateBridge</c> propagates the change to Asterisk (AMI <c>QueuePause</c>).
///
/// <para><b>Force-apply on timeout:</b> if active work never drains, the per-tenant
/// <see cref="TenantAuthConfig.PendingPauseTimeoutMinutes"/> bounds how long a pending
/// request may wait. Past it, the worker force-applies anyway and writes a
/// <c>warning</c> audit entry (so a supervisor can see work was abandoned).
/// <c>&lt;= 0</c> disables the timeout (the pending waits indefinitely to drain).</para>
///
/// <para><b>Leader-gated (ADR-0022 Phase A.5 pattern):</b> the sweep runs on every pod
/// but only the elected leader for <see cref="PendingPauseLeaderResources.Sweep"/> acts
/// (a single pod applies cluster-wide). On single-node deployments with no cluster
/// connection string an <see cref="AlwaysLeader"/> stub is registered so the worker
/// still runs.</para>
///
/// <para><b>Idempotent + restart-tolerant:</b> each candidate is re-loaded and
/// re-checked for a pending pause before applying, so a cancel/force that raced the
/// sweep (already cleared) is a no-op, and a second sweep over an agent this pass
/// already applied no longer finds it in <see cref="IAgentStore.StreamPendingPauseAgentsAsync"/>
/// → no duplicate event.</para>
///
/// <para><b>No-flicker contract (A4):</b> apply publishes ONLY
/// <see cref="AgentStateChangedEvent"/> to the now-non-routable target — NOT
/// <c>AgentPendingStateChangedEvent(null)</c>, which would unpause then re-pause. The
/// agent was already paused when the pending was set; the state-changed event to a
/// non-routable target keeps the pause.</para>
/// </summary>
public sealed partial class PendingPauseDrainWorker : BackgroundService
{
    /// <summary>Default sweep cadence — every 5 s.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(5);

    private readonly IAgentStore _agentStore;
    private readonly IConversationStore _conversationStore;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly PlatformEventBus _eventBus;
    private readonly IAuditService _audit;
    private readonly IClusterLeader _leader;
    private readonly ILogger<PendingPauseDrainWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _sweepInterval;

    public PendingPauseDrainWorker(
        IAgentStore agentStore,
        IConversationStore conversationStore,
        ITenantAuthConfigStore authConfigStore,
        PlatformEventBus eventBus,
        IAuditService audit,
        [FromKeyedServices(PendingPauseLeaderResources.Sweep)] IClusterLeader leader,
        ILogger<PendingPauseDrainWorker> logger,
        TimeProvider? clock = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(agentStore);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(authConfigStore);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(logger);

        _agentStore = agentStore;
        _conversationStore = conversationStore;
        _authConfigStore = authConfigStore;
        _eventBus = eventBus;
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
            LogWorkerCrash(nameof(PendingPauseDrainWorker), fatalEx.Message, fatalEx);
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
        var timeoutCache = new Dictionary<string, int>(StringComparer.Ordinal);   // tenantId -> minutes, per-sweep

        await foreach (var agent in _agentStore.StreamPendingPauseAgentsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // Re-load + re-check (idempotent: a cancel/force racing the sweep already cleared it).
            var fresh = await _agentStore.GetByIdAsync(agent.TenantId, agent.AgentId, ct).ConfigureAwait(false);
            if (fresh is null || !fresh.HasPendingPause)
                continue;

            var activeWork = await _conversationStore.CountActiveWorkAsync(fresh.TenantId, fresh.AgentId, ct).ConfigureAwait(false);

            if (activeWork == 0)
            {
                await ApplyAsync(fresh, forcedTimeout: false, stuckWork: 0, timeoutMinutes: 0, ct).ConfigureAwait(false);   // drained → apply
                continue;
            }

            // Active work remains → check the per-tenant max-pending timeout.
            var tenantKey = fresh.TenantId.Value;
            if (!timeoutCache.TryGetValue(tenantKey, out var timeoutMin))
            {
                var cfg = await _authConfigStore.GetAsync(tenantKey, ct).ConfigureAwait(false);
                timeoutMin = cfg?.PendingPauseTimeoutMinutes ?? 30;
                timeoutCache[tenantKey] = timeoutMin;
            }
            if (timeoutMin > 0 && fresh.PendingSince is { } since && now - since >= TimeSpan.FromMinutes(timeoutMin))
                await ApplyAsync(fresh, forcedTimeout: true, stuckWork: activeWork, timeoutMinutes: timeoutMin, ct).ConfigureAwait(false);
            // else: still draining, leave pending for a later tick
        }
    }

    private async Task ApplyAsync(Agent fresh, bool forcedTimeout, int stuckWork, int timeoutMinutes, CancellationToken ct)
    {
        var tenantKey = fresh.TenantId.Value;
        var agentId = fresh.AgentId.Value;
        var oldState = fresh.State.ToString();
        var target = fresh.PendingState!.Value.ToString();
        // Capture pending_since BEFORE ApplyPendingState() nulls it (audit needs it).
        var pendingSinceIso = fresh.PendingSince?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "";

        fresh.ApplyPendingState();   // flips State to target, clears pending
        await _agentStore.SaveAsync(fresh, ct).ConfigureAwait(false);

        // Publish ONLY AgentStateChangedEvent (no-flicker contract A4): the agent was
        // already paused when pending was set; the change to a non-routable target keeps
        // the pause. Do NOT publish AgentPendingStateChangedEvent(null) — that flickers.
        _eventBus.Publish(new AgentStateChangedEvent(
            tenantKey, agentId, fresh.DisplayName,
            oldState, target));    // RealtimeStateBridge → AMI QueuePause

        LogAgentPauseApplied(agentId, tenantKey, target, forcedTimeout);

        if (forcedTimeout)
        {
            try
            {
                await _audit.RecordAsync(
                    fresh.TenantId,
                    category: "queues",
                    action: "agent.pending_pause.forced_timeout",
                    severity: "warning",
                    actorId: "system",
                    actorType: "system",
                    targetId: agentId,
                    targetType: "Agent",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["applied_state"] = target,
                        ["pending_since"] = pendingSinceIso,
                        ["timeout_minutes"] = timeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["stuck_active_work_count"] = stuckWork.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogAuditWriteFailed(agentId, ex);
            }
        }
    }

    [LoggerMessage(EventId = 9120, Level = LogLevel.Error,
        Message = "Pending-pause drain sweep failed.")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(EventId = 9121, Level = LogLevel.Warning,
        Message = "Pending-pause apply succeeded but audit emission failed for agent {AgentId}.")]
    partial void LogAuditWriteFailed(string agentId, Exception ex);

    [LoggerMessage(EventId = 9122, Level = LogLevel.Information,
        Message = "Agent {AgentId} (tenant {TenantId}) pending pause applied -> {Target} by drain sweep (forcedTimeout={ForcedTimeout}).")]
    partial void LogAgentPauseApplied(string agentId, string tenantId, string target, bool forcedTimeout);

    [LoggerMessage(EventId = 9123, Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
