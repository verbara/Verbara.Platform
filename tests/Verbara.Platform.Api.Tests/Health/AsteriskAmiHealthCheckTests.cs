using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Enums;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// readiness-without-asterisk, design D2 (spec <c>api-readiness-scope</c>, requirement 4): the <c>asterisk</c> check
/// reads this pod's primary AMI connection through <see cref="IVoiceConnectionStatus"/> and the voice-plane worker
/// heartbeats with the three-interval rule.
///
/// <para>The circuit cases these tests used to hold encoded a dead path: no code executes a policy under
/// <c>asterisk.ami</c> or <c>asterisk.ari</c>, and <c>worker.asterisk-capacity-sync</c> has no caller (design,
/// Context). The overlay is gone, so the check takes no circuit observer at all.</para>
/// </summary>
public sealed class AsteriskAmiHealthCheckTests
{
    private static Task<HealthCheckResult> CheckAsync(
        FixedVoiceConnectionStatus connection, ServiceHeartbeat? heartbeat = null) =>
        new AsteriskAmiHealthCheck(connection, heartbeat ?? new ServiceHeartbeat())
            .CheckHealthAsync(new HealthCheckContext());

    /// <summary>A registry whose voice inbound worker last beat two hours ago against a one-second interval.</summary>
    private static ServiceHeartbeat StaleVoiceInboundHeartbeat()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTickAt(
            nameof(StasisInboundConsumer), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        return heartbeat;
    }

    private static IReadOnlyDictionary<string, object> Heartbeats(HealthCheckResult result) =>
        (IReadOnlyDictionary<string, object>)result.Data[AsteriskAmiHealthCheck.HeartbeatsDataKey];

    /// <summary>Every <see cref="AmiConnectionState"/> but <c>Connected</c>, so a state the Sdk adds is covered too.</summary>
    public static TheoryData<AmiConnectionState> NotConnectedStates
    {
        get
        {
            var states = new TheoryData<AmiConnectionState>();
            foreach (var state in Enum.GetValues<AmiConnectionState>().Where(s => s != AmiConnectionState.Connected))
                states.Add(state);
            return states;
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenAmiNotConfigured()
    {
        var result = await CheckAsync(FixedVoiceConnectionStatus.NotConfigured());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not configured");
        result.Data[AsteriskAmiHealthCheck.ConnectionStateDataKey].Should().Be(AsteriskAmiHealthCheck.NotConfiguredState);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenPrimaryConnectionIsConnected()
    {
        var result = await CheckAsync(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data[AsteriskAmiHealthCheck.ConnectionStateDataKey].Should().Be("Connected");
    }

    [Theory]
    [MemberData(nameof(NotConnectedStates))]
    public async Task CheckHealthAsync_ShouldReturnUnhealthyWithTheState_WhenPrimaryConnectionIsNotConnected(
        AmiConnectionState state)
    {
        var result = await CheckAsync(FixedVoiceConnectionStatus.Configured(state));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(state.ToString());
        result.Data[AsteriskAmiHealthCheck.ConnectionStateDataKey].Should().Be(state.ToString());
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenConfiguredButPoolHasNoPrimaryServer()
    {
        var result = await CheckAsync(FixedVoiceConnectionStatus.Configured(state: null));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("no primary server");
        result.Data[AsteriskAmiHealthCheck.ConnectionStateDataKey].Should().Be(VoiceConnectionSnapshot.NoPrimaryServer);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthyNamingTheWorker_WhenConnectedButVoiceInboundHeartbeatStale()
    {
        var result = await CheckAsync(
            FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected), StaleVoiceInboundHeartbeat());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(nameof(StasisInboundConsumer));
        var worker = (IReadOnlyDictionary<string, object>)Heartbeats(result)[nameof(StasisInboundConsumer)];
        worker["isHealthy"].Should().Be(false);
        worker["expectedIntervalSeconds"].Should().Be(1.0);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenConnectedAndVoiceInboundHeartbeatFresh()
    {
        // A one-hour interval, so a slow or single-core runner cannot make the tick stale between record and read.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTickAt(nameof(StasisInboundConsumer), TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

        var result = await CheckAsync(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected), heartbeat);

        result.Status.Should().Be(HealthStatus.Healthy);
        var worker = (IReadOnlyDictionary<string, object>)Heartbeats(result)[nameof(StasisInboundConsumer)];
        worker["isHealthy"].Should().Be(true);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldIgnoreDigitalWorkerHeartbeats_WhenTheyAreStale()
    {
        // A digital worker's heartbeat belongs to `services` (readiness), never to the voice plane.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTickAt(
            nameof(QueueDistributionWorker), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var result = await CheckAsync(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected), heartbeat);

        result.Status.Should().Be(HealthStatus.Healthy);
        Heartbeats(result).Should().NotContainKey(nameof(QueueDistributionWorker));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldNameBothTheStateAndTheWorker_WhenNotConnectedAndVoiceInboundHeartbeatStale()
    {
        var result = await CheckAsync(
            FixedVoiceConnectionStatus.Configured(AmiConnectionState.Reconnecting), StaleVoiceInboundHeartbeat());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Reconnecting").And.Contain(nameof(StasisInboundConsumer));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthyAndKeepTheHeartbeatInData_WhenAmiNotConfiguredAndVoiceHeartbeatStale()
    {
        // Spec requirement 4's first row is unconditional: AMI not configured reads Healthy (digital-only). A voice
        // heartbeat that is still recorded stays visible in the data, so nothing is hidden from an operator.
        var result = await CheckAsync(FixedVoiceConnectionStatus.NotConfigured(), StaleVoiceInboundHeartbeat());

        result.Status.Should().Be(HealthStatus.Healthy);
        Heartbeats(result).Should().ContainKey(nameof(StasisInboundConsumer));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReadTheConnectionOnEveryCheck_WhenTheStateChanges()
    {
        // The check holds no state of its own: each run reads the seam again.
        var connection = new SwitchableVoiceConnection(AmiConnectionState.Connected);
        var check = new AsteriskAmiHealthCheck(connection, new ServiceHeartbeat());

        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
        connection.State = AmiConnectionState.Disconnected;

        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
    }

    private sealed class SwitchableVoiceConnection(AmiConnectionState state) : IVoiceConnectionStatus
    {
        public AmiConnectionState State { get; set; } = state;

        public VoiceConnectionSnapshot Read() => new(IsConfigured: true, State);
    }
}
