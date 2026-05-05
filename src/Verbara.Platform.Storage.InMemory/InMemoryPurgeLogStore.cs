using System.Collections.Concurrent;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryPurgeLogStore : IPurgeLogStore
{
    private readonly ConcurrentBag<PurgeEntry> _entries = [];

    public Task SaveAsync(PurgeEntry entry, CancellationToken ct)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? until,
        int page, int pageSize, CancellationToken ct)
    {
        var filtered = _entries.AsEnumerable();

        if (!string.IsNullOrEmpty(tenantId))
            filtered = filtered.Where(e => e.TenantId == tenantId);
        if (from.HasValue)
            filtered = filtered.Where(e => e.PurgedAt >= from.Value);
        if (until.HasValue)
            filtered = filtered.Where(e => e.PurgedAt <= until.Value);

        var ordered = filtered.OrderByDescending(e => e.PurgedAt).ToList();
        var totalCount = ordered.Count;
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<PurgeEntry>(items, totalCount, page, pageSize));
    }
}
