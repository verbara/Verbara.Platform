using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Llm.Wire;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Llm;

/// <summary>
/// Azure OpenAI <see cref="ILlmProvider"/>. Speaks the same OpenAI chat-completions wire
/// (<see cref="ChatCompletionRequest"/> / <see cref="LlmJsonContext"/>) but with Azure's
/// deployment-scoped URL and the <c>api-key</c> header (NOT <c>Authorization: Bearer</c>).
/// <para>
/// Endpoint: <c>{BaseUrl}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}</c>.
/// Resilience + metrics structure mirror <see cref="OpenAiCompatibleLlmProvider"/>.
/// </para>
/// </summary>
public sealed class AzureOpenAiLlmProvider : ILlmProvider, IDisposable
{
    /// <summary>Keyed-service name for the shared <see cref="ResiliencePolicy"/> (see OpenAI provider).</summary>
    public const string ResiliencePolicyKey = OpenAiCompatibleLlmProvider.ResiliencePolicyKey;

    private const string ApiKeyHeader = "api-key";

    private readonly HttpClient _http;
    private readonly LlmEffectiveOptions _options;
    private readonly string _deployment;
    private readonly string _apiVersion;
    private readonly ResiliencePolicy _policy;
    private readonly LlmMetrics _metrics;
    private readonly ILogger<AzureOpenAiLlmProvider> _logger;

    public AzureOpenAiLlmProvider(
        HttpClient http,
        LlmEffectiveOptions options,
        string deployment,
        string apiVersion,
        ResiliencePolicy? policy = null,
        IMeterFactory? meterFactory = null,
        ILogger<AzureOpenAiLlmProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(apiVersion);
        _http = http;
        _options = options;
        _deployment = deployment;
        _apiVersion = apiVersion;
        _policy = policy ?? ResiliencePolicy.NoOp;
        _metrics = new LlmMetrics(meterFactory);
        _logger = logger ?? NullLogger<AzureOpenAiLlmProvider>.Instance;
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wireRequest = new ChatCompletionRequest(
            Model: _options.Model,
            Messages: MapMessages(request.Messages),
            Temperature: request.Temperature,
            MaxTokens: request.MaxTokens);

        var endpoint = BuildEndpoint(_options.BaseUrl, _deployment, _apiVersion);
        var timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        var sw = Stopwatch.StartNew();

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
                    // Guard a null/empty key defensively so a keyless probe degrades to an auth
                    // failure from the provider rather than an ArgumentNullException on header add.
                    if (!string.IsNullOrEmpty(_options.ApiKey))
                        httpRequest.Headers.Add(ApiKeyHeader, _options.ApiKey);

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
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Errors.Add(1);
                LlmMetrics.LogProviderFailure(_logger, sw.Elapsed.TotalMilliseconds, ex);
                throw;
            }

            var parsed = await response.Content
                .ReadFromJsonAsync(LlmJsonContext.Default.ChatCompletionResponse, ct)
                .ConfigureAwait(false);

            sw.Stop();
            _metrics.RequestLatency.Record(sw.Elapsed.TotalMilliseconds);
            _metrics.Requests.Add(1);

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

    private static string BuildEndpoint(string? baseUrl, string deployment, string apiVersion) =>
        // Escape the deployment path segment + the api-version query value so a deployment name or
        // version with URL-significant characters (spaces, '/', '?', '&') can't break the URL.
        $"{(baseUrl ?? string.Empty).TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(apiVersion)}";

    /// <inheritdoc />
    public void Dispose() => _metrics.Dispose();
}
