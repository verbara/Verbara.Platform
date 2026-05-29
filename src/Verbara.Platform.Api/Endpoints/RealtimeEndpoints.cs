using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        var profiles = app.MapGroup("/admin/realtime/profiles")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.Realtime);

        // Static routes BEFORE parameterised routes to avoid conflicts
        profiles.MapGet("/default/{type}", GetDefaultProfile);
        profiles.MapPost("/seed-defaults", SeedDefaults);

        profiles.MapGet("/", ListProfiles);
        profiles.MapGet("/{id:long}", GetProfile);
        profiles.MapPost("/", CreateProfile);
        profiles.MapPut("/{id:long}", UpdateProfile);
        profiles.MapDelete("/{id:long}", DeleteProfile);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListProfiles(
        HttpContext context,
        [FromServices] EndpointProfileStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await store.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetProfile(
        long id,
        HttpContext context,
        [FromServices] EndpointProfileStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var profile = await store.GetAsync(id, tenantId, ct);
        return profile is null ? Results.NotFound() : Results.Ok(MapToDto(profile));
    }

    private static async Task<IResult> GetDefaultProfile(
        string type,
        HttpContext context,
        [FromServices] EndpointProfileStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var profileType = ParseProfileType(type);
        var profile = await store.GetDefaultAsync(tenantId, profileType, ct);
        return profile is null ? Results.NotFound() : Results.Ok(MapToDto(profile));
    }

    private static async Task<IResult> CreateProfile(
        HttpContext context,
        [FromBody] CreateEndpointProfileRequest body,
        [FromServices] EndpointProfileStoreBase store,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var tenant = await tenantStore.GetAsync(tenantId, ct);
        var (valid, resolvedContext, error) = ValidateContext(body.Context, tenant?.Options.DialplanContextPrefix);
        if (!valid)
            return Results.BadRequest(new { error });

        var profile = new EndpointProfile
        {
            Name = body.Name,
            Type = ParseProfileType(body.Type),
            Transport = body.Transport ?? "transport-udp",
            Codecs = body.Codecs ?? "ulaw,alaw,g722",
            Webrtc = body.Webrtc ?? false,
            MaxContacts = body.MaxContacts ?? 1,
            DirectMedia = body.DirectMedia ?? false,
            Context = resolvedContext,
            QualifyFrequency = body.QualifyFrequency ?? 30,
        };
        var id = await store.CreateAsync(profile, tenantId, ct);
        profile.Id = id;
        return Results.Created($"/admin/realtime/profiles/{id}", MapToDto(profile));
    }

    private static async Task<IResult> UpdateProfile(
        long id,
        HttpContext context,
        [FromBody] UpdateEndpointProfileRequest body,
        [FromServices] EndpointProfileStoreBase store,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var profile = await store.GetAsync(id, tenantId, ct);
        if (profile is null)
            return Results.NotFound();

        if (body.Name is not null) profile.Name = body.Name;
        if (body.Transport is not null) profile.Transport = body.Transport;
        if (body.Codecs is not null) profile.Codecs = body.Codecs;
        if (body.Webrtc.HasValue) profile.Webrtc = body.Webrtc.Value;
        if (body.MaxContacts.HasValue) profile.MaxContacts = body.MaxContacts.Value;
        if (body.IsDefault.HasValue) profile.IsDefault = body.IsDefault.Value;
        if (body.DirectMedia.HasValue) profile.DirectMedia = body.DirectMedia.Value;
        if (body.Context is not null)
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            var (valid, resolvedContext, error) = ValidateContext(body.Context, tenant?.Options.DialplanContextPrefix);
            if (!valid)
                return Results.BadRequest(new { error });
            profile.Context = resolvedContext;
        }
        if (body.QualifyFrequency.HasValue) profile.QualifyFrequency = body.QualifyFrequency.Value;

        await store.UpdateAsync(profile, tenantId, ct);
        return Results.Ok(MapToDto(profile));
    }

    private static async Task<IResult> DeleteProfile(
        long id,
        HttpContext context,
        [FromServices] EndpointProfileStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(id, tenantId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SeedDefaults(
        HttpContext context,
        EndpointProfileStoreBase store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.SeedDefaultsAsync(tenantId, ct);
        return Results.NoContent();
    }

    // ─── Validation Helpers ───────────────────────────────────────────────────

    private static readonly string[] AllowedContexts = ["from-internal", "from-external", "default"];

    private static (bool Valid, string ResolvedContext, string? Error) ValidateContext(
        string? requestedContext, string? dialplanContextPrefix)
    {
        var context = requestedContext ?? "from-internal";

        if (dialplanContextPrefix is not null)
            return (true, $"{dialplanContextPrefix}-{context}", null);

        if (AllowedContexts.Contains(context, StringComparer.OrdinalIgnoreCase))
            return (true, context, null);

        return (false, context, $"Context must be one of: {string.Join(", ", AllowedContexts)}. " +
            "Configure DialplanContextPrefix on the tenant for custom contexts.");
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static EndpointProfileDto MapToDto(EndpointProfile p) =>
        new(p.Id, p.Name, p.Type.ToString().ToLowerInvariant(), p.IsDefault,
            p.Transport, p.Codecs, p.Webrtc, p.MaxContacts, p.DirectMedia,
            p.Context, p.QualifyFrequency);

    private static EndpointProfileType ParseProfileType(string type) =>
        type.ToLowerInvariant() switch
        {
            "agent" => EndpointProfileType.Agent,
            "trunk" => EndpointProfileType.Trunk,
            _ => EndpointProfileType.Agent,
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

internal sealed record EndpointProfileDto(
    long Id, string Name, string Type, bool IsDefault,
    string Transport, string Codecs, bool Webrtc,
    int MaxContacts, bool DirectMedia, string Context, int QualifyFrequency);

internal sealed record CreateEndpointProfileRequest(
    string Name, string Type, string? Transport, string? Codecs,
    bool? Webrtc, int? MaxContacts, bool? DirectMedia, string? Context, int? QualifyFrequency);

internal sealed record UpdateEndpointProfileRequest(
    string? Name, string? Transport, string? Codecs,
    bool? Webrtc, int? MaxContacts, bool? IsDefault, bool? DirectMedia, string? Context, int? QualifyFrequency);
