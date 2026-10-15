using System.Security.Cryptography;
using Verbara.Platform.Identity.Mfa;
using OtpNet;

namespace Verbara.Platform.Api.Services;

internal sealed class MfaService
{
    private const int SecretSize = 20;
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeBytes = 8;

    /// <summary>
    /// Longest submitted code that is evaluated at all, after trimming. Minted codes are 16 (hex) or 8
    /// (Base32) characters; anything longer is a miss with no evaluation, so a caller cannot make one
    /// attempt hash an arbitrarily large input ten times.
    /// </summary>
    private const int MaxSubmittedCodeLength = 64;

    public static (string Secret, string QrUri) GenerateSetup(string email, string issuer = "Verbara")
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(SecretSize);
        var secret = Base32Encoding.ToString(secretBytes);
        var qrUri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}" +
                    $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        return (secret, qrUri);
    }

    public static bool VerifyCode(string secret, string code)
    {
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    public static IReadOnlyList<string> GenerateRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(RecoveryCodeBytes);
            codes.Add(Convert.ToHexStringLower(bytes));
        }
        return codes;
    }

    /// <summary>
    /// The single mint digest for recovery codes: one Argon2id digest per code, over the normalised
    /// plaintext (<see cref="Normalize"/>), at the password parameters (<see cref="Argon2idHasher"/>).
    /// All four mint paths call it: <c>POST /auth/mfa/setup</c>,
    /// <c>POST /auth/mfa/recovery-codes/regenerate</c>, <c>POST /profile/security/mfa/enroll/verify</c>
    /// and <c>POST /profile/security/recovery-codes/regenerate</c>. Each path keeps its own plaintext
    /// generator. The codes are hashed one after another (about 40 ms each): hashing them in parallel
    /// would spend the same CPU and only move the contention.
    /// </summary>
    public static IReadOnlyList<string> HashRecoveryCodes(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var digests = new List<string>(codes.Count);
        foreach (var code in codes)
            digests.Add(Argon2idHasher.Hash(Normalize(code)));
        return digests;
    }

    /// <summary>
    /// Trim and upper-case a recovery code, as the salted-SHA-256 family always has. The Argon2id mint
    /// and the Argon2id branch of <see cref="ValidateRecoveryCode"/> both apply it, so a code typed in
    /// lower case or with surrounding whitespace still matches. The BCrypt branch verifies the raw input,
    /// exactly as before.
    /// </summary>
    private static string Normalize(string code) => code.Trim().ToUpperInvariant();

    /// <summary>
    /// Verify a submitted recovery code against the stored digests. Each stored
    /// element is classified by its own prefix (<see cref="RecoveryCodeFamilies.Classify"/>) and
    /// dispatched independently:
    /// <list type="bullet">
    ///   <item><description><c>$argon2id$…</c> → Argon2id verify of the normalised code through
    ///   <see cref="Argon2idHasher.SafeVerify"/> (written by <see cref="HashRecoveryCodes"/>, the
    ///   family every mint path writes).</description></item>
    ///   <item><description><c>$2a$/$2b$…</c> → BCrypt verify of the raw code (legacy: written by the
    ///   <c>/auth/mfa/*</c> paths before they moved to Argon2id).</description></item>
    ///   <item><description>anything else → salted SHA-256 verify via <see cref="IRecoveryCodeService.Verify"/>
    ///   (legacy: written by the profile wizard through <see cref="IRecoveryCodeService.Hash"/>).</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three digest families coexist in <c>users.mfa_recovery_codes</c>. New codes are Argon2id; codes
    /// minted earlier stay BCrypt cost 10 or salted SHA-256 until the user regenerates, because only the
    /// redeemed code ever arrives in plaintext and it is consumed, so the rest of an array cannot be
    /// rehashed. Dispatch is <b>per element</b>, never once for the whole array, so an array that mixes
    /// families still verifies every element on its own terms.
    /// </para>
    /// <para>
    /// The pattern originates in Platform/ADR-0013 (password-hash migration), whose
    /// "Forward compatibility" section pre-authorises adding a branch:
    /// <see cref="PasswordService.VerifyPassword"/> carries the identical prefix dispatch and the same
    /// guards (<see cref="BcryptVerifyGuard"/>, <see cref="Argon2idHasher.SafeVerify"/>).
    /// </para>
    /// <para>
    /// The method is fail-closed by construction: a stored value that cannot be
    /// <i>positively</i> verified is a non-match, never an exception. An unrecognised,
    /// truncated, corrupt or out-of-bound digest therefore yields <c>(false, -1)</c> rather than
    /// escaping to <c>ErrorHandlingMiddleware</c> as an HTTP 500. A submitted code longer than
    /// 64 characters after trimming is a miss with no evaluation.
    /// </para>
    /// <para>
    /// The salt is a parameter because <see cref="IRecoveryCodeService.Hash"/> salts
    /// with the user's id and that value is not recoverable from the digest; the
    /// caller supplies <c>user.UserId.Value</c>. Argon2id digests carry their own salt and ignore it.
    /// Every comparison is constant-time and lives in one already-tested place: the SHA-256 branch
    /// delegates to <see cref="IRecoveryCodeService.Verify"/>
    /// (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>),
    /// and the Argon2id branch leaves the tag comparison to the library.
    /// </para>
    /// </remarks>
    /// <param name="recoveryCodes">Salted-SHA-256 verifier for the legacy SHA-256 family.</param>
    /// <param name="code">The plaintext recovery code submitted by the user.</param>
    /// <param name="hashedCodes">The stored digests, in any mix of the three families.</param>
    /// <param name="salt">Per-user salt for the SHA-256 family — <c>user.UserId.Value</c>.</param>
    /// <returns>
    /// <c>(true, index)</c> for the first element that positively verifies, so the
    /// caller can remove exactly that element; otherwise <c>(false, -1)</c>.
    /// </returns>
    public static (bool IsValid, int Index) ValidateRecoveryCode(
        IRecoveryCodeService recoveryCodes,
        string code,
        IReadOnlyList<string> hashedCodes,
        string salt)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        if (string.IsNullOrWhiteSpace(code))
            return (false, -1);

        var normalized = Normalize(code);
        if (normalized.Length > MaxSubmittedCodeLength)
            return (false, -1);

        for (var i = 0; i < hashedCodes.Count; i++)
        {
            var stored = hashedCodes[i];

            switch (RecoveryCodeFamilies.Classify(stored))
            {
                case RecoveryCodeFamily.Argon2id:
                    // Bounded before evaluation and fail-closed: a stored digest with an out-of-bound
                    // cost or a field the library rejects is "does not match", never an exception.
                    if (Argon2idHasher.SafeVerify(stored, normalized))
                        return (true, i);
                    break;

                case RecoveryCodeFamily.Bcrypt:
                    // Any failure to parse this stored element is "does not match", never a
                    // raised exception and never a match. The guard lives in one place
                    // (BcryptVerifyGuard) because catching SaltParseException alone is NOT
                    // enough: a digest corrupt INSIDE the "$2" family raises
                    // IndexOutOfRangeException / FormatException / ArgumentOutOfRangeException
                    // instead, two of which reach ErrorHandlingMiddleware's `_` arm as a 500.
                    if (BcryptVerifyGuard.SafeVerify(code, stored))
                        return (true, i);
                    break;

                default:
                    // Salted SHA-256 (or unrecognised). Delegate; do not reimplement the
                    // digest or the constant-time comparison on an auth path.
                    if (recoveryCodes.Verify(code, salt, stored))
                        return (true, i);
                    break;
            }
        }

        return (false, -1);
    }
}
