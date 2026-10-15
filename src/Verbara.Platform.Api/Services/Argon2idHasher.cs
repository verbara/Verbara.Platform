using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The one place the Api derives and checks Argon2id digests: password hashes
/// (<see cref="PasswordService"/>) and MFA recovery codes (<see cref="MfaService"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both secrets use the same parameters, so retuning the cost here retunes both. The values are
/// the v1.14.2 point on the OWASP-2025 curve (m = 12 MiB, t = 3, p = 1). OWASP lists m = 46 MiB /
/// t = 1, m = 19 MiB / t = 2 and m = 12 MiB / t = 3 as roughly equal total work; v1.14.0 shipped
/// m = 19 MiB, which saturated memory bandwidth and GC under sustained load (a single replica fell
/// to about 50 req/s from 75 req/s before the change). Lowering m and raising t keeps the OWASP
/// floor while shrinking the working set of each concurrent verify. See
/// docs/operations/auth-horizontal-scaling.md.
/// </para>
/// <para>
/// <see cref="SafeVerify"/> is fail-closed, as <see cref="BcryptVerifyGuard"/> is for BCrypt. The
/// library (Isopoh.Cryptography.Argon2 2.0.0) throws <see cref="ArgumentException"/> when a stored
/// digest encodes <c>t=0</c>, <c>p=0</c> or an empty salt. It also honours whatever memory cost the
/// stored string names: one verify of a stored <c>m=262144</c> allocated over 2 GiB. So a stored
/// digest is parsed and its parameters bounded <b>before</b> anything is evaluated, and any library
/// exception is a non-match (defence in depth: no input that passes the bound check is known to make
/// Isopoh 2.0.0 throw, so the catch is unreachable today; a probe of 13 in-bound shapes found none).
/// The derived tag is compared by the library, in constant time
/// (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>);
/// this class never compares tags itself.
/// </para>
/// </remarks>
internal static class Argon2idHasher
{
    /// <summary>
    /// Encoded-Argon2id prefix. Isopoh emits <c>$argon2id$v=19$m=…,t=…,p=…$salt$tag</c>; the prefix
    /// alone tells it apart from BCrypt (<c>$2</c>) and salted SHA-256 (hex).
    /// </summary>
    public const string Prefix = "$argon2id$";

    /// <summary>Memory cost in KiB: 12 MiB (v1.14.2, was 19 MiB).</summary>
    internal const int MemoryCostKib = 12 * 1024;

    /// <summary>Time cost (iterations): 3 (v1.14.2, was 2).</summary>
    internal const int TimeCost = 3;

    /// <summary>Lanes: 1, the OWASP-2025 floor.</summary>
    internal const int Lanes = 1;

    /// <summary>Tag (output) length in bytes.</summary>
    internal const int HashLength = 32;

    /// <summary>Salt length in bytes (RFC 9106 recommendation).</summary>
    internal const int SaltLength = 16;

    // Bounds a stored digest must meet before it is evaluated. They admit every digest this Api has
    // ever written (m = 19 MiB / t = 2 before v1.14.2, m = 12 MiB / t = 3 since) and at most twice the
    // current work (m × t ≤ 2 × 12288 × 3), so the costliest stored digest the bound lets through costs
    // about two production verifies: m = 24 MiB / t = 3 allocated about 200 MiB against about 100 MiB at
    // production (Argon2idHasherTests). The first bound (m ≤ 64 MiB, t ≤ 10) let m = 64 MiB / t = 10
    // through, which allocated 1,472 MiB in one verify. Retuning above this is a change to these lines.
    private const int MaxMemoryCostKib = 32 * 1024;
    private const int MaxTimeCost = 4;
    private const long MaxWorkKibPasses = 2L * MemoryCostKib * TimeCost;
    private const int MinSaltBytes = 8;
    private const int MinTagBytes = 16;
    private const int MaxSaltOrTagBytes = 1024;

    /// <summary>Derive an Argon2id digest of <paramref name="secret"/> with a fresh random salt.</summary>
    /// <returns>The PHC-encoded digest, <c>$argon2id$v=19$m=12288,t=3,p=1$salt$tag</c>.</returns>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var salt = new byte[SaltLength];
        RandomNumberGenerator.Fill(salt);

        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = TimeCost,
            MemoryCost = MemoryCostKib,
            Lanes = Lanes,
            Threads = Lanes,
            Password = Encoding.UTF8.GetBytes(secret),
            Salt = salt,
            HashLength = HashLength,
        };

        using var argon2 = new Argon2(config);
        using var hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }

    /// <summary>
    /// Check <paramref name="secret"/> against a stored Argon2id digest. Returns <see langword="true"/>
    /// only on a positive match; a malformed, out-of-bound or otherwise unverifiable digest returns
    /// <see langword="false"/> and never throws. <see cref="OperationCanceledException"/> and
    /// <see cref="OutOfMemoryException"/> still propagate, as in <see cref="BcryptVerifyGuard"/>: they
    /// describe the process, not the stored digest.
    /// </summary>
    public static bool SafeVerify(string encoded, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (!IsWithinBounds(encoded))
            return false;

        try
        {
            return Argon2.Verify(encoded, secret);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Stored material that the bound check let through but the library refuses is a
            // non-match. The exception's message names the corrupt field, so it must not reach the
            // caller (ADR-0013: a credential verifier never reveals the stored shape).
            return false;
        }
    }

    /// <summary>
    /// Parse <c>$argon2id$v=19$m=M,t=T,p=P$salt$tag</c> without evaluating anything and accept it only
    /// when every parameter is inside the fixed bounds above.
    /// </summary>
    private static bool IsWithinBounds(string? encoded)
    {
        if (encoded is null || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // ["", "argon2id", "v=19", "m=…,t=…,p=…", salt, tag]
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[2] != "v=19")
            return false;

        var parameters = parts[3].Split(',');
        if (parameters.Length != 3
            || !TryReadParameter(parameters[0], "m=", out var memoryKib)
            || !TryReadParameter(parameters[1], "t=", out var timeCost)
            || !TryReadParameter(parameters[2], "p=", out var lanes))
        {
            return false;
        }

        // Argon2 needs at least 8 KiB per lane; anything else here is a value no mint path writes.
        if (memoryKib < 8 * Lanes || memoryKib > MaxMemoryCostKib
            || timeCost < 1 || timeCost > MaxTimeCost
            || (long)memoryKib * timeCost > MaxWorkKibPasses
            || lanes != Lanes)
        {
            return false;
        }

        return TryDecodedLength(parts[4], out var saltBytes) && saltBytes >= MinSaltBytes
            && TryDecodedLength(parts[5], out var tagBytes) && tagBytes >= MinTagBytes;
    }

    private static bool TryReadParameter(string segment, string name, out int value)
    {
        value = 0;
        if (!segment.StartsWith(name, StringComparison.Ordinal))
            return false;

        var digits = segment.AsSpan(name.Length);
        // Bounded length first so a long run of digits cannot overflow or cost anything to parse.
        return digits.Length is > 0 and <= 7
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Decoded byte length of an unpadded standard-base64 segment, or false when it is not one.</summary>
    private static bool TryDecodedLength(string segment, out int length)
    {
        length = 0;
        if (segment.Length == 0 || segment.Length > MaxSaltOrTagBytes * 2 || segment.Length % 4 == 1)
            return false;

        var padded = segment.Length % 4 == 0 ? segment : segment + new string('=', 4 - (segment.Length % 4));
        Span<byte> buffer = stackalloc byte[(padded.Length / 4) * 3];
        return Convert.TryFromBase64String(padded, buffer, out length);
    }
}
