namespace Verbara.Platform.Api.Services.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D5): the one normalizer of a number Platform dials toward a trunk. It keeps the
/// digits and a single leading <c>+</c> (an E.164 number is dialed as typed; the <c>[outbound-agent]</c> context matches
/// <c>_+X.</c>), and drops every other character, including a <c>+</c> that is not leading.
/// </summary>
internal static class DialNumber
{
    /// <summary>The digits of <paramref name="raw"/>, prefixed with <c>+</c> when its first non-blank character is one.</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var trimmed = raw.AsSpan().TrimStart();
        var plus = trimmed.Length > 0 && trimmed[0] == '+';
        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        return plus && digits.Length > 0 ? "+" + digits : digits;
    }
}
