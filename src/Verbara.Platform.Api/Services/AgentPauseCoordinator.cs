using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The one owner of an agent's Asterisk Realtime pause decision: the pause rule and the per-agent
/// lock that every <c>queue_members.paused</c> writer in this process takes
/// (queue-members-stay-unpaused-across-reconcile, design D2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IRealtimeSyncService.AddQueueMemberAsync"/> inserts a new row with <c>paused</c> = 1
/// and, from Sdk.Pro 2.17.1-pro, leaves an existing row's <c>paused</c> untouched. So after every
/// upsert Platform issues, <see cref="ConvergeAsync"/> re-asserts the agent's real state with
/// <see cref="IRealtimeSyncService.SyncAgentPausedAsync"/>, which sets every row of the agent's
/// interface. It writes the database only, never AMI (design D3): <c>app_queue</c> re-reads the
/// realtime row when a caller joins. <see cref="RealtimeStateBridge"/> keeps sending AMI
/// <c>QueuePause</c> on real state changes, under the same lock.
/// </para>
/// <para>
/// <b>Per-process lock (accepted risk R2).</b> The semaphores live in this singleton, so they
/// serialize writers inside one API process only. With several API replicas, one pod's reconcile
/// can still race another pod's bridge; the fresh read under the lock narrows that window to the
/// read-to-write gap, and the next state event re-asserts the truth.
/// </para>
/// <para>
/// The coordinator has no external dependency: the agent store and the sync service are passed
/// per call, so it is safe to construct at host start without any realtime configuration.
/// </para>
/// </remarks>
internal sealed class AgentPauseCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// The pause rule shared with <see cref="RealtimeStateBridge"/>: an agent is paused in Asterisk
    /// when its state is not routable (<see cref="AgentState.Available"/> and <see cref="AgentState.Busy"/>
    /// are) or when a deferred pause is pending.
    /// </summary>
    public static bool ShouldPause(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return !AgentStateMachine.IsRoutable(agent.State) || agent.PendingState is not null;
    }

    /// <summary>
    /// Mirrors the Sdk.Pro voice gate of <see cref="IRealtimeSyncService.AddQueueMemberAsync"/>
    /// (ADR-0026 Phase B): a <c>null</c> channel list, or one that contains <c>"voice"</c>
    /// (case-insensitive), upserts a <c>queue_members</c> row. Any other list is digital-only and
    /// removes the row instead, so there is nothing to converge.
    /// </summary>
    public static bool CreatesVoiceRow(IReadOnlyList<string>? allowedChannels)
    {
        if (allowedChannels is null)
            return true;
        foreach (var channel in allowedChannels)
        {
            if (string.Equals(channel, "voice", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Waits for the per-agent lock and returns a handle that releases it on dispose. Every
    /// <c>queue_members.paused</c> write for <paramref name="agentId"/> in this process runs under it.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string agentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        var semaphore = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    /// <summary>
    /// Re-reads the agent under its lock and writes its real pause state to every
    /// <c>queue_members</c> row of its interface. Returns the value written, or <c>null</c> when
    /// the agent no longer exists (nothing is written).
    /// </summary>
    /// <param name="tenantId">The agent's tenant.</param>
    /// <param name="agentId">The agent to converge.</param>
    /// <param name="agents">An agent store that reads the current row (no tick-level cache).</param>
    /// <param name="sync">The realtime sync service that owns the write.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool?> ConvergeAsync(
        TenantId tenantId, EntityId agentId, IAgentStore agents, IRealtimeSyncService sync, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(sync);

        using (await AcquireAsync(agentId.Value, ct).ConfigureAwait(false))
        {
            var agent = await agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
            if (agent is null)
                return null;

            var shouldPause = ShouldPause(agent);
            await sync.SyncAgentPausedAsync(tenantId.Value, agentId.Value, shouldPause, ct).ConfigureAwait(false);
            return shouldPause;
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
