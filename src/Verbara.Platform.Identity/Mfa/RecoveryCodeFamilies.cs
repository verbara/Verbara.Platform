namespace Verbara.Platform.Identity.Mfa;

/// <summary>The digest family a stored MFA recovery code was written in.</summary>
public enum RecoveryCodeFamily
{
    /// <summary>Salted SHA-256 hex (legacy; the profile wizard wrote it), and anything unrecognised.</summary>
    Sha256 = 0,

    /// <summary>BCrypt cost 10 (legacy; <c>/auth/mfa/*</c> wrote it).</summary>
    Bcrypt = 1,

    /// <summary>Argon2id at the password parameters; every mint path writes it.</summary>
    Argon2id = 2,
}

/// <summary>
/// Classifies a stored recovery-code digest by its prefix alone, with no cryptography.
/// </summary>
/// <remarks>
/// <para>
/// One definition of the prefixes, read by the verification dispatch, the <c>GET /users/me</c>
/// <c>recoveryCodesNeedRegeneration</c> flag, the legacy-population census and the redemption
/// counter. It lives in Identity because the Postgres census cannot reference the Api.
/// </para>
/// <para>
/// Anything that is neither <c>$argon2id$</c> nor <c>$2</c> is <see cref="RecoveryCodeFamily.Sha256"/>,
/// the same fallback the dispatch uses, so a corrupt value counts as legacy: the conservative reading
/// for an operator count. Whether a digest actually verifies is the verifier's question, not this one's.
/// </para>
/// <para>
/// Recovery codes do not converge on their own: only the redeemed code ever arrives in plaintext, and
/// it is consumed, so the other digests of an array cannot be rehashed. A legacy array becomes Argon2id
/// only when the user regenerates.
/// </para>
/// </remarks>
public static class RecoveryCodeFamilies
{
    /// <summary>Prefix of a PHC-encoded Argon2id digest.</summary>
    public const string Argon2idPrefix = "$argon2id$";

    /// <summary>Prefix shared by BCrypt's <c>$2a$</c> and <c>$2b$</c> digests.</summary>
    public const string BcryptPrefix = "$2";

    /// <summary>The family of one stored digest. Null or unrecognised is <see cref="RecoveryCodeFamily.Sha256"/>.</summary>
    public static RecoveryCodeFamily Classify(string? storedDigest)
    {
        if (storedDigest is null)
            return RecoveryCodeFamily.Sha256;

        if (storedDigest.StartsWith(Argon2idPrefix, StringComparison.Ordinal))
            return RecoveryCodeFamily.Argon2id;

        return storedDigest.StartsWith(BcryptPrefix, StringComparison.Ordinal)
            ? RecoveryCodeFamily.Bcrypt
            : RecoveryCodeFamily.Sha256;
    }

    /// <summary>
    /// True when any element is in a legacy family (BCrypt or salted SHA-256). Null or empty is false:
    /// a user with no codes has nothing to regenerate.
    /// </summary>
    public static bool HoldsLegacy(IReadOnlyList<string>? digests)
    {
        if (digests is null)
            return false;

        for (var i = 0; i < digests.Count; i++)
        {
            if (Classify(digests[i]) != RecoveryCodeFamily.Argon2id)
                return true;
        }

        return false;
    }
}
