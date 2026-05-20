using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresScheduledReportStore : IScheduledReportStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresScheduledReportStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string ReportColumns =
        "report_id, tenant_id, name, report_type, schedule, filters, recipients, format, " +
        "is_active, created_by, created_at, updated_at, last_run_at, next_run_at";

    public async Task<ScheduledReport?> GetByIdAsync(string reportId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {ReportColumns} FROM scheduled_reports WHERE report_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", reportId)),
            ReportRow.Map, ct);
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<ScheduledReport>> ListByTenantAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {ReportColumns} FROM scheduled_reports WHERE tenant_id = @TenantId ORDER BY created_at DESC",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
            ReportRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ScheduledReport>> GetDueReportsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {ReportColumns} FROM scheduled_reports " +
            "WHERE is_active = true AND next_run_at IS NOT NULL AND next_run_at <= @Now",
            p => p.Add(new NpgsqlParameter("Now", now.UtcDateTime)),
            ReportRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SaveAsync(ScheduledReport report, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
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
            p =>
            {
                p.Add(new NpgsqlParameter("ReportId", report.ReportId));
                p.Add(new NpgsqlParameter("TenantId", report.TenantId));
                p.Add(new NpgsqlParameter("Name", report.Name));
                p.Add(new NpgsqlParameter("ReportType", report.ReportType));
                p.Add(new NpgsqlParameter("Schedule", report.Schedule));
                p.Add(new NpgsqlParameter("Filters", NpgsqlDbType.Text) { Value = (object?)report.Filters ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Recipients", report.Recipients));
                p.Add(new NpgsqlParameter("Format", report.Format));
                p.Add(new NpgsqlParameter("IsActive", report.IsActive));
                p.Add(new NpgsqlParameter("CreatedBy", report.CreatedBy));
                p.Add(new NpgsqlParameter("CreatedAt", report.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", report.UpdatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("LastRunAt", NpgsqlDbType.TimestampTz) { Value = (object?)report.LastRunAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("NextRunAt", NpgsqlDbType.TimestampTz) { Value = (object?)report.NextRunAt?.UtcDateTime ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(string reportId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM scheduled_reports WHERE report_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", reportId)),
            ct);
    }

    public async Task UpdateLastRunAsync(string reportId, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE scheduled_reports SET last_run_at = @LastRunAt, next_run_at = @NextRunAt, updated_at = @UpdatedAt " +
            "WHERE report_id = @ReportId",
            p =>
            {
                p.Add(new NpgsqlParameter("ReportId", reportId));
                p.Add(new NpgsqlParameter("LastRunAt", lastRunAt.UtcDateTime));
                p.Add(new NpgsqlParameter("NextRunAt", NpgsqlDbType.TimestampTz) { Value = (object?)nextRunAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedAt", DateTime.UtcNow));
            },
            ct);
    }

    public async Task SaveExecutionAsync(ReportExecution execution, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO report_executions " +
            "(execution_id, report_id, tenant_id, started_at, completed_at, status, format, file_size_bytes, error_message, recipients_sent) " +
            "VALUES (@ExecutionId, @ReportId, @TenantId, @StartedAt, @CompletedAt, @Status, @Format, @FileSizeBytes, @ErrorMessage, @RecipientsSent) " +
            "ON CONFLICT (execution_id) DO UPDATE SET " +
            "completed_at = EXCLUDED.completed_at, status = EXCLUDED.status, " +
            "file_size_bytes = EXCLUDED.file_size_bytes, error_message = EXCLUDED.error_message, " +
            "recipients_sent = EXCLUDED.recipients_sent",
            p =>
            {
                p.Add(new NpgsqlParameter("ExecutionId", execution.ExecutionId));
                p.Add(new NpgsqlParameter("ReportId", execution.ReportId));
                p.Add(new NpgsqlParameter("TenantId", execution.TenantId));
                p.Add(new NpgsqlParameter("StartedAt", execution.StartedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("CompletedAt", NpgsqlDbType.TimestampTz) { Value = (object?)execution.CompletedAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Status", execution.Status));
                p.Add(new NpgsqlParameter("Format", execution.Format));
                p.Add(new NpgsqlParameter("FileSizeBytes", NpgsqlDbType.Bigint) { Value = (object?)execution.FileSizeBytes ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ErrorMessage", NpgsqlDbType.Text) { Value = (object?)execution.ErrorMessage ?? DBNull.Value });
                p.Add(new NpgsqlParameter("RecipientsSent", NpgsqlDbType.Integer) { Value = (object?)execution.RecipientsSent ?? DBNull.Value });
            },
            ct);
    }

    public async Task<IReadOnlyList<ReportExecution>> GetExecutionsAsync(string reportId, int limit, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT execution_id, report_id, tenant_id, started_at, completed_at, status, format, " +
            "file_size_bytes, error_message, recipients_sent " +
            "FROM report_executions WHERE report_id = @ReportId ORDER BY started_at DESC LIMIT @Limit",
            p => { p.Add(new NpgsqlParameter("ReportId", reportId)); p.Add(new NpgsqlParameter("Limit", limit)); },
            ExecutionRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<ReportExecution?> GetExecutionAsync(string executionId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT execution_id, report_id, tenant_id, started_at, completed_at, status, format, " +
            "file_size_bytes, error_message, recipients_sent " +
            "FROM report_executions WHERE execution_id = @Id",
            p => p.Add(new NpgsqlParameter("Id", executionId)),
            ExecutionRow.Map, ct);
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

        public static ReportRow Map(NpgsqlDataReader r) => new()
        {
            report_id = r.GetString("report_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            report_type = r.GetString("report_type"),
            schedule = r.GetString("schedule"),
            filters = r.GetStringOrNull("filters"),
            recipients = r.GetString("recipients"),
            format = r.GetString("format"),
            is_active = r.GetBoolean("is_active"),
            created_by = r.GetString("created_by"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
            last_run_at = r.GetDateTimeOrNull("last_run_at"),
            next_run_at = r.GetDateTimeOrNull("next_run_at"),
        };

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

        public static ExecutionRow Map(NpgsqlDataReader r) => new()
        {
            execution_id = r.GetString("execution_id"),
            report_id = r.GetString("report_id"),
            tenant_id = r.GetString("tenant_id"),
            started_at = r.GetDateTime("started_at"),
            completed_at = r.GetDateTimeOrNull("completed_at"),
            status = r.GetString("status"),
            format = r.GetString("format"),
            file_size_bytes = r.GetInt64OrNull("file_size_bytes"),
            error_message = r.GetStringOrNull("error_message"),
            recipients_sent = r.GetInt32OrNull("recipients_sent"),
        };

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
