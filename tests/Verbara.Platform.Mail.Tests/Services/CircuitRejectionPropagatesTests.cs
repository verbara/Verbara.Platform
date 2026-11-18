using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Resilience;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The SMTP sender and the Graph mailbox service map no transport failure to a result, so the rejection reaches each one's caller
/// unchanged, and the wrapped call never runs.
/// </summary>
public sealed class CircuitRejectionPropagatesTests
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
    public async Task SmtpSender_SendAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(SmtpSender.ResiliencePolicyKey);
        var sends = 0;
        var sut = new SmtpSender(
            MsOptions.Create(new SmtpOptions
            {
                Host = "smtp.example.com",
                Port = 587,
                UseTls = true,
                FromAddress = "noreply@example.com",
                FromName = "Test Platform",
            }),
            NullLogger<SmtpSender>.Instance,
            policy,
            (_, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.CompletedTask;
            });

        var act = async () => await sut.SendAsync(
            new EmailMessage { Subject = "Hello", TextBody = "body", Recipients = [new EmailRecipient("to@example.com")] },
            CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(SmtpSender.ResiliencePolicyKey);
        sends.Should().Be(0, "a rejected call never reaches the SMTP server");

        probeOutcome.SetResult(0);
        await probe;
    }

    [Fact]
    public async Task GraphMailboxService_ListMessagesAsync_ShouldPropagateTheRejection_WhenTheCircuitRejectsTheCall()
    {
        var (policy, probeOutcome, probe) = await HoldProbeAsync(GraphMailboxService.ResiliencePolicyKey);
        var handler = new CountingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") });
        var sut = new GraphMailboxService(factory, NullLogger<GraphMailboxService>.Instance, policy);

        var act = async () => await sut.ListMessagesAsync(
            "test-token", unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        (await act.Should().ThrowAsync<CircuitBreakerOpenException>())
            .Which.Key.Should().Be(GraphMailboxService.ResiliencePolicyKey);
        handler.Attempts.Should().Be(0, "a rejected call never reaches Microsoft Graph");

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
