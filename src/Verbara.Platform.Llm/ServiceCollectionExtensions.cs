using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Llm;

/// <summary>
/// DI registration extensions for the concrete OpenAI-compatible LLM provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="OpenAiCompatibleLlmProvider"/> as the <see cref="ILlmProvider"/>
    /// when the supplied options are fully configured (<see cref="LlmProviderOptions.IsConfigured"/>).
    /// <para>
    /// When unconfigured, this is a no-op: nothing is added, leaving the default
    /// <c>DisabledLlmProvider</c> stub (registered via <c>TryAddSingleton</c> by
    /// <c>AddPlatformFlows</c>) in place. Call this <em>before</em> <c>AddPlatformFlows</c> so the
    /// real provider wins the <c>TryAdd</c>.
    /// </para>
    /// <para>
    /// <see cref="Microsoft.Extensions.Logging.ILogger{TCategoryName}"/> and
    /// <see cref="System.Diagnostics.Metrics.IMeterFactory"/> are injected automatically from DI
    /// when present; the provider ctor falls back to no-op stubs otherwise.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPlatformLlm(
        this IServiceCollection services,
        Action<LlmProviderOptions>? configure = null)
    {
        var options = new LlmProviderOptions();
        configure?.Invoke(options);

        // Unconfigured → leave the Flows DisabledLlmProvider stub in place.
        if (!options.IsConfigured)
        {
            return services;
        }

        services.AddSingleton(Options.Create(options));

        // Registers the interface→impl typed HttpClient directly, so resolving ILlmProvider
        // yields the OpenAiCompatibleLlmProvider with its own configured HttpClient.
        // The DI container resolves IMeterFactory and ILogger<OpenAiCompatibleLlmProvider>
        // from the service provider when constructing the typed-client instance; the ctor
        // declares both as nullable so this works even in test containers that omit them.
        services.AddHttpClient<ILlmProvider, OpenAiCompatibleLlmProvider>();

        return services;
    }
}
