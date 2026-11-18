using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Rcs;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Channels.Rcs.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The RCS connector maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class RcsConnectorCircuitRejectionTests
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
    public async Task SendAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(RcsConnector.ResiliencePolicyKey);
        var provider = Substitute.For<IRcsProvider>();
        var sut = new RcsConnector(provider, policy);

        var act = async () => await sut.SendAsync(
            new OutboundMessage(
                new ChannelAddress(ChannelType.Rcs, "+15559998888"),
                new MessageEnvelope([new TextBlock("hello")]),
                new TenantId("test-tenant"),
                EntityId.From("conv-1")),
            CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(RcsConnector.ResiliencePolicyKey);
        await provider.DidNotReceiveWithAnyArgs().CheckCapabilityAsync(default!, default);
        await provider.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);

        probeOutcome.SetResult(0);
        await probe;
    }
}
