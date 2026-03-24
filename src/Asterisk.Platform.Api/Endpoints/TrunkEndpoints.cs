using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Dialer.Routing;

namespace Asterisk.Platform.Api.Endpoints;

internal static class TrunkEndpoints
{
    public static void MapTrunkEndpoints(this IEndpointRouteBuilder app)
    {
        var trunks = app.MapGroup("/api/admin/trunks").RequireAuthorization();

        trunks.MapGet("/", ListTrunks);
        trunks.MapGet("/{id:long}", GetTrunk);
        trunks.MapPost("/", CreateTrunk);
        trunks.MapPut("/{id:long}", UpdateTrunk);
        trunks.MapDelete("/{id:long}", DeleteTrunk);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTrunks(
        HttpContext context,
        TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await trunkStore.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetTrunk(
        long id,
        HttpContext context,
        TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = await trunkStore.GetAsync(id, tenantId, ct);
        return trunk is null ? Results.NotFound() : Results.Ok(MapToDto(trunk));
    }

    private static async Task<IResult> CreateTrunk(
        HttpContext context,
        CreateTrunkRequest body,
        TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = new Trunk
        {
            Name = body.Name,
            DisplayName = body.DisplayName,
            Type = ParseTrunkType(body.Type),
            IsActive = body.IsActive,
            MaxChannels = body.MaxChannels,
        };
        var id = await trunkStore.CreateAsync(trunk, tenantId, ct);
        trunk.Id = id;
        return Results.Created($"/api/admin/trunks/{id}", MapToDto(trunk));
    }

    private static async Task<IResult> UpdateTrunk(
        long id,
        HttpContext context,
        UpdateTrunkRequest body,
        TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = await trunkStore.GetAsync(id, tenantId, ct);
        if (trunk is null)
            return Results.NotFound();

        if (body.Name is not null) trunk.Name = body.Name;
        if (body.DisplayName is not null) trunk.DisplayName = body.DisplayName;
        if (body.Type is not null) trunk.Type = ParseTrunkType(body.Type);
        if (body.IsActive.HasValue) trunk.IsActive = body.IsActive.Value;
        if (body.MaxChannels.HasValue) trunk.MaxChannels = body.MaxChannels.Value;

        await trunkStore.UpdateAsync(trunk, tenantId, ct);
        return Results.Ok(MapToDto(trunk));
    }

    private static async Task<IResult> DeleteTrunk(
        long id,
        HttpContext context,
        TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await trunkStore.DeleteAsync(id, tenantId, ct);
        return Results.NoContent();
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static TrunkDto MapToDto(Trunk t) =>
        new(t.Id, t.Name, t.DisplayName, t.Type.ToString(), t.IsActive, t.MaxChannels);

    private static TrunkType ParseTrunkType(string type) =>
        type.ToLowerInvariant() switch
        {
            "sip" => TrunkType.Sip,
            "iax2" => TrunkType.Iax2,
            "dahdi" => TrunkType.Dahdi,
            "pjsip" => TrunkType.Pjsip,
            _ => TrunkType.Pjsip,
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ─────────────────────────────────────────────────────

internal sealed record CreateTrunkRequest(
    string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels);

internal sealed record UpdateTrunkRequest(
    string? Name, string? DisplayName, string? Type, bool? IsActive, int? MaxChannels);

internal sealed record TrunkDto(
    long Id, string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels);
