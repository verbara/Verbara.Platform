using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Identity;
using Isopoh.Cryptography.Argon2;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Password hashing + verification facade.
/// </summary>
/// <remarks>
/// <para>
/// AHH Phase 4 — replaces BCrypt (workFactor 12, ~162 ms verify on AMD 9900X
/// per Phase 0 baseline) with Argon2id at the OWASP-2025 floor parameters
/// (m=19 MiB, t=2, p=1, ~33 ms verify) as the canonical hash for new
/// passwords. Legacy BCrypt hashes (<c>$2a$/$2b$</c>) remain verifiable so
/// already-deployed credentials keep working; the on-login rehash path in
/// <see cref="Asterisk.Platform.Api.Endpoints.AuthEndpoints"/> migrates each
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
    /// v1.14.2 retuned: Argon2id m=12 MiB (was 19 MiB).
    /// OWASP-2025 specifies a parameter CURVE — m=46 MiB / t=1 OR
    /// m=19 MiB / t=2 OR m=12 MiB / t=3 all target roughly the same total
    /// work factor. v1.14.0 shipped with m=19 MiB which empirically
    /// saturates memory bandwidth + GC under sustained load (post-AHH
    /// single-replica regressed to ~50 req/s vs pre-AHH 75 req/s).
    /// Lowering m + raising t keeps the OWASP floor while shrinking the
    /// working-set per concurrent verify so GC pressure is less steep at
    /// 100+ req/s. See docs/operations/auth-horizontal-scaling.md.
    /// </summary>
    private const int Argon2MemoryCostKib = 12 * 1024;

    /// <summary>v1.14.2 retuned: Argon2id t=3 (was 2). See above.</summary>
    private const int Argon2TimeCost = 3;

    /// <summary>OWASP-2025 floor: Argon2id p=1.</summary>
    private const int Argon2Lanes = 1;

    /// <summary>Argon2id output length in bytes.</summary>
    private const int Argon2HashLength = 32;

    /// <summary>Salt length in bytes (RFC 9106 recommendation).</summary>
    private const int Argon2SaltLength = 16;

    /// <summary>
    /// Encoded-Argon2id prefix. Isopoh emits <c>$argon2id$v=19$m=…,t=…,p=…$salt$hash</c>;
    /// only the prefix is needed to discriminate from BCrypt.
    /// </summary>
    private const string Argon2idPrefix = "$argon2id$";

    /// <summary>
    /// Hash a password with Argon2id at the OWASP-2025 floor parameters.
    /// </summary>
    public static string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = new byte[Argon2SaltLength];
        RandomNumberGenerator.Fill(salt);

        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = Argon2TimeCost,
            MemoryCost = Argon2MemoryCostKib,
            Lanes = Argon2Lanes,
            Threads = Argon2Lanes,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = salt,
            HashLength = Argon2HashLength,
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }

    /// <summary>
    /// Verify a password against an existing hash. Detects the algorithm by
    /// prefix and dispatches:
    /// <list type="bullet">
    ///   <item><description><c>$argon2id$…</c> → Argon2id verify.</description></item>
    ///   <item><description><c>$2a$/$2b$…</c> → BCrypt verify (legacy).</description></item>
    /// </list>
    /// </summary>
    public static bool VerifyPassword(string password, string hash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hash);

        if (hash.StartsWith(Argon2idPrefix, StringComparison.Ordinal))
            return Argon2.Verify(hash, password);

        // Default: legacy BCrypt. BCrypt.Net-Next accepts both $2a$ and $2b$
        // variants; throws on malformed input which is treated as a verify
        // failure (not an error path that should reveal hash shape).
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
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
