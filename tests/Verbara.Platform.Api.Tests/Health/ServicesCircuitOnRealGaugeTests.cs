using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// The <c>services</c> check stays <c>Degraded</c> until a tripped dependency circuit closes (spec
/// <c>api-readiness-scope</c>, "Dependency circuits never gate readiness"). The Sdk <c>circuit.state</c> gauge reads
/// <c>HalfOpen</c> once a tripped circuit's open window has elapsed, whether or not a call came, so a check that
/// counted only <c>Open</c> would read <c>Healthy</c> while the dependency is still down.
///
/// <para>These run the observer against the REAL static gauge (see <see cref="CircuitGaugeReadsCurrentStateTests"/>):
/// each test uses a key no other test writes, and the policy's circuit clock and the observer's poll timer share one
/// <see cref="FakeTimeProvider"/>. <see cref="ResilienceCircuitSnapshot.Duration"/> reads the wall clock while
/// <see cref="ResilienceCircuitSnapshot.StateSince"/> comes from the fake one, so the assertions read the state and
/// the status, never the duration.</para>
/// </summary>
public sealed class ServicesCircuitOnRealGaugeTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static string UniqueKey() => $"test.services-circuit-real-gauge.{Guid.NewGuid():N}";

    private static object CircuitData(HealthCheckResult result, string key) =>
        ((IReadOnlyDictionary<string, object>)result.Data["circuits"])[key];

    [Fact]
    public async Task CheckHealthAsync_ShouldStayDegradedUntilASuccessfulCall_WhenTrippedDependencyCircuitSeesNoCallPastItsWindow()
    {
        var time = new FakeTimeProvider();
        var key = UniqueKey();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 1, openDuration: OpenWindow)
            .WithTimeProvider(time)
            .Build();
        using var observer = new ResilienceStateObserver(time, PollInterval);
        await observer.StartAsync(CancellationToken.None);

        var options = new PlatformHealthCheckOptions();
        options.WorkerWatchedPolicyKeys.Add(key);
        var check = new BackgroundServiceHealthCheck(new ServiceHeartbeat(), observer, Options.Create(options));

        var trip = async () => await policy.ExecuteAsync<int>(
            key, _ => throw new HttpRequestException("receiver unreachable"), CancellationToken.None);
        await trip.Should().ThrowAsync<HttpRequestException>();

        time.Advance(PollInterval);
        var open = await check.CheckHealthAsync(new HealthCheckContext());
        open.Status.Should().Be(HealthStatus.Degraded, "the circuit reads Open inside its window");
        CircuitData(open, key).ToString().Should().Contain("state = Open");

        // No call reaches the policy again. Six minutes after the trip the gauge reads HalfOpen.
        time.Advance(TimeSpan.FromMinutes(6) - PollInterval);
        var idle = await check.CheckHealthAsync(new HealthCheckContext());
        CircuitData(idle, key).ToString().Should().Contain("state = HalfOpen",
            "the gauge reads HalfOpen from the end of the open window until a probe's result is recorded");
        idle.Status.Should().Be(HealthStatus.Degraded,
            "a tripped dependency circuit with no later call is not closed, so services stays Degraded");

        var result = await policy.ExecuteAsync(key, _ => ValueTask.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
        time.Advance(PollInterval);

        var recovered = await check.CheckHealthAsync(new HealthCheckContext());
        CircuitData(recovered, key).ToString().Should().Contain("state = Closed");
        recovered.Status.Should().Be(HealthStatus.Healthy, "a successful probe closes the circuit");

        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void EvaluateCircuits_ShouldReturnDegradedNotUnhealthy_WhenKeyHalfOpenLongerThanTheThreshold()
    {
        // Only an Open key escalates: a half-open circuit admits a probe, so it is not an unbroken outage.
        var key = UniqueKey();
        var observer = new ResilienceStateObserver();
        observer.SetForTest(key, ResilienceCircuitState.HalfOpen, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        var (status, message, _) = CircuitHealthEvaluator.EvaluateCircuits(
            [key], unhealthyThreshold: TimeSpan.FromMinutes(5), observer);

        status.Should().Be(HealthStatus.Degraded, "a half-open key is not closed, and only an Open key escalates");
        message.Should().Contain(key);
    }

    [Fact]
    public void EvaluateCircuits_ShouldNameEachNotClosedKeyWithItsState_WhenNoThresholdIsGiven()
    {
        var open = UniqueKey();
        var halfOpen = UniqueKey();
        var closed = UniqueKey();
        var observer = new ResilienceStateObserver();
        observer.SetForTest(open, ResilienceCircuitState.Open, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        observer.SetForTest(halfOpen, ResilienceCircuitState.HalfOpen, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        observer.SetForTest(closed, ResilienceCircuitState.Closed, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        var (status, message, _) = CircuitHealthEvaluator.EvaluateCircuits(
            [open, halfOpen, closed], unhealthyThreshold: null, observer);

        status.Should().Be(HealthStatus.Degraded, "with no threshold a key that is not closed is Degraded at most");
        message.Should().Be(
            $"Circuit not closed: {open} (Open), {halfOpen} (HalfOpen)",
            "the message names every key that is not closed, with its state, and leaves the closed key out");
    }
}
