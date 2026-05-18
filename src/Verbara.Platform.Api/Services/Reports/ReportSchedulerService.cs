using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NCrontab;

namespace Verbara.Platform.Api.Services.Reports;

internal sealed partial class ReportSchedulerService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-tick <see cref="ResiliencePolicy"/> that wraps the
    /// scheduler pass (DB query + fan-out). Circuit-open skips the current tick.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.report-scheduler";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly IScheduledReportStore _store;
    private readonly ReportDataBuilderRegistry _registry;
    private readonly IReportRenderer _pdfRenderer;
    private readonly IReportRenderer _csvRenderer;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly ITenantBrandingStore _brandingStore;
    private readonly IAuditService _auditService;
    private readonly IClock _clock;
    private readonly SmtpOptions _smtpOptions;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<ReportSchedulerService> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter = new(3, 3);

    public ReportSchedulerService(
        IScheduledReportStore store,
        ReportDataBuilderRegistry registry,
        [FromKeyedServices("pdf")] IReportRenderer pdfRenderer,
        [FromKeyedServices("csv")] IReportRenderer csvRenderer,
        IEmailService emailService,
        IEmailTemplateService emailTemplateService,
        ITenantBrandingStore brandingStore,
        IAuditService auditService,
        IClock clock,
        IOptions<SmtpOptions> smtpOptions,
        ILogger<ReportSchedulerService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _store = store;
        _registry = registry;
        _pdfRenderer = pdfRenderer;
        _csvRenderer = csvRenderer;
        _emailService = emailService;
        _emailTemplateService = emailTemplateService;
        _brandingStore = brandingStore;
        _auditService = auditService;
        _clock = clock;
        _smtpOptions = smtpOptions.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await RunSchedulerTickAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    LogCircuitOpen();
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(ReportSchedulerService), fatalEx.Message, fatalEx);
            throw;
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
            // Load branding early — used for both PDF color and email template
            var tenantBranding = await _brandingStore.GetAsync(report.TenantId, ct);
            var brandingContext = new BrandingContext(
                CompanyName: tenantBranding?.DisplayName ?? "Platform",
                LogoUrl: tenantBranding?.LogoUrl,
                PrimaryColor: tenantBranding?.PrimaryColor ?? "#1E40AF",
                SecondaryColor: tenantBranding?.SecondaryColor ?? "#64748B",
                AccentColor: tenantBranding?.AccentColor ?? "#0D9488",
                SupportEmail: tenantBranding?.SupportEmail,
                SupportUrl: tenantBranding?.SupportUrl,
                FromName: tenantBranding?.EmailFromName ?? _smtpOptions.FromName,
                FromAddress: tenantBranding?.EmailFromAddress ?? _smtpOptions.FromAddress);

            // Build report data via registered builder
            var data = await BuildReportDataAsync(report, now, ct);
            data.PrimaryColor = brandingContext.PrimaryColor;

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

                    var variables = new Dictionary<string, string>
                    {
                        ["ReportName"] = report.Name,
                        ["Period"] = $"{data.From:yyyy-MM-dd} — {data.To:yyyy-MM-dd}",
                        ["GeneratedAt"] = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    };
                    var html = _emailTemplateService.Render("scheduled-report", brandingContext, variables);

                    var message = new EmailMessage
                    {
                        Recipients = recipients,
                        Subject = subject,
                        HtmlBody = html,
                        TextBody = $"Please find the {report.ReportType} report attached.",
                        FromName = brandingContext.FromName,
                        FromAddress = brandingContext.FromAddress,
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

    private async Task<ReportData> BuildReportDataAsync(
        ScheduledReport report, DateTimeOffset now, CancellationToken ct)
    {
        if (!_registry.TryGetBuilder(report.ReportType, out var builder))
        {
            throw new PlatformException(
                "UNKNOWN_REPORT_TYPE",
                $"No builder registered for report type '{report.ReportType}'");
        }

        var to = now;
        var from = to.AddDays(-30);
        return await builder.BuildAsync(report.TenantId, from, to, report.Filters, ct);
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

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.report-scheduler — skipping tick")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
