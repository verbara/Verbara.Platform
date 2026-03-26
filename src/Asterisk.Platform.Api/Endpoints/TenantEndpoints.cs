using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Endpoints;

internal static class TenantEndpoints
{
    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/tenants").RequireAuthorization("AdminOnly");

        group.MapGet("/", ListTenants);
        group.MapGet("/{id}", GetTenant);
        group.MapPost("/", CreateTenant);
        group.MapPut("/{id}", UpdateTenant);
        group.MapDelete("/{id}", DeleteTenant);
        group.MapGet("/{id}/stats", GetTenantStats);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTenants(IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.Ok(Array.Empty<TenantDto>());

        var tenants = await store.GetAllActiveAsync(ct);
        return Results.Ok(tenants.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetTenant(string id, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.NotFound();

        var tenant = await store.GetAsync(id, ct);
        return tenant is null ? Results.NotFound() : Results.Ok(MapToDto(tenant));
    }

    private static async Task<IResult> CreateTenant(
        CreateTenantRequest body,
        IServiceProvider services,
        CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.Problem("MultiTenant not registered", statusCode: 503);

        var tenant = new Tenant
        {
            TenantId = body.TenantId,
            Name = body.Name,
            Status = TenantStatus.Active,
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
        return Results.Created($"/api/admin/tenants/{tenant.TenantId}", MapToDto(tenant));
    }

    private static async Task<IResult> UpdateTenant(
        string id,
        UpdateTenantRequest body,
        IServiceProvider services,
        CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.Problem("MultiTenant not registered", statusCode: 503);

        var existing = await store.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        if (body.MaxConcurrentChannels.HasValue)
            existing.Options.MaxConcurrentChannels = body.MaxConcurrentChannels.Value;

        if (body.MaxActiveCampaigns.HasValue)
            existing.Options.MaxActiveCampaigns = body.MaxActiveCampaigns.Value;

        // For status-only updates use the dedicated method
        if (body.Status.HasValue)
            await store.UpdateStatusAsync(id, body.Status.Value, ct);

        var updated = new Tenant
        {
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            Status = body.Status ?? existing.Status,
            Options = existing.Options,
            Metadata = existing.Metadata,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(updated, ct);
        return Results.Ok(MapToDto(updated));
    }

    private static async Task<IResult> DeleteTenant(string id, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.Problem("MultiTenant not registered", statusCode: 503);

        var tenant = await store.GetAsync(id, ct);
        if (tenant is null)
            return Results.NotFound();

        await store.UpdateStatusAsync(id, TenantStatus.Deleted, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetTenantStats(string id, IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetService<ITenantStore>();
        if (store is null)
            return Results.NotFound();

        var tenant = await store.GetAsync(id, ct);
        if (tenant is null)
            return Results.NotFound();

        // Return basic stats from the tenant record — can be enriched later with live metrics
        return Results.Ok(new TenantStatsDto(
            id,
            tenant.Status.ToString(),
            tenant.Options.MaxConcurrentChannels,
            tenant.Options.MaxActiveCampaigns,
            tenant.CreatedAt,
            tenant.UpdatedAt));
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    private static TenantDto MapToDto(Tenant t) =>
        new(t.TenantId, t.Name, t.Status.ToString(),
            t.Options.MaxConcurrentChannels, t.Options.MaxActiveCampaigns,
            t.Metadata, t.CreatedAt, t.UpdatedAt);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record TenantDto(
    string TenantId,
    string Name,
    string Status,
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    Dictionary<string, string>? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record CreateTenantRequest(
    string TenantId,
    string Name,
    int? MaxConcurrentChannels,
    int? MaxActiveCampaigns,
    Dictionary<string, string>? Metadata);

internal sealed record UpdateTenantRequest(
    string? Name,
    TenantStatus? Status,
    int? MaxConcurrentChannels,
    int? MaxActiveCampaigns);

internal sealed record TenantStatsDto(
    string TenantId,
    string Status,
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
