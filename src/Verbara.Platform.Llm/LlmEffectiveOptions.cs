namespace Verbara.Platform.Llm;

/// <summary>
/// The per-resolve <em>effective</em> LLM configuration a concrete <see cref="ILlmProvider"/>
/// is built from (P2c.1). This is the seam that lets a single provider implementation be
/// constructed BOTH from the global host-bound <see cref="LlmProviderOptions"/> (the legacy
/// Flows typed-client path) AND from a per-tenant <see cref="TenantLlmConfig"/> resolved at
/// call time. The resolver decrypts the API key once at build time and materialises it here;
/// the value never leaves the server.
/// </summary>
/// <param name="BaseUrl">
/// Provider base URL. For OpenAI-compatible this is the API root up to (excluding)
/// <c>/chat/completions</c>; for Azure the resource endpoint; for Anthropic the API root
/// (defaults applied by the provider when null).
/// </param>
/// <param name="ApiKey">The (decrypted) API key sent on each request.</param>
/// <param name="Model">The model identifier sent on every completion request.</param>
/// <param name="Temperature">Default sampling temperature.</param>
/// <param name="MaxTokens">Default maximum response tokens.</param>
/// <param name="TimeoutSeconds">Per-call timeout in seconds, applied via a linked cancellation source.</param>
public sealed record LlmEffectiveOptions(
    string? BaseUrl,
    string? ApiKey,
    string Model,
    double Temperature,
    int MaxTokens,
    int TimeoutSeconds)
{
    /// <summary>
    /// Adapter that maps the global host-bound <see cref="LlmProviderOptions"/> to
    /// <see cref="LlmEffectiveOptions"/>. Used by the legacy Flows typed-client path so the
    /// single global <c>AddHttpClient&lt;ILlmProvider, OpenAiCompatibleLlmProvider&gt;</c>
    /// registration keeps working unchanged after the per-tenant refactor.
    /// </summary>
    public static LlmEffectiveOptions FromProviderOptions(LlmProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LlmEffectiveOptions(
            BaseUrl: options.BaseUrl,
            ApiKey: options.ApiKey,
            Model: options.Model ?? string.Empty,
            Temperature: options.Temperature,
            MaxTokens: options.MaxTokens,
            TimeoutSeconds: options.TimeoutSeconds);
    }
}
