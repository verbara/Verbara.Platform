using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Flows.Nodes;
using Verbara.Platform.Flows.Tests.Helpers;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Flows.Tests.NodeHandlers;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The flow HTTP request node maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class HttpRequestNodeHandlerCircuitRejectionTests
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
    public async Task ExecuteAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(HttpRequestNodeHandler.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var sut = new HttpRequestNodeHandler(
            new HttpClient(handler) { BaseAddress = new Uri("http://test/") }, new TemplateRenderer(), policy);
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string> { ["url"] = "http://test/api", ["method"] = "GET" });
        var execution = FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

        var act = async () => await sut.ExecuteAsync(node, execution, null, CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(HttpRequestNodeHandler.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches the endpoint");

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
