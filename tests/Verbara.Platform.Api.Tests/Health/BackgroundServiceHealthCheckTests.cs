using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// The <c>services</c> readiness check (readiness-without-asterisk, design D3; spec <c>api-readiness-scope</c>,
/// requirement 3): the digital worker heartbeats keep gating readiness, the voice-plane heartbeats
/// (<see cref="VoicePlaneHeartbeats"/>) are left to the <c>asterisk</c> check, and a dependency circuit makes the
/// check <c>Degraded</c> at most, however long it has been open.
///
/// <para>The old "open more than 5 min gives Unhealthy" case encoded the escalation that let one tenant's unreachable
/// webhook receiver, or its identity provider, drain the pod for every tenant. It now gives Degraded. Each observer
/// is constructed and never started, so no gauge reading can overwrite the seeded snapshots.</para>
/// </summary>
public class BackgroundServiceHealthCheckTests
{
    private static readonly TimeSpan PastEscalationThreshold = TimeSpan.FromMinutes(10);

    private static BackgroundServiceHealthCheck BuildCheck(ServiceHeartbeat heartbeat, ResilienceStateObserver observer) =>
        new(heartbeat, observer, Options.Create(new PlatformHealthCheckOptions()));

    private static IReadOnlyDictionary<string, object> Circuits(HealthCheckResult result) =>
        (IReadOnlyDictionary<string, object>)result.Data["circuits"];

    private static IReadOnlyDictionary<string, object> Heartbeats(HealthCheckResult result) =>
        (IReadOnlyDictionary<string, object>)result.Data["heartbeats"];

    public static TheoryData<string> WatchedKeys => new() { "webhook.delivery", "oidc.token-exchange" };

    public static TheoryData<string> KeysTheApiNeverExecutes => new() { "smtp.send", "worker.asterisk-capacity-sync" };

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenAllHeartbeatsOkAndNoCircuits()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        heartbeat.RecordTick("Worker2", TimeSpan.FromSeconds(10));

        var result = await BuildCheck(heartbeat, new ResilienceStateObserver()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenHeartbeatStale()
    {
        var heartbeat = new ServiceHeartbeat();
        // Force stale deterministically: record a tick 2h in the past with a 1ms expected
        // interval. IsHealthy reads real UtcNow, so elapsed (~2h) far exceeds 1ms * 3 → stale,
        // with no wall-clock sleep.
        heartbeat.RecordTickAt("Worker1", TimeSpan.FromMilliseconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var result = await BuildCheck(heartbeat, new ResilienceStateObserver()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Worker1");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenWorkerCircuitOpenLessThan5Min()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "webhook.delivery",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("webhook.delivery");
    }

    [Theory]
    [MemberData(nameof(WatchedKeys))]
    public async Task CheckHealthAsync_ShouldReturnDegradedWithTheKeyStateInData_WhenWatchedCircuitOpenMoreThan5Min(string key)
    {
        // Was "gives Unhealthy" (smtp.send, a key the Api never executes): a dependency circuit is Degraded at most.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(key, ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded, "an open dependency circuit never gates readiness");
        result.Description.Should().Contain(key);
        Circuits(result)[key].ToString().Should().Contain("state = Open", "the key's state stays visible in the data");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenWatchedCircuitOpenForADay()
    {
        // "Whatever its duration": no second threshold sits behind the first one.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest("webhook.delivery", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - TimeSpan.FromDays(1));

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnDegradedNamingEveryOpenKey_WhenOneCircuitOpenPastThresholdAndAnotherRecent()
    {
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest("webhook.delivery", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);
        observer.SetForTest("oidc.token-exchange", ResilienceCircuitState.Open, DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("webhook.delivery").And.Contain("oidc.token-exchange",
            "with no escalation, every open key is named, not only the long-open ones");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldPreferHeartbeatUnhealthy_OverCircuitDegraded()
    {
        var heartbeat = new ServiceHeartbeat();
        // Force stale deterministically (see CheckHealthAsync_ShouldReturnUnhealthy_WhenHeartbeatStale).
        heartbeat.RecordTickAt("StaleWorker", TimeSpan.FromMilliseconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(
            "webhook.delivery",
            ResilienceCircuitState.Open,
            DateTimeOffset.UtcNow - PastEscalationThreshold);

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        // Heartbeat-driven Unhealthy dominates over circuit-driven Degraded.
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("StaleWorker");
    }

    [Fact]
    public void WorkerWatchedPolicyKeys_ShouldBeTheTwoDependencyCircuitsTheApiExecutes_WhenDefault()
    {
        new PlatformHealthCheckOptions().WorkerWatchedPolicyKeys.Should().Equal("webhook.delivery", "oidc.token-exchange");
    }

    [Theory]
    [MemberData(nameof(KeysTheApiNeverExecutes))]
    public async Task CheckHealthAsync_ShouldNotWatchTheKey_WhenTheApiNeverExecutesIt(string key)
    {
        // smtp.send runs only in the Mail service; worker.asterisk-capacity-sync has no caller and is voice-plane.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTick("Worker1", TimeSpan.FromSeconds(10));
        var observer = new ResilienceStateObserver();
        observer.SetForTest(key, ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var result = await BuildCheck(heartbeat, observer).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy, "an unwatched key never reaches the check");
        Circuits(result).Should().NotContainKey(key);
        Circuits(result).Keys.Should().BeEquivalentTo("webhook.delivery", "oidc.token-exchange");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldIgnoreAndOmitTheVoiceInboundHeartbeat_WhenItIsStale()
    {
        // The voice inbound worker's heartbeat is voice-plane: the asterisk check reports it (design D2), and a
        // stalled one never drains the pod. The digital heartbeat next to it still counts.
        var heartbeat = new ServiceHeartbeat();
        heartbeat.RecordTickAt(
            nameof(StasisInboundConsumer), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        heartbeat.RecordTick(nameof(QueueDistributionWorker), TimeSpan.FromHours(1));

        var result = await BuildCheck(heartbeat, new ResilienceStateObserver()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().NotContain(nameof(StasisInboundConsumer));
        Heartbeats(result).Should().NotContainKey(nameof(StasisInboundConsumer));
        Heartbeats(result).Should().ContainKey(nameof(QueueDistributionWorker));
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnUnhealthyNamingOnlyTheDigitalWorker_WhenBothItAndTheVoiceInboundWorkerAreStale()
    {
        var heartbeat = new ServiceHeartbeat();
        var twoHoursAgo = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        heartbeat.RecordTickAt(nameof(StasisInboundConsumer), TimeSpan.FromSeconds(1), twoHoursAgo);
        heartbeat.RecordTickAt(nameof(QueueDistributionWorker), TimeSpan.FromSeconds(1), twoHoursAgo);

        var result = await BuildCheck(heartbeat, new ResilienceStateObserver()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(nameof(QueueDistributionWorker));
        result.Description.Should().NotContain(nameof(StasisInboundConsumer));
    }
}
