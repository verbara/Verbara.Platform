using System.Text.RegularExpressions;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Reusable, allow-list-aware PII screen for AI-extracted typification field values
/// (D1, P2b). Detects payment cards (Luhn-validated), national-id/SSN, phone numbers,
/// and emails and replaces each occurrence with a type token unless the tenant has
/// allow-listed that <see cref="PiiType"/>.
/// </summary>
/// <remarks>
/// Detection patterns mirror Pro's <c>RegexTranscriptRedactor</c> (adapted, not referenced —
/// Typification MUST NOT depend on Pro). All regexes are <c>[GeneratedRegex]</c> source-gen
/// partials for Native AOT (no reflection, no <c>new Regex(...)</c>).
/// </remarks>
public static partial class PiiScreen
{
    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.None)]
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

        // Collect all matches across all patterns, then apply replacements right-to-left
        // so earlier offsets stay valid while we splice. Allow-listed types are skipped
        // at collection time so they are never masked.
        var matches = new List<(int Start, int Length, string Token)>();

        if (!policy.AllowStore.Contains(PiiType.Card))
        {
            foreach (Match m in CardPattern().Matches(value))
            {
                var digits = m.Value.Replace(" ", "").Replace("-", "");
                if (IsLuhnValid(digits))
                    matches.Add((m.Index, m.Length, CardToken));
            }
        }

        if (!policy.AllowStore.Contains(PiiType.NationalId))
        {
            foreach (Match m in NationalIdPattern().Matches(value))
                matches.Add((m.Index, m.Length, NationalIdToken));
        }

        if (!policy.AllowStore.Contains(PiiType.Phone))
        {
            foreach (Match m in PhonePattern().Matches(value))
                matches.Add((m.Index, m.Length, PhoneToken));
        }

        if (!policy.AllowStore.Contains(PiiType.Email))
        {
            foreach (Match m in EmailPattern().Matches(value))
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
