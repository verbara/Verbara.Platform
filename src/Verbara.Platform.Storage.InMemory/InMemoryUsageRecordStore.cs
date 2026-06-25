using System.Collections.Concurrent;
using System.Globalization;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryUsageRecordStore : IUsageRecordStore
{
    private readonly ConcurrentDictionary<TenantId, List<UsageRecord>> _records = new();

    public Task SaveAsync(UsageRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var list = _records.GetOrAdd(record.TenantId, _ => []);
        lock (list)
        {
            list.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            var list = _records.GetOrAdd(record.TenantId, _ => []);
            lock (list)
            {
                list.Add(record);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.RecordedAt >= from && r.RecordedAt < until);

        IReadOnlyList<UsageSummary> summaries = filtered
            .GroupBy(r => r.UsageType)
            .Select(g => new UsageSummary
            {
                TenantId = tenantId,
                PeriodStart = from,
                PeriodEnd = until,
                UsageType = g.Key,
                TotalQuantity = g.Sum(r => r.Quantity),
                RecordCount = g.Count(),
                LastUpdatedAt = g.Max(r => r.RecordedAt),
            })
            .ToList();

        return Task.FromResult(summaries);
    }

    public Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.UsageType == type && r.RecordedAt >= from && r.RecordedAt < until)
            .ToList();

        if (filtered.Count == 0)
            return Task.FromResult<UsageSummary?>(null);

        var summary = new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = until,
            UsageType = type,
            TotalQuantity = filtered.Sum(r => r.Quantity),
            RecordCount = filtered.Count,
            LastUpdatedAt = filtered.Max(r => r.RecordedAt),
        };

        return Task.FromResult<UsageSummary?>(summary);
    }

    public Task<AiTokenBreakdown> GetAiTokenBreakdownAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.UsageType == type && r.RecordedAt >= from && r.RecordedAt < until);

        decimal inSum = 0m, outSum = 0m, unsplit = 0m;
        foreach (var r in filtered)
        {
            if (r.Metadata is { } m
                && m.TryGetValue("inputTokens", out var inStr)
                && m.TryGetValue("outputTokens", out var outStr))
            {
                inSum += decimal.Parse(inStr, CultureInfo.InvariantCulture);
                outSum += decimal.Parse(outStr, CultureInfo.InvariantCulture);
            }
            else
            {
                unsplit += r.Quantity;
            }
        }

        return Task.FromResult(new AiTokenBreakdown(inSum, outSum, unsplit));
    }

    public Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.RecordedAt >= from && r.RecordedAt < until);

        if (type is not null)
            filtered = filtered.Where(r => r.UsageType == type.Value);

        IReadOnlyList<UsageRecord> result = filtered
            .OrderByDescending(r => r.RecordedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!_records.TryGetValue(tenantId, out var list))
            return Task.FromResult(0);

        int deleted;
        lock (list)
        {
            var before = list.Count;
            list.RemoveAll(r => r.RecordedAt < cutoff);
            deleted = before - list.Count;
        }

        return Task.FromResult(deleted);
    }

    private List<UsageRecord> GetTenantRecords(TenantId tenantId)
    {
        if (!_records.TryGetValue(tenantId, out var list))
            return [];

        lock (list)
        {
            return [.. list];
        }
    }
}
