namespace Asterisk.Platform.Channels.Sms;

/// <summary>
/// Calculates SMS segment counts and truncates bodies according to GSM-7 and UCS-2 encoding rules.
/// </summary>
public sealed class SmsSegmentCalculator
{
    // GSM-7 basic character set (128 chars) plus extension table chars that count as 2 chars.
    // Single-segment limits: GSM-7 = 160, UCS-2 = 70
    // Multi-segment limits (with UDH): GSM-7 = 153 per segment, UCS-2 = 67 per segment
    private const int Gsm7SingleSegment = 160;
    private const int Gsm7MultiSegment = 153;
    private const int Ucs2SingleSegment = 70;
    private const int Ucs2MultiSegment = 67;

    private static readonly HashSet<char> Gsm7BasicChars = new(
    [
        '@', '£', '$', '¥', 'è', 'é', 'ù', 'ì', 'ò', 'Ç', '\n', 'Ø', 'ø', '\r', 'Å', 'å',
        'Δ', '_', 'Φ', 'Γ', 'Λ', 'Ω', 'Π', 'Ψ', 'Σ', 'Θ', 'Ξ', '\x1b', 'Æ', 'æ', 'ß', 'É',
        ' ', '!', '"', '#', '¤', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.', '/',
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ':', ';', '<', '=', '>', '?',
        '¡', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O',
        'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'Ä', 'Ö', 'Ñ', 'Ü', '§',
        '¿', 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o',
        'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', 'ä', 'ö', 'ñ', 'ü', 'à',
    ]);

    // GSM-7 extension table characters — each counts as 2 chars in the encoded message.
    private static readonly HashSet<char> Gsm7ExtendedChars = new(
    [
        '\f', '^', '{', '}', '\\', '[', '~', ']', '|', '€',
    ]);

    /// <summary>Returns true if the body contains any character outside the GSM-7 charset.</summary>
    public static bool RequiresUcs2(string body)
    {
        foreach (var ch in body)
        {
            if (!Gsm7BasicChars.Contains(ch) && !Gsm7ExtendedChars.Contains(ch))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Calculates the number of SMS segments for the given body.
    /// Extended GSM-7 chars (e.g. €) count as 2 characters each.
    /// </summary>
    public static int CalculateSegments(string body)
    {
        if (string.IsNullOrEmpty(body))
            return 1;

        if (RequiresUcs2(body))
        {
            var len = body.Length;
            if (len <= Ucs2SingleSegment)
                return 1;
            return (len + Ucs2MultiSegment - 1) / Ucs2MultiSegment;
        }
        else
        {
            var encodedLength = 0;
            foreach (var ch in body)
            {
                encodedLength += Gsm7ExtendedChars.Contains(ch) ? 2 : 1;
            }

            if (encodedLength <= Gsm7SingleSegment)
                return 1;
            return (encodedLength + Gsm7MultiSegment - 1) / Gsm7MultiSegment;
        }
    }

    /// <summary>
    /// Truncates the body so it fits within <paramref name="maxSegments"/> SMS segments.
    /// Returns the original body if it already fits.
    /// </summary>
    public static string Truncate(string body, int maxSegments)
    {
        if (string.IsNullOrEmpty(body) || maxSegments <= 0)
            return string.Empty;

        if (CalculateSegments(body) <= maxSegments)
            return body;

        if (RequiresUcs2(body))
        {
            var maxChars = maxSegments == 1 ? Ucs2SingleSegment : maxSegments * Ucs2MultiSegment;
            return body.Length > maxChars ? body[..maxChars] : body;
        }
        else
        {
            var maxEncoded = maxSegments == 1 ? Gsm7SingleSegment : maxSegments * Gsm7MultiSegment;
            var count = 0;
            var i = 0;
            while (i < body.Length)
            {
                count += Gsm7ExtendedChars.Contains(body[i]) ? 2 : 1;
                if (count > maxEncoded)
                    break;
                i++;
            }
            return body[..i];
        }
    }
}
