using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Dev/test in-memory implementation of <see cref="ITenantAutonomousDispositionStore"/> (ADR-0034).
/// One record per tenant; revocation soft-deletes by stamping <c>RevokedAt</c>/<c>RevokedByUserId</c>.
/// </summary>
internal sealed class InMemoryTenantAutonomousDispositionStore : ITenantAutonomousDispositionStore
{
    private readonly ConcurrentDictionary<string, TenantAutonomousDisposition> _items = new();

    public Task<TenantAutonomousDisposition?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        _items.TryGetValue(tenantId.Value, out var record);
        return Task.FromResult(record);
    }

    public Task UpsertAsync(TenantAutonomousDisposition record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        _items[record.TenantId.Value] = record;
        return Task.CompletedTask;
    }

    public Task RevokeAsync(TenantId tenantId, EntityId revokedBy, CancellationToken ct)
    {
        // Soft-delete: a no-op if no row exists; preserves the first revocation if already revoked.
        if (_items.TryGetValue(tenantId.Value, out var existing) && existing.RevokedAt is null)
        {
            _items[tenantId.Value] = new TenantAutonomousDisposition
            {
                TenantId = existing.TenantId,
                AttestedByUserId = existing.AttestedByUserId,
                AttestedAt = existing.AttestedAt,
                RevokedAt = DateTimeOffset.UtcNow,
                RevokedByUserId = revokedBy.Value,
            };
        }

        return Task.CompletedTask;
    }
}
