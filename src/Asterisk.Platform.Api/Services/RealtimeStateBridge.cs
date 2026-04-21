using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Pro.Realtime;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Listens for <see cref="AgentStateChangedEvent"/> on the <see cref="PlatformEventBus"/>
/// and propagates the new paused/unpaused state to:
/// <list type="number">
///   <item>Asterisk Realtime DB via <see cref="IRealtimeSyncService.SyncAgentPausedAsync"/>.</item>
///   <item>Asterisk AMI via a <c>QueuePause</c> action through the <see cref="AsteriskServerPool"/>.</item>
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
    private readonly AsteriskServerPool _serverPool;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<RealtimeStateBridge> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new();
    private IDisposable? _subscription;

    public RealtimeStateBridge(
        PlatformEventBus eventBus,
        IRealtimeSyncService syncService,
        AsteriskServerPool serverPool,
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

    private async void OnEvent(PlatformEvent evt)
    {
        if (evt is not AgentStateChangedEvent e) return;

        var semaphore = _agentLocks.GetOrAdd(e.AgentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var shouldPause = !AgentStateMachine.IsRoutable(
                Enum.Parse<AgentState>(e.NewState));
            var iface = $"PJSIP/{e.TenantId}-agent-{e.AgentId}";

            // DB and AMI are independent side-effects — wrap each separately
            // so a DB failure does NOT prevent the AMI QueuePause from being
            // attempted (and vice versa). Both share the policy key so circuit
            // state is aggregated at the bridge level.
            try
            {
                await _policy.ExecuteAsync(
                    ResiliencePolicyKey,
                    async innerCt =>
                    {
                        await _syncService.SyncAgentPausedAsync(e.TenantId, e.AgentId, shouldPause, innerCt);
                        return 0;
                    },
                    CancellationToken.None);
            }
            catch (CircuitBreakerOpenException)
            {
                Log.CircuitOpen(_logger, e.AgentId);
            }
            catch (Exception ex)
            {
                Log.SyncPausedDbFailed(_logger, e.AgentId, ex);
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
                                    Reason = e.NewState,
                                },
                                innerCt);
                            return 0;
                        },
                        CancellationToken.None);
                }
                catch (CircuitBreakerOpenException)
                {
                    Log.CircuitOpen(_logger, e.AgentId);
                }
                catch (Exception ex)
                {
                    Log.QueuePauseFailed(_logger, e.AgentId, ex);
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
