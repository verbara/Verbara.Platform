using System.Globalization;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Dialer.Scheduling;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class HolidayCalendarEndpoints
{
    public static void MapHolidayCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/holiday-calendars")
            .RequireAuthorization("AdminOnly")
            .RequireLicenseFeature(LicenseFeature.Dialer);

        // CRUD
        group.MapGet("/", ListCalendars);
        group.MapGet("/{id:long}", GetCalendar);
        group.MapPost("/", CreateCalendar);
        group.MapPut("/{id:long}", UpdateCalendar);
        group.MapDelete("/{id:long}", DeleteCalendar);

        // Holidays
        group.MapGet("/{id:long}/holidays", ListHolidays);
        group.MapPost("/{id:long}/holidays", AddHoliday);
        group.MapDelete("/{id:long}/holidays/{holidayId:long}", RemoveHoliday);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> ListCalendars(
        HttpContext context,
        [FromServices] HolidayCalendarStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetCalendar(
        long id,
        HttpContext context,
        [FromServices] HolidayCalendarStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var item = await store.GetAsync(id, tenantId, ct);
        return item is null ? Results.NotFound() : Results.Ok(MapToDto(item));
    }

    private static async Task<IResult> CreateCalendar(
        HttpContext context,
        [FromBody] CreateHolidayCalendarRequest body,
        HolidayCalendarStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var calendar = new HolidayCalendar { Name = body.Name };
        var id = await store.CreateAsync(calendar, tenantId, ct);
        calendar.Id = id;
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "holiday_calendar.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(CultureInfo.InvariantCulture), targetType: "holiday_calendar",
            changes: new AuditChanges(Before: null, After: MapToDto(calendar)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/holiday-calendars/{id}", MapToDto(calendar));
    }

    private static async Task<IResult> UpdateCalendar(
        long id,
        HttpContext context,
        [FromBody] UpdateHolidayCalendarRequest body,
        HolidayCalendarStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetAsync(id, tenantId, ct);
        if (existing is null) return Results.NotFound();

        var before = MapToDto(existing);

        if (body.Name is not null) existing.Name = body.Name;

        await store.UpdateAsync(existing, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "holiday_calendar.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(CultureInfo.InvariantCulture), targetType: "holiday_calendar",
            changes: new AuditChanges(Before: before, After: MapToDto(existing)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(MapToDto(existing));
    }

    private static async Task<IResult> DeleteCalendar(
        long id,
        HttpContext context,
        [FromServices] HolidayCalendarStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await store.GetAsync(id, tenantId, ct);
        await store.DeleteAsync(id, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "holiday_calendar.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(CultureInfo.InvariantCulture), targetType: "holiday_calendar",
            changes: new AuditChanges(Before: before is null ? null : MapToDto(before), After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    // ─── Holiday Handlers ─────────────────────────────────────────────────────

    private static async Task<IResult> ListHolidays(
        long id,
        HttpContext context,
        [FromServices] HolidayCalendarStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var holidays = await store.ListHolidaysAsync(id, tenantId, ct);
        return Results.Ok(holidays.Select(MapHolidayToDto).ToList());
    }

    private static async Task<IResult> AddHoliday(
        long id,
        HttpContext context,
        [FromBody] AddHolidayRequest body,
        HolidayCalendarStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var holiday = new Holiday
        {
            CalendarId = id,
            Date = DateOnly.Parse(body.Date, CultureInfo.InvariantCulture),
            Name = body.Name,
            AllowedStartTime = body.AllowedStart is not null ? TimeOnly.Parse(body.AllowedStart, CultureInfo.InvariantCulture) : null,
            AllowedEndTime = body.AllowedEnd is not null ? TimeOnly.Parse(body.AllowedEnd, CultureInfo.InvariantCulture) : null,
        };
        await store.AddHolidayAsync(id, tenantId, holiday, ct);
        return Results.Created($"/admin/holiday-calendars/{id}/holidays/{holiday.Id}", MapHolidayToDto(holiday));
    }

    private static async Task<IResult> RemoveHoliday(
        long id,
        long holidayId,
        HttpContext context,
        [FromServices] HolidayCalendarStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.RemoveHolidayAsync(id, holidayId, tenantId, ct);
        return Results.NoContent();
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static HolidayCalendarDto MapToDto(HolidayCalendar c) =>
        new(c.Id, c.Name);

    private static HolidayDto MapHolidayToDto(Holiday h) =>
        new(h.Id, h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.Name,
            h.AllowedStartTime?.ToString("HH:mm", CultureInfo.InvariantCulture), h.AllowedEndTime?.ToString("HH:mm", CultureInfo.InvariantCulture));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record CreateHolidayCalendarRequest(string Name);
internal sealed record UpdateHolidayCalendarRequest(string? Name);
internal sealed record AddHolidayRequest(string Date, string Name, string? AllowedStart, string? AllowedEnd);
internal sealed record HolidayCalendarDto(long Id, string Name);
internal sealed record HolidayDto(long Id, string Date, string Name, string? AllowedStart, string? AllowedEnd);
