namespace Verbara.Platform.Identity.Auth.Jwt;

/// <summary>
/// Algorithm of a <see cref="JwtKeyEntry"/>'s key material. AHH Phase 3
/// generalizes the rotation pool from symmetric-only (R5.4 S5.9) to support
/// the live RS256 issuer (legacy <c>jwt-signing-key.xml</c>) alongside new
/// HS256 keys.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator lives on <see cref="JwtKeyEntry"/> so consumers
/// (<c>JwtTokenService</c>) can dispatch token issuance + validation to the
/// right <see cref="System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler"/>
/// surface. The <see cref="JwtKeyEntry.Key"/> field carries the algorithm-
/// specific material:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><b>Hs256</b> — base64-encoded random bytes
///     (<see cref="System.Security.Cryptography.RandomNumberGenerator"/>),
///     interpreted as an HMAC-SHA256 secret. R5.4 S5.9 default.</description>
///   </item>
///   <item>
///     <description><b>Rs256</b> — base64-encoded PKCS#8 RSA private key
///     (<c>RSA.ExportPkcs8PrivateKey()</c>). Used by the migration shim that
///     imports the legacy file-based key on first boot, and for any future
///     deployment that prefers asymmetric signing for JWKS publishing
///     interop.</description>
///   </item>
/// </list>
/// <para>
/// The enum value <see cref="Hs256"/> is intentionally <c>0</c> so existing
/// Redis entries (R5.4-era, no <c>keyAlgorithm</c> property in JSON)
/// deserialize with the correct default — preserving zero-config backward
/// compatibility.
/// </para>
/// </remarks>
public enum JwtKeyAlgorithm
{
    /// <summary>HMAC-SHA256 (default; R5.4 S5.9 behavior).</summary>
    Hs256 = 0,

    /// <summary>RSA-SHA256 with PKCS#8-encoded private key in <see cref="JwtKeyEntry.Key"/>.</summary>
    Rs256 = 1,
}
