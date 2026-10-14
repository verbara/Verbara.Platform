using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// The <c>asterisk</c> check: this pod's voice plane (readiness-without-asterisk, design D2; Platform/ADR-0025
/// addendum 2026-10-13). It is registered with the tag <c>voice</c> only, so it is reported on <c>/health/voice</c>
/// and never gates <c>/health/ready</c>.
/// </summary>
/// <remarks>
/// <para>Its status comes from the primary AMI connection, read through <see cref="IVoiceConnectionStatus"/> (the seam
/// the voice route gate shares), and from the voice-plane worker heartbeats (<see cref="VoicePlaneHeartbeats"/>),
/// stale past three intervals as in <see cref="ServiceHeartbeat"/>:</para>
/// <list type="bullet">
/// <item>AMI not configured: Healthy (digital-only);</item>
/// <item><c>Connected</c>, every voice heartbeat fresh: Healthy;</item>
/// <item>any other state, or no primary server: Unhealthy, with the state in the data;</item>
/// <item><c>Connected</c>, a voice heartbeat stale: Unhealthy, naming the worker.</item>
/// </list>
/// <para>No circuit state feeds it. Nothing executes a policy under <c>asterisk.ami</c> or <c>asterisk.ari</c>, and
/// <c>worker.asterisk-capacity-sync</c> has no caller, so the old overlay could never trip. The body never carries the
/// AMI host or port, because the endpoint is anonymous.</para>
/// </remarks>
internal sealed class AsteriskAmiHealthCheck : IHealthCheck
{
    /// <summary>The data key holding the primary connection's state, <see cref="NotConfiguredState"/> or
    /// <see cref="VoiceConnectionSnapshot.NoPrimaryServer"/>.</summary>
    internal const string ConnectionStateDataKey = "connectionState";

    /// <summary>The data key holding each recorded voice-plane heartbeat.</summary>
    internal const string HeartbeatsDataKey = "heartbeats";

    /// <summary>The <see cref="ConnectionStateDataKey"/> value when <c>Asterisk:Ami:Hostname</c> is not set.</summary>
    internal const string NotConfiguredState = "NotConfigured";

    private readonly IVoiceConnectionStatus _connection;
    private readonly IServiceHeartbeat _heartbeat;

    public AsteriskAmiHealthCheck(IVoiceConnectionStatus connection, IServiceHeartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(heartbeat);
        _connection = connection;
        _heartbeat = heartbeat;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var snapshot = _connection.Read();
        var voiceHeartbeats = _heartbeat.GetAll()
            .Where(kv => VoicePlaneHeartbeats.Names.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ConnectionStateDataKey] = snapshot.IsConfigured ? snapshot.StateName : NotConfiguredState,
            [HeartbeatsDataKey] = voiceHeartbeats.ToDictionary(
                kv => kv.Key, kv => (object)HeartbeatData(kv.Value), StringComparer.Ordinal),
        };

        if (!snapshot.IsConfigured)
            return Task.FromResult(HealthCheckResult.Healthy("Asterisk AMI not configured (digital-only mode)", data));

        var stale = voiceHeartbeats.Where(kv => !kv.Value.IsHealthy).Select(kv => kv.Key).ToList();
        var staleSuffix = stale.Count > 0 ? $"; voice workers stale: {string.Join(", ", stale)}" : string.Empty;

        if (!snapshot.IsConnected)
        {
            var reason = snapshot.State is null
                ? "AMI configured but the server pool holds no primary server"
                : $"AMI primary connection is {snapshot.StateName}";
            return Task.FromResult(HealthCheckResult.Unhealthy(reason + staleSuffix, data: data));
        }

        if (stale.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"AMI primary connection is Connected{staleSuffix}", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("AMI primary connection is Connected", data));
    }

    private static Dictionary<string, object> HeartbeatData(ServiceHeartbeatInfo info) =>
        new(StringComparer.Ordinal)
        {
            ["isHealthy"] = info.IsHealthy,
            ["lastTick"] = info.LastTick,
            ["expectedIntervalSeconds"] = Math.Round(info.ExpectedInterval.TotalSeconds, 1),
        };
}
