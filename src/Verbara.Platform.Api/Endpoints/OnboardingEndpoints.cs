using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

// ── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record OnboardingStatusDto(
    bool WizardCompleted,
    string? TemplateApplied,
    IReadOnlyList<ChecklistItemDto> Checklist,
    bool ChecklistDismissed);

internal sealed record ChecklistItemDto(string Key, string Label, bool Completed);

internal sealed record ApplyTemplateRequest(string Template);

// ── Endpoints ────────────────────────────────────────────────────────────────

internal static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/onboarding")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/status", GetStatus);
        group.MapPost("/apply-template", ApplyTemplate);
        group.MapPost("/complete", Complete);
        group.MapPut("/dismiss-checklist", DismissChecklist);
    }

    private static async Task<IResult> GetStatus(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IQueueStore queueStore,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        var meta = tenant.Metadata ?? new Dictionary<string, string>();

        var wizardCompleted = meta.GetValueOrDefault("OnboardingCompleted") == "true";
        var templateApplied = meta.TryGetValue("OnboardingTemplate", out var tpl) ? tpl : null;
        var dismissed = meta.GetValueOrDefault("OnboardingDismissedChecklist") == "true";
        var hasChannels = meta.ContainsKey("OnboardingChannels");
        var testCompleted = meta.GetValueOrDefault("OnboardingTestCompleted") == "true";

        // Parallel store queries for checklist items
        var queueTask = queueStore.ListAsync(new TenantId(tenantId), new PagedQuery(1, 1), ct);
        var userTask = userStore.ListAsync(new TenantId(tenantId), new PagedQuery(1, 2), ct);

        await Task.WhenAll(queueTask, userTask);

        var queues = await queueTask;
        var users = await userTask;
        var branding = await brandingStore.GetAsync(tenantId, ct);

        var hasFirstQueue = queues.TotalCount > 0;
        var hasFirstAgent = users.TotalCount > 1; // admin always exists
        var hasBranding = branding is not null && branding.DisplayName is not null;

        var checklist = new List<ChecklistItemDto>
        {
            new("org_profile",        "Set up organization profile",        wizardCompleted),
            new("channels_selected",  "Select communication channels",      hasChannels),
            new("first_queue",        "Create first queue",                 hasFirstQueue),
            new("channel_credentials","Configure channel credentials",       false),
            new("first_agent",        "Invite first agent",                 hasFirstAgent),
            new("test_interaction",   "Complete a test interaction",         testCompleted),
            new("branding",           "Customize branding",                 hasBranding),
        };

        return Results.Ok(new OnboardingStatusDto(
            WizardCompleted: wizardCompleted,
            TemplateApplied: templateApplied,
            Checklist: checklist,
            ChecklistDismissed: dismissed));
    }

    private static async Task<IResult> ApplyTemplate(
        HttpContext context,
        [FromBody] ApplyTemplateRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TenantProvisioningService provisioningService,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        if (!TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
            return Results.BadRequest(new ErrorResponse(
                $"Invalid template '{body.Template}'. Valid templates: {string.Join(", ", TenantProvisioningTemplates.ValidTemplateNames)}"));

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        var meta = tenant.Metadata ?? new Dictionary<string, string>();
        if (meta.TryGetValue("OnboardingTemplate", out var existing) &&
            !string.Equals(existing, "none", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new ErrorResponse(
                $"Template '{existing}' has already been applied to this tenant"));
        }

        var newMetadata = new Dictionary<string, string>(meta)
        {
            ["OnboardingTemplate"] = body.Template,
        };

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = newMetadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);
        await provisioningService.ApplyTemplateAsync(updated, body.Template, ct);

        return Results.Ok(new MessageResponse($"Template '{body.Template}' applied successfully"));
    }

    private static async Task<IResult> Complete(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        var newMetadata = new Dictionary<string, string>(tenant.Metadata ?? new Dictionary<string, string>())
        {
            ["OnboardingCompleted"] = "true",
        };

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = newMetadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);

        return Results.Ok(new MessageResponse("Onboarding wizard marked as completed"));
    }

    private static async Task<IResult> DismissChecklist(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null)
            return Results.NotFound(new ErrorResponse("Tenant not found"));

        var newMetadata = new Dictionary<string, string>(tenant.Metadata ?? new Dictionary<string, string>())
        {
            ["OnboardingDismissedChecklist"] = "true",
        };

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = newMetadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);

        return Results.Ok(new MessageResponse("Checklist dismissed"));
    }
}
