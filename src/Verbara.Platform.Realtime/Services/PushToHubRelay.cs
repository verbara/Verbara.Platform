using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Bus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Services;

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

/// <summary>
/// Bridges the in-process <see cref="IPushEventBus"/> T27 events to the
/// SignalR <see cref="PlatformHub"/> so connected clients receive real-time
/// conversation, agent, and cluster node state transitions.
///
/// <para>
/// <b>Multi-pod note (ADR-0022 Phase A):</b> Realtime currently ships
/// single-pod (Helm values <c>realtime.hpa.maxReplicas: 1</c>) because
/// Pro.Cluster does not yet expose a leader-election API. When the
/// follow-up phase adds an <c>IClusterLeadershipGate</c> abstraction, the
/// relay will short-circuit on non-leader pods and the SignalR Redis
/// backplane (already wired in <c>Program.cs</c>) will fan the leader's
/// broadcasts to every pod's connected clients.
/// </para>
/// </summary>
public sealed class PushToHubRelay : IHostedService
{
    private readonly IPushEventBus _bus;
    private readonly IHubContext<PlatformHub, IPlatformHubClient> _hubContext;
    private readonly ILogger<PushToHubRelay> _logger;

    private IDisposable? _conversationSubscription;
    private IDisposable? _agentSubscription;
    private IDisposable? _clusterSubscription;

    public PushToHubRelay(
        IPushEventBus bus,
        IHubContext<PlatformHub, IPlatformHubClient> hubContext,
        ILogger<PushToHubRelay> logger)
    {
        _bus = bus;
        _hubContext = hubContext;
        _logger = logger;
    }

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
