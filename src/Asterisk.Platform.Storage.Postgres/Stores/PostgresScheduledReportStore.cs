using Dapper;
using Npgsql;
using Asterisk.Platform.Core.Reports;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresScheduledReportStore : IScheduledReportStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresScheduledReportStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string ReportColumns =
        "report_id, tenant_id, name, report_type, schedule, filters, recipients, format, " +
        "is_active, created_by, created_at, updated_at, last_run_at, next_run_at";

    public async Task<ScheduledReport?> GetByIdAsync(string reportId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ReportRow>(
            $"SELECT {ReportColumns} FROM scheduled_reports WHERE report_id = @Id",
            new { Id = reportId });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<ScheduledReport>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReportRow>(
            $"SELECT {ReportColumns} FROM scheduled_reports WHERE tenant_id = @TenantId ORDER BY created_at DESC",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ScheduledReport>> GetDueReportsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReportRow>(
            $"SELECT {ReportColumns} FROM scheduled_reports " +
            "WHERE is_active = true AND next_run_at IS NOT NULL AND next_run_at <= @Now",
            new { Now = now.UtcDateTime });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(ScheduledReport report, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO scheduled_reports " +
            "(report_id, tenant_id, name, report_type, schedule, filters, recipients, format, " +
            "is_active, created_by, created_at, updated_at, last_run_at, next_run_at) " +
            "VALUES (@ReportId, @TenantId, @Name, @ReportType, @Schedule, @Filters, @Recipients, @Format, " +
            "@IsActive, @CreatedBy, @CreatedAt, @UpdatedAt, @LastRunAt, @NextRunAt) " +
            "ON CONFLICT (report_id) DO UPDATE SET " +
            "name = EXCLUDED.name, report_type = EXCLUDED.report_type, " +
            "schedule = EXCLUDED.schedule, filters = EXCLUDED.filters, " +
            "recipients = EXCLUDED.recipients, format = EXCLUDED.format, " +
            "is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at, " +
            "last_run_at = EXCLUDED.last_run_at, next_run_at = EXCLUDED.next_run_at",
            new
            {
                report.ReportId,
                report.TenantId,
                report.Name,
                report.ReportType,
                report.Schedule,
                report.Filters,
                report.Recipients,
                report.Format,
                report.IsActive,
                report.CreatedBy,
                CreatedAt = report.CreatedAt.UtcDateTime,
                UpdatedAt = report.UpdatedAt.UtcDateTime,
                LastRunAt = report.LastRunAt?.UtcDateTime,
                NextRunAt = report.NextRunAt?.UtcDateTime,
            });
    }

    public async Task DeleteAsync(string reportId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM scheduled_reports WHERE report_id = @Id",
            new { Id = reportId });
    }

    public async Task UpdateLastRunAsync(string reportId, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE scheduled_reports SET last_run_at = @LastRunAt, next_run_at = @NextRunAt, updated_at = @UpdatedAt " +
            "WHERE report_id = @ReportId",
            new
            {
                ReportId = reportId,
                LastRunAt = lastRunAt.UtcDateTime,
                NextRunAt = nextRunAt?.UtcDateTime,
                UpdatedAt = DateTime.UtcNow,
            });
    }

    public async Task SaveExecutionAsync(ReportExecution execution, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO report_executions " +
            "(execution_id, report_id, tenant_id, started_at, completed_at, status, format, file_size_bytes, error_message, recipients_sent) " +
            "VALUES (@ExecutionId, @ReportId, @TenantId, @StartedAt, @CompletedAt, @Status, @Format, @FileSizeBytes, @ErrorMessage, @RecipientsSent) " +
            "ON CONFLICT (execution_id) DO UPDATE SET " +
            "completed_at = EXCLUDED.completed_at, status = EXCLUDED.status, " +
            "file_size_bytes = EXCLUDED.file_size_bytes, error_message = EXCLUDED.error_message, " +
            "recipients_sent = EXCLUDED.recipients_sent",
            new
            {
                execution.ExecutionId,
                execution.ReportId,
                execution.TenantId,
                StartedAt = execution.StartedAt.UtcDateTime,
                CompletedAt = execution.CompletedAt?.UtcDateTime,
                execution.Status,
                execution.Format,
                execution.FileSizeBytes,
                execution.ErrorMessage,
                execution.RecipientsSent,
            });
    }

    public async Task<IReadOnlyList<ReportExecution>> GetExecutionsAsync(string reportId, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ExecutionRow>(
            "SELECT execution_id, report_id, tenant_id, started_at, completed_at, status, format, " +
            "file_size_bytes, error_message, recipients_sent " +
            "FROM report_executions WHERE report_id = @ReportId ORDER BY started_at DESC LIMIT @Limit",
            new { ReportId = reportId, Limit = limit });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<ReportExecution?> GetExecutionAsync(string executionId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExecutionRow>(
            "SELECT execution_id, report_id, tenant_id, started_at, completed_at, status, format, " +
            "file_size_bytes, error_message, recipients_sent " +
            "FROM report_executions WHERE execution_id = @Id",
            new { Id = executionId });
        return row?.ToModel();
    }

    private sealed class ReportRow
    {
        public string report_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string report_type { get; init; } = null!;
        public string schedule { get; init; } = null!;
        public string? filters { get; init; }
        public string recipients { get; init; } = null!;
        public string format { get; init; } = null!;
        public bool is_active { get; init; }
        public string created_by { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }
        public DateTime? last_run_at { get; init; }
        public DateTime? next_run_at { get; init; }

        public ScheduledReport ToModel() => new()
        {
            ReportId = report_id,
            TenantId = tenant_id,
            Name = name,
            ReportType = report_type,
            Schedule = schedule,
            Filters = filters,
            Recipients = recipients,
            Format = format,
            IsActive = is_active,
            CreatedBy = created_by,
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
            LastRunAt = last_run_at.HasValue ? new DateTimeOffset(last_run_at.Value, TimeSpan.Zero) : null,
            NextRunAt = next_run_at.HasValue ? new DateTimeOffset(next_run_at.Value, TimeSpan.Zero) : null,
        };
    }

    private sealed class ExecutionRow
    {
        public string execution_id { get; init; } = null!;
        public string report_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public DateTime started_at { get; init; }
        public DateTime? completed_at { get; init; }
        public string status { get; init; } = null!;
        public string format { get; init; } = null!;
        public long? file_size_bytes { get; init; }
        public string? error_message { get; init; }
        public int? recipients_sent { get; init; }

        public ReportExecution ToModel() => new()
        {
            ExecutionId = execution_id,
            ReportId = report_id,
            TenantId = tenant_id,
            StartedAt = new DateTimeOffset(started_at, TimeSpan.Zero),
            CompletedAt = completed_at.HasValue ? new DateTimeOffset(completed_at.Value, TimeSpan.Zero) : null,
            Status = status,
            Format = format,
            FileSizeBytes = file_size_bytes,
            ErrorMessage = error_message,
            RecipientsSent = recipients_sent,
        };
    }
}
