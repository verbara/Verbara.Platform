using System.Globalization;
using System.Net.Http.Headers;
using Asterisk.Platform.Conversations;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Flows.Nodes;

/// <summary>
/// Node type: <c>http_request</c>.
/// Makes an HTTP request with a rendered URL and optional body, storing the response body
/// in <c>execution.Variables[response_variable]</c> (default: <c>http_response</c>).
/// Config keys: <c>url</c>, <c>method</c> (default GET), <c>body</c>, <c>content_type</c>,
/// <c>response_variable</c>, <c>timeout_seconds</c> (per-call deadline; default 30s).
/// </summary>
public sealed class HttpRequestNodeHandler : IFlowNodeHandler
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps the HTTP send.
    /// Circuit + retry only — the per-call timeout comes from the flow's
    /// <c>timeout_seconds</c> config so authors can set domain-appropriate deadlines without
    /// mutating the shared policy budget. See the DI registration in Platform.Api Program.cs
    /// for the exact circuit/retry values.
    /// </summary>
    public const string ResiliencePolicyKey = "flow.http-request";

    /// <summary>
    /// Default per-call timeout applied when the flow config does not specify one.
    /// Kept intentionally generous because user-authored flows call arbitrary third-party
    /// endpoints — policy-level timeout is a larger upper bound, not the operational
    /// deadline (that is enforced via <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>).
    /// </summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ITemplateRenderer _renderer;
    private readonly ResiliencePolicy _policy;

    public HttpRequestNodeHandler(
        HttpClient http,
        ITemplateRenderer renderer,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _http = http;
        _renderer = renderer;
        _policy = policy ?? ResiliencePolicy.NoOp;
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
        node.Config.TryGetValue("timeout_seconds", out var timeoutRaw);

        var url = _renderer.Render(urlTemplate ?? string.Empty, execution.Variables);
        var httpMethod = string.IsNullOrEmpty(method)
            ? HttpMethod.Get
            : new HttpMethod(method.ToUpperInvariant());

        var perCallTimeout = ParseTimeout(timeoutRaw);

        // The ResiliencePolicy owns circuit + retry only; the timeout below is a per-call
        // deadline sourced from flow config (user-configurable) and layered on top of the
        // policy's own (intentionally-large) upper-bound timeout. We cancel the inner send
        // via a linked CTS so each retry attempt gets its own fresh deadline.
        var response = await _policy.ExecuteAsync(
            ResiliencePolicyKey,
            async innerCt =>
            {
                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                perCallCts.CancelAfter(perCallTimeout);

                using var request = new HttpRequestMessage(httpMethod, url);
                if (!string.IsNullOrEmpty(bodyTemplate))
                {
                    var body = _renderer.Render(bodyTemplate, execution.Variables);
                    var ct2 = contentType ?? "application/json";
                    request.Content = new StringContent(body, new MediaTypeHeaderValue(ct2));
                }

                return await _http.SendAsync(request, perCallCts.Token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        try
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var varName = string.IsNullOrEmpty(responseVariable) ? "http_response" : responseVariable;
            execution.Variables[varName] = responseBody;
            execution.Variables["http_status"] = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);

            var condition = response.IsSuccessStatusCode ? "success" : "error";
            return new FlowNodeResult(NextEdgeCondition: condition, WaitForInput: false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static TimeSpan ParseTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeout;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 && seconds <= 300)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultTimeout;
    }
}
