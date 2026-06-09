using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<ITypificationAiClassifier, DefaultTypificationAiClassifier>();
        return services;
    }
}
