using System.Collections.Concurrent;
using Verbara.Platform.Core.Reports;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryScheduledReportStore : IScheduledReportStore
{
    private readonly ConcurrentDictionary<string, ScheduledReport> _reports = new();
    private readonly ConcurrentDictionary<string, ReportExecution> _executions = new();

    public Task<ScheduledReport?> GetByIdAsync(string reportId, CancellationToken ct)
    {
        _reports.TryGetValue(reportId, out var report);
        return Task.FromResult(report);
    }

    public Task<IReadOnlyList<ScheduledReport>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        IReadOnlyList<ScheduledReport> result = _reports.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ScheduledReport>> GetDueReportsAsync(DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<ScheduledReport> result = _reports.Values
            .Where(r => r.IsActive && r.NextRunAt.HasValue && r.NextRunAt.Value <= now)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(ScheduledReport report, CancellationToken ct)
    {
        _reports[report.ReportId] = report;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reportId, CancellationToken ct)
    {
        _reports.TryRemove(reportId, out _);
        return Task.CompletedTask;
    }

    public Task UpdateLastRunAsync(string reportId, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct)
    {
        if (_reports.TryGetValue(reportId, out var existing))
        {
            // ScheduledReport is immutable — create updated copy via reflection-free pattern
            // We store a new instance that carries updated run times
            _reports[reportId] = new ScheduledReport
            {
                ReportId = existing.ReportId,
                TenantId = existing.TenantId,
                Name = existing.Name,
                ReportType = existing.ReportType,
                Schedule = existing.Schedule,
                Filters = existing.Filters,
                Recipients = existing.Recipients,
                Format = existing.Format,
                IsActive = existing.IsActive,
                CreatedBy = existing.CreatedBy,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastRunAt = lastRunAt,
                NextRunAt = nextRunAt,
            };
        }
        return Task.CompletedTask;
    }

    public Task SaveExecutionAsync(ReportExecution execution, CancellationToken ct)
    {
        _executions[execution.ExecutionId] = execution;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReportExecution>> GetExecutionsAsync(string reportId, int limit, CancellationToken ct)
    {
        IReadOnlyList<ReportExecution> result = _executions.Values
            .Where(e => string.Equals(e.ReportId, reportId, StringComparison.Ordinal))
            .OrderByDescending(e => e.StartedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ReportExecution?> GetExecutionAsync(string executionId, CancellationToken ct)
    {
        _executions.TryGetValue(executionId, out var execution);
        return Task.FromResult(execution);
    }
}
