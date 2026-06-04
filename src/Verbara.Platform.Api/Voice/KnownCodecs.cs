namespace Verbara.Platform.Api.Voice;

/// <summary>
/// The set of Asterisk/PJSIP codec tokens the platform recognises, plus a tolerant parser for
/// <c>core show codecs</c> output and a static fallback catalog used when Asterisk cannot be queried.
/// Token strings are load-bearing — they map 1:1 to PJSIP <c>allow=</c> values.
/// </summary>
internal static class KnownCodecs
{
    /// <summary>Every negotiable audio + video codec token we recognise (validation allowlist).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // audio
        "ulaw", "alaw", "g722", "opus", "g729", "gsm", "ilbc", "g726", "g726aal2", "adpcm",
        "speex", "speex16", "siren7", "siren14", "g719", "g723", "lpc10", "silk",
        // video
        "vp8", "vp9", "h264", "h263p", "h263", "h261", "mpeg4",
    };

    /// <summary>Returned when Asterisk cannot be queried (not AMI leader / unreachable / parse empty).</summary>
    public static readonly string[] FallbackCatalog =
    [
        "ulaw", "alaw", "g722", "opus", "g729", "gsm", "ilbc", "vp8", "h264",
    ];

    /// <summary>
    /// Extracts recognised codec tokens from raw <c>core show codecs</c> output. Tolerant of column
    /// layout differences across Asterisk versions: it tokenises every line and keeps only tokens that
    /// match <see cref="All"/> (so headers, descriptions, IDs and non-negotiable formats like
    /// <c>slin</c>/<c>wav</c> are naturally ignored). Preserves first-seen order, de-duplicates.
    /// </summary>
    public static string[] ParseInstalledCodecs(string? amiOutput)
    {
        if (string.IsNullOrWhiteSpace(amiOutput))
            return [];

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in amiOutput.Split('\n'))
        {
            foreach (var raw in line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (All.Contains(token) && seen.Add(token))
                    found.Add(token.ToLowerInvariant());
            }
        }

        return [.. found];
    }

    /// <summary>Returns the tokens in a comma-separated codec string that are NOT recognised (typos).</summary>
    public static IReadOnlyList<string> InvalidTokens(string? codecs)
    {
        if (string.IsNullOrWhiteSpace(codecs))
            return [];

        return [.. codecs
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !All.Contains(t))];
    }
}
