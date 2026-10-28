using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// Boot check that the licence trust anchor of the built host is the key embedded in Pro.
/// </summary>
/// <remarks>
/// The Pro licence validator takes its public key from the container's <c>byte[]</c> service, and
/// <c>AddProLicensing()</c> registers <see cref="LicenseTrustAnchor.OfficialPublicKey"/> through
/// <c>TryAddSingleton</c>: any other <c>byte[]</c> registration, made before it or after it, would
/// change the key every licence is checked against. Gate #13 of
/// <c>scripts/check-endpoint-invariants.py</c> warns about the source shapes it can read; this check
/// is the guarantee, because it sees what the container actually resolves, whichever code, package,
/// configuration or branch registered it.
/// </remarks>
internal static class LicenseTrustAnchorCheck
{
    /// <summary>
    /// Throws unless at least one <c>byte[]</c> service is registered and every one of them equals
    /// <see cref="LicenseTrustAnchor.OfficialPublicKey"/>.
    /// </summary>
    internal static void EnsureEmbeddedLicenceTrustAnchor(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var official = LicenseTrustAnchor.OfficialPublicKey;
        var registered = 0;
        foreach (var key in services.GetServices<byte[]>())
        {
            registered++;
            if (key is null || !key.AsSpan().SequenceEqual(official))
                throw new InvalidOperationException(
                    "The licence trust anchor is not the key embedded in Verbara.Sdk.Pro.Licensing: "
                    + "a byte[] service other than LicenseTrustAnchor.OfficialPublicKey is registered. "
                    + "Remove that registration; the host refuses to start with it.");
        }

        if (registered == 0)
            throw new InvalidOperationException(
                "No licence trust anchor is registered: AddProLicensing() must run before the host is built.");
    }
}
