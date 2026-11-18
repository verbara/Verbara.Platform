using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Identity.OidcTokenExchange;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Identity.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The token exchange maps no transport failure to a result, so the rejection reaches its caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class OidcTokenExchangeCircuitRejectionTests
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
    public async Task ExchangeCodeAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheTokenPost()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(OidcTokenExchangeService.ResiliencePolicyKey);
        var discoveryJson = JsonSerializer.Serialize(new OidcDiscoveryDocument
        {
            Issuer = "https://idp.example.com",
            TokenEndpoint = "https://idp.example.com/oauth/token",
            JwksUri = "https://idp.example.com/.well-known/jwks.json",
            AuthorizationEndpoint = "https://idp.example.com/authorize",
        }, OidcJsonContext.Default.OidcDiscoveryDocument);
        var handler = new FlakyHttpHandler(discoveryResponse: discoveryJson, tokenResponse: "{}", failTokenPostCount: 0);
        var sut = new OidcTokenExchangeService(
            new MockHttpClientFactory(handler), NullLogger<OidcTokenExchangeService>.Instance, policy);

        var act = async () => await sut.ExchangeCodeAsync(
            "https://idp.example.com", "auth-code-123", "code-verifier-abc",
            "https://app.example.com/callback", "client-id", "client-secret", CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(OidcTokenExchangeService.ResiliencePolicyKey);
        handler.TokenPostAttempts.Should().Be(0, "a rejected call never reaches the identity provider");

        probeOutcome.SetResult(0);
        await probe;
    }
}
