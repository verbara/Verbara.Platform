using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Channels.Sms.Providers;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Channels.Sms.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The Twilio provider maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class TwilioSmsProviderCircuitRejectionTests
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
        var (policy, probeOutcome, probe) = await HoldProbeAsync(TwilioSmsProvider.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var sut = new TwilioSmsProvider(
            factory, Options.Create(new TwilioOptions { AccountSid = "AC_test", AuthToken = "auth_token" }), policy);

        var act = async () => await sut.SendAsync("+15550001111", "+15559998888", "hello", CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(TwilioSmsProvider.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches Twilio");

        probeOutcome.SetResult(0);
        await probe;
    }
}

/// <summary>Counts the sends that reach the transport; a rejected call must never reach it, so a send fails.</summary>
file sealed class CountingHandler : HttpMessageHandler
{
    private int _attempts;

    public int Attempts => _attempts;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        throw new InvalidOperationException("a call the circuit rejected reached the transport");
    }
}
