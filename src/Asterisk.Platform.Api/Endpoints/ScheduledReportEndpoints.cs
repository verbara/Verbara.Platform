using System.Collections.Concurrent;

namespace Asterisk.Platform.Api.Endpoints;

// ─── In-memory singleton store for scheduled reports ─────────────────────────

internal sealed class ScheduledReportStore
{
    private readonly ConcurrentDictionary<string, ScheduledReportDto> _reports = new();

    public IReadOnlyCollection<ScheduledReportDto> List() => _reports.Values.ToArray();

    public ScheduledReportDto? Get(string reportId) =>
        _reports.TryGetValue(reportId, out var report) ? report : null;

    public void Save(ScheduledReportDto report) => _reports[report.ReportId] = report;

    public bool Delete(string reportId) => _reports.TryRemove(reportId, out _);
}

// ─── Endpoint registration ────────────────────────────────────────────────────

internal static class ScheduledReportEndpoints
{
    public static void MapScheduledReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/reports").RequireAuthorization("AdminOnly");

        group.MapGet("/", ListReports);
        group.MapPost("/", CreateReport);
        group.MapGet("/{id}", GetReport);
        group.MapPut("/{id}", UpdateReport);
        group.MapDelete("/{id}", DeleteReport);
    }

    private static IResult ListReports(ScheduledReportStore store)
    {
        return Results.Ok(store.List());
    }

    private static IResult CreateReport(ScheduledReportDto body, ScheduledReportStore store)
    {
        var report = body with { ReportId = Guid.NewGuid().ToString() };
        store.Save(report);
        return Results.Created($"/api/admin/reports/{report.ReportId}", report);
    }

    private static IResult GetReport(string id, ScheduledReportStore store)
    {
        var report = store.Get(id);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }

    private static IResult UpdateReport(string id, ScheduledReportDto body, ScheduledReportStore store)
    {
        if (store.Get(id) is null) return Results.NotFound();

        var updated = body with { ReportId = id };
        store.Save(updated);
        return Results.Ok(updated);
    }

    private static IResult DeleteReport(string id, ScheduledReportStore store)
    {
        return store.Delete(id) ? Results.NoContent() : Results.NotFound();
    }
}

// ─── DTO ──────────────────────────────────────────────────────────────────────

internal sealed record ScheduledReportDto(
    string ReportId,
    string Name,
    string ReportType,
    string Schedule,
    string? Filters,
    string? Recipients,
    string Format,
    bool IsActive);
