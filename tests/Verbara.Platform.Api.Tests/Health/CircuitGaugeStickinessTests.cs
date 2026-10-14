using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// Characterization of design D3's premise (readiness-without-asterisk): the Sdk <c>circuit.state</c> gauge is
/// set to <c>Closed</c> only after a successful call, so a circuit that trips and then sees no traffic still
/// reads <c>Open</c> through <see cref="ResilienceStateObserver"/> long after its open window has elapsed. That
/// is why an open dependency circuit can never be allowed to escalate readiness on duration alone.
///
/// <para>Unlike the host tests in <see cref="ReadinessScopeTests"/>, these run the observer against the REAL
/// static gauge, which every test in the assembly shares. Each test therefore uses a key no other test can
/// write (spec <c>test-determinism</c>), and reads only that key. The observer's poll timer and the policy's
/// circuit clock both run on one <see cref="FakeTimeProvider"/>, so time only moves when the test advances it
/// and every poll runs synchronously inside <see cref="FakeTimeProvider.Advance"/>.</para>
/// </summary>
public sealed class CircuitGaugeStickinessTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static string UniqueKey() => $"test.circuit-gauge-stickiness.{Guid.NewGuid():N}";

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
    public async Task GetSnapshot_ShouldStillReadOpen_WhenTrippedCircuitSeesNoCallsPastItsOpenWindow()
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

        // No call reaches the policy again. Six minutes later the circuit itself would admit a probe (its open
        // window is 30 s), yet nothing has written the gauge since the trip.
        time.Advance(TimeSpan.FromMinutes(6));

        var later = observer.GetSnapshot(key);
        later.Should().NotBeNull();
        later!.State.Should().Be(ResilienceCircuitState.Open,
            "the gauge returns to Closed only after a success, so an idle tripped circuit reads open indefinitely");
        later.StateSince.Should().Be(firstSeenOpen.StateSince,
            "the observer keeps the first time it saw Open, so its duration keeps growing past any threshold");
        (time.GetUtcNow() - later.StateSince).Should().BeGreaterThan(TimeSpan.FromMinutes(5));

        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetSnapshot_ShouldReadClosed_WhenTrippedCircuitLaterSeesASuccess()
    {
        // The other half of the premise: one successful call past the open window is the only way back.
        var time = new FakeTimeProvider();
        var key = UniqueKey();
        var policy = TripOnFirstFailure(time);
        using var observer = new ResilienceStateObserver(time, PollInterval);
        await observer.StartAsync(CancellationToken.None);

        await TripAsync(policy, key);
        time.Advance(OpenWindow + PollInterval);
        observer.GetSnapshot(key)!.State.Should().Be(ResilienceCircuitState.Open);

        var result = await policy.ExecuteAsync(key, _ => ValueTask.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
        time.Advance(PollInterval);

        observer.GetSnapshot(key)!.State.Should().Be(ResilienceCircuitState.Closed);

        await observer.StopAsync(CancellationToken.None);
    }
}
