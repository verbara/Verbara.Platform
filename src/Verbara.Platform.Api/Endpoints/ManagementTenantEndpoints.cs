using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Endpoints;

internal static class ManagementTenantEndpoints
{
    public static void MapManagementTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/tenants").RequireAuthorization("PlatformAdminOnly");

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
        {
            tenants = await store.GetChildrenAsync(parentId, ct);
        }
        else
        {
            // Management list: return ALL tenants regardless of status (admins need to see
            // Suspended / Disabled to reactivate them). GetAllActiveAsync only returns Active
            // so we compose children-of-platform + platform itself for a full view.
            var host = await store.GetHostTenantAsync(ct);
            var children = host is null
                ? []
                : await store.GetChildrenAsync(host.TenantId, ct);
            // Exclude soft-deleted tenants from the management list (but KEEP Suspended /
            // Disabled / Warning / Degraded / PendingDeletion so admins can reactivate them).
            var visibleChildren = children.Where(t => t.Status != TenantStatus.Deleted).ToList();
            tenants = host is null ? visibleChildren : [host, .. visibleChildren];
        }

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
        HttpContext context,
        [FromBody] CreateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        [FromServices] IAuditService audit,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
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

        // Validate ownership: non-platform callers can only create under their own tenant
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is not null)
        {
            var hostTenantId = (await store.GetHostTenantAsync(ct))?.TenantId;

            if (!string.Equals(callerTenantId, hostTenantId, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(parentId, callerTenantId, StringComparison.OrdinalIgnoreCase))
                    return Results.Problem("Non-platform tenants can only create children under their own tenant.", statusCode: 403);
            }
        }

        // Parent must be active
        if (parent.Status != TenantStatus.Active)
            return Results.BadRequest(new ErrorResponse("Cannot create children under an inactive tenant."));

        // Validate template if provided
        if (body.Template is not null && !TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
            return Results.BadRequest(new ErrorResponse($"Invalid template '{body.Template}'. Valid: support, sales, blended."));

        var metadata = body.Metadata ?? new Dictionary<string, string>();
        if (body.Template is not null)
            metadata["OnboardingTemplate"] = body.Template;

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
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(tenant, ct);
        await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantCreatedAsync(tenant, ct));
        await audit.RecordAsync(
            new TenantId(tenant.TenantId), category: "admin", action: "tenant.created", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: tenant.TenantId, targetType: "tenant",
            changes: new AuditChanges(Before: null, After: new { tenant.TenantId, tenant.Name, tenant.Type }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/management/tenants/{tenant.TenantId}", MapToDto(tenant));
    }

    private static async Task<IResult> UpdateTenant(
        string id,
        HttpContext context,
        [FromBody] UpdateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        var before = new { existing.TenantId, existing.Name };

        var newStatus = existing.Status;
        if (body.Status is not null
            && Enum.TryParse<TenantStatus>(body.Status, ignoreCase: true, out var parsedStatus))
        {
            newStatus = parsedStatus;
        }

        var updated = new Tenant
        {
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            Status = newStatus,
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
        await audit.RecordAsync(
            new TenantId(id), category: "admin", action: "tenant.updated", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "tenant",
            changes: new AuditChanges(Before: before, After: new { updated.TenantId, updated.Name }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(MapToDto(updated));
    }

    private static async Task<IResult> SuspendTenant(
        string id,
        HttpContext context,
        [FromServices] ITenantStore store,
        [FromServices] IAuditService audit,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
        [FromServices] ILogger<Program> logger,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new ErrorResponse("Cannot suspend the Platform tenant."));

        await store.UpdateStatusAsync(id, TenantStatus.Suspended, ct);
        await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantSuspendedAsync(id, ct), logger);
        await audit.RecordAsync(
            new TenantId(id), category: "admin", action: "tenant.suspended", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "tenant",
            changes: new AuditChanges(Before: new { Status = "Active" }, After: new { Status = "Suspended" }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(new StatusUpdateResponse(id, "Suspended"));
    }

    private static async Task<IResult> ActivateTenant(
        string id,
        HttpContext context,
        [FromServices] ITenantStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();

        await store.UpdateStatusAsync(id, TenantStatus.Active, ct);
        await audit.RecordAsync(
            new TenantId(id), category: "admin", action: "tenant.activated", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "tenant",
            changes: new AuditChanges(Before: new { tenant.Status }, After: new { Status = TenantStatus.Active }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(new StatusUpdateResponse(id, "Active"));
    }

    private static async Task<IResult> DeleteTenant(
        string id,
        HttpContext context,
        [FromServices] ITenantStore store,
        [FromServices] IAuditService audit,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
        [FromServices] ILogger<Program> logger,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new ErrorResponse("Cannot delete the Platform tenant."));

        // UpdateStatusAsync throws if active children exist
        await store.UpdateStatusAsync(id, TenantStatus.Deleted, ct);
        await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantDeletedAsync(id, ct), logger);
        await audit.RecordAsync(
            new TenantId(id), category: "admin", action: "tenant.deleted", severity: "critical",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "tenant",
            changes: new AuditChanges(Before: new { tenant.TenantId, tenant.Name, tenant.Status }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    private static MgmtTenantDto MapToDto(Tenant t) =>
        new(t.TenantId, t.Name, t.Status.ToString(), t.Type.ToString(),
            t.ParentTenantId, t.Options.MaxConcurrentChannels,
            t.Options.MaxActiveCampaigns, t.Metadata, t.CreatedAt, t.UpdatedAt);

    private static async Task DispatchLifecycleAsync(
        IEnumerable<ITenantLifecycleHandler> handlers,
        Func<ITenantLifecycleHandler, ValueTask> action,
        ILogger? logger = null)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await action(handler);
            }
            catch (Exception ex)
            {
#pragma warning disable CA1848
                logger?.LogWarning(ex, "Lifecycle handler {Handler} failed", handler.GetType().Name);
#pragma warning restore CA1848
            }
        }
    }
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
    Dictionary<string, string>? Metadata = null,
    string? Template = null);

internal sealed record UpdateMgmtTenantRequest(
    string? Name = null,
    string? Status = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    Dictionary<string, string>? Metadata = null);
