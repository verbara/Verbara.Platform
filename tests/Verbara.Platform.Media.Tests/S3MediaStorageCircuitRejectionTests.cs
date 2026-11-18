using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Core;
using Verbara.Platform.Media;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Media.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The S3 storage (whose only mapped failure is a 404 on a read, to no stream) maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class S3MediaStorageCircuitRejectionTests
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
    public async Task UploadAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(S3MediaStorage.ResiliencePolicyKey);
        var client = Substitute.For<IAmazonS3>();
        using var sut = new S3MediaStorage(client, "test-bucket", policy);
        using var content = new MemoryStream([1, 2, 3]);

        var act = async () => await sut.UploadAsync(
            new TenantId("tenant-1"), "file.txt", "text/plain", content, CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(S3MediaStorage.ResiliencePolicyKey);
        await client.DidNotReceiveWithAnyArgs().PutObjectAsync(default!, default);

        probeOutcome.SetResult(0);
        await probe;
    }
}
