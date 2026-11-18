using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Channels.Video;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Channels.Video.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The video session manager maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class VideoSessionManagerCircuitRejectionTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Trips <paramref name="key"/>'s circuit, lets its open window pass and holds the probe in flight, so every
    /// other call to the key is rejected. Complete <c>ProbeOutcome</c> and await <c>Probe</c> to close the circuit.
    /// </summary>
    private static async Task<(ResiliencePolicy Policy, TaskCompletionSource<int> ProbeOutcome, Task Probe)>
        HoldProbeAsync(string key)
    {
        var time = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 1, openDuration: OpenWindow)
            .WithTimeProvider(time)
            .Build();
        var trip = async () => await policy.ExecuteAsync<int>(
            key, _ => throw new HttpRequestException("dependency unreachable"), CancellationToken.None);
        await trip.Should().ThrowAsync<HttpRequestException>();
        time.Advance(OpenWindow + TimeSpan.FromSeconds(1));

        var outcome = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = policy.ExecuteAsync(key, _ => new ValueTask<int>(outcome.Task), CancellationToken.None).AsTask();
        return (policy, outcome, probe);
    }

    [Fact]
    public async Task CreateAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(VideoConnector.ResiliencePolicyKey);
        var transport = Substitute.For<IVideoTransport>();
        var sut = new VideoSessionManager(transport, Options.Create(new VideoOptions()), policy);

        var act = async () => await sut.CreateAsync(
            new VideoSessionRequest(new TenantId("tenant-a"), EntityId.From("conv-1"), EntityId.From("agent-1"), "customer-abc"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(VideoConnector.ResiliencePolicyKey);
        await transport.DidNotReceiveWithAnyArgs().CreateSessionAsync(default!, default);

        probeOutcome.SetResult(0);
        await probe;
    }
}
