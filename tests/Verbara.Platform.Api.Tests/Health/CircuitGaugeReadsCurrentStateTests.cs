using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// Characterization of the Sdk <c>circuit.state</c> gauge as of Sdk 2.10.x, read through
/// <see cref="ResilienceStateObserver"/>: it reads the breaker's current state, <c>Open</c> while the open window
/// runs, <c>HalfOpen</c> from the end of the window until a probe's result is recorded, and <c>Closed</c> after a
/// successful call. The premise of readiness-without-asterisk design D3, that the gauge stayed <c>Open</c> until a
/// call succeeded, no longer holds: the gauge is no longer sticky. A dependency circuit therefore keeps the
/// <c>services</c> check <c>Degraded</c> at most by rule (<see cref="CircuitHealthEvaluator"/>: any key that is not
/// <c>Closed</c> is <c>Degraded</c>, and only an <c>Open</c> key escalates where a threshold is passed), not because
/// of the gauge.
///
/// <para>Unlike the host tests in <see cref="ReadinessScopeTests"/>, these run the observer against the REAL
/// static gauge, which every test in the assembly shares. Each test therefore uses a key no other test can
/// write (spec <c>test-determinism</c>), and reads only that key. The observer's poll timer and the policy's
/// circuit clock both run on one <see cref="FakeTimeProvider"/>, so time only moves when the test advances it
/// and every poll runs synchronously inside <see cref="FakeTimeProvider.Advance"/>.</para>
/// </summary>
public sealed class CircuitGaugeReadsCurrentStateTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static string UniqueKey() => $"test.circuit-gauge-current-state.{Guid.NewGuid():N}";

    private static ResiliencePolicy TripOnFirstFailure(TimeProvider time) =>
        new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 1, openDuration: OpenWindow)
            .WithTimeProvider(time)
            .Build();

    private static async Task TripAsync(ResiliencePolicy policy, string key)
    {
        var tripped = async () => await policy.ExecuteAsync<int>(
            key, _ => throw new HttpRequestException("receiver unreachable"), CancellationToken.None);
        await tripped.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetSnapshot_ShouldReadHalfOpen_WhenTrippedCircuitSeesNoCallsPastItsOpenWindow()
    {
        var time = new FakeTimeProvider();
        var key = UniqueKey();
        var policy = TripOnFirstFailure(time);
        using var observer = new ResilienceStateObserver(time, PollInterval);
        await observer.StartAsync(CancellationToken.None);

        await TripAsync(policy, key);
        time.Advance(PollInterval);
        var firstSeenOpen = observer.GetSnapshot(key);
        firstSeenOpen.Should().NotBeNull("the observer polled the gauge after the trip");
        firstSeenOpen!.State.Should().Be(ResilienceCircuitState.Open);

        // No call reaches the policy again. Six minutes later the open window (30 s) has long elapsed.
        time.Advance(TimeSpan.FromMinutes(6));

        var later = observer.GetSnapshot(key);
        later.Should().NotBeNull();
        later!.State.Should().Be(ResilienceCircuitState.HalfOpen,
            "the gauge reads the breaker's state: half-open from the end of the window until a probe's result");

        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetSnapshot_ShouldReadClosed_WhenTrippedCircuitLaterSeesASuccess()
    {
        var time = new FakeTimeProvider();
        var key = UniqueKey();
        var policy = TripOnFirstFailure(time);
        using var observer = new ResilienceStateObserver(time, PollInterval);
        await observer.StartAsync(CancellationToken.None);

        await TripAsync(policy, key);
        time.Advance(OpenWindow + PollInterval);
        observer.GetSnapshot(key)!.State.Should().Be(ResilienceCircuitState.HalfOpen,
            "past the open window, before any call, the gauge reads HalfOpen");

        var result = await policy.ExecuteAsync(key, _ => ValueTask.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
        time.Advance(PollInterval);

        observer.GetSnapshot(key)!.State.Should().Be(ResilienceCircuitState.Closed,
            "a successful probe closes the circuit");

        await observer.StopAsync(CancellationToken.None);
    }
}
