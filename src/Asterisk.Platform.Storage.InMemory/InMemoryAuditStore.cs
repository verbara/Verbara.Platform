using System.Collections.Concurrent;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryAuditStore : IAuditStore
{
    // Ordered list per tenant — append-only by design.
    private readonly ConcurrentDictionary<TenantId, List<AuditEntry>> _entries = new();

    public Task SaveAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var list = _entries.GetOrAdd(entry.TenantId, _ => []);
        lock (list)
        {
            list.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(TenantId tenantId, string entityType, string entityId, CancellationToken ct)
    {
        IReadOnlyList<AuditEntry> result = GetTenantEntries(tenantId)
            .Where(e => string.Equals(e.TargetType, entityType, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(e.TargetId, entityId, StringComparison.Ordinal))
            .OrderBy(e => e.OccurredAt)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(result);
    }

    public Task<PagedResult<AuditEntry>> SearchAsync(TenantId tenantId, AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filtered = ApplyFilters(GetTenantEntries(tenantId), query)
            .OrderByDescending(e => e.OccurredAt)
            .ToList();

        var totalCount = filtered.Count;
        var offset = (query.Page - 1) * query.PageSize;
        var items = filtered.Skip(offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<AuditEntry>(items, totalCount, query.Page, query.PageSize));
    }

    public async IAsyncEnumerable<AuditEntry> StreamAsync(
        TenantId tenantId,
        AuditQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matches = ApplyFilters(GetTenantEntries(tenantId), query)
            .OrderByDescending(e => e.OccurredAt);

        foreach (var entry in matches)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
            await Task.Yield();
        }
    }

    private static IEnumerable<AuditEntry> ApplyFilters(IEnumerable<AuditEntry> source, AuditQuery query) =>
        source
            .Where(e => query.Action == null || string.Equals(e.Action, query.Action, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.ActionPrefix == null || e.Action.StartsWith(query.ActionPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.EntityType == null || string.Equals(e.TargetType, query.EntityType, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.TargetType == null || string.Equals(e.TargetType, query.TargetType, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.TargetId == null || string.Equals(e.TargetId, query.TargetId, StringComparison.Ordinal))
            .Where(e => query.PerformedBy == null || string.Equals(e.ActorId, query.PerformedBy, StringComparison.Ordinal))
            .Where(e => query.ActorId == null || string.Equals(e.ActorId, query.ActorId, StringComparison.Ordinal))
            .Where(e => query.ActorSearch == null
                || e.ActorId.Contains(query.ActorSearch, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.Category == null || string.Equals(e.Category, query.Category, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.Severity == null || string.Equals(e.Severity, query.Severity, StringComparison.OrdinalIgnoreCase))
            .Where(e => query.CorrelationId == null || e.CorrelationId == query.CorrelationId)
            .Where(e => query.From == null || e.OccurredAt >= query.From)
            .Where(e => query.To == null || e.OccurredAt <= query.To);

    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!_entries.TryGetValue(tenantId, out var list))
            return Task.FromResult(0);

        int deleted;
        lock (list)
        {
            var before = list.Count;
            list.RemoveAll(e => e.OccurredAt < cutoff);
            deleted = before - list.Count;
        }

        return Task.FromResult(deleted);
    }

    private List<AuditEntry> GetTenantEntries(TenantId tenantId)
    {
        if (!_entries.TryGetValue(tenantId, out var list))
            return [];

        lock (list)
        {
            return [.. list];
        }
    }
}
