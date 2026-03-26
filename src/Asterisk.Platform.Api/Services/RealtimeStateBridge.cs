using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Pro.Realtime;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Listens for <see cref="AgentStateChangedEvent"/> on the <see cref="PlatformEventBus"/>
/// and propagates the new paused/unpaused state to:
/// <list type="number">
///   <item>Asterisk Realtime DB via <see cref="IRealtimeSyncService.SyncAgentPausedAsync"/>.</item>
///   <item>Asterisk AMI via a <c>QueuePause</c> action (when an <see cref="IAmiConnection"/> is available).</item>
/// </list>
/// Both operations are best-effort; failures are logged without interrupting the event stream.
/// </summary>
internal sealed class RealtimeStateBridge : IHostedService, IDisposable
{
    private readonly PlatformEventBus _eventBus;
    private readonly IRealtimeSyncService _syncService;
    private readonly IAmiConnection? _ami;
    private readonly ILogger<RealtimeStateBridge> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new();
    private IDisposable? _subscription;

    public RealtimeStateBridge(
        PlatformEventBus eventBus,
        IRealtimeSyncService syncService,
        ILogger<RealtimeStateBridge> logger,
        IAmiConnection? ami = null)
    {
        _eventBus = eventBus;
        _syncService = syncService;
        _logger = logger;
        _ami = ami;
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
                _logger.LogWarning(ex, "Failed to sync paused state to DB for {AgentId}", e.AgentId);
            }

            // 2. AMI QueuePause (best-effort)
            if (_ami is not null)
            {
                try
                {
                    await _ami.SendActionAsync(new QueuePauseAction
                    {
                        Interface = iface,
                        Paused = shouldPause,
                        Reason = e.NewState,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send QueuePause for {AgentId}", e.AgentId);
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
}
