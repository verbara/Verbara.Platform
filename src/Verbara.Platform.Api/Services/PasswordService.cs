using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Password hashing + verification facade.
/// </summary>
/// <remarks>
/// <para>
/// AHH Phase 4 — replaces BCrypt (workFactor 12, ~162 ms verify on AMD 9900X
/// per Phase 0 baseline) with Argon2id at the OWASP-2025 floor parameters
/// (m=19 MiB, t=2, p=1, ~33 ms verify; retuned to m=12 MiB, t=3 in v1.14.2,
/// see <see cref="Argon2idHasher"/>) as the canonical hash for new passwords. Legacy BCrypt hashes (<c>$2a$/$2b$</c>) remain verifiable so
/// already-deployed credentials keep working; the on-login rehash path in
/// <see cref="Verbara.Platform.Api.Endpoints.AuthEndpoints"/> migrates each
/// user transparently the next time they log in successfully.
/// </para>
/// <para>
/// <see cref="HashPassword"/> always emits Argon2id — there is no path that
/// produces a fresh BCrypt hash post-Phase-4. <see cref="VerifyPassword"/>
/// detects the algorithm by hash prefix and dispatches to the matching
/// implementation. <see cref="IsBcryptHash"/> exposes the discriminator so
/// callers (specifically the login handler) can decide whether to enqueue a
/// rehash command.
/// </para>
/// </remarks>
internal sealed class PasswordService
{
    /// <summary>BCrypt cost factor used for verify-only legacy compatibility.</summary>
    /// <remarks>
    /// Phase 4 ships with this constant unchanged at 12. The verify-only path
    /// pays full BCrypt cost; the migration to Argon2id happens in the
    /// background via <c>PasswordRehashCommand</c>. The constant is kept for
    /// any test that needs to forge a legacy hash.
    /// </remarks>
    public const int LegacyBcryptWorkFactor = 12;

    /// <summary>
    /// Hash a password with Argon2id. The parameters (m = 12 MiB, t = 3, p = 1) live in
    /// <see cref="Argon2idHasher"/>, shared with MFA recovery codes so one place tunes both.
    /// </summary>
    public static string HashPassword(string password) => Argon2idHasher.Hash(password);

    /// <summary>
    /// Verify a password against an existing hash. Detects the algorithm by
    /// prefix and dispatches:
    /// <list type="bullet">
    ///   <item><description><c>$argon2id$…</c> → Argon2id verify through <see cref="Argon2idHasher.SafeVerify"/>,
    ///   which bounds the stored parameters before evaluating and treats a library exception as a
    ///   failed verify.</description></item>
    ///   <item><description><c>$2a$/$2b$…</c> → BCrypt verify (legacy).</description></item>
    /// </list>
    /// </summary>
    public static bool VerifyPassword(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);

        // A corrupt stored Argon2id hash is a failed login, never an exception: Isopoh throws
        // ArgumentException for an encoded t=0, p=0 or empty salt, and that message would otherwise
        // reach ErrorHandlingMiddleware from the login path (ADR-0013).
        if (hash.StartsWith(Argon2idHasher.Prefix, StringComparison.Ordinal))
            return Argon2idHasher.SafeVerify(hash, password);

        // Default: legacy BCrypt. BCrypt.Net-Next accepts both $2a$ and $2b$
        // variants; any failure to parse the STORED hash is treated as a verify
        // failure (not an error path that should reveal hash shape).
        //
        // The guard moved into BcryptVerifyGuard because catching SaltParseException
        // alone left a hole: that type is raised only when the stored value does not
        // begin with "$". A hash corrupt INSIDE the "$2" family — truncated, or with a
        // non-numeric cost factor — raises IndexOutOfRangeException / FormatException /
        // ArgumentOutOfRangeException instead, and two of those surface as HTTP 500 on
        // the login path. ADR-0013 states the requirement; this makes both credential
        // verifiers actually meet it.
        return BcryptVerifyGuard.SafeVerify(password, hash);
    }

    /// <summary>
    /// True if the supplied hash is a BCrypt hash (legacy format), false if
    /// it is Argon2id (post-Phase-4 canonical format).
    /// </summary>
    /// <remarks>
    /// AHH Phase 4 — the login handler calls this immediately after a
    /// successful <see cref="VerifyPassword"/> to decide whether to enqueue a
    /// <c>PasswordRehashCommand</c> on the AuthWriteQueue. BCrypt hashes that
    /// validate today get re-emitted as Argon2id on the next user-driven
    /// successful login.
    /// </remarks>
    public static bool IsBcryptHash(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        // BCrypt.Net-Next emits "$2a$" historically and "$2b$" since 4.x; both
        // start with "$2" — that's the canonical discriminator.
        return hash.Length >= 4 && hash[0] == '$' && hash[1] == '2';
    }

    public static PasswordValidationResult ValidatePolicy(string password, TenantAuthConfig config)
    {
        var errors = new List<string>();

        if (password.Length < config.PasswordMinLength)
            errors.Add($"Password must be at least {config.PasswordMinLength} characters");

        if (config.PasswordRequireUppercase && !password.Any(char.IsUpper))
            errors.Add("Password must contain at least one uppercase letter");

        if (config.PasswordRequireNumber && !password.Any(char.IsDigit))
            errors.Add("Password must contain at least one number");

        if (config.PasswordRequireSpecial && password.All(char.IsLetterOrDigit))
            errors.Add("Password must contain at least one special character");

        return new PasswordValidationResult(errors.Count == 0, errors);
    }
}

internal sealed record PasswordValidationResult(bool IsValid, IReadOnlyList<string> Errors);
