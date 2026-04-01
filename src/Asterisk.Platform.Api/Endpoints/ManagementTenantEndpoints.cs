using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementTenantEndpoints
{
    public static void MapManagementTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/tenants").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", ListTenants);
        group.MapGet("/{id}", GetTenant);
        group.MapPost("/", CreateTenant);
        group.MapPut("/{id}", UpdateTenant);
        group.MapPost("/{id}/suspend", SuspendTenant);
        group.MapPost("/{id}/activate", ActivateTenant);
        group.MapDelete("/{id}", DeleteTenant);
    }

    private static async Task<IResult> ListTenants(
        [FromQuery] string? parentId,
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        IReadOnlyList<Tenant> tenants;

        if (!string.IsNullOrEmpty(parentId))
            tenants = await store.GetChildrenAsync(parentId, ct);
        else
            tenants = await store.GetAllActiveAsync(ct);

        // Apply optional filters
        var result = tenants.AsEnumerable();

        if (Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var statusFilter))
            result = result.Where(t => t.Status == statusFilter);

        if (Enum.TryParse<TenantType>(type, ignoreCase: true, out var typeFilter))
            result = result.Where(t => t.Type == typeFilter);

        return Results.Ok(result.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        return tenant is null ? Results.NotFound() : Results.Ok(MapToDto(tenant));
    }

    private static async Task<IResult> CreateTenant(
        [FromBody] CreateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        // Validate type
        if (body.Type is not (TenantType.Customer or TenantType.Partner))
            return Results.BadRequest(new ErrorResponse("Type must be Customer or Partner."));

        // Resolve parent
        var parentId = body.ParentTenantId;
        if (string.IsNullOrEmpty(parentId))
        {
            // Default parent: the host tenant
            var host = await store.GetHostTenantAsync(ct);
            if (host is null)
                return Results.Problem("Platform not initialized. Run POST /api/setup first.", statusCode: 503);
            parentId = host.TenantId;
        }

        // Validate parent exists
        var parent = await store.GetAsync(parentId, ct);
        if (parent is null)
            return Results.BadRequest(new ErrorResponse($"Parent tenant '{parentId}' not found."));

        // Validate hierarchy: Partner can only be child of Platform, Customer can be child of Platform or Partner
        if (body.Type == TenantType.Partner && parent.Type != TenantType.Platform)
            return Results.BadRequest(new ErrorResponse("Partner tenants must be children of the Platform tenant."));

        if (body.Type == TenantType.Customer && parent.Type is not (TenantType.Platform or TenantType.Partner))
            return Results.BadRequest(new ErrorResponse("Customer tenants must be children of Platform or a Partner."));

        var tenant = new Tenant
        {
            TenantId = body.TenantId,
            Name = body.Name,
            Status = TenantStatus.Active,
            Type = body.Type,
            ParentTenantId = parentId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = body.MaxConcurrentChannels ?? 100,
                MaxActiveCampaigns = body.MaxActiveCampaigns ?? 10,
            },
            Metadata = body.Metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(tenant, ct);
        return Results.Created($"/api/management/tenants/{tenant.TenantId}", MapToDto(tenant));
    }

    private static async Task<IResult> UpdateTenant(
        string id,
        [FromBody] UpdateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        var updated = new Tenant
        {
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            Status = existing.Status,
            Type = existing.Type,
            ParentTenantId = existing.ParentTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = body.MaxConcurrentChannels ?? existing.Options.MaxConcurrentChannels,
                MaxActiveCampaigns = body.MaxActiveCampaigns ?? existing.Options.MaxActiveCampaigns,
                DialplanContextPrefix = existing.Options.DialplanContextPrefix,
                NodeAffinity = existing.Options.NodeAffinity,
                AllowedDialingModes = existing.Options.AllowedDialingModes,
            },
            Metadata = body.Metadata ?? existing.Metadata,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(updated, ct);
        return Results.Ok(MapToDto(updated));
    }

    private static async Task<IResult> SuspendTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new ErrorResponse("Cannot suspend the Platform tenant."));

        await store.UpdateStatusAsync(id, TenantStatus.Suspended, ct);
        return Results.Ok(new StatusUpdateResponse(id, "Suspended"));
    }

    private static async Task<IResult> ActivateTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();

        await store.UpdateStatusAsync(id, TenantStatus.Active, ct);
        return Results.Ok(new StatusUpdateResponse(id, "Active"));
    }

    private static async Task<IResult> DeleteTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new ErrorResponse("Cannot delete the Platform tenant."));

        // UpdateStatusAsync throws if active children exist
        await store.UpdateStatusAsync(id, TenantStatus.Deleted, ct);
        return Results.NoContent();
    }

    private static MgmtTenantDto MapToDto(Tenant t) =>
        new(t.TenantId, t.Name, t.Status.ToString(), t.Type.ToString(),
            t.ParentTenantId, t.Options.MaxConcurrentChannels,
            t.Options.MaxActiveCampaigns, t.Metadata, t.CreatedAt, t.UpdatedAt);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtTenantDto(
    string TenantId,
    string Name,
    string Status,
    string Type,
    string? ParentTenantId,
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    Dictionary<string, string>? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record CreateMgmtTenantRequest(
    string TenantId,
    string Name,
    TenantType Type = TenantType.Customer,
    string? ParentTenantId = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    Dictionary<string, string>? Metadata = null);

internal sealed record UpdateMgmtTenantRequest(
    string? Name = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    Dictionary<string, string>? Metadata = null);
