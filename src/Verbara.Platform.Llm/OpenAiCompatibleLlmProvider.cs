using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Llm.Wire;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Llm;

/// <summary>
/// The first concrete <see cref="ILlmProvider"/>: an OpenAI-compatible chat-completions client.
/// Works against any endpoint implementing the OpenAI <c>POST /chat/completions</c> contract
/// (OpenAI, Azure OpenAI-compatible gateways, vLLM, Ollama's OpenAI shim, etc.).
/// <para>
/// AOT-safe: all (de)serialization goes through the source-generated <see cref="LlmJsonContext"/>.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleLlmProvider : ILlmProvider, IDisposable
{
    /// <summary>
    /// Keyed-service name for the <see cref="ResiliencePolicy"/> that wraps the HTTP send
    /// (circuit + retry). The per-call deadline comes from <see cref="LlmProviderOptions.TimeoutSeconds"/>
    /// via a linked cancellation source, mirroring the Flows <c>http_request</c> handler pattern.
    /// </summary>
    public const string ResiliencePolicyKey = "llm.completions";

    private readonly HttpClient _http;
    private readonly LlmProviderOptions _options;
    private readonly ResiliencePolicy _policy;
    private readonly LlmMetrics _metrics;
    private readonly ILogger<OpenAiCompatibleLlmProvider> _logger;

    public OpenAiCompatibleLlmProvider(
        HttpClient http,
        IOptions<LlmProviderOptions> options,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null,
        IMeterFactory? meterFactory = null,
        ILogger<OpenAiCompatibleLlmProvider>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _policy = policy ?? ResiliencePolicy.NoOp;
        _metrics = new LlmMetrics(meterFactory);
        _logger = logger ?? NullLogger<OpenAiCompatibleLlmProvider>.Instance;
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wireRequest = new ChatCompletionRequest(
            Model: _options.Model ?? string.Empty,
            Messages: MapMessages(request.Messages),
            Temperature: request.Temperature,
            MaxTokens: request.MaxTokens);

        var endpoint = BuildEndpoint(_options.BaseUrl);
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        var sw = Stopwatch.StartNew();

        // The ResiliencePolicy owns circuit + retry; the per-call timeout below is layered on top
        // via a linked CTS so each attempt gets a fresh deadline. Mirrors HttpRequestNodeHandler.
        HttpResponseMessage response;
        try
        {
            response = await _policy.ExecuteAsync(
                ResiliencePolicyKey,
                async innerCt =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                    perCallCts.CancelAfter(timeout);

                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = JsonContent.Create(
                            wireRequest,
                            LlmJsonContext.Default.ChatCompletionRequest),
                    };
                    httpRequest.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _options.ApiKey);

                    return await _http.SendAsync(httpRequest, perCallCts.Token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.Errors.Add(1);
            LlmMetrics.LogProviderFailure(_logger, sw.Elapsed.TotalMilliseconds, ex);
            throw;
        }

        using (response)
        {
            var elapsedMs = sw.Elapsed.TotalMilliseconds;

            try
            {
                // Let callers degrade — the disposition AI-node classifier catches and falls back.
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _metrics.Errors.Add(1);
                LlmMetrics.LogProviderFailure(_logger, elapsedMs, ex);
                throw;
            }

            _metrics.RequestLatency.Record(elapsedMs);
            _metrics.Requests.Add(1);

            var parsed = await response.Content
                .ReadFromJsonAsync(LlmJsonContext.Default.ChatCompletionResponse, ct)
                .ConfigureAwait(false);

            var content = parsed?.Choices is { Count: > 0 } choices
                ? choices[0].Message?.Content
                : null;

            if (string.IsNullOrEmpty(content))
                LlmMetrics.LogEmptyContent(_logger);

            var usage = parsed?.Usage is { } u
                ? new LlmUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens)
                : null;

            if (usage is not null)
            {
                _metrics.TokensIn.Add(usage.PromptTokens);
                _metrics.TokensOut.Add(usage.CompletionTokens);
            }

            return new LlmResponse(content ?? string.Empty, usage);
        }
    }

    private static ChatMessage[] MapMessages(IReadOnlyList<LlmMessage> messages)
    {
        var mapped = new ChatMessage[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            mapped[i] = new ChatMessage(m.Role, m.Content);
        }

        return mapped;
    }

    private static string BuildEndpoint(string? baseUrl) =>
        $"{(baseUrl ?? string.Empty).TrimEnd('/')}/chat/completions";

    /// <inheritdoc />
    public void Dispose() => _metrics.Dispose();
}
