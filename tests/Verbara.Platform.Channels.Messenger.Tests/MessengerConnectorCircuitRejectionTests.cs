using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Channels.Messenger.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The connector maps that rejection to the failed result its transport-failure arm
/// returns (<c>HTTP_ERROR</c>), and logs the circuit's key.
/// </summary>
public sealed class MessengerConnectorCircuitRejectionTests
{
    private static readonly TimeSpan OpenWindow = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SendAsync_ShouldReturnHttpError_WhenTheCircuitRejectsTheSendWhileAProbeIsInFlight()
    {
        var time = new FakeTimeProvider();
        var policy = new ResiliencePolicyBuilder()
            .WithCircuitBreaker(threshold: 1, openDuration: OpenWindow)
            .WithTimeProvider(time)
            .Build();
        var trip = async () => await policy.ExecuteAsync<int>(
            MessengerConnector.ResiliencePolicyKey,
            _ => throw new HttpRequestException("provider unreachable"),
            CancellationToken.None);
        await trip.Should().ThrowAsync<HttpRequestException>();
        time.Advance(OpenWindow + TimeSpan.FromSeconds(1));

        // The next call after the window is the probe; holding it in flight makes every other call a rejection.
        var probeOutcome = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = policy.ExecuteAsync(
            MessengerConnector.ResiliencePolicyKey,
            _ => new ValueTask<int>(probeOutcome.Task),
            CancellationToken.None).AsTask();

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var handler = new FakeHttpMessageHandler(response);
        var logger = new RecordingLogger<MessengerConnector>();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var connector = new MessengerConnector(
            httpClient,
            Options.Create(new MessengerOptions
            {
                PageId = "page-123",
                PageAccessToken = "EAAtest",
                WebhookVerifyToken = "token",
                AppSecret = "secret",
                ApiVersion = "v21.0",
                BaseUrl = "https://graph.facebook.com",
            }),
            logger,
            policy);

        var result = await connector.SendAsync(
            new OutboundMessage(
                new ChannelAddress(ChannelType.Messenger, "user-456"),
                new MessageEnvelope([new TextBlock("hello")]),
                new TenantId("tenant-a"),
                EntityId.From("conv-1")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HTTP_ERROR");
        handler.LastRequestUri.Should().BeNull("a rejected send never reaches the provider");
        logger.Messages.Should().Contain(
            m => m.Contains(MessengerConnector.ResiliencePolicyKey, StringComparison.Ordinal),
            "the log line names the circuit");

        probeOutcome.SetResult(0);
        await probe;
    }
}

file sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
}
