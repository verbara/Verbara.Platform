using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class DncListEndpoints
{
    public static void MapDncListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dnc-lists")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Dialer)
            .RequirePlanFeature(PlanFeature.Dialer);

        // CRUD
        group.MapGet("/", ListDncLists);
        group.MapGet("/{id:long}", GetDncList);
        // A Global-scope list applies to every tenant's campaigns, so only the host operator makes one; this group
        // admits Customer tenants only (RequireOperationalTenant), so it refuses the Global scope with 403.
        group.MapPost("/", CreateDncList).Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapPut("/{id:long}", UpdateDncList).Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        group.MapDelete("/{id:long}", DeleteDncList);

        // Entries — each answers 404 when the list is not one of the caller's tenant.
        group.MapGet("/{id:long}/entries", ListEntries);
        group.MapPost("/{id:long}/entries", AddEntry);
        group.MapDelete("/{id:long}/entries/{phone}", RemoveEntry);

        // Import
        group.MapPost("/{id:long}/import", ImportCsv).DisableAntiforgery();

        // Check
        group.MapGet("/{id:long}/check/{phone}", CheckNumber);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<Ok<List<DncListDto>>> ListDncLists(
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return TypedResults.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<Results<Ok<DncListDto>, NotFound>> GetDncList(
        long id,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var item = await store.GetAsync(id, tenantId, ct);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(MapToDto(item));
    }

    private static async Task<Results<Created<DncListDto>, JsonHttpResult<ErrorResponse>>> CreateDncList(
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
            Scope = ParseScope(body.Scope) ?? DncListScope.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (list.Scope == DncListScope.Global)
            return GlobalScopeRefused();
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
        return TypedResults.Created($"/admin/dnc-lists/{id}", MapToDto(list));
    }

    private static async Task<Results<Ok<DncListDto>, NotFound, JsonHttpResult<ErrorResponse>>> UpdateDncList(
        long id,
        HttpContext context,
        [FromBody] UpdateDncListRequest body,
        DncListStoreBase store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var scope = ParseScope(body.Scope);
        if (scope == DncListScope.Global)
            return GlobalScopeRefused();

        var tenantId = GetTenantId(context);
        var existing = await store.GetAsync(id, tenantId, ct);
        if (existing is null) return TypedResults.NotFound();

        var before = MapToDto(existing);

        if (body.Name is not null) existing.Name = body.Name;
        if (scope is { } newScope)
            existing.Scope = newScope;

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
        return TypedResults.Ok(MapToDto(existing));
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

    private static async Task<Results<Ok<List<DncEntryDto>>, NotFound>> ListEntries(
        long id,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var entries = await store.ListEntriesAsync(id, tenantId, offset, limit, ct);
        return entries is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(entries.Select(MapEntryToDto).ToList());
    }

    private static async Task<Results<Created<DncEntryDto>, NotFound>> AddEntry(
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
            ExpiresAt = body.ExpiresAt.ToUtcInstant(),
        };
        if (!await store.AddEntryAsync(id, tenantId, entry, ct))
            return TypedResults.NotFound();
        return TypedResults.Created($"/admin/dnc-lists/{id}/entries/{body.PhoneNumber}", MapEntryToDto(entry));
    }

    private static async Task<Results<NoContent, NotFound>> RemoveEntry(
        long id,
        string phone,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        return await store.RemoveEntryAsync(id, phone, tenantId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    // ─── Import Handler ──────────────────────────────────────────────────────

    private static async Task<Results<Ok<DncImportResultDto>, BadRequest<string>, NotFound>> ImportCsv(
        long id,
        HttpContext context,
        DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!context.Request.HasFormContentType)
            return TypedResults.BadRequest("Expected multipart/form-data");

        var form = await context.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return TypedResults.BadRequest("No file uploaded");

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

        if (await store.BulkImportAsync(id, tenantId, entries, ct) is not { } imported)
            return TypedResults.NotFound();
        return TypedResults.Ok(new DncImportResultDto(imported, entries.Count - imported));
    }

    // ─── Check Handler ───────────────────────────────────────────────────────

    private static async Task<Results<Ok<DncCheckResultDto>, NotFound>> CheckNumber(
        long id,
        string phone,
        HttpContext context,
        [FromServices] DncListStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        return await store.CheckNumberAsync(id, phone, tenantId, ct) is { } exists
            ? TypedResults.Ok(new DncCheckResultDto(exists))
            : TypedResults.NotFound();
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static DncListDto MapToDto(DncList l) =>
        new(l.Id, l.Name, l.Scope.ToString(), l.EntryCount, l.CreatedAt);

    private static DncEntryDto MapEntryToDto(DncEntry e) =>
        new(e.Id, e.PhoneNumber, e.Reason, e.CreatedAt, e.ExpiresAt);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The scope a request names, or <see langword="null"/> when it names none or an unknown one.</summary>
    private static DncListScope? ParseScope(string? scope) =>
        scope is not null && Enum.TryParse<DncListScope>(scope, true, out var parsed) ? parsed : null;

    private static JsonHttpResult<ErrorResponse> GlobalScopeRefused() =>
        TypedResults.Json(
            new ErrorResponse("Only the host operator can create or update a Global-scope DNC list."),
            ApiJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status403Forbidden);

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
