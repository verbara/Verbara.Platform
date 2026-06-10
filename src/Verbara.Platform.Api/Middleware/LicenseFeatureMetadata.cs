using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Middleware;

internal sealed record LicenseFeatureMetadata(LicenseFeature RequiredFeature);

internal static class LicenseFeatureEndpointExtensions
{
    public static RouteGroupBuilder RequireLicenseFeature(
        this RouteGroupBuilder group, LicenseFeature feature)
    {
        group.WithMetadata(new LicenseFeatureMetadata(feature));
        return group;
    }

    /// <summary>
    /// Per-endpoint variant of <see cref="RequireLicenseFeature(RouteGroupBuilder, LicenseFeature)"/>
    /// for gating a single route inside an otherwise-ungated group. The
    /// <see cref="LicenseGateMiddleware"/> reads ONE <see cref="LicenseFeatureMetadata"/>
    /// via <c>GetMetadata&lt;T&gt;()</c> and checks <c>LicensedFeatures.HasFlag(feature)</c>,
    /// so to require BOTH features pass a combined flags value
    /// (e.g. <c>AdvancedTypification | TypificationAi</c>) — <c>HasFlag</c> on a combined
    /// value returns <see langword="true"/> only when EVERY set bit is licensed, so the gate
    /// returns 402 unless both are present.
    /// </summary>
    public static RouteHandlerBuilder RequireLicenseFeature(
        this RouteHandlerBuilder builder, LicenseFeature feature)
    {
        builder.WithMetadata(new LicenseFeatureMetadata(feature));
        return builder;
    }
}
