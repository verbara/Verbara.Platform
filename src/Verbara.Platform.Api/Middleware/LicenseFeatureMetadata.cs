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
}
