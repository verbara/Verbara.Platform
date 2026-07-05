using System.Collections.Concurrent;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

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

    public Task<long> CountByActorAsync(TenantId tenantId, string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var count = GetTenantEntries(tenantId)
            .LongCount(e => string.Equals(e.ActorId, actorId, StringComparison.Ordinal));
        return Task.FromResult(count);
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

        // ADR-0034 Decision 4: honour the per-record retention floor. Delete only entries that are
        // both older than the cutoff AND past their floor — RetainUntil is compared to the CURRENT
        // time (UtcNow), NOT the cutoff (which is in the past), so a record still inside its window
        // survives even when its OccurredAt predates the cutoff.
        var now = DateTimeOffset.UtcNow;
        int deleted;
        lock (list)
        {
            var before = list.Count;
            list.RemoveAll(e => e.OccurredAt < cutoff && (e.RetainUntil is null || e.RetainUntil < now));
            deleted = before - list.Count;
        }

        return Task.FromResult(deleted);
    }

    public Task<int> RedactContactLinkageAsync(
        TenantId tenantId, IReadOnlyCollection<string> conversationIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        if (conversationIds.Count == 0 || !_entries.TryGetValue(tenantId, out var list))
            return Task.FromResult(0);

        var targets = new HashSet<string>(conversationIds, StringComparer.Ordinal);
        var redacted = 0;
        lock (list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (!string.Equals(entry.Action, AutonomousAuditRedaction.AutonomousCommitAction, StringComparison.Ordinal))
                    continue;
                if (entry.TargetId is null || !targets.Contains(entry.TargetId))
                    continue;

                var redactedEntry = AutonomousAuditRedaction.Redact(entry);
                if (!ReferenceEquals(redactedEntry, entry))
                {
                    list[i] = redactedEntry;
                    redacted++;
                }
            }
        }

        return Task.FromResult(redacted);
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
