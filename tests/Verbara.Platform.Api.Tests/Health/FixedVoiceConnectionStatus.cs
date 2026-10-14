using Verbara.Platform.Api.Health;
using Verbara.Sdk.Enums;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// A fixed reading of the voice connection seam (readiness-without-asterisk, design D2/D5), for the <c>asterisk</c>
/// check's unit tests and for the hosts that replace the real seam in DI.
/// </summary>
internal sealed class FixedVoiceConnectionStatus(VoiceConnectionSnapshot snapshot) : IVoiceConnectionStatus
{
    /// <summary>AMI configured, with the primary connection in <paramref name="state"/> (<see langword="null"/>: no primary server).</summary>
    public static FixedVoiceConnectionStatus Configured(AmiConnectionState? state) => new(new VoiceConnectionSnapshot(true, state));

    /// <summary><c>Asterisk:Ami:Hostname</c> not set: a digital-only pod.</summary>
    public static FixedVoiceConnectionStatus NotConfigured() => new(new VoiceConnectionSnapshot(false, null));

    public VoiceConnectionSnapshot Read() => snapshot;
}
