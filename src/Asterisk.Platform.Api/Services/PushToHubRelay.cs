using Asterisk.Sdk.Pro.Push.SignalR.Events;
using Asterisk.Sdk.Pro.Push.SignalR.Hubs;
using Asterisk.Sdk.Push.Bus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

// ---------------------------------------------------------------------------
// Log messages
// ---------------------------------------------------------------------------
internal static partial class PushToHubRelayLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[RELAY] Skipping event {EventType}: TenantId is null or empty.")]
    public static partial void SkippedNullTenant(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[RELAY] Skipping cluster event {EventType}: NodeId is null or empty.")]
    public static partial void SkippedNullNodeId(ILogger logger, string eventType);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[RELAY] Forwarded {EventType} for tenant={TenantId} to SignalR group.")]
    public static partial void Forwarded(ILogger logger, string eventType, string tenantId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[RELAY] Forwarded {EventType} for node={NodeId} to admins:platform group.")]
    public static partial void ForwardedCluster(ILogger logger, string eventType, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[RELAY] Error forwarding {EventType}: {Reason}")]
    public static partial void ForwardError(ILogger logger, string eventType, string reason);
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// <summary>
/// Bridges the in-process <see cref="IPushEventBus"/> T27 events to the
/// SignalR <see cref="PlatformHub"/> so connected clients receive real-time
/// conversation, agent, and cluster node state transitions.
///
/// Subscribes to <see cref="ConversationStateChangedEvent"/>,
/// <see cref="AgentStateChangedEvent"/>, and <see cref="ClusterNodeStateChangedEvent"/>
/// on <see cref="StartAsync"/> and disposes the subscriptions on <see cref="StopAsync"/>.
/// Conversation and agent events whose <c>Metadata.TenantId</c> is null or empty are
/// silently skipped. Cluster events whose <c>NodeId</c> is null or empty are skipped.
/// </summary>
public sealed class PushToHubRelay : IHostedService
{
    private readonly IPushEventBus _bus;
    private readonly IHubContext<PlatformHub, IPlatformHubClient> _hubContext;
    private readonly ILogger<PushToHubRelay> _logger;

    private IDisposable? _conversationSubscription;
    private IDisposable? _agentSubscription;
    private IDisposable? _clusterSubscription;

    /// <summary>Creates a new relay instance.</summary>
    public PushToHubRelay(
        IPushEventBus bus,
        IHubContext<PlatformHub, IPlatformHubClient> hubContext,
        ILogger<PushToHubRelay> logger)
    {
        _bus = bus;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _conversationSubscription = _bus.OfType<ConversationStateChangedEvent>()
            .Subscribe(evt => ForwardConversation(evt));

        _agentSubscription = _bus.OfType<AgentStateChangedEvent>()
            .Subscribe(evt => ForwardAgent(evt));

        _clusterSubscription = _bus.OfType<ClusterNodeStateChangedEvent>()
            .Subscribe(evt => ForwardClusterNode(evt));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _conversationSubscription?.Dispose();
        _agentSubscription?.Dispose();
        _clusterSubscription?.Dispose();
        _conversationSubscription = null;
        _agentSubscription = null;
        _clusterSubscription = null;
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Internal forwarding helpers (fire-and-forget; errors logged, not thrown)
    // -----------------------------------------------------------------------

    private void ForwardConversation(ConversationStateChangedEvent evt)
    {
        var tenantId = evt.Metadata?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            return;
        }

        var payload = new ConversationStatePayload(
            ConversationId: evt.ConversationId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendConversationAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType);
    }

    private void ForwardAgent(AgentStateChangedEvent evt)
    {
        var tenantId = evt.Metadata?.TenantId;
        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            return;
        }

        var payload = new AgentStatePayload(
            AgentId: evt.AgentId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ReasonCode: evt.ReasonCode,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendAgentAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType);
    }

    private void ForwardClusterNode(ClusterNodeStateChangedEvent evt)
    {
        if (string.IsNullOrEmpty(evt.NodeId))
        {
            PushToHubRelayLog.SkippedNullNodeId(_logger, evt.EventType);
            return;
        }

        var payload = new ClusterNodeStatePayload(
            NodeId: evt.NodeId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ChangedAt: evt.ChangedAt);

        _ = SendClusterAsync(payload, evt.NodeId, evt.EventType);
    }

    private async Task SendConversationAsync(string group, ConversationStatePayload payload, string tenantId, string eventType)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .OnConversationStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.Forwarded(_logger, eventType, tenantId);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
        }
    }

    private async Task SendAgentAsync(string group, AgentStatePayload payload, string tenantId, string eventType)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .OnAgentStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.Forwarded(_logger, eventType, tenantId);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
        }
    }

    private async Task SendClusterAsync(ClusterNodeStatePayload payload, string nodeId, string eventType)
    {
        try
        {
            await _hubContext.Clients.Group("admins:platform")
                .OnClusterNodeStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.ForwardedCluster(_logger, eventType, nodeId);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
        }
    }
}
