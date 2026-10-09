using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The force-offline teardown shared by the admin <c>POST /admin/agents/{id}/force-offline</c> action and
/// a user status change that leaves access (licensed-agent-metering, design D2): the agent is set Offline
/// from any state, its liveness key is removed, and every <c>queue_members</c> row of its interface is
/// paused before the call returns.
/// </summary>
/// <remarks>
/// <para>
/// The Offline write and the <c>paused</c> = 1 write run under the per-agent lock of
/// <see cref="AgentPauseCoordinator"/>, the lock every <c>queue_members.paused</c> writer in this process
/// takes (realtime-queue-member-sync), and the agent is re-read under it, so a reconcile or a state
/// change racing the teardown cannot leave the agent unpaused. The <see cref="AgentStateChangedEvent"/>
/// is published after the lock is released, only on a real transition; <see cref="RealtimeStateBridge"/>
/// then sends the AMI <c>QueuePause</c> under the same lock.
/// </para>
/// <para>
/// <see cref="IRealtimeSyncService"/> is resolved at first use, never at construction: without
/// Asterisk Realtime the database pause write is skipped and the rest of the teardown still runs. A
/// failed pause write is logged (EventId 4130) and left to the reconciler and the bridge.
/// </para>
/// </remarks>
internal sealed class AgentForceOfflineService
{
    private readonly IAgentStore _agents;
    private readonly IAgentLivenessStore _liveness;
    private readonly PlatformEventBus _eventBus;
    private readonly AgentPauseCoordinator _pauseCoordinator;
    private readonly TimeProvider _clock;
    private readonly IServiceProvider _services;
    private readonly ILogger<AgentForceOfflineService> _logger;

    public AgentForceOfflineService(
        IAgentStore agents,
        IAgentLivenessStore liveness,
        PlatformEventBus eventBus,
        AgentPauseCoordinator pauseCoordinator,
        TimeProvider clock,
        IServiceProvider services,
        ILogger<AgentForceOfflineService> logger)
    {
        _agents = agents;
        _liveness = liveness;
        _eventBus = eventBus;
        _pauseCoordinator = pauseCoordinator;
        _clock = clock;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Forces <paramref name="agentId"/> Offline. Idempotent: an agent already Offline is saved and
    /// paused again but publishes no event.
    /// </summary>
    /// <returns>The agent as saved and the state it left, or <see langword="null"/> when there is no such agent.</returns>
    public async Task<ForcedOffline?> ApplyAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        Agent? agent;
        AgentState oldState;
        using (await _pauseCoordinator.AcquireAsync(agentId.Value, ct).ConfigureAwait(false))
        {
            agent = await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
            if (agent is null)
                return null;

            oldState = agent.State;
            agent.ForceOffline(_clock.GetUtcNow());   // W5 — deterministic grace stamp
            await _agents.SaveAsync(agent, ct).ConfigureAwait(false);

            if (_services.GetService<IRealtimeSyncService>() is { } sync)
            {
                try
                {
                    await sync.SyncAgentPausedAsync(tenantId.Value, agentId.Value, true, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RealtimeSyncDeferralLog.Deferred(_logger, "SyncAgentPaused", agentId.Value, tenantId.Value, ex);
                }
            }
        }

        await _liveness.RemoveAsync(tenantId, agentId, ct).ConfigureAwait(false);

        // Publish ONLY on a real transition so a force-offline against an already-Offline agent
        // doesn't spam RealtimeStateBridge → AMI QueuePause.
        if (oldState != AgentState.Offline)
        {
            _eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                AgentState.Offline.ToString()));
        }

        return new ForcedOffline(agent, oldState);
    }
}

/// <summary>The outcome of <see cref="AgentForceOfflineService.ApplyAsync"/>: the agent as saved and the state it left.</summary>
internal sealed record ForcedOffline(Agent Agent, AgentState OldState);
