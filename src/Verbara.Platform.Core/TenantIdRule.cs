namespace Verbara.Platform.Core;

/// <summary>
/// The one tenant-id rule every create path applies (realtime-objects-stay-in-their-tenant, design D1). Realtime
/// object names start with the tenant id followed by <c>-</c>, and trunks use the literal prefix <c>t-</c>, so a new id
/// must be a lowercase slug, must not be reserved, and must not overlap a stored id.
/// </summary>
public static class TenantIdRule
{
    /// <summary>The longest tenant id a create path accepts.</summary>
    public const int MaxLength = 63;

    /// <summary>1-63 characters from <c>[a-z0-9-]</c>, first and last not <c>-</c>. No trimming, no case folding.</summary>
    public static bool IsWellFormed(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
            return false;
        foreach (var c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' || c == '-'))
                return false;
        }
        return id[0] != '-' && id[^1] != '-';
    }

    /// <summary>
    /// <c>t</c> (the trunk namespace <c>t-{n}</c> shares tables with tenant-prefixed names) or <c>platform</c> (the
    /// host tenant id).
    /// </summary>
    public static bool IsReserved(string? id) =>
        string.Equals(id, "t", StringComparison.Ordinal) || string.Equals(id, "platform", StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are equal ignoring case, or one, lowercased, starts with
    /// the other, lowercased, followed by <c>-</c>.
    /// </summary>
    public static bool Overlaps(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return longer.Length > shorter.Length
            && longer[shorter.Length] == '-'
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }
}
