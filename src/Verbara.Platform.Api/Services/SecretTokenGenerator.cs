using System.Security.Cryptography;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The single canonical minter of credential secrets (management / service API keys).
/// Enforces the ADR-0012 Ola-3 invariant that a credential secret comes from a CSPRNG —
/// never from <c>Guid.NewGuid()</c>, which is contractually unique but not unguessable
/// (~122 bits, fixed version/variant nibbles). Pairs with the deterministic "no
/// Guid.NewGuid in a credential mint" gate #7 in <c>scripts/check-endpoint-invariants.py</c>.
/// See verbara-meta/ADR-0012 addendum 2026-07-20.
///
/// A static utility rather than an injected service on purpose: the Program.cs composition
/// root is frozen at its LOC budget (gate #9), and minting is a pure CSPRNG function with
/// nothing to configure or mock. The deferred 6-site "single secret factory" consolidation
/// (ADR-0012 addendum, its own decision_ref) is where this graduates to an injected seam.
/// </summary>
internal static class SecretTokenGenerator
{
    private const int SecretByteLength = 32; // 256-bit CSPRNG

    /// <summary>
    /// A 256-bit (32-byte) CSPRNG secret rendered as lowercase hex, with
    /// <paramref name="prefix"/> prepended verbatim (e.g. <c>"mgmt_"</c>).
    /// </summary>
    public static string Mint(string prefix = "") =>
        prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretByteLength));
}
