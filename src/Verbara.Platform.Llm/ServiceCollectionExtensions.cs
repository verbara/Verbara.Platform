using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Llm;

/// <summary>
/// DI registration extensions for the LLM providers and the per-tenant provider resolver (P2c.1).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LLM seam.
    /// <para>
    /// <strong>Always</strong> (regardless of whether a global key is configured): the keyed
    /// <c>llm.completions</c> <see cref="ResiliencePolicy"/> (per-tenant BYO providers need it even
    /// when no global key is set), the three named provider <see cref="System.Net.Http.HttpClient"/>s,
    /// and the <see cref="ILlmProviderResolver"/> singleton.
    /// </para>
    /// <para>
    /// <strong>Only when configured</strong> (<see cref="LlmProviderOptions.IsConfigured"/>): the
    /// global <c>AddHttpClient&lt;ILlmProvider, OpenAiCompatibleLlmProvider&gt;</c> typed client +
    /// its <see cref="LlmProviderOptions"/> singleton. This is the dual-consumer seam the Flows engine
    /// (<c>ai_classify</c>/<c>ai_generate</c>) depends on: resolving <see cref="ILlmProvider"/> still
    /// yields the global OpenAI provider exactly as before. When unconfigured, the
    /// <c>DisabledLlmProvider</c> stub (registered via <c>TryAdd</c> by <c>AddPlatformFlows</c>)
    /// remains. Call this <em>before</em> <c>AddPlatformFlows</c> so the real provider wins the
    /// <c>TryAdd</c>.
    /// </para>
    /// <para>
    /// <see cref="Microsoft.Extensions.Logging.ILogger{TCategoryName}"/> and
    /// <see cref="System.Diagnostics.Metrics.IMeterFactory"/> are injected automatically from DI
    /// when present; provider ctors fall back to no-op stubs otherwise.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPlatformLlm(
        this IServiceCollection services,
        Action<LlmProviderOptions>? configure = null,
        Action<PlatformLlmOptions>? configurePlatform = null)
    {
        var options = new LlmProviderOptions();
        configure?.Invoke(options);

        // ── ALWAYS (per-tenant BYO needs these even when the global key is unset) ──────────────

        // llm.completions: circuit 3/60s + retry 2/500ms + timeout from LlmProviderOptions.TimeoutSeconds
        // (default 20s when unconfigured). Shared by the global typed-client AND every per-tenant
        // provider the resolver builds. Mirrors the flow.http-request registration shape (ADR-0029).
        var timeoutSeconds = options.TimeoutSeconds;
        services.TryAddKeyedSingleton<ResiliencePolicy>(
            OpenAiCompatibleLlmProvider.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 3, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(timeoutSeconds))
                .Build());

        // Named clients per provider type — obtained via IHttpClientFactory for socket pooling.
        services.AddHttpClient(DefaultLlmProviderResolver.OpenAiClientName);
        services.AddHttpClient(DefaultLlmProviderResolver.AzureClientName);
        services.AddHttpClient(DefaultLlmProviderResolver.AnthropicClientName);

        services.TryAddSingleton<ILlmProviderResolver, DefaultLlmProviderResolver>();

        // ── ALWAYS: platform-managed operator options (Enabled defaults false) ────────────────
        var platformOptions = new PlatformLlmOptions();
        configurePlatform?.Invoke(platformOptions);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(platformOptions));

        // ── ONLY WHEN CONFIGURED: the global typed-client the Flows engine resolves ────────────

        if (options.IsConfigured)
        {
            services.AddSingleton(Options.Create(options));

            // Registers the interface→impl typed HttpClient directly, so resolving ILlmProvider
            // yields the OpenAiCompatibleLlmProvider with its own configured HttpClient. The DI
            // container resolves IMeterFactory and ILogger<OpenAiCompatibleLlmProvider> from the
            // service provider when constructing the typed-client instance; the ctor declares both
            // as nullable so this works even in test containers that omit them.
            services.AddHttpClient<ILlmProvider, OpenAiCompatibleLlmProvider>();
        }

        return services;
    }
}
