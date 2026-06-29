using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Listens for <see cref="AgentStateChangedEvent"/> on the <see cref="PlatformEventBus"/>
/// and propagates the new paused/unpaused state to:
/// <list type="number">
///   <item>Asterisk Realtime DB via <see cref="IRealtimeSyncService.SyncAgentPausedAsync"/>.</item>
///   <item>Asterisk AMI via a <c>QueuePause</c> action through the <see cref="VerbaraServerPool"/>.</item>
/// </list>
/// Both operations are best-effort; failures are logged without interrupting the event stream.
/// </summary>
internal sealed partial class RealtimeStateBridge : IHostedService, IDisposable
{
    /// <summary>
    /// Keyed-service name for the per-event <see cref="ResiliencePolicy"/> that wraps the
    /// combined DB + AMI pair. Circuit-open drops the current event; the subscription stays
    /// alive for subsequent events.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.realtime-state-bridge";

    private readonly PlatformEventBus _eventBus;
    private readonly IRealtimeSyncService _syncService;
    private readonly VerbaraServerPool _serverPool;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<RealtimeStateBridge> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new();
    private IDisposable? _subscription;

    public RealtimeStateBridge(
        PlatformEventBus eventBus,
        IRealtimeSyncService syncService,
        VerbaraServerPool serverPool,
        ILogger<RealtimeStateBridge> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _eventBus = eventBus;
        _syncService = syncService;
        _serverPool = serverPool;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _eventBus.Events.Subscribe(OnEvent);
        return Task.CompletedTask;
    }

    // Synchronous, never-throwing OnNext: dispatches the fully-guarded async handler fire-and-forget.
    private void OnEvent(PlatformEvent evt) => _ = HandleEventAsync(evt);

    internal Task HandleEventAsync(PlatformEvent evt)
    {
        switch (evt)
        {
            case AgentStateChangedEvent e:
                return ApplyPauseAsync(
                    e.TenantId,
                    e.AgentId,
                    !AgentStateMachine.IsRoutable(Enum.Parse<AgentState>(e.NewState)),
                    e.NewState,
                    CancellationToken.None);
            case AgentPendingStateChangedEvent p:
                // SET (pending != null) => pause now (block new; active call/chat continues).
                // CANCEL (pending == null) => unpause (State is routable on cancel).
                return ApplyPauseAsync(
                    p.TenantId,
                    p.AgentId,
                    shouldPause: p.PendingState is not null,
                    reason: p.PendingState ?? "pause_cancelled",
                    CancellationToken.None);
            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Propagates a paused/unpaused decision to the Asterisk Realtime DB and AMI
    /// under a per-agent semaphore. Both side-effects are independent and best-effort:
    /// a DB failure does NOT prevent the AMI QueuePause from being attempted (and vice
    /// versa). Both share the resilience policy key so circuit state is aggregated at
    /// the bridge level. Shared by <see cref="AgentStateChangedEvent"/> (drain/normal
    /// state flips) and <see cref="AgentPendingStateChangedEvent"/> (W4 deferred-pause
    /// set/cancel, where State stays routable).
    /// </summary>
    private async Task ApplyPauseAsync(string tenantId, string agentId, bool shouldPause, string reason, CancellationToken ct)
    {
        var semaphore = _agentLocks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            var iface = $"PJSIP/{tenantId}-agent-{agentId}";

            try
            {
                await _policy.ExecuteAsync(
                    ResiliencePolicyKey,
                    async innerCt =>
                    {
                        await _syncService.SyncAgentPausedAsync(tenantId, agentId, shouldPause, innerCt);
                        return 0;
                    },
                    CancellationToken.None);
            }
            catch (CircuitBreakerOpenException)
            {
                Log.CircuitOpen(_logger, agentId);
            }
            catch (Exception ex)
            {
                Log.SyncPausedDbFailed(_logger, agentId, ex);
            }

            var server = _serverPool.GetServer("primary");
            if (server is not null)
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await server.Connection.SendActionAsync(
                                new QueuePauseAction
                                {
                                    Interface = iface,
                                    Paused = shouldPause,
                                    Reason = reason,
                                },
                                innerCt);
                            return 0;
                        },
                        CancellationToken.None);
                }
                catch (CircuitBreakerOpenException)
                {
                    Log.CircuitOpen(_logger, agentId);
                }
                catch (Exception ex)
                {
                    Log.QueuePauseFailed(_logger, agentId, ex);
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to sync paused state to DB for {AgentId}")]
        public static partial void SyncPausedDbFailed(ILogger logger, string agentId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send QueuePause for {AgentId}")]
        public static partial void QueuePauseFailed(ILogger logger, string agentId, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Circuit open for worker.realtime-state-bridge — dropping event for {AgentId}")]
        public static partial void CircuitOpen(ILogger logger, string agentId);
    }
}
