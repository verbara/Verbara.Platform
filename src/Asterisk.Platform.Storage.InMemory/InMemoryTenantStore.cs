using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        _tenants.TryGetValue(tenantId, out var tenant);
        return new(tenant);
    }

    public ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var result = _tenants.Values
            .Where(t => t.Status == TenantStatus.Active)
            .ToList();
        return new(result);
    }

    public ValueTask UpsertAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        // Enforce: only one Platform tenant allowed
        if (tenant.Type == TenantType.Platform)
        {
            var existing = _tenants.Values.FirstOrDefault(t => t.Type == TenantType.Platform);
            if (existing is not null && existing.TenantId != tenant.TenantId)
                throw new InvalidOperationException("Only one Platform tenant is allowed.");
        }

        // Enforce: max depth 3 (Platform -> Partner -> Customer)
        if (tenant.ParentTenantId is not null)
        {
            if (_tenants.TryGetValue(tenant.ParentTenantId, out var parent))
            {
                if (parent.ParentTenantId is not null &&
                    _tenants.TryGetValue(parent.ParentTenantId, out var grandparent) &&
                    grandparent.ParentTenantId is not null)
                {
                    throw new InvalidOperationException("Maximum tenant hierarchy depth (3 levels) exceeded.");
                }
            }
        }

        _tenants[tenant.TenantId] = tenant;
        return default;
    }

    public ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken cancellationToken = default)
    {
        if (_tenants.TryGetValue(tenantId, out var existing))
        {
            // Block deletion if active children exist
            if (status == TenantStatus.Deleted)
            {
                var activeChildren = _tenants.Values
                    .Any(t => t.ParentTenantId == tenantId && t.Status == TenantStatus.Active);
                if (activeChildren)
                    throw new InvalidOperationException("Cannot delete tenant with active children.");
            }

            existing.Status = status;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return default;
    }

    public ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default)
    {
        var host = _tenants.Values.FirstOrDefault(t => t.Type == TenantType.Platform);
        return new(host);
    }

    public ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default)
    {
        var children = _tenants.Values
            .Where(t => t.ParentTenantId == parentTenantId)
            .ToList();
        return new(children);
    }
}
