using Verbara.Sdk.Enums;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// This pod's view of the voice plane's connection: the primary AMI connection (readiness-without-asterisk,
/// design D2/D5). The <c>asterisk</c> health check and the voice action-route gate both read it, so they never
/// disagree about whether AMI is up.
/// </summary>
public interface IVoiceConnectionStatus
{
    /// <summary>
    /// Reads the connection now. Every call reads the live state; nothing is cached, so a caller that needs one
    /// consistent answer reads once and uses the snapshot.
    /// </summary>
    VoiceConnectionSnapshot Read();
}

/// <summary>One reading of the primary AMI connection.</summary>
/// <param name="IsConfigured">Whether <c>Asterisk:Ami:Hostname</c> is set, the key that seeds the <c>primary</c> server.</param>
/// <param name="State">The primary connection's state, or <see langword="null"/> when the pool holds no primary server.</param>
public readonly record struct VoiceConnectionSnapshot(bool IsConfigured, AmiConnectionState? State)
{
    /// <summary>The <see cref="StateName"/> reported when the pool holds no primary server.</summary>
    public const string NoPrimaryServer = "NoPrimaryServer";

    /// <summary>True only when a primary server exists and its connection is <see cref="AmiConnectionState.Connected"/>.</summary>
    public bool IsConnected => State == AmiConnectionState.Connected;

    /// <summary>The state as reported in health data: the enum name, or <see cref="NoPrimaryServer"/>.</summary>
    public string StateName => State?.ToString() ?? NoPrimaryServer;
}
