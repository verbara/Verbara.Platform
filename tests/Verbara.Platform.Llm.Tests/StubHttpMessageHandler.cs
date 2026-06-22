using System.Net;

namespace Verbara.Platform.Llm.Tests;

/// <summary>
/// Test double that captures the outbound <see cref="HttpRequestMessage"/> (URI, method, headers,
/// body) and returns a canned response. Mirrors the per-provider stub-handler pattern used by
/// <c>OpenAiCompatibleLlmProviderTests</c>.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public HttpRequestMessage? Request { get; private set; }

    public string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        if (request.Content is not null)
        {
            RequestBody = await request.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
