namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Single row recorded by <c>PushToHubRelay</c> for every push-event observed
/// on this pod. The relay snapshots <see cref="IsLeaderAtTime"/> +
/// <see cref="LeaderInstanceId"/> at decision time so post-hoc replay can
/// distinguish "I skipped because I wasn't leader" from "I forwarded because
/// I was leader" without racing the live <c>IClusterLeader</c> state.
/// </summary>
/// <param name="Ts">UTC timestamp the relay recorded the outcome.</param>
/// <param name="EventType">The <c>PushEvent.EventType</c> (e.g. <c>conversation.state_changed</c>).</param>
/// <param name="TenantId">Tenant the event was scoped to. Null/empty for cluster-scoped events or when the source metadata was missing the field.</param>
/// <param name="Resource">The leader-election resource consulted (e.g. <c>realtime:fanout:leader</c>).</param>
/// <param name="Outcome">What the relay actually did with the event — see <see cref="RelayOutcomeKind"/>.</param>
/// <param name="IsLeaderAtTime">Snapshot of <c>IClusterLeader.IsLeader</c> taken at decision time.</param>
/// <param name="LeaderInstanceId">Snapshot of <c>IClusterLeader.CurrentLeaderInstanceId</c> at decision time (null when not yet observed).</param>
/// <param name="PodInstanceId">Identity of THIS pod (typically <c>POD_NAME</c> from the K8s downward API; null in dev when unset).</param>
/// <param name="DetailMessage">Optional reason text — populated for <see cref="RelayOutcomeKind.ForwardError"/> with the exception message, null otherwise.</param>
public sealed record RelayOutcomeEntry(
    DateTimeOffset Ts,
    string EventType,
    string? TenantId,
    string Resource,
    RelayOutcomeKind Outcome,
    bool IsLeaderAtTime,
    string? LeaderInstanceId,
    string? PodInstanceId,
    string? DetailMessage);
