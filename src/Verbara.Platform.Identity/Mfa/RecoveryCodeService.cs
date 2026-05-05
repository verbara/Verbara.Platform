using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Verbara.Platform.Identity.Mfa;

/// <summary>
/// Default <see cref="IRecoveryCodeService"/> implementation backed by
/// <see cref="RandomNumberGenerator"/> + SHA-256.
/// </summary>
/// <remarks>
/// <para>
/// AOT-safe: no reflection, no third-party crypto. <see cref="Generate"/> uses
/// <c>stackalloc</c> spans for the entropy buffer and the encoded output, so no
/// transient heap allocation occurs per code beyond the returned <see cref="string"/>.
/// </para>
/// <para>
/// The Base32 alphabet excludes <c>0</c>, <c>1</c>, <c>O</c>, and <c>I</c> so codes
/// remain unambiguous when handwritten or read aloud. The 32-char alphabet
/// guarantees ~5 bits of entropy per character, so an 8-char code carries
/// ~40 bits — well within the threshold for one-time emergency codes that are
/// rate-limited and rotated on regenerate.
/// </para>
/// </remarks>
public sealed class RecoveryCodeService : IRecoveryCodeService
{
    private const int CodeCount = 10;
    private const int CodeLength = 8;

    // RFC 4648 Base32 alphabet without the visually confusable glyphs 0/1/O/I.
    // Length is exactly 32 to keep the bitmask + shift cheap.
    private static readonly char[] Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    /// <inheritdoc />
    public IReadOnlyList<string> Generate()
    {
        // Use HashSet to guarantee uniqueness within the batch. With 32^8 == ~1.1T
        // possible codes the probability of collision across 10 picks is negligible
        // but the loop guards against pathological CSPRNG failure modes anyway.
        var codes = new HashSet<string>(CodeCount, StringComparer.Ordinal);
        while (codes.Count < CodeCount)
        {
            codes.Add(GenerateOne());
        }

        return codes.ToList();
    }

    /// <inheritdoc />
    public string Hash(string code, string salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(salt);

        // SHA-256 of "{salt}:{normalized-code}". Normalisation = trim + uppercase
        // so users can type lowercase or surrounding whitespace without breaking
        // the lookup. Salt MUST be per-user (never shared) so identical codes
        // across two users produce different hashes.
        var normalized = code.Trim().ToUpperInvariant();
        var combined = Encoding.UTF8.GetBytes($"{salt}:{normalized}");
        var hash = SHA256.HashData(combined);
        return Convert.ToHexString(hash);
    }

    /// <inheritdoc />
    public bool Verify(string code, string salt, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var computed = Hash(code, salt);
        // Constant-time comparison to defeat timing attacks. Both values are
        // hex-encoded SHA-256 so length is fixed at 64 chars.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(storedHash));
    }

    private static string GenerateOne()
    {
        // 8 chars × 5 bits/char == 40 bits of entropy → 5 random bytes is enough
        // but we draw 8 so we can pick one byte per output character via a 5-bit
        // mask; rejecting indices > 31 is unnecessary because 0xF8 truncation
        // never goes out of range with a 32-char alphabet.
        Span<byte> entropy = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(entropy);

        Span<char> buffer = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            // Mask the low 5 bits → index 0..31 → never exceeds Alphabet.Length.
            buffer[i] = Alphabet[entropy[i] & 0x1F];
        }

        return new string(buffer);
    }
}
