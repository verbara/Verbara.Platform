using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// <see cref="IVoiceConnectionStatus"/> over <c>VerbaraServerPool.GetServer("primary")?.Connection.State</c>, the
/// accessor the voice services already use (<c>VoiceCallControlService</c>, <c>DeferredPrimaryAmiConnection</c>).
/// </summary>
/// <remarks>
/// "Configured" is read from <c>Asterisk:Ami:Hostname</c>, the key Program.cs reads to seed the pool's
/// <c>primary</c> server, and never from <c>IOptions&lt;VerbaraOptions&gt;</c>: no registration configures that
/// options type, so it always carries the Sdk default <c>localhost</c> and reads as configured on every pod.
/// Construction touches no connection (defer-external-seams); each <see cref="Read"/> looks the server up again,
/// so a reconnect or a server added after boot is seen on the next read.
/// </remarks>
internal sealed class PrimaryAmiVoiceConnectionStatus : IVoiceConnectionStatus
{
    /// <summary>The configuration key whose presence means AMI is configured on this pod.</summary>
    internal const string AmiHostnameKey = "Asterisk:Ami:Hostname";

    private const string PrimaryServerId = "primary";

    private readonly VerbaraServerPool _serverPool;
    private readonly bool _isConfigured;

    public PrimaryAmiVoiceConnectionStatus(VerbaraServerPool serverPool, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serverPool);
        ArgumentNullException.ThrowIfNull(configuration);
        _serverPool = serverPool;
        _isConfigured = !string.IsNullOrWhiteSpace(configuration[AmiHostnameKey]);
    }

    public VoiceConnectionSnapshot Read() =>
        new(_isConfigured, _serverPool.GetServer(PrimaryServerId)?.Connection.State);
}
