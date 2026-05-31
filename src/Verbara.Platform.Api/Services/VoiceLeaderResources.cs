namespace Verbara.Platform.Api.Services;

/// <summary>
/// Central registry of Pro.Cluster leader-election resource names used by
/// <c>Verbara.Platform.Api</c>. Mirrors <c>RealtimeLeaderResources</c> in the
/// Realtime microservice: the keyed-DI registration
/// (<see cref="Verbara.Sdk.Pro.Cluster.Leadership.VerbaraClusterOptionsBuilder.RegisterLeader(string)"/>)
/// and the consuming <c>[FromKeyedServices]</c> attribute reference the same
/// constant so they can never drift.
/// </summary>
internal static class VoiceLeaderResources
{
    /// <summary>
    /// Per-resource leader that gates <see cref="StasisInboundConsumer"/>'s ARI
    /// WebSocket connection. Asterisk delivers a Stasis application to exactly
    /// ONE WebSocket, and a physical inbound call cannot be re-emitted — so only
    /// the elected leader pod connects + consumes. Non-leader pods stay
    /// disconnected (stronger than the relay's per-event short-circuit).
    /// </summary>
    public const string Inbound = "voice:stasis:inbound:leader";

    /// <summary>
    /// Per-resource leader that designates the single pod allowed to EMIT AMI-driven
    /// voice side-effects (creating the tracked voice <c>Conversation</c>, capacity
    /// reserve/release, agent presence Busy/ACW transitions, and stamping the resolved
    /// tenant onto the live <c>CallSession</c>). Every pod's <c>CallSessionManager</c>
    /// observes the same broadcast AMI stream (warm standby), but only the leader acts —
    /// so the side-effects happen exactly once cluster-wide without a cold-start gap on
    /// failover (the warm-standby / leader-emit pattern). The per-call
    /// <c>voice_linked_id</c> idempotency index (migration 027) is the failover safety net
    /// behind this gate. Distinct from <see cref="Inbound"/> because AMI capability is not
    /// the same as ARI capability — a pod may have one and not the other.
    /// </summary>
    public const string AmiOwner = "voice:ami:owner:leader";
}
