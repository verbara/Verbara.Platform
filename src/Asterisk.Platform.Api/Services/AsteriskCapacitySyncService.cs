using System.Reactive.Linq;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Queues.Services;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Bridges voice call events (from Asterisk AMI) with the digital capacity tracker.
/// When an agent takes a voice call, their digital availability is updated, and vice versa.
/// </summary>
internal sealed partial class AsteriskCapacitySyncService : BackgroundService
{
    private readonly IAgentCapacityService _capacityService;
    private readonly IAgentStore _agentStore;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<AsteriskCapacitySyncService> _logger;
    private IDisposable? _subscription;

    public AsteriskCapacitySyncService(
        IAgentCapacityService capacityService,
        IAgentStore agentStore,
        PlatformEventBus eventBus,
        ILogger<AsteriskCapacitySyncService> logger)
    {
        _capacityService = capacityService;
        _agentStore = agentStore;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Events
            .OfType<AgentCapacityChangedEvent>()
            .Subscribe(evt => _ = HandleCapacityChangedAsync(evt));

        return Task.Delay(Timeout.Infinite, stoppingToken);
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

        await _capacityService.ReserveAsync(tid, agent.AgentId, ChannelType.Voice, ct);
        LogVoiceReserved(agent.AgentId.Value, extension);
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

        await _capacityService.ReleaseAsync(tid, agent.AgentId, ChannelType.Voice, ct);
        LogVoiceReleased(agent.AgentId.Value, extension);
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
}
