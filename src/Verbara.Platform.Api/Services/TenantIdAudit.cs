using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

/// <summary>Why a stored tenant id is flagged (realtime-objects-stay-in-their-tenant, design D2).</summary>
internal static class TenantIdFlagReason
{
    public const string NotASlug = "not-a-slug";
    public const string Reserved = "reserved";
    public const string CaseVariant = "case-variant";
    public const string PrefixPair = "prefix-pair";
}

/// <summary>One reason a stored tenant id is flagged.</summary>
internal sealed record TenantIdFlag(string TenantId, TenantStatus Status, string Reason);

/// <summary>Two stored ids that overlap, once, as (shorter, longer); same length in ordinal order.</summary>
internal sealed record TenantIdPair(StoredTenantId Shorter, StoredTenantId Longer, string Reason);

/// <summary>What <see cref="TenantIdAudit.RunAsync"/> found over every stored tenant id.</summary>
internal sealed record TenantIdAuditReport(
    IReadOnlyList<StoredTenantId> Stored,
    IReadOnlyList<TenantIdFlag> Flags,
    IReadOnlyList<TenantIdPair> Pairs)
{
    /// <summary>True when <paramref name="tenantId"/> carries any flag.</summary>
    public bool IsFlagged(string tenantId) => Flags.Any(f => string.Equals(f.TenantId, tenantId, StringComparison.Ordinal));

    /// <summary>The reasons of <paramref name="tenantId"/>, comma-separated, or null when it carries none.</summary>
    public string? ReasonOf(string tenantId)
    {
        var reasons = Flags.Where(f => string.Equals(f.TenantId, tenantId, StringComparison.Ordinal))
            .Select(f => f.Reason).Distinct(StringComparer.Ordinal).ToList();
        return reasons.Count == 0 ? null : string.Join(',', reasons);
    }

    /// <summary>The stored tenant whose id equals <paramref name="tenantId"/> exactly, or null.</summary>
    public StoredTenantId? Find(string tenantId) =>
        Stored.FirstOrDefault(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal));
}

/// <summary>
/// Flags every stored tenant id, of any status, that breaks the tenant-id rule (realtime-objects-stay-in-their-tenant,
/// design D2): not a slug, reserved (the host tenant itself excepted), equal to another ignoring case, or forming a
/// hyphen-prefix pair with another. Both members of a pair are flagged. Pairs are found in O(n log n) (see
/// <see cref="OverlappingPairs"/>), since the audit runs on each probe, reconcile and fence. Evaluated on each call, never cached: the
/// flagged set is a function of the stored ids alone, and no path of this release renames or removes a stored id.
/// </summary>
internal sealed class TenantIdAudit
{
    /// <summary>The host tenant id, exempt from <see cref="TenantIdFlagReason.Reserved"/>.</summary>
    public const string HostTenantId = "platform";

    private readonly ITenantIdRegistry _registry;

    public TenantIdAudit(ITenantIdRegistry registry) => _registry = registry;

    public async ValueTask<TenantIdAuditReport> RunAsync(CancellationToken ct)
    {
        var stored = await _registry.ListAllIdsAsync(ct).ConfigureAwait(false);
        var flags = new List<TenantIdFlag>();
        var seen = new HashSet<(string, string)>();
        void Flag(StoredTenantId s, string reason)
        {
            if (seen.Add((s.TenantId, reason)))
                flags.Add(new TenantIdFlag(s.TenantId, s.Status, reason));
        }

        foreach (var s in stored)
        {
            if (!TenantIdRule.IsWellFormed(s.TenantId))
                Flag(s, TenantIdFlagReason.NotASlug);
            if (TenantIdRule.IsReserved(s.TenantId) && !string.Equals(s.TenantId, HostTenantId, StringComparison.Ordinal))
                Flag(s, TenantIdFlagReason.Reserved);
        }

        var pairs = new List<TenantIdPair>();
        foreach (var (a, b) in OverlappingPairs(stored))
        {
            var reason = string.Equals(a.TenantId, b.TenantId, StringComparison.OrdinalIgnoreCase)
                ? TenantIdFlagReason.CaseVariant
                : TenantIdFlagReason.PrefixPair;
            Flag(a, reason);
            Flag(b, reason);
            var aFirst = a.TenantId.Length != b.TenantId.Length
                ? a.TenantId.Length < b.TenantId.Length
                : string.CompareOrdinal(a.TenantId, b.TenantId) < 0;
            pairs.Add(aFirst ? new TenantIdPair(a, b, reason) : new TenantIdPair(b, a, reason));
        }

        return new TenantIdAuditReport(stored, flags, pairs);
    }

    /// <summary>
    /// Every unordered pair of stored ids for which <see cref="TenantIdRule.Overlaps"/> holds, in
    /// O(n log n + n·L + pairs) instead of comparing every pair. Two ids overlap only when one, case-folded, is a
    /// prefix of the other, so the ids are sorted by their case-folded form (upper-cased, as
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> folds) and walked with a stack that holds the chain of earlier
    /// ids each a prefix of the next. In ordinal order every id between a prefix and its extension also starts with
    /// that prefix, so the stack still holds every earlier prefix of the current id. Plain neighbour comparison is not
    /// enough: in <c>x</c>, <c>x-a</c>, <c>x-b</c> the pair <c>x</c> / <c>x-b</c> is not adjacent. Each candidate is
    /// confirmed with <see cref="TenantIdRule.Overlaps"/> itself.
    /// </summary>
    internal static IEnumerable<(StoredTenantId A, StoredTenantId B)> OverlappingPairs(IReadOnlyList<StoredTenantId> stored)
    {
        var keyed = new (string Key, StoredTenantId Id)[stored.Count];
        for (var i = 0; i < stored.Count; i++)
            keyed[i] = (stored[i].TenantId.ToUpperInvariant(), stored[i]);
        Array.Sort(keyed, static (x, y) =>
        {
            var byKey = string.CompareOrdinal(x.Key, y.Key);
            return byKey != 0 ? byKey : string.CompareOrdinal(x.Id.TenantId, y.Id.TenantId);
        });

        var chain = new List<(string Key, StoredTenantId Id)>();
        foreach (var current in keyed)
        {
            while (chain.Count > 0 && !current.Key.StartsWith(chain[^1].Key, StringComparison.Ordinal))
                chain.RemoveAt(chain.Count - 1);
            foreach (var earlier in chain.Where(e => TenantIdRule.Overlaps(e.Id.TenantId, current.Id.TenantId)))
                yield return (earlier.Id, current.Id);
            chain.Add(current);
        }
    }
}
