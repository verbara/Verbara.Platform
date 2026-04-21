using System.Net;
using System.Net.Http;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using Asterisk.Sdk.Resilience;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class HttpRequestNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    private static ResiliencePolicy BuildPolicy() => new ResiliencePolicyBuilder()
        .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
        .Build();

    private static HttpRequestNodeHandler BuildHandler(HttpStatusCode status, string responseBody, ResiliencePolicy? policy = null)
    {
        var fakeHandler = new FakeHttpMessageHandler(status, responseBody);
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("http://test/") };
        return new HttpRequestNodeHandler(httpClient, new TemplateRenderer(), policy);
    }

    private static HttpRequestNodeHandler BuildHandlerWithRetryingHandler(CountingHttpHandler handler, ResiliencePolicy policy)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test/") };
        return new HttpRequestNodeHandler(httpClient, new TemplateRenderer(), policy);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCallUrl_AndStoreResponse()
    {
        var handler = BuildHandler(HttpStatusCode.OK, "{\"result\":\"ok\"}");
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string>
            {
                ["url"] = "http://test/api/data",
                ["method"] = "GET",
            });

        var execution = MakeExecution();
        var result = await handler.ExecuteAsync(node, execution, null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("success");
        result.WaitForInput.Should().BeFalse();
        execution.Variables["http_response"].Should().Be("{\"result\":\"ok\"}");
        execution.Variables["http_status"].Should().Be("200");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnError_WhenServerReturns500()
    {
        var handler = BuildHandler(HttpStatusCode.InternalServerError, "error");
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string> { ["url"] = "http://test/fail", ["method"] = "GET" });

        var result = await handler.ExecuteAsync(node, MakeExecution(), null, CancellationToken.None);

        result.NextEdgeCondition.Should().Be("error");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInvokeHttpSendOnce_WhenDeliverySucceeds()
    {
        // Happy-path resilience check: single send succeeds, policy is transparent.
        var counting = new CountingHttpHandler(HttpStatusCode.OK, "ok", failFirstCount: 0);
        var sut = BuildHandlerWithRetryingHandler(counting, BuildPolicy());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string> { ["url"] = "http://test/api" });

        await sut.ExecuteAsync(node, MakeExecution(), null, CancellationToken.None);

        counting.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransientError()
    {
        var counting = new CountingHttpHandler(HttpStatusCode.OK, "ok", failFirstCount: 1);
        var sut = BuildHandlerWithRetryingHandler(counting, BuildPolicy());
        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string> { ["url"] = "http://test/api" });

        var execution = MakeExecution();
        await sut.ExecuteAsync(node, execution, null, CancellationToken.None);

        counting.Attempts.Should().Be(2, "first attempt threw, policy retried and succeeded");
        execution.Variables["http_response"].Should().Be("ok");
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        HttpRequestNodeHandler.ResiliencePolicyKey.Should().Be("flow.http-request");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHonorPerCallTimeout_FromFlowConfig()
    {
        // Prove timeout-from-config path wires CancelAfter on the per-call CTS, not on the
        // shared policy. A user-authored flow can set a sub-second deadline independent of
        // the policy's larger upper-bound timeout.
        var slowHandler = new SlowHttpMessageHandler(TimeSpan.FromSeconds(5));
        var httpClient = new HttpClient(slowHandler) { BaseAddress = new Uri("http://test/") };
        // Policy: no retry so we observe exactly one cancellation cycle.
        var policy = new ResiliencePolicyBuilder().Build();
        var sut = new HttpRequestNodeHandler(httpClient, new TemplateRenderer(), policy);

        var node = FlowTestHelpers.MakeNode(EntityId.New(), "http_request",
            new Dictionary<string, string>
            {
                ["url"] = "http://test/slow",
                ["timeout_seconds"] = "0.1",
            });

        var start = DateTimeOffset.UtcNow;
        var act = () => sut.ExecuteAsync(node, MakeExecution(), null, CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
        (DateTimeOffset.UtcNow - start).Should().BeLessThan(TimeSpan.FromSeconds(2),
            "per-call timeout from flow config should cancel well before the slow response completes");
    }

    private sealed class FakeHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _responseStatus;
        private readonly string _body;
        private readonly int _failFirstCount;
        private int _attempts;

        public CountingHttpHandler(HttpStatusCode responseStatus, string body, int failFirstCount)
        {
            _responseStatus = responseStatus;
            _body = body;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failFirstCount)
                throw new HttpRequestException($"transient failure (attempt {attempt})");

            return Task.FromResult(new HttpResponseMessage(_responseStatus) { Content = new StringContent(_body) });
        }
    }

    private sealed class SlowHttpMessageHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("late") };
        }
    }
}
