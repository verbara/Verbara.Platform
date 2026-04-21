using System.Net;
using System.Text;
using Asterisk.Platform.Mail.Endpoints;
using Asterisk.Platform.Mail.Services;
using Asterisk.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Mail.Tests.Services;

public sealed class GraphMailboxServiceTests
{
    private const string AccessToken = "test-token";

    // Minimal Graph v1.0 JSON payload for a Messages list response — enough for the service
    // to return zero messages without parsing individual entries (which exercises the code path
    // around the ResiliencePolicy wrap end-to-end).
    private const string EmptyMessagesResponse = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users('me')/messages","@odata.count":0,"value":[]}
        """;

    private static (GraphMailboxService Service, CountingGraphHandler Handler) BuildSut(
        ResiliencePolicy? policy = null, int failFirstCount = 0)
    {
        var handler = new CountingGraphHandler(EmptyMessagesResponse, failFirstCount);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("MicrosoftGraph")
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
            });

        var sut = new GraphMailboxService(
            httpClientFactory,
            NullLogger<GraphMailboxService>.Instance,
            policy);

        return (sut, handler);
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldInvokeHttpSendOnce_WhenGraphReturnsSuccess()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy);

        var (messages, total) = await sut.ListMessagesAsync(
            AccessToken, unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        handler.Attempts.Should().Be(1);
        messages.Should().BeEmpty();
        total.Should().Be(0);
    }

    [Fact]
    public async Task ListMessagesAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler) = BuildSut(policy, failFirstCount: 1);

        var (_, total) = await sut.ListMessagesAsync(
            AccessToken, unreadOnly: false, top: 10, skip: 0, CancellationToken.None);

        handler.Attempts.Should().Be(2, "first attempt threw HttpRequestException; retry succeeded");
        total.Should().Be(0);
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        GraphMailboxService.ResiliencePolicyKey.Should().Be("mail.graph");
    }

    /// <summary>
    /// Handler that optionally throws transient failures on the first N attempts, then returns
    /// a static successful Graph JSON response. Used to assert that the ResiliencePolicy retry
    /// contract wraps the underlying HttpClient used by the Graph SDK.
    /// </summary>
    private sealed class CountingGraphHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _failFirstCount;
        private int _attempts;

        public CountingGraphHandler(string body, int failFirstCount)
        {
            _body = body;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _attempts);
            if (n <= _failFirstCount)
                throw new HttpRequestException($"transient Graph failure (attempt {n})");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
