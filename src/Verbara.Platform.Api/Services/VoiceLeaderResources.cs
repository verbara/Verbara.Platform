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
}
