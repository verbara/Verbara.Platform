using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Typification.Licensing;

/// <summary>
/// The one licence rule every Pro surface follows: <c>LicenseGateMiddleware</c> on the HTTP path
/// and <c>DefaultAutonomousDispositionPolicy</c> for autonomous stamping. It lives here because
/// Typification is the lowest project that references <c>Verbara.Sdk.Pro.Licensing</c>, and
/// <c>Verbara.Platform.Api</c> sees it through <c>InternalsVisibleTo</c>.
/// </summary>
internal static class LicenceGateRule
{
    /// <summary>
    /// True for a signed licence, current or not (<c>Valid</c>, <c>GracePeriod</c>,
    /// <c>MissingFeature</c>, <c>Expired</c>, <c>UnauthorizedImage</c>); false for <c>Invalid</c>
    /// and for any value this code does not know.
    /// </summary>
    internal static bool IsAuthentic(LicenseValidationResult result) =>
        result is LicenseValidationResult.Valid
            or LicenseValidationResult.GracePeriod
            or LicenseValidationResult.MissingFeature
            or LicenseValidationResult.Expired
            or LicenseValidationResult.UnauthorizedImage;

    /// <summary>
    /// Decides whether the loaded licence opens <paramref name="required"/> (one bit, or several that
    /// must all hold, e.g. <c>AdvancedTypification | TypificationAi</c>). The feature bits alone never
    /// decide, because the tracker keeps the bits of a licence that failed validation:
    /// <list type="bullet">
    ///   <item><description>A result that does not come from an authentic, signed licence
    ///   (<see cref="LicenseValidationResult.Invalid"/>, which is also "no licence loaded", or any
    ///   value this code does not know) opens nothing.</description></item>
    ///   <item><description>A signed licence in date (<see cref="LicenseValidationResult.Valid"/>,
    ///   <see cref="LicenseValidationResult.GracePeriod"/>, and
    ///   <see cref="LicenseValidationResult.MissingFeature"/>, which Pro's licence classification
    ///   derives only from those two) opens what it carries.</description></item>
    ///   <item><description>A signed licence that is not current
    ///   (<see cref="LicenseValidationResult.Expired"/>,
    ///   <see cref="LicenseValidationResult.UnauthorizedImage"/>) keeps
    ///   <paramref name="neverGuarded"/> on the bit, and lets every other feature through only
    ///   while <paramref name="guard"/> allows it. The guard owns the grace window, so the HTTP
    ///   surface and the Pro engines that call the guard switch off together.</description></item>
    /// </list>
    /// While the licence is Valid, in GracePeriod or MissingFeature the guard is still asked about the
    /// guarded bits and its answer is not used, so it records them as seen valid and its grace window
    /// starts from real use of this surface.
    /// </summary>
    internal static bool IsLicensed(
        ILicenseStatus status,
        ILicenseGuard guard,
        LicenseFeature required,
        LicenseFeature neverGuarded)
    {
        var result = status.LastResult;
        if (!IsAuthentic(result) || !status.LicensedFeatures.HasFlag(required))
            return false;

        var guarded = required & ~neverGuarded;
        if (guarded == LicenseFeature.None)
            return true;

        var allowed = true;
        for (var remaining = (int)guarded; remaining != 0; remaining &= remaining - 1)
        {
            // Every bit is asked, even after a denial, so each one is recorded while current. A bit
            // that is not a LicenseFeature member cannot be put to the guard, so it fails closed.
            var bit = (LicenseFeature)(remaining & -remaining);
            allowed &= Enum.IsDefined(bit) && guard.CanExecute(bit).Allowed;
        }

        return result is LicenseValidationResult.Valid
                   or LicenseValidationResult.GracePeriod
                   or LicenseValidationResult.MissingFeature
               || allowed;
    }
}
