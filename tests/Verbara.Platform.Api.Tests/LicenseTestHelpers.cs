using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// v2.5.0-pro consumer migration helper. Pro v2.5.0-pro removed
/// <c>LicenseOptions.EnforcementMode</c> (ADR-0012), so test factories can no
/// longer call <c>services.Configure&lt;LicenseOptions&gt;(o =&gt; o.EnforcementMode = Disabled)</c>
/// to bypass the gate. Instead, register a substitute <see cref="ILicenseStatus"/>
/// that reports every feature as licensed — the middleware short-circuits to
/// <c>_next(context)</c> before consulting the rest of Pro's license stack.
/// </summary>
internal static class LicenseTestHelpers
{
    /// <summary>
    /// Every flag in <see cref="LicenseFeature"/> OR'd together so
    /// <c>LicensedFeatures.HasFlag(anyFeature)</c> always returns <c>true</c>.
    /// Update this when new feature bits are added in Pro.
    /// </summary>
    public const LicenseFeature AllProFeatures = LicenseFeature.All;

    /// <summary>
    /// Registers a substitute <see cref="ILicenseStatus"/> that reports every
    /// Pro feature as licensed and the license as valid. Call once per test
    /// factory's <c>ConfigureServices</c> in place of the old
    /// <c>services.Configure&lt;LicenseOptions&gt;(o =&gt; o.EnforcementMode = Disabled)</c>.
    /// Removes any pre-existing <see cref="ILicenseStatus"/> registration so the
    /// substitute wins regardless of when <c>AddProLicensing()</c> ran.
    /// </summary>
    public static IServiceCollection AddAllProFeaturesLicensed(this IServiceCollection services)
    {
        var existing = services
            .Where(d => d.ServiceType == typeof(ILicenseStatus))
            .ToList();
        foreach (var d in existing) services.Remove(d);

        var status = Substitute.For<ILicenseStatus>();
        status.IsValid.Returns(true);
        status.LicensedFeatures.Returns(AllProFeatures);
        status.LastResult.Returns(LicenseValidationResult.Valid);
        services.AddSingleton(status);
        return services;
    }

    /// <summary>
    /// Inverse of <see cref="AddAllProFeaturesLicensed"/> for tests that need to
    /// observe the HTTP 402 path. Reports NO features licensed + IsValid=false.
    /// </summary>
    public static IServiceCollection AddNoProFeaturesLicensed(this IServiceCollection services)
    {
        var existing = services
            .Where(d => d.ServiceType == typeof(ILicenseStatus))
            .ToList();
        foreach (var d in existing) services.Remove(d);

        var status = Substitute.For<ILicenseStatus>();
        status.IsValid.Returns(false);
        status.LicensedFeatures.Returns(LicenseFeature.None);
        status.LastResult.Returns(LicenseValidationResult.Invalid);
        services.AddSingleton(status);
        return services;
    }
}
