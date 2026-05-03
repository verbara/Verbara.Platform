using System.Collections.Concurrent;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

/// <summary>
/// In-memory implementation of <see cref="ITenantIpAllowlistStore"/> for
/// dev / tests / non-Postgres environments. Thread-safe via ConcurrentDictionary.
/// Validates the cidr format eagerly via <see cref="System.Net.IPNetwork.Parse(string)"/>
/// so test code surfaces malformed values up front.
/// </summary>
public sealed class InMemoryTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, IpAllowlistEntry>> _byTenant = new();

    public Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult<IReadOnlyList<IpAllowlistEntry>>(Array.Empty<IpAllowlistEntry>());
        return Task.FromResult<IReadOnlyList<IpAllowlistEntry>>(entries.Values.ToArray());
    }

    public Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(cidr);

        // Validate cidr eagerly so test setup surfaces malformed input.
        var canonical = System.Net.IPNetwork.Parse(cidr).ToString();

        var bucket = _byTenant.GetOrAdd(tenantId, static _ => new ConcurrentDictionary<Guid, IpAllowlistEntry>());

        // Reject duplicates (cidr_per_tenant_unique mirror).
        var existing = bucket.Values.FirstOrDefault(e => e.Cidr == canonical);
        if (existing is not null)
            return Task.FromResult(existing);

        var entry = new IpAllowlistEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Cidr = canonical,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
        };
        bucket[entry.Id] = entry;
        return Task.FromResult(entry);
    }

    public Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult(false);
        return Task.FromResult(entries.TryRemove(entryId, out _));
    }

    public Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult(0);
        return Task.FromResult(entries.Count);
    }
}
