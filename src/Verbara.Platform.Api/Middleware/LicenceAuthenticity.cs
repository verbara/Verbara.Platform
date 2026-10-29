using Verbara.Platform.Typification.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Middleware;

/// <summary>
/// Whether a validation result says the loaded licence's signature verified against the trust
/// anchor. <see cref="LicenseStatusTracker"/> keeps the fields of a licence that failed validation
/// (features, id, licensee, limits), so every reader of those fields asks this first: the licence
/// gate, the ledger's licence id and the licence info the admin console shows.
/// </summary>
internal static class LicenceAuthenticity
{
    /// <summary>
    /// True for a signed licence, current or not (<c>Valid</c>, <c>GracePeriod</c>,
    /// <c>MissingFeature</c>, <c>Expired</c>, <c>UnauthorizedImage</c>); false for <c>Invalid</c>
    /// and for any value this code does not know.
    /// </summary>
    /// <remarks>The definition is <see cref="LicenceGateRule.IsAuthentic"/>, which the licence gate
    /// and the autonomous disposition policy share.</remarks>
    internal static bool IsAuthentic(LicenseValidationResult result) => LicenceGateRule.IsAuthentic(result);
}
