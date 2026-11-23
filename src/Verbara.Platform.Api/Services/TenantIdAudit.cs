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
    public bool IsFlagged(string tenantId) => throw new NotImplementedException();

    /// <summary>The reasons of <paramref name="tenantId"/>, comma-separated, or null when it carries none.</summary>
    public string? ReasonOf(string tenantId) => throw new NotImplementedException();

    /// <summary>The stored tenant whose id equals <paramref name="tenantId"/> exactly, or null.</summary>
    public StoredTenantId? Find(string tenantId) => throw new NotImplementedException();
}

/// <summary>
/// Flags every stored tenant id, of any status, that breaks the tenant-id rule (realtime-objects-stay-in-their-tenant,
/// design D2): not a slug, reserved (the host tenant itself excepted), equal to another ignoring case, or forming a
/// hyphen-prefix pair with another. Both members of a pair are flagged. Evaluated on each call, never cached: the
/// flagged set is a function of the stored ids alone.
/// </summary>
internal sealed class TenantIdAudit
{
    /// <summary>The host tenant id, exempt from <see cref="TenantIdFlagReason.Reserved"/>.</summary>
    public const string HostTenantId = "platform";

    private readonly ITenantIdRegistry _registry;

    public TenantIdAudit(ITenantIdRegistry registry) => _registry = registry;

    public ValueTask<TenantIdAuditReport> RunAsync(CancellationToken ct) => throw new NotImplementedException();
}
