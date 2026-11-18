using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.Reports;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The mail service clients and the PDF renderer map no transport failure to a result, so the rejection reaches each one's caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class CircuitRejectionPropagatesTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://dependency.test.invalid") });
        return factory;
    }

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
    public async Task HttpEmailService_SendAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(HttpEmailService.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var sut = new HttpEmailService(
            FactoryFor(handler), NullLogger<HttpEmailService>.Instance, policy);

        var act = async () => await sut.SendAsync(
            new EmailMessage { Subject = "Hello", TextBody = "body", Recipients = [new EmailRecipient("to@example.com")] },
            CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(HttpEmailService.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches the mail service");

        probeOutcome.SetResult(0);
        await probe;
    }

    [Fact]
    public async Task HttpEmailTemplateService_Render_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(HttpEmailTemplateService.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var sut = new HttpEmailTemplateService(
            FactoryFor(handler), NullLogger<HttpEmailTemplateService>.Instance, policy);
        var branding = new BrandingContext(
            "Acme", null, "#000000", "#111111", "#222222", null, null, "Acme", "noreply@example.com");

        var act = () => sut.Render("welcome", branding, new Dictionary<string, string>());

        act.Should().Throw<CircuitBreakerOpenException>()
            .Which.Key.Should().Be(HttpEmailTemplateService.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches the mail service");

        probeOutcome.SetResult(0);
        await probe;
    }

    [Fact]
    public async Task HttpPdfReportRenderer_RenderAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(HttpPdfReportRenderer.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var sut = new HttpPdfReportRenderer(
            FactoryFor(handler), NullLogger<HttpPdfReportRenderer>.Instance, policy);
        var data = new ReportData
        {
            ReportName = "test-report",
            TenantName = "tenant-1",
            ReportType = "agent-performance",
            From = DateTimeOffset.UnixEpoch,
            To = DateTimeOffset.UnixEpoch.AddDays(7),
            GeneratedAt = DateTimeOffset.UnixEpoch.AddDays(7),
        };

        var act = async () => await sut.RenderAsync(data, CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(HttpPdfReportRenderer.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches the renderer");

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
