using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Validation;

namespace Verbara.Platform.Typification;

/// <summary>DI registration extensions for Platform.Typification services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Platform.Typification services: the server-authoritative schema
    /// validator (D1), the most-specific-wins binding resolver (D2), the
    /// most-specific-wins reason-hint resolver (P1), the wrap-up prefill resolver (P1),
    /// and the direct LLM AI classifier (P2a). Callers must separately register
    /// implementations of the store interfaces in
    /// <c>Verbara.Platform.Typification.Stores</c> and an <c>ILlmProvider</c> for the AI
    /// classifier.
    /// </summary>
    public static IServiceCollection AddPlatformTypification(this IServiceCollection services)
    {
        services.AddSingleton<ITypificationValidator, DefaultTypificationValidator>();
        services.AddSingleton<ITypificationResolver, DefaultTypificationResolver>();
        services.AddSingleton<IReasonHintResolver, DefaultReasonHintResolver>();
        services.AddSingleton<ITypificationPrefillResolver, DefaultTypificationPrefillResolver>();

        // Transient (NOT singleton): DefaultTypificationAiClassifier depends on
        // ILlmProvider, which AddPlatformLlm registers as a factory-managed typed
        // HttpClient (AddHttpClient<ILlmProvider, ...>). A singleton capturing a
        // transient typed-client is the IHttpClientFactory captive-dependency
        // anti-pattern — the HttpMessageHandler would never rotate (stale DNS on
        // long-running pods). The classifier is stateless (only ctor deps) so a
        // fresh instance per resolve is correct; the suggestion endpoint resolves it
        // per-request via [FromServices], so each request gets a factory-rotated handler.
        services.AddTransient<ITypificationAiClassifier, DefaultTypificationAiClassifier>();

        // B2 — suggestion metrics (singleton; owns its Meter + cached logger lifetime).
        services.AddSingleton<TypificationAiMetrics>(
            sp => new TypificationAiMetrics(
                sp.GetService<IMeterFactory>(),
                sp.GetService<ILoggerFactory>()));

        // B3 — server-authoritative provenance derivation + correction signal.
        services.AddSingleton<ITypificationProvenanceService, DefaultTypificationProvenanceService>();

        return services;
    }
}
