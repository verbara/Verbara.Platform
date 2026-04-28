namespace Asterisk.Platform.Identity.Auth.Jwt;

/// <summary>
/// Represents one JWT signing key in the rotation pool. Old keys remain valid
/// for the rolling grace window so in-flight tokens don't break during rotation.
/// </summary>
/// <remarks>
/// R5.4 S5.9 — Closes C.1 of post-R5.1 triage. The pool model lets the active
/// signing key change at any time while previously-issued tokens continue to
/// validate against historical keys until <see cref="ExpiresAt"/>.
/// </remarks>
public sealed record JwtKeyEntry
{
    /// <summary>Unique key identifier emitted as JWT <c>kid</c> header.</summary>
    public required string KeyId { get; init; }

    /// <summary>
    /// Key material, base64-encoded. Interpretation depends on
    /// <see cref="Algorithm"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="JwtKeyAlgorithm.Hs256"/> — raw HMAC-SHA256 secret bytes.</description></item>
    ///   <item><description><see cref="JwtKeyAlgorithm.Rs256"/> — PKCS#8-encoded RSA private key (<c>RSA.ExportPkcs8PrivateKey()</c>).</description></item>
    /// </list>
    /// Treat as a secret regardless of algorithm.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Signing algorithm for this entry. AHH Phase 3 — defaults to
    /// <see cref="JwtKeyAlgorithm.Hs256"/> so R5.4-era Redis entries (which
    /// pre-date this field) deserialize correctly.
    /// </summary>
    public JwtKeyAlgorithm Algorithm { get; init; } = JwtKeyAlgorithm.Hs256;

    /// <summary>UTC timestamp when this key became active (signing).</summary>
    public required DateTimeOffset ActivatedAt { get; init; }

    /// <summary>UTC timestamp when this key stops being valid for validation.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>True if this key is currently used to sign new tokens.</summary>
    public required bool IsActive { get; init; }
}
