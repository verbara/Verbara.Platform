namespace Verbara.Platform.Core;

/// <summary>
/// The canonical form of a Voice number, the one reduction the dial and every contact store compare
/// (contacts-writes-follow-their-permissions, design D10): the ASCII digits, prefixed with <c>+</c> only when the first
/// character that is not a blank is <c>+</c>. A blank is an ASCII space, tab, CR or LF and nothing else, so the form is the
/// same wherever it is computed. An input with no ASCII digit reduces to the empty string, never to a lone <c>+</c>. No
/// country code, trunk prefix or international prefix is added or removed.
/// </summary>
public static class VoiceNumber
{
    /// <summary>The canonical form of <paramref name="raw"/>.</summary>
    public static string Canonical(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var trimmed = raw.AsSpan().TrimStart(" \t\r\n");
        var plus = trimmed.Length > 0 && trimmed[0] == '+';
        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        return plus && digits.Length > 0 ? "+" + digits : digits;
    }
}
