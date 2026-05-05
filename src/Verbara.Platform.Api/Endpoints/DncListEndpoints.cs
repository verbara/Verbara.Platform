using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class DncListEndpoints
{
    public static void MapDncListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dnc-lists")
            .RequireAuthorization("AdminOnly")
            .RequireLicenseFeature(LicenseFeature.Dialer)
            .RequirePlanFeature(PlanFeature.Dialer);

        // CRUD
        group.MapGet("/", ListDncLists);
        group.MapGet("/{id:long}", GetDncList);
        group.MapPost("/", CreateDncList);
        group.MapPut("/{id:long}", UpdateDncList);
        group.MapDelete("/{id:long}", DeleteDncList);

        // Entries
        group.MapGet("/{id:long}/entries", ListEntries);
        group.MapPost("/{id:long}/entries", AddEntry);
        group.MapDelete("/{id:long}/entries/{phone}", RemoveEntry);

        // Import
        group.MapPost("/{id:long}/import", ImportCsv).DisableAntiforgery();

        // Check
        group.MapGet("/{id:long}/check/{phone}", CheckNumber);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> ListDncLists(
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetDncList(
        long id,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var item = await store.GetAsync(id, tenantId, ct);
        return item is null ? Results.NotFound() : Results.Ok(MapToDto(item));
    }

    private static async Task<IResult> CreateDncList(
        HttpContext context,
        [FromBody] CreateDncListRequest body,
        DncListStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var list = new DncList
        {
            Name = body.Name,
            Scope = body.Scope is not null && Enum.TryParse<DncListScope>(body.Scope, true, out var s)
                ? s : DncListScope.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var id = await store.CreateAsync(list, tenantId, ct);
        list.Id = id;
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "dnc_list.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "dnc_list",
            changes: new AuditChanges(Before: null, After: MapToDto(list)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/dnc-lists/{id}", MapToDto(list));
    }

    private static async Task<IResult> UpdateDncList(
        long id,
        HttpContext context,
        [FromBody] UpdateDncListRequest body,
        DncListStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetAsync(id, tenantId, ct);
        if (existing is null) return Results.NotFound();

        var before = MapToDto(existing);

        if (body.Name is not null) existing.Name = body.Name;
        if (body.Scope is not null && Enum.TryParse<DncListScope>(body.Scope, true, out var s))
            existing.Scope = s;

        await store.UpdateAsync(existing, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "dnc_list.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "dnc_list",
            changes: new AuditChanges(Before: before, After: MapToDto(existing)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(MapToDto(existing));
    }

    private static async Task<IResult> DeleteDncList(
        long id,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await store.GetAsync(id, tenantId, ct);
        await store.DeleteAsync(id, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "dnc_list.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "dnc_list",
            changes: new AuditChanges(Before: before is null ? null : MapToDto(before), After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    // ─── Entry Handlers ───────────────────────────────────────────────────────

    private static async Task<IResult> ListEntries(
        long id,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var entries = await store.ListEntriesAsync(id, tenantId, offset, limit, ct);
        return Results.Ok(entries.Select(MapEntryToDto).ToList());
    }

    private static async Task<IResult> AddEntry(
        long id,
        HttpContext context,
        [FromBody] AddDncEntryRequest body,
        DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var entry = new DncEntry
        {
            DncListId = id,
            PhoneNumber = body.PhoneNumber,
            Reason = body.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = body.ExpiresAt,
        };
        await store.AddEntryAsync(id, tenantId, entry, ct);
        return Results.Created($"/admin/dnc-lists/{id}/entries/{body.PhoneNumber}", MapEntryToDto(entry));
    }

    private static async Task<IResult> RemoveEntry(
        long id,
        string phone,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.RemoveEntryAsync(id, phone, tenantId, ct);
        return Results.NoContent();
    }

    // ─── Import Handler ──────────────────────────────────────────────────────

    private static async Task<IResult> ImportCsv(
        long id,
        HttpContext context,
        DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!context.Request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data");

        var form = await context.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest("No file uploaded");

        var entries = new List<DncEntry>();
        using var reader = new StreamReader(file.OpenReadStream());
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var phone = line.Trim();
            // Support CSV with header or multiple columns — take first column
            if (phone.Length == 0) continue;
            var parts = phone.Split(',');
            phone = parts[0].Trim().Trim('"');
            if (phone.Length == 0 || phone.Equals("phone", StringComparison.OrdinalIgnoreCase)
                || phone.Equals("phone_number", StringComparison.OrdinalIgnoreCase))
                continue;

            entries.Add(new DncEntry
            {
                DncListId = id,
                PhoneNumber = phone,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var imported = await store.BulkImportAsync(id, tenantId, entries, ct);
        return Results.Ok(new DncImportResultDto(imported, entries.Count - imported));
    }

    // ─── Check Handler ───────────────────────────────────────────────────────

    private static async Task<IResult> CheckNumber(
        long id,
        string phone,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var exists = await store.CheckNumberAsync(id, phone, tenantId, ct);
        return Results.Ok(new DncCheckResultDto(exists));
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static DncListDto MapToDto(DncList l) =>
        new(l.Id, l.Name, l.Scope.ToString(), l.EntryCount, l.CreatedAt);

    private static DncEntryDto MapEntryToDto(DncEntry e) =>
        new(e.Id, e.PhoneNumber, e.Reason, e.CreatedAt, e.ExpiresAt);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record CreateDncListRequest(string Name, string? Scope);
internal sealed record UpdateDncListRequest(string? Name, string? Scope);
internal sealed record AddDncEntryRequest(string PhoneNumber, string? Reason, DateTimeOffset? ExpiresAt);
internal sealed record DncListDto(long Id, string Name, string Scope, int EntryCount, DateTimeOffset CreatedAt);
internal sealed record DncEntryDto(long Id, string PhoneNumber, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
internal sealed record DncImportResultDto(int Imported, int Skipped);
internal sealed record DncCheckResultDto(bool Exists);
