using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// W3 — leader-gated background sweep that reconciles "Postgres says the agent is
/// routable" against "Redis says the agent is alive" and forces dead agents Offline.
///
/// A routable agent's browser proves liveness by periodically refreshing a Redis
/// presence key with a short TTL (<see cref="IAgentLivenessStore"/>). When the key
/// is missing — tab closed, network dropped, pod crash — the agent is a routing
/// zombie: still <c>Available</c>/<c>Busy</c> in Postgres so the router can hand it
/// work, but with no live client to handle it. This reaper detects that gap and
/// calls <see cref="Agent.ForceOffline"/>, then publishes
/// <see cref="AgentStateChangedEvent"/> so the existing <c>RealtimeStateBridge</c>
/// propagates the change to Asterisk (AMI <c>QueuePause</c>).
///
/// <para><b>Leader-gated (ADR-0022 Phase A.5 pattern):</b> the sweep runs on every
/// pod but only the elected leader for <see cref="AgentLivenessLeaderResources.Sweep"/>
/// acts (a single pod reconciles cluster-wide). On single-node deployments with no
/// cluster connection string an <see cref="AlwaysLeader"/> stub is registered so the
/// reaper still runs.</para>
///
/// <para><b>Idempotent + restart-tolerant:</b> each candidate is re-loaded and
/// re-checked for routability before being forced Offline, so a graceful-offline that
/// raced the sweep (already non-routable) is a no-op, and a second sweep over an
/// agent this pass already reaped publishes no duplicate event. The per-tenant
/// <see cref="TenantAuthConfig.AgentLivenessTimeoutSeconds"/> of <c>&lt;= 0</c>
/// disables reaping for that tenant.</para>
/// </summary>
public sealed partial class AgentLivenessReaper : BackgroundService
{
    /// <summary>Default sweep cadence — every 15 s.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(15);

    private readonly IAgentStore _agentStore;
    private readonly IAgentLivenessStore _livenessStore;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly PlatformEventBus _eventBus;
    private readonly IAuditService _audit;
    private readonly IClusterLeader _leader;
    private readonly ILogger<AgentLivenessReaper> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _sweepInterval;

    public AgentLivenessReaper(
        IAgentStore agentStore,
        IAgentLivenessStore livenessStore,
        ITenantAuthConfigStore authConfigStore,
        PlatformEventBus eventBus,
        IAuditService audit,
        [FromKeyedServices(AgentLivenessLeaderResources.Sweep)] IClusterLeader leader,
        ILogger<AgentLivenessReaper> logger,
        TimeProvider? clock = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(agentStore);
        ArgumentNullException.ThrowIfNull(livenessStore);
        ArgumentNullException.ThrowIfNull(authConfigStore);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(logger);

        _agentStore = agentStore;
        _livenessStore = livenessStore;
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
            LogWorkerCrash(nameof(AgentLivenessReaper), fatalEx.Message, fatalEx);
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

        var nowOffline = AgentState.Offline.ToString();
        var ttlCache = new Dictionary<string, int>(StringComparer.Ordinal);   // tenantId -> ttlSeconds, per-sweep

        await foreach (var agent in _agentStore.StreamRoutableAgentsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var tenantKey = agent.TenantId.Value;

            if (!ttlCache.TryGetValue(tenantKey, out var ttl))
            {
                var cfg = await _authConfigStore.GetAsync(tenantKey, ct).ConfigureAwait(false);
                ttl = cfg?.AgentLivenessTimeoutSeconds ?? 60;
                ttlCache[tenantKey] = ttl;
            }
            if (ttl <= 0)
                continue;                             // tenant disabled liveness reaping

            if (await _livenessStore.IsAliveAsync(agent.TenantId, agent.AgentId, ct).ConfigureAwait(false))
                continue;                             // heartbeat present → alive

            // Re-load to avoid acting on a stale stream snapshot, and re-check routable
            // (idempotent: a graceful-offline that raced the sweep already left it non-routable).
            var fresh = await _agentStore.GetByIdAsync(agent.TenantId, agent.AgentId, ct).ConfigureAwait(false);
            if (fresh is null || !AgentStateMachine.IsRoutable(fresh.State))
                continue;

            var oldState = fresh.State.ToString();
            fresh.ForceOffline();
            await _agentStore.SaveAsync(fresh, ct).ConfigureAwait(false);

            _eventBus.Publish(new AgentStateChangedEvent(
                tenantKey, fresh.AgentId.Value, fresh.DisplayName,
                oldState, nowOffline));    // RealtimeStateBridge → AMI QueuePause

            LogAgentReaped(fresh.AgentId.Value, tenantKey, oldState);

            try
            {
                await _audit.RecordAsync(
                    agent.TenantId,
                    category: "queues",
                    action: "agent.liveness.force_offline",
                    severity: "warning",
                    actorId: "system",
                    actorType: "system",
                    targetId: fresh.AgentId.Value,
                    targetType: "Agent",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["old_state"] = oldState,
                        ["timeout_seconds"] = ttl.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = "missing_presence_key",
                    },
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogAuditWriteFailed(fresh.AgentId.Value, ex);
            }
        }
    }

    [LoggerMessage(EventId = 9110, Level = LogLevel.Error,
        Message = "Agent liveness sweep failed.")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Warning,
        Message = "Agent liveness force-offline succeeded but audit emission failed for agent {AgentId}.")]
    partial void LogAuditWriteFailed(string agentId, Exception ex);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Information,
        Message = "Agent {AgentId} (tenant {TenantId}) forced Offline by liveness sweep; was {OldState}.")]
    partial void LogAgentReaped(string agentId, string tenantId, string oldState);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
