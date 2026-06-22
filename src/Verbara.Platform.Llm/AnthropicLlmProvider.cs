using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Llm.Wire;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Llm;

/// <summary>
/// Anthropic Messages API <see cref="ILlmProvider"/>. Uses the dedicated
/// <see cref="AnthropicMessagesRequest"/> / <see cref="AnthropicJsonContext"/> contract:
/// auth via <c>x-api-key</c> + <c>anthropic-version</c> headers, endpoint
/// <c>{BaseUrl}/v1/messages</c> (default <c>https://api.anthropic.com</c>), and the system
/// turn hoisted out of <c>messages</c> into the top-level <c>system</c> field.
/// <para>
/// Resilience + metrics structure mirror <see cref="OpenAiCompatibleLlmProvider"/>.
/// </para>
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider, IDisposable
{
    /// <summary>Keyed-service name for the shared <see cref="ResiliencePolicy"/> (see OpenAI provider).</summary>
    public const string ResiliencePolicyKey = OpenAiCompatibleLlmProvider.ResiliencePolicyKey;

    /// <summary>Default Anthropic API base URL when the tenant supplies none.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Default <c>anthropic-version</c> header value when the tenant supplies none.</summary>
    public const string DefaultAnthropicVersion = "2023-06-01";

    private const string SystemRole = "system";
    private const string ApiKeyHeader = "x-api-key";
    private const string VersionHeader = "anthropic-version";

    private readonly HttpClient _http;
    private readonly LlmEffectiveOptions _options;
    private readonly string _anthropicVersion;
    private readonly ResiliencePolicy _policy;
    private readonly LlmMetrics _metrics;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    public AnthropicLlmProvider(
        HttpClient http,
        LlmEffectiveOptions options,
        string? anthropicVersion = null,
        ResiliencePolicy? policy = null,
        IMeterFactory? meterFactory = null,
        ILogger<AnthropicLlmProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _anthropicVersion = string.IsNullOrWhiteSpace(anthropicVersion)
            ? DefaultAnthropicVersion
            : anthropicVersion;
        _policy = policy ?? ResiliencePolicy.NoOp;
        _metrics = new LlmMetrics(meterFactory);
        _logger = logger ?? NullLogger<AnthropicLlmProvider>.Instance;
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (system, messages) = SplitSystemTurn(request.Messages);

        var wireRequest = new AnthropicMessagesRequest(
            Model: _options.Model,
            MaxTokens: request.MaxTokens,
            System: system,
            Messages: messages,
            Temperature: request.Temperature);

        var endpoint = BuildEndpoint(_options.BaseUrl);
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
                            AnthropicJsonContext.Default.AnthropicMessagesRequest),
                    };
                    // Guard a null/empty key defensively so a keyless probe degrades to an auth
                    // failure from the provider rather than an ArgumentNullException on header add.
                    if (!string.IsNullOrEmpty(_options.ApiKey))
                        httpRequest.Headers.Add(ApiKeyHeader, _options.ApiKey);
                    httpRequest.Headers.Add(VersionHeader, _anthropicVersion);

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
                .ReadFromJsonAsync(AnthropicJsonContext.Default.AnthropicMessagesResponse, ct)
                .ConfigureAwait(false);

            sw.Stop();
            _metrics.RequestLatency.Record(sw.Elapsed.TotalMilliseconds);
            _metrics.Requests.Add(1);

            // Anthropic may interleave non-text blocks (e.g. tool_use/thinking) ahead of the text.
            // Pick the first explicit "text" block; fall back to the first block carrying any text.
            var content = parsed?.Content is { Count: > 0 } blocks
                ? blocks.FirstOrDefault(b => b.Type == "text")?.Text
                    ?? blocks.FirstOrDefault(b => b.Text is not null)?.Text
                    ?? string.Empty
                : null;

            if (string.IsNullOrEmpty(content))
                LlmMetrics.LogEmptyContent(_logger);

            var usage = parsed?.Usage is { } u
                ? new LlmUsage(u.InputTokens, u.OutputTokens, u.InputTokens + u.OutputTokens)
                : null;

            if (usage is not null)
            {
                _metrics.TokensIn.Add(usage.PromptTokens);
                _metrics.TokensOut.Add(usage.CompletionTokens);
            }

            return new LlmResponse(content ?? string.Empty, usage);
        }
    }

    /// <summary>
    /// Hoists the system-role turn(s) out of the message list into Anthropic's top-level
    /// <c>system</c> field. Multiple system turns (rare) are joined with newlines; the remaining
    /// non-system turns keep their order. Returns <c>(null, …)</c> when there is no system turn.
    /// </summary>
    private static (string? System, AnthropicMessage[] Messages) SplitSystemTurn(
        IReadOnlyList<LlmMessage> source)
    {
        var systemParts = new List<string>();
        var rest = new List<AnthropicMessage>(source.Count);

        foreach (var m in source)
        {
            if (string.Equals(m.Role, SystemRole, StringComparison.OrdinalIgnoreCase))
                systemParts.Add(m.Content);
            else
                rest.Add(new AnthropicMessage(m.Role, m.Content));
        }

        var system = systemParts.Count == 0 ? null : string.Join("\n", systemParts);
        return (system, rest.ToArray());
    }

    private static string BuildEndpoint(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
        return $"{root}/v1/messages";
    }

    /// <inheritdoc />
    public void Dispose() => _metrics.Dispose();
}
