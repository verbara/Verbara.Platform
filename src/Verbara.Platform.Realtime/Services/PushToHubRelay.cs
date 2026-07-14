using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Bus;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CoreEvents = Verbara.Platform.Core;
// csat-completion: the typed IPlatformHubClient.OnCsatResponseRecorded takes the Pro-package payload.
// Both Verbara.Platform.Realtime.Contracts.Dtos and Verbara.Sdk.Pro.Push.SignalR.Events define a
// CsatResponseRecordedPayload with the same field shape, so the Pro one is aliased to disambiguate.
using CsatResponseRecordedPayload = Verbara.Sdk.Pro.Push.SignalR.Events.CsatResponseRecordedPayload;

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
    // csat-completion (Platform/ADR-0020): the CSAT recorded event now routes through the strongly-typed
    // _hubContext (IPlatformHubClient.OnCsatResponseRecorded) like every other branch — the untyped
    // IHubContext<PlatformHub> the csat-runner Phase B relay used is retired now the Pro method ships.
    private readonly IClusterLeader _fanoutLeader;
    private readonly IRelayOutcomeSink _outcomeSink;
    private readonly ILogger<PushToHubRelay> _logger;

    private IDisposable? _conversationSubscription;
    private IDisposable? _agentSubscription;
    private IDisposable? _clusterSubscription;
    private IDisposable? _csatSubscription;
    // v2.4.7 — Platform.Api PlatformEventBus publishes Verbara.Platform.Core
    // event types directly (no Core→Pro translator exists), so the Pro-typed
    // subscriptions above never matched anything from the Api side. The Core
    // subscriptions below map field shapes to the same Pro Payloads and fan
    // out through the same SendXxxAsync helpers. Cluster events stay
    // single-source (Pro.Cluster publishes the Pro-typed event internally).
    private IDisposable? _coreConversationSubscription;
    private IDisposable? _coreAgentSubscription;

    // Test-only seam: records the most recently started fire-and-forget send Task.
    // The bus OnNext is synchronous, so this field is assigned during Emit and the
    // SendXAsync continuation (which records its own outcome) runs detached after the
    // first await. Production semantics are unchanged — the send still fires-and-forgets.
    private Task? _lastSend;

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

        _coreConversationSubscription = _bus.OfType<CoreEvents.ConversationStateChangedEvent>()
            .Subscribe(evt => ForwardCoreConversation(evt));

        _coreAgentSubscription = _bus.OfType<CoreEvents.AgentStateChangedEvent>()
            .Subscribe(evt => ForwardCoreAgent(evt));

        // csat-runner Phase B — CSAT recorded → supervisor:{tenantId}.
        _csatSubscription = _bus.OfType<CoreEvents.CsatResponseRecordedEvent>()
            .Subscribe(evt => ForwardCsatRecorded(evt));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _conversationSubscription?.Dispose();
        _agentSubscription?.Dispose();
        _clusterSubscription?.Dispose();
        _coreConversationSubscription?.Dispose();
        _coreAgentSubscription?.Dispose();
        _csatSubscription?.Dispose();
        _conversationSubscription = null;
        _agentSubscription = null;
        _clusterSubscription = null;
        _coreConversationSubscription = null;
        _coreAgentSubscription = null;
        _csatSubscription = null;
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

        _lastSend = SendConversationAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType, leaderSnapshot);
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

        _lastSend = SendAgentAsync($"tenant:{tenantId}", payload, tenantId, evt.EventType, leaderSnapshot);
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

        _lastSend = SendClusterAsync(payload, evt.NodeId, evt.EventType, leaderSnapshot);
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

    // ─── Core event handlers (v2.4.7) ─────────────────────────────────────
    // Platform.Api's PlatformEventBus publishes Verbara.Platform.Core event
    // records (the legacy/canonical shape). These handlers translate field
    // shapes to the Pro.Push.SignalR Payload records the hub clients expect.

    private void ForwardCoreConversation(CoreEvents.ConversationStateChangedEvent evt)
    {
        var leaderSnapshot = SnapshotLeader();
        var tenantId = evt.TenantId;
        var eventType = evt.Type;

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, eventType, leaderSnapshot.Resource);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, eventType);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNullTenant);
            return;
        }

        var payload = new ConversationStatePayload(
            ConversationId: evt.ConversationId,
            PreviousState: evt.OldState,
            NewState: evt.NewState,
            ChangedAt: evt.Timestamp,
            TenantId: tenantId);

        _lastSend = SendConversationAsync($"tenant:{tenantId}", payload, tenantId, eventType, leaderSnapshot);
    }

    private void ForwardCoreAgent(CoreEvents.AgentStateChangedEvent evt)
    {
        var leaderSnapshot = SnapshotLeader();
        var tenantId = evt.TenantId;
        var eventType = evt.Type;

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, eventType, leaderSnapshot.Resource);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, eventType);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNullTenant);
            return;
        }

        var payload = new AgentStatePayload(
            AgentId: evt.AgentId,
            PreviousState: evt.OldState,
            NewState: evt.NewState,
            ReasonCode: null,
            ChangedAt: evt.Timestamp,
            TenantId: tenantId);

        _lastSend = SendAgentAsync($"tenant:{tenantId}", payload, tenantId, eventType, leaderSnapshot);
    }

    // ─── CSAT recorded (csat-runner Phase B) ──────────────────────────────────
    // Routes Verbara.Platform.Core.CsatResponseRecordedEvent to the
    // supervisor:{tenantId} group. Uses the untyped hub context because the
    // strongly-typed IPlatformHubClient (Pro package) has no OnCsatResponseRecorded
    // method yet; the "OnCsatResponseRecorded" client method name is the wire contract.

    private void ForwardCsatRecorded(CoreEvents.CsatResponseRecordedEvent evt)
    {
        var leaderSnapshot = SnapshotLeader();
        var tenantId = evt.TenantId;
        var eventType = evt.Type;

        if (!leaderSnapshot.IsLeader)
        {
            RealtimeLog.SkippedForwardNotLeader(_logger, eventType, leaderSnapshot.Resource);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNotLeader);
            return;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            PushToHubRelayLog.SkippedNullTenant(_logger, eventType);
            RecordOutcome(eventType, tenantId, leaderSnapshot, RelayOutcomeKind.SkippedNullTenant);
            return;
        }

        // The typed IPlatformHubClient.OnCsatResponseRecorded expects the Pro-package payload
        // (Verbara.Sdk.Pro.Push.SignalR.Events.CsatResponseRecordedPayload) — the same field shape as
        // the Platform.Realtime.Contracts payload, disambiguated here by full qualification.
        var payload = new CsatResponseRecordedPayload(
            TenantId: tenantId,
            ResponseId: evt.ResponseId,
            SurveyId: evt.SurveyId,
            ConversationId: evt.ConversationId,
            Channel: evt.Channel,
            QueueName: evt.QueueName,
            Rating: evt.Rating,
            Comment: evt.Comment,
            CapturedAt: evt.CapturedAt);

        _lastSend = SendCsatAsync($"supervisor:{tenantId}", payload, tenantId, eventType, leaderSnapshot);
    }

    // csat-completion (Platform/ADR-0020): the typed IPlatformHubClient.OnCsatResponseRecorded method
    // now exists on the advanced Pro pin, so the CSAT branch routes through the strongly-typed hub
    // context like SendConversationAsync/SendAgentAsync (the untyped name-based relay is retired). The
    // wire method name ("OnCsatResponseRecorded") and the CsatResponseRecordedPayload shape are
    // unchanged (fixtures/csat-response-recorded-payload.v1.json), so no SignalR client observes a
    // change — this is type-safety hardening only. The channel set now includes 'voice'.
    private async Task SendCsatAsync(
        string group,
        CsatResponseRecordedPayload payload,
        string tenantId,
        string eventType,
        LeaderSnapshot leaderSnapshot)
    {
        try
        {
            await _hubContext.Clients.Group(group)
                .OnCsatResponseRecorded(payload)
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

    /// <summary>
    /// Test-only seam: awaits the most recently started fire-and-forget send (or a completed
    /// Task if none has run) against the caller's bounded timeout token. Production never calls
    /// this. Single-field tracking is sufficient because the bus <c>OnNext</c> is synchronous
    /// and each event triggers exactly one send, and tests await once per emit (the multi-emit
    /// test awaits after each) — so "most recent" always equals "the one just started".
    /// SendXAsync swallows its own faults (Forwarded / ForwardError), so the recorded Task
    /// completes normally even when the SignalR send throws.
    /// </summary>
    internal Task WaitForDispatchAsync(CancellationToken cancellationToken) =>
        (_lastSend ?? Task.CompletedTask).WaitAsync(cancellationToken);

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
