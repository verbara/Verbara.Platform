using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services.QaAvailability;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The QA availability read (qa-scores-are-produced-or-not-shown, design D1): <see cref="QaAvailabilityService"/> over
/// the host's <see cref="IQaAnalysisSetup"/>. The host binds no transcript source yet, so the setup is
/// <see cref="UnconfiguredQaAnalysisSetup"/> unless one is already registered.
/// </summary>
internal static class QaAvailabilityExtensions
{
    /// <summary>
    /// Registers <see cref="QaAvailabilityService"/>, and <see cref="UnconfiguredQaAnalysisSetup"/> as the setup when none
    /// is registered.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="analysisRegistered">Whether this host registered post-call analysis (an analytics or dialer
    /// connection string is configured).</param>
    public static IServiceCollection AddQaAvailability(this IServiceCollection services, bool analysisRegistered)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IQaAnalysisSetup>(UnconfiguredQaAnalysisSetup.Instance);
        services.TryAddSingleton(sp => new QaAvailabilityService(
            analysisRegistered,
            sp.GetRequiredService<IQaAnalysisSetup>(),
            sp.GetRequiredService<ILicenseGuard>()));
        return services;
    }
}
