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

        // Transient (NOT singleton): DefaultTypificationAiClassifier resolves the per-tenant BYO
        // provider through ILlmProviderResolver (P2c.1), which builds providers over
        // IHttpClientFactory named clients (AddHttpClient("llm.<type>")). A singleton classifier
        // would itself be fine (the resolver is the long-lived singleton and rotates handlers via
        // the factory), but the classifier is kept transient/stateless so each suggestion request
        // resolves a fresh instance via [FromServices] with no captured per-request state. The
        // resolver — not the classifier — owns the per-tenant provider cache + handler rotation.
        services.AddTransient<ITypificationAiClassifier, DefaultTypificationAiClassifier>();

        // B2 — suggestion metrics (singleton; owns its Meter + cached logger lifetime).
        services.AddSingleton<TypificationAiMetrics>(
            sp => new TypificationAiMetrics(
                sp.GetService<IMeterFactory>(),
                sp.GetService<ILoggerFactory>()));

        // B3 — server-authoritative provenance derivation + correction signal.
        services.AddSingleton<ITypificationProvenanceService, DefaultTypificationProvenanceService>();

        // B4a — calibration gate: determines per-schema readiness for AutoFill / autonomous mode.
        services.AddSingleton<ITypificationCalibration, DefaultTypificationCalibration>();

        // ADR-0034 — autonomous disposition policy: the pure commit-or-skip decision the
        // abandoned-wrap-up close path consults (gate + license + confidence + verification).
        // Depends on ITenantAutonomousDispositionStore (registered in the storage extensions) and
        // ILicenseStatus (registered by the Pro AddProLicensing() in the host composition root).
        services.AddSingleton<IAutonomousDispositionPolicy, DefaultAutonomousDispositionPolicy>();

        // E3 — per-tenant daily LLM token budget (fail-closed). SINGLETON: the accumulator is an
        // in-process running per-(tenant, UTC-day) token sum, so a single shared instance is the
        // whole point (a transient would reset the count every request). Single-instance accurate;
        // a multi-instance deployment would back this with Redis (future — the interface is the seam).
        services.AddSingleton<ITypificationTokenBudget, InMemoryTypificationTokenBudget>();

        return services;
    }
}
