using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Channels.Twitter.Tests;

/// <summary>
/// From Sdk 2.10.x a half-open circuit admits one probe: once a tripped circuit's open window has elapsed, one call is
/// admitted and every other call to the key is rejected with <see cref="CircuitBreakerOpenException"/> until the
/// probe's result is recorded. The connector maps that rejection to the failed result its transport-failure arm
/// returns (<c>HTTP_ERROR</c>), and logs the circuit's key.
/// </summary>
public sealed class TwitterConnectorCircuitRejectionTests
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
            TwitterConnector.ResiliencePolicyKey,
            _ => throw new HttpRequestException("provider unreachable"),
            CancellationToken.None);
        await trip.Should().ThrowAsync<HttpRequestException>();
        time.Advance(OpenWindow + TimeSpan.FromSeconds(1));

        // The next call after the window is the probe; holding it in flight makes every other call a rejection.
        var probeOutcome = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = policy.ExecuteAsync(
            TwitterConnector.ResiliencePolicyKey,
            _ => new ValueTask<int>(probeOutcome.Task),
            CancellationToken.None).AsTask();

        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var handler = new FakeTwitterHttpHandler(response);
        var logger = new RecordingLogger<TwitterConnector>();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var connector = new TwitterConnector(
            httpClient,
            Options.Create(new TwitterOptions
            {
                ApiKey = "api-key",
                ApiSecret = "api-secret",
                AccessToken = "access-token",
                AccessTokenSecret = "access-token-secret",
                BearerToken = "my-bearer-token",
                BaseUrl = "https://api.x.com",
            }),
            logger,
            policy);

        var result = await connector.SendAsync(
            new OutboundMessage(
                new ChannelAddress(ChannelType.Twitter, ParticipantId),
                new MessageEnvelope([new TextBlock("hello")]),
                new TenantId("tenant-a"),
                EntityId.From("conv-1")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HTTP_ERROR");
        handler.LastRequestUri.Should().BeNull("a rejected send never reaches the provider");
        logger.Messages.Should().Contain(
            m => m.Contains(TwitterConnector.ResiliencePolicyKey, StringComparison.Ordinal),
            "the log line names the circuit");
        logger.Messages.Should().NotContain(
            m => m.Contains(ParticipantId, StringComparison.Ordinal),
            "the request path carries the recipient's user id, so the log leaves the path out");

        probeOutcome.SetResult(0);
        await probe;
    }

    [Fact]
    public async Task SendAsync_ShouldLogWithoutTheRecipientId_WhenTheTransportFails()
    {
        var logger = new RecordingLogger<TwitterConnector>();
        using var httpClient = new HttpClient(new ThrowingHttpHandler(), disposeHandler: true);
        var connector = new TwitterConnector(
            httpClient,
            Options.Create(new TwitterOptions
            {
                ApiKey = "api-key",
                ApiSecret = "api-secret",
                AccessToken = "access-token",
                AccessTokenSecret = "access-token-secret",
                BearerToken = "my-bearer-token",
                BaseUrl = "https://api.x.com",
            }),
            logger);

        var result = await connector.SendAsync(
            new OutboundMessage(
                new ChannelAddress(ChannelType.Twitter, ParticipantId),
                new MessageEnvelope([new TextBlock("hello")]),
                new TenantId("tenant-a"),
                EntityId.From("conv-1")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HTTP_ERROR");
        logger.Messages.Should().ContainSingle("the transport failure is logged once");
        logger.Messages.Should().NotContain(
            m => m.Contains(ParticipantId, StringComparison.Ordinal),
            "the request path carries the recipient's user id, so the log leaves the path out");
    }

    private const string ParticipantId = "participant-1";
}

file sealed class ThrowingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new HttpRequestException("connection refused");
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
