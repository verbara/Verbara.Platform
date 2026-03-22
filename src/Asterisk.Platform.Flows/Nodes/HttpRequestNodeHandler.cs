using System.Net.Http.Headers;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>http_request</c>.
/// Makes an HTTP request with a rendered URL and optional body, storing the response body
/// in <c>execution.Variables[response_variable]</c> (default: <c>http_response</c>).
/// Config keys: <c>url</c>, <c>method</c> (default GET), <c>body</c>, <c>content_type</c>,
/// <c>response_variable</c>.
/// </summary>
internal sealed class HttpRequestNodeHandler : IFlowNodeHandler
{
    private readonly HttpClient _http;
    private readonly ITemplateRenderer _renderer;

    public HttpRequestNodeHandler(HttpClient http, ITemplateRenderer renderer)
    {
        _http = http;
        _renderer = renderer;
    }

    public string NodeType => "http_request";

    public async Task<FlowNodeResult> ExecuteAsync(
        FlowNode node,
        FlowExecution execution,
        MessageEnvelope? inboundMessage,
        CancellationToken ct)
    {
        node.Config.TryGetValue("url", out var urlTemplate);
        node.Config.TryGetValue("method", out var method);
        node.Config.TryGetValue("body", out var bodyTemplate);
        node.Config.TryGetValue("content_type", out var contentType);
        node.Config.TryGetValue("response_variable", out var responseVariable);

        var url = _renderer.Render(urlTemplate ?? string.Empty, execution.Variables);
        var httpMethod = string.IsNullOrEmpty(method)
            ? HttpMethod.Get
            : new HttpMethod(method.ToUpperInvariant());

        using var request = new HttpRequestMessage(httpMethod, url);

        if (!string.IsNullOrEmpty(bodyTemplate))
        {
            var body = _renderer.Render(bodyTemplate, execution.Variables);
            var ct2 = contentType ?? "application/json";
            request.Content = new StringContent(body, new MediaTypeHeaderValue(ct2));
        }

        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var varName = string.IsNullOrEmpty(responseVariable) ? "http_response" : responseVariable;
        execution.Variables[varName] = responseBody;
        execution.Variables["http_status"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var condition = response.IsSuccessStatusCode ? "success" : "error";
        return new FlowNodeResult(NextEdgeCondition: condition, WaitForInput: false);
    }
}
