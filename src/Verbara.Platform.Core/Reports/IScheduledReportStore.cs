namespace Verbara.Platform.Core.Reports;

public interface IScheduledReportStore
{
    Task<ScheduledReport?> GetByIdAsync(string reportId, CancellationToken ct);
    Task<IReadOnlyList<ScheduledReport>> ListByTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<ScheduledReport>> GetDueReportsAsync(DateTimeOffset now, CancellationToken ct);
    Task SaveAsync(ScheduledReport report, CancellationToken ct);
    Task DeleteAsync(string reportId, CancellationToken ct);
    Task UpdateLastRunAsync(string reportId, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct);
    Task SaveExecutionAsync(ReportExecution execution, CancellationToken ct);
    Task<IReadOnlyList<ReportExecution>> GetExecutionsAsync(string reportId, int limit, CancellationToken ct);
    Task<ReportExecution?> GetExecutionAsync(string executionId, CancellationToken ct);
}
