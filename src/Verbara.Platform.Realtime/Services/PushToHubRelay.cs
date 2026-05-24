using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Bus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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
/// <b>Multi-pod (ADR-0022 Phase A.5):</b> the relay subscribes on every pod
/// but short-circuits the <see cref="IHubContext{THub,T}.Clients"/> call when
/// this pod is not the elected leader for
/// <see cref="RealtimeLeaderResources.Fanout"/>. The SignalR Redis backplane
/// (wired in <c>Program.cs</c>) then fans the leader's broadcasts to clients
/// connected to every pod, so each event is delivered exactly once
/// platform-wide regardless of replica count.
/// </para>
/// <para>
/// <b>Observability (2026-05-24 audit endpoint):</b> every outcome
/// (Forwarded / SkippedNotLeader / SkippedNullTenant / SkippedNullNodeId /
/// ForwardError) is also pushed to <see cref="IRelayOutcomeSink"/>, which
/// powers (a) the in-memory ring buffer exposed by
/// <c>GET /admin/realtime/audit</c> for E2E harness assertions and (b) the
/// <c>verbara_realtime_relay_outcomes_total</c> counter consumed by
/// Prometheus / OpenTelemetry exporters.
/// </para>
/// </summary>
internal sealed class PushToHubRelay : IHostedService
{
    private readonly IPushEventBus _bus;
    private readonly IHubContext<PlatformHub, IPlatformHubClient> _hubContext;
    private readonly IClusterLeader _fanoutLeader;
    private readonly IRelayOutcomeSink _outcomeSink;
    private readonly ILogger<PushToHubRelay> _logger;

    private IDisposable? _conversationSubscription;
    private IDisposable? _agentSubscription;
    private IDisposable? _clusterSubscription;

    public PushToHubRelay(
        IPushEventBus bus,
        IHubContext<PlatformHub, IPlatformHubClient> hubContext,
        [FromKeyedServices(RealtimeLeaderResources.Fanout)] IClusterLeader fanoutLeader,
        IRelayOutcomeSink outcomeSink,
        ILogger<PushToHubRelay> logger)
    {
        _bus = bus;
        _hubContext = hubContext;
        _fanoutLeader = fanoutLeader;
        _outcomeSink = outcomeSink;
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
        var leaderSnapshot = SnapshotLeader();
        var tenantId = evt.Metadata?.TenantId;

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, evt.EventType, leaderSnapshot.Resource);
            RecordOutcome(evt.EventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            RecordOutcome(evt.EventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNullTenant);
            return;
        }

        var payload = new ConversationStatePayload(
            ConversationId: evt.ConversationId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendConversationAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType, leaderSnapshot);
    }

    private void ForwardAgent(AgentStateChangedEvent evt)
    {
        var leaderSnapshot = SnapshotLeader();
        var tenantId = evt.Metadata?.TenantId;

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, evt.EventType, leaderSnapshot.Resource);
            RecordOutcome(evt.EventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, evt.EventType);
            RecordOutcome(evt.EventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNullTenant);
            return;
        }

        var payload = new AgentStatePayload(
            AgentId: evt.AgentId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ReasonCode: evt.ReasonCode,
            ChangedAt: evt.ChangedAt,
            TenantId: tenantId);

        _ = SendAgentAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType, leaderSnapshot);
    }

    private void ForwardClusterNode(ClusterNodeStateChangedEvent evt)
    {
        var leaderSnapshot = SnapshotLeader();

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, evt.EventType, leaderSnapshot.Resource);
            RecordOutcome(evt.EventType, tenantId: null, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(evt.NodeId))
        {
            PushToHubRelayLog.SkippedNullNodeId(_logger, evt.EventType);
            RecordOutcome(evt.EventType, tenantId: null, leaderSnapshot, RelayOutcomeKind.SkippedNullNodeId);
            return;
        }

        var payload = new ClusterNodeStatePayload(
            NodeId: evt.NodeId,
            PreviousState: evt.PreviousState,
            NewState: evt.NewState,
            ChangedAt: evt.ChangedAt);

        _ = SendClusterAsync(payload, evt.NodeId, evt.EventType, leaderSnapshot);
    }

    private async Task SendConversationAsync(
        string group,
        ConversationStatePayload payload,
        string tenantId,
        string eventType,
        LeaderSnapshot leaderSnapshot)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .OnConversationStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.Forwarded(_logger, eventType, tenantId);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.Forwarded);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.ForwardError, ex.Message);
        }
    }

    private async Task SendAgentAsync(
        string group,
        AgentStatePayload payload,
        string tenantId,
        string eventType,
        LeaderSnapshot leaderSnapshot)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .OnAgentStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.Forwarded(_logger, eventType, tenantId);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.Forwarded);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.ForwardError, ex.Message);
        }
    }

    private async Task SendClusterAsync(
        ClusterNodeStatePayload payload,
        string nodeId,
        string eventType,
        LeaderSnapshot leaderSnapshot)
    {
        try
        {
            await _hubContext.Clients.Group("admins:platform")
                .OnClusterNodeStateChanged(payload)
                .ConfigureAwait(false);

            PushToHubRelayLog.ForwardedCluster(_logger, eventType, nodeId);
            RecordOutcome(eventType, tenantId: null, leaderSnapshot, RelayOutcomeKind.Forwarded);
        }
        catch (Exception ex)
        {
            PushToHubRelayLog.ForwardError(_logger, eventType, ex.Message);
            RecordOutcome(eventType, tenantId: null, leaderSnapshot, RelayOutcomeKind.ForwardError, ex.Message);
        }
    }

    private LeaderSnapshot SnapshotLeader() => new(
        Resource: _fanoutLeader.Resource,
        IsLeader: _fanoutLeader.IsLeader,
        LeaderInstanceId: _fanoutLeader.CurrentLeaderInstanceId);

    private void RecordOutcome(
        string eventType,
        string? tenantId,
        LeaderSnapshot leaderSnapshot,
        RelayOutcomeKind outcome,
        string? detailMessage = null)
    {
        _outcomeSink.Record(new RelayOutcomeEntry(
            Ts: DateTimeOffset.UtcNow,
            EventType: eventType,
            TenantId: tenantId,
            Resource: leaderSnapshot.Resource,
            Outcome: outcome,
            IsLeaderAtTime: leaderSnapshot.IsLeader,
            LeaderInstanceId: leaderSnapshot.LeaderInstanceId,
            PodInstanceId: null,
            DetailMessage: detailMessage));
    }

    private readonly record struct LeaderSnapshot(string Resource, bool IsLeader, string? LeaderInstanceId);
}
