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
    public static bool IsWellFormed(string? id) => throw new NotImplementedException();

    /// <summary>
    /// <c>t</c> (the trunk namespace <c>t-{n}</c> shares tables with tenant-prefixed names) or <c>platform</c> (the
    /// host tenant id).
    /// </summary>
    public static bool IsReserved(string? id) => throw new NotImplementedException();

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are equal ignoring case, or one, lowercased, starts with
    /// the other, lowercased, followed by <c>-</c>.
    /// </summary>
    public static bool Overlaps(string a, string b) => throw new NotImplementedException();
}
