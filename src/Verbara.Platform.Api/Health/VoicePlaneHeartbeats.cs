using System.Collections.Frozen;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// The voice-plane worker heartbeats (readiness-without-asterisk, design D2/D3): the workers whose input is the
/// Asterisk connection, today only <see cref="StasisInboundConsumer"/>. The <c>asterisk</c> check reports them with the
/// three-interval rule of <see cref="ServiceHeartbeat"/>, and design D3 keeps them out of the <c>services</c> readiness
/// check, so a stalled voice worker never drains the pod. Both checks read this one list. Names are compared
/// ordinally, as the heartbeat registry stores them (<c>nameof</c> of the worker).
/// </summary>
internal static class VoicePlaneHeartbeats
{
    internal static readonly FrozenSet<string> Names = new[]
    {
        nameof(StasisInboundConsumer),
    }.ToFrozenSet(StringComparer.Ordinal);
}
