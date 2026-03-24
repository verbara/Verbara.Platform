using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Dialer.Routing;

namespace Asterisk.Platform.Api.Endpoints;

internal static class CallerIdPoolEndpoints
{
    public static void MapCallerIdPoolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/caller-id-pools").RequireAuthorization();

        // CRUD
        group.MapGet("/", ListPools);
        group.MapGet("/{id:long}", GetPool);
        group.MapPost("/", CreatePool);
        group.MapPut("/{id:long}", UpdatePool);
        group.MapDelete("/{id:long}", DeletePool);

        // Entries
        group.MapGet("/{id:long}/entries", ListEntries);
        group.MapPost("/{id:long}/entries", AddEntry);
        group.MapDelete("/{id:long}/entries/{entryId:long}", RemoveEntry);
    }

    // ─── CRUD Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> ListPools(
        HttpContext context,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetPool(
        long id,
        HttpContext context,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var item = await store.GetAsync(id, tenantId, ct);
        return item is null ? Results.NotFound() : Results.Ok(MapToDto(item));
    }

    private static async Task<IResult> CreatePool(
        HttpContext context,
        CreateCallerIdPoolRequest body,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var pool = new CallerIdPool { Name = body.Name };
        var id = await store.CreateAsync(pool, tenantId, ct);
        pool.Id = id;
        return Results.Created($"/api/admin/caller-id-pools/{id}", MapToDto(pool));
    }

    private static async Task<IResult> UpdatePool(
        long id,
        HttpContext context,
        UpdateCallerIdPoolRequest body,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var existing = await store.GetAsync(id, tenantId, ct);
        if (existing is null) return Results.NotFound();

        if (body.Name is not null) existing.Name = body.Name;

        await store.UpdateAsync(existing, tenantId, ct);
        return Results.Ok(MapToDto(existing));
    }

    private static async Task<IResult> DeletePool(
        long id,
        HttpContext context,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(id, tenantId, ct);
        return Results.NoContent();
    }

    // ─── Entry Handlers ───────────────────────────────────────────────────────

    private static async Task<IResult> ListEntries(
        long id,
        HttpContext context,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var entries = await store.ListEntriesAsync(id, tenantId, ct);
        return Results.Ok(entries.Select(MapEntryToDto).ToList());
    }

    private static async Task<IResult> AddEntry(
        long id,
        HttpContext context,
        AddCallerIdEntryRequest body,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var entry = new CallerIdEntry
        {
            PoolId = id,
            PhoneNumber = body.PhoneNumber,
            AreaCode = body.AreaCode,
            IsActive = body.IsActive ?? true,
        };
        await store.AddEntryAsync(id, tenantId, entry, ct);
        return Results.Created($"/api/admin/caller-id-pools/{id}/entries/{entry.Id}", MapEntryToDto(entry));
    }

    private static async Task<IResult> RemoveEntry(
        long id,
        long entryId,
        HttpContext context,
        CallerIdPoolStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.RemoveEntryAsync(id, entryId, tenantId, ct);
        return Results.NoContent();
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────

    private static CallerIdPoolDto MapToDto(CallerIdPool p) =>
        new(p.Id, p.Name);

    private static CallerIdEntryDto MapEntryToDto(CallerIdEntry e) =>
        new(e.Id, e.PhoneNumber, e.AreaCode, e.IsActive);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ────────────────────────────────────────────────────

internal sealed record CreateCallerIdPoolRequest(string Name);
internal sealed record UpdateCallerIdPoolRequest(string? Name);
internal sealed record AddCallerIdEntryRequest(string PhoneNumber, string? AreaCode, bool? IsActive);
internal sealed record CallerIdPoolDto(long Id, string Name);
internal sealed record CallerIdEntryDto(long Id, string PhoneNumber, string? AreaCode, bool IsActive);
