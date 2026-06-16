using System.Text.RegularExpressions;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Reusable, allow-list-aware PII screen for AI-extracted typification field values
/// (D1, P2b). Detects payment cards (Luhn-validated), national-id/SSN, phone numbers,
/// and emails and replaces each occurrence with a type token unless the tenant has
/// allow-listed that <see cref="PiiType"/>.
/// </summary>
/// <remarks>
/// <para>
/// Detection patterns mirror Pro's <c>RegexTranscriptRedactor</c> (adapted, not referenced —
/// Typification MUST NOT depend on Pro). All regexes are <c>[GeneratedRegex]</c> source-gen
/// partials for Native AOT (no reflection, no <c>new Regex(...)</c>).
/// </para>
/// <para>
/// <b>ReDoS posture:</b> unlike <c>DefaultTypificationValidator</c> — which runs UNTRUSTED,
/// schema-supplied regexes under a per-call <c>RegexTimeout</c> — this screen uses ONLY the
/// FIXED, source-generated patterns below, applied to short AI-extracted field values, and
/// those patterns are verified non-catastrophic (linear, no nested unbounded quantifiers /
/// no backtracking blow-up). Consequently no per-call <c>RegexMatchTimeout</c> is configured
/// (contrast with the validator's untrusted-pattern path). Enforcing a maximum field length
/// is the CALLER's responsibility — D3's field caps run before storage. A length guard is
/// deliberately NOT added here: returning <c>(value, false)</c> on over-length input would
/// fail OPEN and let unmasked PII through.
/// </para>
/// </remarks>
public static partial class PiiScreen
{
    // [0-9] (not \d): after NormalizeDigits the input is ASCII for matching, so this is exact
    // and avoids any residual Unicode-digit ambiguity. \b boundary mirrors the Pro redactor:
    // a card fused into an alphanumeric token (e.g. "X4111111111111111Y", no word boundary) is
    // intentionally NOT detected, avoiding false-positives on long alphanumeric IDs.
    [GeneratedRegex(@"\b(?:[0-9][ -]*?){13,19}\b", RegexOptions.None)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\b\d{3}[-. ]?\d{2}[-. ]?\d{4}\b", RegexOptions.None)]
    private static partial Regex NationalIdPattern();

    // Leading boundary is a lookbehind for "not preceded by a digit/word char" instead of
    // \b, so an optional leading '+' (a non-word char) is included in the match rather than
    // orphaned outside it (e.g. "+1 415-555-0132" → the whole span, not "1 415-555-0132").
    [GeneratedRegex(@"(?<![\w+])(?:\+?1[-. ]?)?\(?\d{3}\)?[-. ]?\d{3}[-. ]?\d{4}\b", RegexOptions.None)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\b[\w.\-]+@[\w.\-]+\.\w{2,}\b", RegexOptions.None)]
    private static partial Regex EmailPattern();

    private const string CardToken = "[CARD]";
    private const string NationalIdToken = "[NATIONAL_ID]";
    private const string PhoneToken = "[PHONE]";
    private const string EmailToken = "[EMAIL]";

    /// <summary>
    /// Screens a single (AI-extracted) field value: detects card (Luhn-validated),
    /// national-id/SSN, phone, and email occurrences and replaces each with a type token
    /// ([CARD] / [NATIONAL_ID] / [PHONE] / [EMAIL]) UNLESS that PiiType is in
    /// <paramref name="policy"/>.AllowStore. Returns the (possibly) screened value and
    /// whether any masking occurred. Never throws; a null/empty/whitespace value returns
    /// (value, false).
    /// </summary>
    public static (string Value, bool Masked) Apply(string value, PiiPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (value, false);

        // Normalize Unicode decimal digits (fullwidth, Arabic-Indic, Devanagari, …) to ASCII so
        // the digit-based patterns + Luhn operate on a consistent ASCII view. NormalizeDigits is a
        // 1-char → 1-char mapping, so offsets are preserved: matches found against `normalized`
        // splice cleanly into the ORIGINAL `value` at the same indices/lengths. Without this a real
        // card written in non-ASCII digits would match the pattern but fail the ASCII-only Luhn
        // (digits[i] - '0'), slipping through unmasked — the dangerous false-negative direction.
        var normalized = NormalizeDigits(value);

        // Collect all matches across all patterns, then apply replacements right-to-left
        // so earlier offsets stay valid while we splice. Allow-listed types are skipped
        // at collection time so they are never masked.
        var matches = new List<(int Start, int Length, string Token)>();

        if (!policy.AllowStore.Contains(PiiType.Card))
        {
            foreach (Match m in CardPattern().Matches(normalized))
            {
                var digits = m.Value.Replace(" ", "").Replace("-", "");
                if (IsLuhnValid(digits))
                    matches.Add((m.Index, m.Length, CardToken));
            }
        }

        if (!policy.AllowStore.Contains(PiiType.NationalId))
        {
            foreach (Match m in NationalIdPattern().Matches(normalized))
                matches.Add((m.Index, m.Length, NationalIdToken));
        }

        if (!policy.AllowStore.Contains(PiiType.Phone))
        {
            foreach (Match m in PhonePattern().Matches(normalized))
                matches.Add((m.Index, m.Length, PhoneToken));
        }

        if (!policy.AllowStore.Contains(PiiType.Email))
        {
            foreach (Match m in EmailPattern().Matches(normalized))
                matches.Add((m.Index, m.Length, EmailToken));
        }

        if (matches.Count == 0)
            return (value, false);

        // Sort by start position descending so we can splice without invalidating offsets.
        matches.Sort((a, b) => b.Start.CompareTo(a.Start));

        // Filter overlapping matches (keep the first encountered while sorted descending —
        // i.e. the rightmost non-overlapping run; deterministic overlap resolution).
        var result = value;
        var anyReplaced = false;
        int lastStart = int.MaxValue;
        foreach (var (start, length, token) in matches)
        {
            if (start + length > lastStart)
                continue;

            result = result[..start] + token + result[(start + length)..];
            lastStart = start;
            anyReplaced = true;
        }

        return (result, anyReplaced);
    }

    private static string NormalizeDigits(string value)
    {
        // Map any Unicode decimal digit (fullwidth, Arabic-Indic, Devanagari, …) to ASCII
        // so the digit-based patterns + Luhn operate on a consistent ASCII view. 1:1 char
        // mapping → offsets are preserved, so matches found here splice into the original.
        char[]? buffer = null;
        for (var i = 0; i < value.Length; i++)
        {
            var d = System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(value[i]);
            if (d >= 0 && value[i] is < '0' or > '9') // a non-ASCII decimal digit
            {
                buffer ??= value.ToCharArray();
                buffer[i] = (char)('0' + d);
            }
        }
        return buffer is null ? value : new string(buffer);
    }

    private static bool IsLuhnValid(string digits)
    {
        int sum = 0;
        bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
