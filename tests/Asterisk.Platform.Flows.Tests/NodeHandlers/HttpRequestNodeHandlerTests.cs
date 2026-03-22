using System.Net;
using System.Net.Http;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Flows.Nodes;
using Asterisk.Platform.Flows.Tests.Helpers;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests.NodeHandlers;

public sealed class HttpRequestNodeHandlerTests
{
    private static FlowExecution MakeExecution() =>
        FlowTestHelpers.MakeRunningExecution(EntityId.New(), EntityId.New(), EntityId.New());

    private static HttpRequestNodeHandler BuildHandler(HttpStatusCode status, string responseBody)
    {
        var fakeHandler = new FakeHttpMessageHandler(status, responseBody);
        var httpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("http://test/") };
        return new HttpRequestNodeHandler(httpClient, new TemplateRenderer());
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

    private sealed class FakeHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
