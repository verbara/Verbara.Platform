using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using NCrontab;

namespace Asterisk.Platform.Api.Services.Reports;

internal sealed partial class ReportSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IScheduledReportStore _store;
    private readonly IReportRenderer _pdfRenderer;
    private readonly IReportRenderer _csvRenderer;
    private readonly IEmailService _emailService;
    private readonly IAuditService _auditService;
    private readonly IClock _clock;
    private readonly ILogger<ReportSchedulerService> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter = new(3, 3);

    public ReportSchedulerService(
        IScheduledReportStore store,
        [FromKeyedServices("pdf")] IReportRenderer pdfRenderer,
        [FromKeyedServices("csv")] IReportRenderer csvRenderer,
        IEmailService emailService,
        IAuditService auditService,
        IClock clock,
        ILogger<ReportSchedulerService> logger)
    {
        _store = store;
        _pdfRenderer = pdfRenderer;
        _csvRenderer = csvRenderer;
        _emailService = emailService;
        _auditService = auditService;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSchedulerTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogTickError(ex);
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    internal async Task RunSchedulerTickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var dueReports = await _store.GetDueReportsAsync(now, ct);

        if (dueReports.Count == 0)
            return;

        LogDueReports(dueReports.Count);

        var tasks = dueReports.Select(report => ExecuteReportAsync(report, now, ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    internal Task TriggerReportAsync(ScheduledReport report, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        return ExecuteReportAsync(report, now, ct);
    }

    private async Task ExecuteReportAsync(ScheduledReport report, DateTimeOffset now, CancellationToken ct)
    {
        await _concurrencyLimiter.WaitAsync(ct);
        try
        {
            await RunSingleReportAsync(report, now, ct);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task RunSingleReportAsync(ScheduledReport report, DateTimeOffset now, CancellationToken ct)
    {
        var executionId = Guid.NewGuid().ToString("N");
        var execution = new ReportExecution
        {
            ExecutionId = executionId,
            ReportId = report.ReportId,
            TenantId = report.TenantId,
            StartedAt = now,
            Status = "running",
            Format = report.Format,
        };

        await _store.SaveExecutionAsync(execution, ct);
        LogReportStarted(report.ReportId, report.Name);

        DateTimeOffset? nextRun = null;
        try
        {
            // Build report data (placeholder — actual queries wired by consuming app)
            var data = await BuildReportDataAsync(report, now, ct);

            // Select renderer based on format
            var renderer = string.Equals(report.Format, "csv", StringComparison.OrdinalIgnoreCase)
                ? _csvRenderer
                : _pdfRenderer;

            var fileBytes = await renderer.RenderAsync(data, ct);

            // Send to recipients
            var recipientCount = 0;
            if (!string.IsNullOrWhiteSpace(report.Recipients))
            {
                var emails = report.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var recipients = emails.Select(e => new EmailRecipient(e)).ToList();

                if (recipients.Count > 0)
                {
                    var subject = $"[Report] {report.Name} — {now:yyyy-MM-dd}";
                    var filename = $"{report.Name}_{now:yyyyMMdd}.{renderer.FileExtension}";

                    var message = new EmailMessage
                    {
                        Recipients = recipients,
                        Subject = subject,
                        TextBody = $"Please find the {report.ReportType} report attached.",
                        Attachments =
                        [
                            new EmailAttachment(filename, renderer.ContentType, fileBytes),
                        ],
                    };

                    await _emailService.SendAsync(message, ct);
                    recipientCount = recipients.Count;
                }
            }

            // Calculate next run
            try
            {
                var schedule = CrontabSchedule.Parse(report.Schedule);
                nextRun = new DateTimeOffset(schedule.GetNextOccurrence(now.UtcDateTime), TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                LogInvalidSchedule(report.ReportId, report.Schedule, ex.Message);
            }

            await _store.UpdateLastRunAsync(report.ReportId, now, nextRun, ct);

            var completed = new ReportExecution
            {
                ExecutionId = executionId,
                ReportId = report.ReportId,
                TenantId = report.TenantId,
                StartedAt = now,
                CompletedAt = _clock.UtcNow,
                Status = "completed",
                Format = report.Format,
                FileSizeBytes = fileBytes.LongLength,
                RecipientsSent = recipientCount,
            };
            await _store.SaveExecutionAsync(completed, ct);

            await _auditService.RecordAsync(
                tenantId: new TenantId(report.TenantId),
                category: "reports",
                action: "report.executed",
                severity: "info",
                actorId: "system",
                actorType: "system",
                targetId: report.ReportId,
                targetType: "scheduled_report",
                ct: ct);

            LogReportCompleted(report.ReportId, report.Name, fileBytes.Length, recipientCount);
        }
        catch (Exception ex)
        {
            LogReportFailed(report.ReportId, report.Name, ex);

            var failed = new ReportExecution
            {
                ExecutionId = executionId,
                ReportId = report.ReportId,
                TenantId = report.TenantId,
                StartedAt = now,
                CompletedAt = _clock.UtcNow,
                Status = "failed",
                Format = report.Format,
                ErrorMessage = ex.Message,
            };
            await _store.SaveExecutionAsync(failed, ct);

            // Still advance next run so we don't get stuck in a retry loop
            if (nextRun is null)
            {
                try
                {
                    var schedule = CrontabSchedule.Parse(report.Schedule);
                    nextRun = new DateTimeOffset(schedule.GetNextOccurrence(now.UtcDateTime), TimeSpan.Zero);
                }
                catch
                {
                    // Schedule is invalid — leave next_run_at as-is
                }
            }

            if (nextRun.HasValue)
                await _store.UpdateLastRunAsync(report.ReportId, now, nextRun, ct);
        }
    }

    private static ValueTask<ReportData> BuildReportDataAsync(
        ScheduledReport report, DateTimeOffset now, CancellationToken ct)
    {
        // Placeholder — actual data source queries are wired by consuming application
        var data = new ReportData
        {
            ReportName = report.Name,
            TenantName = report.TenantId,
            ReportType = report.ReportType,
            From = now.AddDays(-30),
            To = now,
            GeneratedAt = now,
        };
        return ValueTask.FromResult(data);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Report scheduler tick: {Count} due reports found")]
    private partial void LogDueReports(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting report execution: reportId={ReportId} name={Name}")]
    private partial void LogReportStarted(string reportId, string name);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Report completed: reportId={ReportId} name={Name} fileBytes={FileBytes} recipients={Recipients}")]
    private partial void LogReportCompleted(string reportId, string name, long fileBytes, int recipients);

    [LoggerMessage(Level = LogLevel.Error, Message = "Report execution failed: reportId={ReportId} name={Name}")]
    private partial void LogReportFailed(string reportId, string name, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Invalid cron schedule for report {ReportId}: '{Schedule}' — {Error}")]
    private partial void LogInvalidSchedule(string reportId, string schedule, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Report scheduler tick failed")]
    private partial void LogTickError(Exception ex);
}
