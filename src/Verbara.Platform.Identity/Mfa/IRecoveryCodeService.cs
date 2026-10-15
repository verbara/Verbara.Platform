namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// Generates, hashes, and verifies MFA recovery codes used as a TOTP fallback.
/// </summary>
/// <remarks>
/// <para>
/// R5.2 PA.2 (S3.4 Frente E) introduces this abstraction so the end-user MFA enroll
/// wizard at <c>/profile/security/mfa/enroll</c> and the recovery-codes regenerate
/// page at <c>/profile/security/recovery-codes/regenerate</c> share a single,
/// AOT-safe, audit-friendly source for code material.
/// </para>
/// <para>
/// Generated codes are 8-character uppercase strings drawn from the unambiguous
/// Base32 alphabet (RFC 4648 minus the visually confusable <c>0/1/O/I</c> glyphs)
/// and produced via <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// </para>
/// <para>
/// <see cref="Hash"/> and <see cref="Verify"/> are the <b>legacy</b> salted SHA-256 family: one
/// unstretched SHA-256 over the per-user salt and the code. No mint path writes it any more; every
/// path stores Argon2id digests, and the Api's recovery-code verifier keeps this family verifiable
/// for codes minted before that change (see <see cref="RecoveryCodeFamilies"/>). New callers must not
/// use <see cref="Hash"/> to store a code.
/// </para>
/// </remarks>
public interface IRecoveryCodeService
{
    /// <summary>
    /// Generates 10 unique 8-character recovery codes.
    /// </summary>
    /// <returns>Plaintext codes — display once; the caller stores their Argon2id digests.</returns>
    IReadOnlyList<string> Generate();

    /// <summary>
    /// Computes the legacy salted SHA-256 digest of <paramref name="code"/> with the
    /// per-user <paramref name="salt"/>. Kept to verify codes minted before the move to
    /// Argon2id and to forge test fixtures; no mint path writes it.
    /// </summary>
    string Hash(string code, string salt);

    /// <summary>
    /// Verifies that <paramref name="code"/> matches a legacy salted SHA-256
    /// <paramref name="storedHash"/> when hashed with <paramref name="salt"/>, in constant time.
    /// </summary>
    bool Verify(string code, string salt, string storedHash);
}
