using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class RbacEndpoints
{
    public static void MapRbacEndpoints(this IEndpointRouteBuilder app)
    {
        // Permission catalog (read-only, requires AdminOnly)
        var perms = app.MapGroup("/admin/permissions").RequireAuthorization("AdminOnly");
        perms.MapGet("/", ListPermissions);
        perms.MapGet("/categories", ListPermissionsByCategory);

        // Role templates (read-only system defaults)
        var templates = app.MapGroup("/admin/role-templates").RequireAuthorization("AdminOnly");
        templates.MapGet("/", ListRoleTemplates);
        templates.MapGet("/{id}", GetRoleTemplate);

        // Tenant roles (CRUD, per-tenant)
        var roles = app.MapGroup("/admin/roles").RequireAuthorization("AdminOnly");
        roles.MapGet("/", ListTenantRoles);
        roles.MapPost("/", CreateTenantRole);
        roles.MapGet("/{id}", GetTenantRole);
        roles.MapPut("/{id}", UpdateTenantRole);
        roles.MapDelete("/{id}", DeleteTenantRole);
        roles.MapPost("/{id}/clone", CloneTenantRole);

        // User role assignments
        var userRoles = app.MapGroup("/admin/users").RequireAuthorization("AdminOnly");
        userRoles.MapGet("/{id}/roles", GetUserRoles);
        userRoles.MapPut("/{id}/roles", ReplaceUserRoles);
        userRoles.MapPost("/{id}/roles/{roleId}", AddUserRole);
        userRoles.MapDelete("/{id}/roles/{roleId}", RemoveUserRole);
        userRoles.MapGet("/{id}/permissions", GetUserEffectivePermissions);
    }

    // --- Permission Catalog ---

    private static async Task<IResult> ListPermissions(
        [FromServices] IPermissionStore store, CancellationToken ct)
    {
        var permissions = await store.GetAllAsync(ct);
        return Results.Ok(permissions);
    }

    private static async Task<IResult> ListPermissionsByCategory(
        [FromServices] IPermissionStore store, CancellationToken ct)
    {
        var all = await store.GetAllAsync(ct);
        var grouped = all.GroupBy(p => p.Category)
            .Select(g => new PermissionGroupDto(g.Key, g.ToList()))
            .OrderBy(g => g.Category)
            .ToList();
        return Results.Ok(grouped);
    }

    // --- Role Templates ---

    private static async Task<IResult> ListRoleTemplates(
        [FromServices] IRoleTemplateStore store, CancellationToken ct)
    {
        var templates = await store.GetAllAsync(ct);
        return Results.Ok(templates);
    }

    private static async Task<IResult> GetRoleTemplate(
        string id, [FromServices] IRoleTemplateStore store, CancellationToken ct)
    {
        var template = await store.GetByIdAsync(id, ct);
        return template is null ? Results.NotFound() : Results.Ok(template);
    }

    // --- Tenant Roles ---

    private static async Task<IResult> ListTenantRoles(
        HttpContext context, [FromServices] ITenantRoleStore store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var roles = await store.ListAsync(tenantId, ct);
        return Results.Ok(roles);
    }

    private static async Task<IResult> CreateTenantRole(
        HttpContext context, [FromBody] CreateTenantRoleRequest body,
        [FromServices] ITenantRoleStore store, [FromServices] IRoleTemplateStore templateStore,
        IClock clock, [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var roleId = Guid.NewGuid().ToString("N")[..12];

        if (body.SourceTemplateId is not null)
        {
            var template = await templateStore.GetByIdAsync(body.SourceTemplateId, ct);
            if (template is null)
                return Results.BadRequest(new ErrorResponse($"Template '{body.SourceTemplateId}' not found"));

            await store.CloneFromTemplateAsync(tenantId, roleId, body.SourceTemplateId,
                body.Name, body.Description, ct);
        }
        else
        {
            var role = new TenantRole
            {
                RoleId = roleId,
                TenantId = tenantId,
                Name = body.Name,
                Description = body.Description,
                CreatedAt = clock.UtcNow,
            };
            await store.SaveAsync(role, ct);

            if (body.Permissions is { Count: > 0 })
                await store.SetPermissionsAsync(tenantId, roleId, body.Permissions, ct);
        }

        var created = await store.GetByIdAsync(tenantId, roleId, ct);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.created", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: roleId, targetType: "role",
            changes: new AuditChanges(Before: null, After: new { RoleId = roleId, body.Name }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/roles/{roleId}", created);
    }

    private static async Task<IResult> GetTenantRole(
        string id, HttpContext context, [FromServices] ITenantRoleStore store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var role = await store.GetByIdAsync(tenantId, id, ct);
        return role is null ? Results.NotFound() : Results.Ok(role);
    }

    private static async Task<IResult> UpdateTenantRole(
        string id, HttpContext context, [FromBody] UpdateTenantRoleRequest body,
        [FromServices] ITenantRoleStore store, [FromServices] PermissionResolver resolver,
        IClock clock, [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var role = await store.GetByIdAsync(tenantId, id, ct);
        if (role is null) return Results.NotFound();

        var before = new { role.RoleId, role.Name, role.Description };

        if (body.Name is not null) role.Name = body.Name;
        if (body.Description is not null) role.Description = body.Description;
        role.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(role, ct);

        if (body.Permissions is not null)
        {
            await store.SetPermissionsAsync(tenantId, id, body.Permissions, ct);
            resolver.InvalidateTenant(tenantId);
        }

        var updated = await store.GetByIdAsync(tenantId, id, ct);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.updated", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "role",
            changes: new AuditChanges(Before: before, After: new { role.RoleId, role.Name, role.Description }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(updated);
    }

    private static async Task<IResult> DeleteTenantRole(
        string id, HttpContext context, [FromServices] ITenantRoleStore store,
        [FromServices] PermissionResolver resolver, [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var role = await store.GetByIdAsync(tenantId, id, ct);
        if (role is null) return Results.NotFound();

        // Prevent deleting roles that are cloned from system templates and marked as default
        if (role.IsDefault)
            return Results.BadRequest(new ErrorResponse("Cannot delete a default role"));

        var userCount = await store.GetUserCountAsync(tenantId, id, ct);
        if (userCount > 0)
            return Results.BadRequest(new ErrorResponse($"Cannot delete role with {userCount} assigned users"));

        await store.DeleteAsync(tenantId, id, ct);
        resolver.InvalidateTenant(tenantId);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.deleted", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "role",
            changes: new AuditChanges(Before: new { role.RoleId, role.Name }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CloneTenantRole(
        string id, HttpContext context, [FromBody] CloneTenantRoleRequest body,
        [FromServices] ITenantRoleStore store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var source = await store.GetByIdAsync(tenantId, id, ct);
        if (source is null) return Results.NotFound();

        var newRoleId = Guid.NewGuid().ToString("N")[..12];
        var newRole = new TenantRole
        {
            RoleId = newRoleId,
            TenantId = tenantId,
            Name = body.Name,
            Description = body.Description ?? source.Description,
            SourceTemplateId = source.SourceTemplateId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(newRole, ct);

        // Copy permissions from source role
        var perms = await store.GetPermissionsAsync(tenantId, id, ct);
        if (perms.Count > 0)
            await store.SetPermissionsAsync(tenantId, newRoleId, perms, ct);

        var created = await store.GetByIdAsync(tenantId, newRoleId, ct);
        return Results.Created($"/admin/roles/{newRoleId}", created);
    }

    // --- User Role Assignments ---

    private static async Task<IResult> GetUserRoles(
        string id, HttpContext context, [FromServices] IUserRoleStore store, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var roles = await store.GetRolesForUserAsync(tenantId, EntityId.From(id), ct);
        return Results.Ok(roles);
    }

    private static async Task<IResult> ReplaceUserRoles(
        string id, HttpContext context, [FromBody] ReplaceUserRolesRequest body,
        [FromServices] IUserRoleStore store, [FromServices] PermissionResolver resolver,
        [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = EntityId.From(id);
        var assignedBy = context.User.FindFirst("user_id")?.Value;

        var before = await store.GetRolesForUserAsync(tenantId, userId, ct);
        await store.ReplaceAllAsync(tenantId, userId, body.RoleIds, assignedBy, ct);
        resolver.InvalidateUser(tenantId, userId);

        var roles = await store.GetRolesForUserAsync(tenantId, userId, ct);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.assigned", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "user",
            changes: new AuditChanges(Before: before, After: roles),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(roles);
    }

    private static async Task<IResult> AddUserRole(
        string id, string roleId, HttpContext context,
        [FromServices] IUserRoleStore store, [FromServices] PermissionResolver resolver,
        [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = EntityId.From(id);
        var assignedBy = context.User.FindFirst("user_id")?.Value;

        await store.AssignAsync(tenantId, userId, roleId, assignedBy, ct);
        resolver.InvalidateUser(tenantId, userId);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.assigned", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "user",
            changes: new AuditChanges(Before: null, After: new { UserId = id, RoleId = roleId }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["role_id"] = roleId,
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveUserRole(
        string id, string roleId, HttpContext context,
        [FromServices] IUserRoleStore store, [FromServices] PermissionResolver resolver,
        [FromServices] IAuditService audit, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = EntityId.From(id);

        await store.RemoveAsync(tenantId, userId, roleId, ct);
        resolver.InvalidateUser(tenantId, userId);
        await audit.RecordAsync(
            tenantId, category: "rbac", action: "role.revoked", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "user",
            changes: new AuditChanges(Before: new { UserId = id, RoleId = roleId }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["role_id"] = roleId,
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetUserEffectivePermissions(
        string id, HttpContext context,
        [FromServices] PermissionResolver resolver, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var userId = EntityId.From(id);
        var permissions = await resolver.ResolveAsync(tenantId, userId, ct);
        return Results.Ok(new UserPermissionsDto(id, permissions.Order().ToList()));
    }

    // --- Helpers ---

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// --- Request DTOs ---

internal sealed record CreateTenantRoleRequest(
    string Name,
    string? Description = null,
    string? SourceTemplateId = null,
    IReadOnlyList<string>? Permissions = null);

internal sealed record UpdateTenantRoleRequest(
    string? Name = null,
    string? Description = null,
    IReadOnlyList<string>? Permissions = null);

internal sealed record CloneTenantRoleRequest(
    string Name,
    string? Description = null);

internal sealed record ReplaceUserRolesRequest(IReadOnlyList<string> RoleIds);

internal sealed record PermissionGroupDto(string Category, IReadOnlyList<PermissionDefinition> Permissions);
internal sealed record UserPermissionsDto(string UserId, IReadOnlyList<string> Permissions);
