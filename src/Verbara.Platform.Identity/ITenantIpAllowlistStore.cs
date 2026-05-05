namespace Verbara.Platform.Identity;

/// <summary>
/// Storage contract for per-tenant IP allowlist entries.
/// Implementations: PostgresTenantIpAllowlistStore (production),
/// InMemoryTenantIpAllowlistStore (tests + dev).
/// </summary>
public interface ITenantIpAllowlistStore
{
    Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct);

    Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct);

    Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct);

    Task<int> CountAsync(string tenantId, CancellationToken ct);
}
