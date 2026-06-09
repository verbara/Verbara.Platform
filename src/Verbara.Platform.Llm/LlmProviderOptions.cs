namespace Verbara.Platform.Llm;

/// <summary>
/// Configuration for the <see cref="OpenAiCompatibleLlmProvider"/>. Bound in the Api host
/// (typically from <c>appsettings</c>/environment); this library only consumes
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
/// <para>
/// The provider is only registered when <see cref="IsConfigured"/> is <see langword="true"/>;
/// otherwise the <c>DisabledLlmProvider</c> stub (registered by <c>AddPlatformFlows</c>) remains.
/// </para>
/// </summary>
public sealed class LlmProviderOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API, up to (but excluding) the <c>/chat/completions</c>
    /// path segment — e.g. <c>https://api.openai.com/v1</c>. A trailing slash is tolerated.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key sent as an <c>Authorization: Bearer</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model identifier (e.g. <c>gpt-4o-mini</c>) sent on every completion request.</summary>
    public string? Model { get; set; }

    /// <summary>Default sampling temperature. Defaults to 0.2 for deterministic classification.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Default maximum response tokens. Defaults to 800.</summary>
    public int MaxTokens { get; set; } = 800;

    /// <summary>Per-call timeout in seconds applied via a linked cancellation source. Defaults to 20.</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// <see langword="true"/> when <see cref="BaseUrl"/>, <see cref="ApiKey"/> and
    /// <see cref="Model"/> are all set — the minimum needed to issue requests.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model);
}
