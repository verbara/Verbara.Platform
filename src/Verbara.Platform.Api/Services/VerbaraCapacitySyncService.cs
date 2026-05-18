using System.Reactive.Linq;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Bridges voice call events (from Asterisk AMI) with the digital capacity tracker.
/// When an agent takes a voice call, their digital availability is updated, and vice versa.
/// </summary>
internal sealed partial class VerbaraCapacitySyncService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-call <see cref="ResiliencePolicy"/> that wraps each
    /// Reserve/Release operation against <see cref="IAgentCapacityService"/>.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.asterisk-capacity-sync";

    private readonly IAgentCapacityService _capacityService;
    private readonly IAgentStore _agentStore;
    private readonly PlatformEventBus _eventBus;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<VerbaraCapacitySyncService> _logger;
    private IDisposable? _subscription;

    public VerbaraCapacitySyncService(
        IAgentCapacityService capacityService,
        IAgentStore agentStore,
        PlatformEventBus eventBus,
        ILogger<VerbaraCapacitySyncService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _capacityService = capacityService;
        _agentStore = agentStore;
        _eventBus = eventBus;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    /// <summary>
    /// Exposes whether the upstream Rx subscription is still attached. Health checks
    /// can short-circuit to Unhealthy when this returns <c>false</c> (i.e.
    /// <see cref="HandleSubscriptionFault"/> has nullified it after an <c>OnError</c>).
    /// </summary>
    internal bool IsSubscriptionHealthy => Volatile.Read(ref _subscription) is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _subscription = _eventBus.Events
                .OfType<AgentCapacityChangedEvent>()
                .Subscribe(
                    onNext: evt => HandleCapacityChangedSafely(evt),
                    onError: HandleSubscriptionFault,
                    onCompleted: () => { /* normal completion — host shutting down */ });

            stoppingToken.Register(() => _subscription?.Dispose());

            // Keep the BackgroundService alive while the Rx subscription does the work.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fatal crash — Critical log + rethrow so the host can stop and be
            // restarted by the orchestrator. Never swallow.
            LogWorkerCrash(nameof(VerbaraCapacitySyncService), ex.Message, ex);
            throw;
        }
    }

    private void HandleCapacityChangedSafely(AgentCapacityChangedEvent evt)
    {
        try
        {
            // Fire-and-forget is intentional — HandleCapacityChangedAsync is fully
            // self-contained (currently returns Task.CompletedTask). This wrapper
            // catches synchronous throws + future await-pre-throws.
            _ = HandleCapacityChangedAsync(evt);
        }
        catch (Exception ex)
        {
            LogFireAndForgetSwallowed(ex.Message);
        }
    }

    private void HandleSubscriptionFault(Exception ex)
    {
        LogSubscriptionFault(ex.Message);
        // Nullify so health checks can report Unhealthy via IsSubscriptionHealthy.
        Interlocked.Exchange(ref _subscription, null);
    }

    /// <summary>
    /// Called when AMI reports a voice call started.
    /// Resolves agent from PJSIP extension and reserves voice capacity.
    /// </summary>
    internal async Task HandleVoiceCallStartedAsync(string tenantId, string extension, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var agent = await _agentStore.GetByExtensionAsync(tid, extension, ct);
        if (agent is null)
        {
            LogAgentNotFound(extension, tenantId);
            return;
        }

        try
        {
            await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    await _capacityService.ReserveAsync(tid, agent.AgentId, ChannelType.Voice, innerCt);
                    return 0;
                },
                ct);
            LogVoiceReserved(agent.AgentId.Value, extension);
        }
        catch (CircuitBreakerOpenException)
        {
            LogCircuitOpen(agent.AgentId.Value, "reserve");
        }
    }

    /// <summary>
    /// Called when AMI reports a voice call ended.
    /// Resolves agent from PJSIP extension and releases voice capacity.
    /// </summary>
    internal async Task HandleVoiceCallEndedAsync(string tenantId, string extension, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var agent = await _agentStore.GetByExtensionAsync(tid, extension, ct);
        if (agent is null)
        {
            LogAgentNotFound(extension, tenantId);
            return;
        }

        try
        {
            await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    await _capacityService.ReleaseAsync(tid, agent.AgentId, ChannelType.Voice, innerCt);
                    return 0;
                },
                ct);
            LogVoiceReleased(agent.AgentId.Value, extension);
        }
        catch (CircuitBreakerOpenException)
        {
            LogCircuitOpen(agent.AgentId.Value, "release");
        }
    }

    private Task HandleCapacityChangedAsync(AgentCapacityChangedEvent evt)
    {
        // When digital capacity changes affect voice availability,
        // we would send QueuePause/QueueAdd AMI actions here.
        // This requires IAmiClient which will be wired when Asterisk is configured.
        LogCapacityChanged(evt.AgentId, evt.Channel, evt.CurrentLoad, evt.MaxLoad, evt.CanAcceptVoice);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice capacity reserved for agent {AgentId} (ext {Extension})")]
    private partial void LogVoiceReserved(string agentId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice capacity released for agent {AgentId} (ext {Extension})")]
    private partial void LogVoiceReleased(string agentId, string extension);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent not found for extension {Extension} in tenant {TenantId}")]
    private partial void LogAgentNotFound(string extension, string tenantId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capacity changed for agent {AgentId}: {Channel} {CurrentLoad}/{MaxLoad}, canVoice={CanAcceptVoice}")]
    private partial void LogCapacityChanged(string agentId, string channel, int currentLoad, int maxLoad, bool canAcceptVoice);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.asterisk-capacity-sync ({Operation}) — dropping call {AgentId}")]
    private partial void LogCircuitOpen(string agentId, string operation);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[CAPACITY-SYNC] Subscription faulted — health check will report Unhealthy. Reason: {Reason}")]
    private partial void LogSubscriptionFault(string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[CAPACITY-SYNC] Fire-and-forget execution failed: {Reason}")]
    private partial void LogFireAndForgetSwallowed(string reason);
}
