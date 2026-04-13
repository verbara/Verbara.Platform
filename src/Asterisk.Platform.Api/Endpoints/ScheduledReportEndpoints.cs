using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Api.Services.Reports;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Reports;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;
using NCrontab;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ScheduledReportEndpoints
{
    public static void MapScheduledReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/reports")
            .RequireAuthorization("AdminOnly")
            .RequireLicenseFeature(LicenseFeature.Analytics)
            .RequirePlanFeature(PlanFeature.ScheduledReports);

        group.MapGet("/", ListReports);
        group.MapPost("/", CreateReport);
        group.MapGet("/{id}", GetReport);
        group.MapPut("/{id}", UpdateReport);
        group.MapDelete("/{id}", DeleteReport);
        group.MapPost("/{id}/run", TriggerManualRun);
        group.MapGet("/{id}/history", GetExecutionHistory);
        group.MapGet("/{id}/history/{executionId}/download", DownloadExecution);
    }

    private static async Task<IResult> ListReports(
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var reports = await store.ListByTenantAsync(tenantId.Value, ct);
        return Results.Ok(reports.Select(ToDto).ToArray());
    }

    private static ScheduledReportDto ToDto(ScheduledReport r) =>
        new(
            Id: r.ReportId,
            Name: r.Name,
            Type: r.ReportType,
            Schedule: r.Schedule,
            Filters: r.Filters,
            Recipients: r.Recipients,
            Format: r.Format,
            IsActive: r.IsActive,
            CreatedBy: r.CreatedBy,
            CreatedAt: r.CreatedAt,
            UpdatedAt: r.UpdatedAt,
            LastRunAt: r.LastRunAt,
            NextRunAt: r.NextRunAt);

    private static async Task<IResult> CreateReport(
        HttpContext context,
        [FromBody] CreateScheduledReportRequest body,
        [FromServices] IScheduledReportStore store,
        [FromServices] ReportDataBuilderRegistry registry,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var type = body.EffectiveType;
        if (string.IsNullOrWhiteSpace(type) || !registry.TryGetBuilder(type, out _))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Unknown report type '{type}'. Valid types: {string.Join(", ", registry.RegisteredTypes)}"));
        }

        var now = clock.UtcNow;

        DateTimeOffset? nextRunAt = null;
        if (!string.IsNullOrWhiteSpace(body.Schedule))
        {
            try
            {
                var schedule = CrontabSchedule.Parse(body.Schedule);
                nextRunAt = new DateTimeOffset(schedule.GetNextOccurrence(now.UtcDateTime), TimeSpan.Zero);
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid cron schedule expression." });
            }
        }

        var report = new ScheduledReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId.Value,
            Name = body.Name,
            ReportType = type,
            Schedule = body.Schedule,
            Filters = body.Filters,
            Recipients = body.Recipients ?? string.Empty,
            Format = body.Format,
            IsActive = body.IsActive,
            CreatedBy = context.User.Identity?.Name ?? "unknown",
            CreatedAt = now,
            UpdatedAt = now,
            NextRunAt = nextRunAt,
        };

        await store.SaveAsync(report, ct);
        return Results.Created($"/admin/reports/{report.ReportId}", ToDto(report));
    }

    private static async Task<IResult> GetReport(
        string id,
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var report = await store.GetByIdAsync(id, ct);
        if (report is null || !string.Equals(report.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();
        return Results.Ok(ToDto(report));
    }

    private static async Task<IResult> UpdateReport(
        string id,
        HttpContext context,
        [FromBody] UpdateScheduledReportRequest body,
        [FromServices] IScheduledReportStore store,
        [FromServices] ReportDataBuilderRegistry registry,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetByIdAsync(id, ct);
        if (existing is null || !string.Equals(existing.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();

        var effectiveType = body.EffectiveType;
        if (effectiveType is not null && !registry.TryGetBuilder(effectiveType, out _))
        {
            return Results.BadRequest(new ErrorResponse(
                $"Unknown report type '{effectiveType}'. Valid types: {string.Join(", ", registry.RegisteredTypes)}"));
        }

        var now = clock.UtcNow;

        DateTimeOffset? nextRunAt = existing.NextRunAt;
        if (!string.IsNullOrWhiteSpace(body.Schedule) &&
            !string.Equals(body.Schedule, existing.Schedule, StringComparison.Ordinal))
        {
            try
            {
                var schedule = CrontabSchedule.Parse(body.Schedule);
                nextRunAt = new DateTimeOffset(schedule.GetNextOccurrence(now.UtcDateTime), TimeSpan.Zero);
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid cron schedule expression." });
            }
        }

        var updated = new ScheduledReport
        {
            ReportId = existing.ReportId,
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            ReportType = effectiveType ?? existing.ReportType,
            Schedule = body.Schedule ?? existing.Schedule,
            Filters = body.Filters ?? existing.Filters,
            Recipients = body.Recipients ?? existing.Recipients,
            Format = body.Format ?? existing.Format,
            IsActive = body.IsActive ?? existing.IsActive,
            CreatedBy = existing.CreatedBy,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
            LastRunAt = existing.LastRunAt,
            NextRunAt = nextRunAt,
        };

        await store.SaveAsync(updated, ct);
        return Results.Ok(ToDto(updated));
    }

    private static async Task<IResult> DeleteReport(
        string id,
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var report = await store.GetByIdAsync(id, ct);
        if (report is null || !string.Equals(report.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();

        await store.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TriggerManualRun(
        string id,
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        [FromServices] ReportSchedulerService scheduler,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var report = await store.GetByIdAsync(id, ct);
        if (report is null || !string.Equals(report.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();

        // Fire and forget — run this specific report in background without blocking the response
        var now = clock.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                await scheduler.TriggerReportAsync(report, CancellationToken.None);
            }
            catch
            {
                // Swallow — errors are logged inside the scheduler
            }
        }, CancellationToken.None);

        return Results.Accepted($"/admin/reports/{id}/history",
            new { message = "Manual execution triggered.", reportId = id, triggeredAt = now });
    }

    private static async Task<IResult> GetExecutionHistory(
        string id,
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var report = await store.GetByIdAsync(id, ct);
        if (report is null || !string.Equals(report.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();

        var pageSize = limit > 0 ? Math.Min(limit, 100) : 25;
        var executions = await store.GetExecutionsAsync(id, pageSize, ct);
        return Results.Ok(executions);
    }

    private static async Task<IResult> DownloadExecution(
        string id,
        string executionId,
        HttpContext context,
        [FromServices] IScheduledReportStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var report = await store.GetByIdAsync(id, ct);
        if (report is null || !string.Equals(report.TenantId, tenantId.Value, StringComparison.Ordinal))
            return Results.NotFound();

        var execution = await store.GetExecutionAsync(executionId, ct);
        if (execution is null || !string.Equals(execution.ReportId, id, StringComparison.Ordinal))
            return Results.NotFound();

        // Placeholder — actual file retrieval (e.g., blob storage) wired by consuming application
        return Results.Ok(execution);
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateScheduledReportRequest(
    string Name,
    string Type,
    string Schedule,
    string Format,
    bool IsActive,
    string? Filters = null,
    string? Recipients = null)
{
    // Backward-compatible alias — some callers send ReportType.
    public string? ReportType { get; init; }
    public string EffectiveType => !string.IsNullOrWhiteSpace(Type) ? Type : ReportType ?? string.Empty;
}

internal sealed record UpdateScheduledReportRequest(
    string? Name = null,
    string? Type = null,
    string? Schedule = null,
    string? Filters = null,
    string? Recipients = null,
    string? Format = null,
    bool? IsActive = null)
{
    public string? ReportType { get; init; }
    public string? EffectiveType => !string.IsNullOrWhiteSpace(Type) ? Type : ReportType;
}

internal sealed record ScheduledReportDto(
    string Id,
    string Name,
    string Type,
    string Schedule,
    string? Filters,
    string? Recipients,
    string Format,
    bool IsActive,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt);
