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
    /// BCrypt digest prefix. BCrypt.Net-Next emits <c>$2a$</c> historically and
    /// <c>$2b$</c> since 4.x; both start with <c>$2</c> — that is the canonical
    /// discriminator, identical to the one
    /// <see cref="PasswordService.IsBcryptHash"/> uses for password hashes.
    /// </summary>
    private const string BcryptPrefix = "$2";

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

    public static IReadOnlyList<string> HashRecoveryCodes(IReadOnlyList<string> codes) =>
        codes.Select(c => BCrypt.Net.BCrypt.HashPassword(c, workFactor: 10)).ToList();

    /// <summary>
    /// Verify a submitted recovery code against the stored digests. Each stored
    /// element is classified by its own prefix and dispatched independently:
    /// <list type="bullet">
    ///   <item><description><c>$2a$/$2b$…</c> → BCrypt verify (written by <see cref="HashRecoveryCodes"/>).</description></item>
    ///   <item><description>anything else → salted SHA-256 verify via <see cref="IRecoveryCodeService.Verify"/> (written by <see cref="IRecoveryCodeService.Hash"/>).</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two digest families coexist in <c>users.mfa_recovery_codes</c>: the legacy
    /// MFA endpoints mint BCrypt cost-10 digests, while the profile enrollment
    /// wizard mints 64-character salted SHA-256 hex digests. Dispatch is
    /// <b>per element</b>, never once for the whole array, so an array that mixes
    /// both families — reachable through a partially-applied migration or a manual
    /// database edit — still verifies every element on its own terms.
    /// </para>
    /// <para>
    /// The pattern originates in Platform/ADR-0013 (password-hash migration), whose
    /// "Forward compatibility" section pre-authorises adding a branch:
    /// <see cref="PasswordService.VerifyPassword"/> carries the identical prefix-sniff
    /// plus <see cref="BCrypt.Net.SaltParseException"/> guard. That guard's rationale —
    /// not leaking hash shape through the exception type — applies here verbatim.
    /// </para>
    /// <para>
    /// The method is fail-closed by construction: a stored value that cannot be
    /// <i>positively</i> verified is a non-match, never an exception. An unrecognised,
    /// truncated, or corrupt digest therefore yields <c>(false, -1)</c> rather than
    /// escaping to <c>ErrorHandlingMiddleware</c> as an HTTP 500.
    /// </para>
    /// <para>
    /// The salt is a parameter because <see cref="IRecoveryCodeService.Hash"/> salts
    /// with the user's id and that value is not recoverable from the digest; the
    /// caller supplies <c>user.UserId.Value</c>. The SHA-256 branch delegates rather
    /// than reimplements, so the constant-time comparison
    /// (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>)
    /// lives in exactly one already-tested place.
    /// </para>
    /// </remarks>
    /// <param name="recoveryCodes">Salted-SHA-256 verifier for the non-BCrypt family.</param>
    /// <param name="code">The plaintext recovery code submitted by the user.</param>
    /// <param name="hashedCodes">The stored digests, in either or both families.</param>
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

        for (var i = 0; i < hashedCodes.Count; i++)
        {
            var stored = hashedCodes[i];

            if (stored.StartsWith(BcryptPrefix, StringComparison.Ordinal))
            {
                // Any failure to parse this stored element is "does not match", never a
                // raised exception and never a match. The guard lives in one place
                // (BcryptVerifyGuard) because catching SaltParseException alone is NOT
                // enough: a digest corrupt INSIDE the "$2" family raises
                // IndexOutOfRangeException / FormatException / ArgumentOutOfRangeException
                // instead, two of which reach ErrorHandlingMiddleware's `_` arm as a 500.
                if (BcryptVerifyGuard.SafeVerify(code, stored))
                    return (true, i);

                continue;
            }

            // Not a BCrypt digest → salted SHA-256. Delegate; do not reimplement
            // the digest or the constant-time comparison on an auth path.
            if (recoveryCodes.Verify(code, salt, stored))
                return (true, i);
        }

        return (false, -1);
    }
}
