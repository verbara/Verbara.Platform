using System.Security.Claims;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal sealed record IpAllowlistEntryDto(
    Guid Id,
    string Cidr,
    string? Description,
    DateTimeOffset CreatedAt);

internal sealed record IpAllowlistListResponse(
    bool Enabled,
    IReadOnlyList<IpAllowlistEntryDto> Entries);

internal sealed record AddIpAllowlistEntryRequest(
    string Cidr,
    string? Description);

internal static class ManagementTenantIpAllowlistEndpoints
{
    public static void MapManagementTenantIpAllowlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/tenants/{tenantId}/ip-allowlist")
            .RequireAuthorization("PlatformAdminOnly")
            .RequirePlanFeature(PlanFeature.IpAllowlist);

        group.MapGet("/", List);
        group.MapPost("/", Add);
        group.MapDelete("/{entryId:guid}", Remove);
    }

    internal static async Task<IResult> List(
        string tenantId,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        CancellationToken ct)
    {
        var config = await authConfigStore.GetAsync(tenantId, ct);
        var entries = await store.ListAsync(tenantId, ct);
        var dto = new IpAllowlistListResponse(
            Enabled: config?.IpAllowlistEnabled ?? false,
            Entries: entries.Select(e => new IpAllowlistEntryDto(e.Id, e.Cidr, e.Description, e.CreatedAt)).ToArray());
        return Results.Ok(dto);
    }

    internal static async Task<IResult> Add(
        string tenantId,
        [FromBody] AddIpAllowlistEntryRequest request,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] IAuditService audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        try
        {
            _ = System.Net.IPNetwork.Parse(request.Cidr);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new ErrorResponse("ip_allowlist_invalid_cidr"));
        }

        var actorId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var entry = await store.AddAsync(tenantId, request.Cidr, request.Description, actorId, ct);

        await audit.RecordAsync(
            tenantId: new TenantId(tenantId),
            category: "auth",
            action: "auth.ip_allowlist.entry_added",
            severity: "info",
            actorId: httpContext.User.Identity?.Name ?? "unknown",
            actorType: "user",
            targetId: entry.Id.ToString(),
            targetType: "IpAllowlistEntry",
            metadata: new Dictionary<string, string>
            {
                ["cidr"] = entry.Cidr,
                ["description"] = entry.Description ?? string.Empty,
            },
            ct: ct);

        var dto = new IpAllowlistEntryDto(entry.Id, entry.Cidr, entry.Description, entry.CreatedAt);
        return Results.Created($"/api/v1/management/tenants/{tenantId}/ip-allowlist/{entry.Id}", dto);
    }

    internal static async Task<IResult> Remove(
        string tenantId,
        Guid entryId,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] IAuditService audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var config = await authConfigStore.GetAsync(tenantId, ct);
        if (config is { IpAllowlistEnabled: true })
        {
            var count = await store.CountAsync(tenantId, ct);
            if (count <= 1)
                return Results.BadRequest(new ErrorResponse("ip_allowlist_cannot_empty_while_enabled"));
        }

        var removed = await store.RemoveAsync(tenantId, entryId, ct);
        if (!removed)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId: new TenantId(tenantId),
            category: "auth",
            action: "auth.ip_allowlist.entry_removed",
            severity: "info",
            actorId: httpContext.User.Identity?.Name ?? "unknown",
            actorType: "user",
            targetId: entryId.ToString(),
            targetType: "IpAllowlistEntry",
            ct: ct);

        return Results.NoContent();
    }
}
