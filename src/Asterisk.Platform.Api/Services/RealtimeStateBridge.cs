using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Live.Server;
using Asterisk.Sdk.Pro.Realtime;

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
    private readonly PlatformEventBus _eventBus;
    private readonly IRealtimeSyncService _syncService;
    private readonly AsteriskServerPool _serverPool;
    private readonly ILogger<RealtimeStateBridge> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new();
    private IDisposable? _subscription;

    public RealtimeStateBridge(
        PlatformEventBus eventBus,
        IRealtimeSyncService syncService,
        AsteriskServerPool serverPool,
        ILogger<RealtimeStateBridge> logger)
    {
        _eventBus = eventBus;
        _syncService = syncService;
        _serverPool = serverPool;
        _logger = logger;
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

            // 1. DB write (best-effort)
            try
            {
                await _syncService.SyncAgentPausedAsync(e.TenantId, e.AgentId, shouldPause);
            }
            catch (Exception ex)
            {
                Log.SyncPausedDbFailed(_logger, e.AgentId, ex);
            }

            // 2. AMI QueuePause via cluster pool (best-effort)
            var server = _serverPool.GetServer("primary");
            if (server is not null)
            {
                try
                {
                    await server.Connection.SendActionAsync(new QueuePauseAction
                    {
                        Interface = iface,
                        Paused = shouldPause,
                        Reason = e.NewState,
                    });
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
    }
}
