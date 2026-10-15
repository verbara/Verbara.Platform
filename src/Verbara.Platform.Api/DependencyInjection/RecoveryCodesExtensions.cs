using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// MFA recovery codes: the plaintext generator and legacy SHA-256 verifier for the profile paths, and the
/// family meter that counts redemptions and publishes the legacy-population census, and the per-user mint
/// gate (<see cref="RecoveryCodeMintGate"/>) the four mint endpoints share. The census itself
/// (<see cref="IRecoveryCodeCensus"/>) is registered by each storage's own <c>ServiceCollectionExtensions</c>.
/// </summary>
public static class RecoveryCodesExtensions
{
    /// <summary>
    /// Registers <see cref="IRecoveryCodeService"/>, <see cref="RecoveryCodeFamilyMetrics"/> (a singleton
    /// that <c>MfaVerify</c> records redemptions on, and a hosted service that refreshes the census gauge) and
    /// its <see cref="RecoveryCodeCensusOptions"/> from <c>Mfa:RecoveryCodeCensus</c>, and
    /// <see cref="RecoveryCodeMintGate"/> with its <see cref="RecoveryCodeMintOptions"/> from
    /// <c>Mfa:RecoveryCodeMint</c>.
    /// </summary>
    public static IServiceCollection AddRecoveryCodes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRecoveryCodeService, RecoveryCodeService>();
        services.TryAddSingleton(sp => RecoveryCodeCensusOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton(sp => new RecoveryCodeFamilyMetrics(
            sp,
            sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            sp.GetRequiredService<RecoveryCodeCensusOptions>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<RecoveryCodeFamilyMetrics>>()));
        services.AddHostedService(sp => sp.GetRequiredService<RecoveryCodeFamilyMetrics>());
        services.TryAddSingleton(sp => new RecoveryCodeMintGate(
            RecoveryCodeMintOptions.From(sp.GetRequiredService<IConfiguration>()),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
