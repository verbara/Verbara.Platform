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
/// Stored hashes use SHA-256 over the per-user salt + plaintext code.
/// </para>
/// </remarks>
public interface IRecoveryCodeService
{
    /// <summary>
    /// Generates 10 unique 8-character recovery codes.
    /// </summary>
    /// <returns>Plaintext codes — display once, then hash with <see cref="Hash"/>.</returns>
    IReadOnlyList<string> Generate();

    /// <summary>
    /// Computes the storage hash for <paramref name="code"/> using the supplied
    /// per-user <paramref name="salt"/>.
    /// </summary>
    string Hash(string code, string salt);

    /// <summary>
    /// Verifies that <paramref name="code"/> matches <paramref name="storedHash"/>
    /// when hashed with <paramref name="salt"/>.
    /// </summary>
    bool Verify(string code, string salt, string storedHash);
}
