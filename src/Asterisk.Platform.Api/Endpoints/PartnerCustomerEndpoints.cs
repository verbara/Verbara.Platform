using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerCustomerEndpoints
{
    public static RouteGroupBuilder MapPartnerCustomerEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner")
            .WithTags("Partner - Customers")
            .RequireAuthorization("PartnerAdminOnly");

        group.MapGet("/customers", ListCustomers)
            .RequireAuthorization("partner:customer:view");
        group.MapPost("/customers", CreateCustomer)
            .RequireAuthorization("partner:customer:create");
        group.MapGet("/customers/{customerId}", GetCustomer)
            .RequireAuthorization("partner:customer:view");
        group.MapPut("/customers/{customerId}", UpdateCustomer)
            .RequireAuthorization("partner:customer:manage");
        group.MapPost("/customers/{customerId}/suspend", SuspendCustomer)
            .RequireAuthorization("partner:customer:manage");
        group.MapPost("/customers/{customerId}/activate", ActivateCustomer)
            .RequireAuthorization("partner:customer:manage");
        group.MapGet("/customers/{customerId}/settings", GetCustomerSettings)
            .RequireAuthorization("partner:customer:manage");
        group.MapPut("/customers/{customerId}/settings", UpdateCustomerSettings)
            .RequireAuthorization("partner:customer:manage");

        return app;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListCustomers(
        HttpContext context,
        [FromQuery] string? status,
        [FromQuery] string? plan,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var children = await tenantStore.GetChildrenAsync(callerTenantId, ct);

        var result = children.Where(t => t.Type == TenantType.Customer).AsEnumerable();

        if (Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var statusFilter))
            result = result.Where(t => t.Status == statusFilter);

        if (Enum.TryParse<TenantPlan>(plan, ignoreCase: true, out var planFilter))
            result = result.Where(t => t.GetPlan() == planFilter);

        return Results.Ok(result.Select(MapToDto).ToList());
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private static async Task<IResult> CreateCustomer(
        HttpContext context,
        [FromBody] CreatePartnerCustomerRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
        [FromServices] TenantTierCache tierCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        // Partner must be Active
        var partner = await tenantStore.GetAsync(callerTenantId, ct);
        if (partner is null || partner.Status != TenantStatus.Active)
            return Results.BadRequest(new ErrorResponse("Partner tenant must be Active to create customers."));

        // Check for duplicate TenantId
        var existing = await tenantStore.GetAsync(body.TenantId, ct);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse($"Tenant '{body.TenantId}' already exists."));

        // Resolve plan — enforce hierarchy ceiling (customer cannot exceed partner plan)
        var partnerPlan = partner.GetPlan();
        TenantPlan customerPlan;
        if (body.Plan is not null && Enum.TryParse<TenantPlan>(body.Plan, ignoreCase: true, out var requestedPlan))
        {
            if (requestedPlan > partnerPlan)
                return Results.BadRequest(new ErrorResponse($"Customer plan '{requestedPlan}' exceeds partner plan '{partnerPlan}'."));
            customerPlan = requestedPlan;
        }
        else
        {
            customerPlan = TenantPlan.Starter;
        }

        // Validate template if provided
        if (body.Template is not null && !TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
            return Results.BadRequest(new ErrorResponse($"Invalid template '{body.Template}'. Valid: support, sales, blended."));

        var derivedTier = PlanDefinition.GetDefaultTier(customerPlan);
        var metadata = new Dictionary<string, string>
        {
            ["Plan"] = customerPlan.ToString(),
            ["RateLimitTier"] = derivedTier.ToString(),
        };
        if (body.Template is not null)
            metadata["OnboardingTemplate"] = body.Template;

        var tenant = new Tenant
        {
            TenantId = body.TenantId,
            Name = body.Name,
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = callerTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = 100,
                MaxActiveCampaigns = 10,
            },
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(tenant, ct);
        tierCache.SetTier(tenant.TenantId, derivedTier);
        await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantCreatedAsync(tenant, ct));
        await audit.RecordAsync(
            new TenantId(callerTenantId), category: "admin", action: "partner.customer.created", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: tenant.TenantId, targetType: "tenant",
            changes: new AuditChanges(Before: null, After: new { tenant.TenantId, tenant.Name, Plan = customerPlan.ToString() }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return Results.Created($"/partner/customers/{tenant.TenantId}", MapToDto(tenant));
    }

    // ── Get ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetCustomer(
        string customerId,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        return Results.Ok(MapToDto(customer));
    }

    // ── Update ───────────────────────────────────────────────────────────────

    private static async Task<IResult> UpdateCustomer(
        string customerId,
        HttpContext context,
        [FromBody] UpdatePartnerCustomerRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var before = new { customer.TenantId, customer.Name };

        var updated = new Tenant
        {
            TenantId = customer.TenantId,
            Name = body.Name ?? customer.Name,
            Status = customer.Status,
            Type = customer.Type,
            ParentTenantId = customer.ParentTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = body.MaxConcurrentChannels ?? customer.Options.MaxConcurrentChannels,
                MaxActiveCampaigns = body.MaxActiveCampaigns ?? customer.Options.MaxActiveCampaigns,
                DialplanContextPrefix = customer.Options.DialplanContextPrefix,
                NodeAffinity = customer.Options.NodeAffinity,
                AllowedDialingModes = customer.Options.AllowedDialingModes,
            },
            Metadata = customer.Metadata,
            CreatedAt = customer.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);
        await audit.RecordAsync(
            new TenantId(callerTenantId), category: "admin", action: "partner.customer.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: customerId, targetType: "tenant",
            changes: new AuditChanges(Before: before, After: new { updated.TenantId, updated.Name }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return Results.Ok(MapToDto(updated));
    }

    // ── Suspend ──────────────────────────────────────────────────────────────

    private static async Task<IResult> SuspendCustomer(
        string customerId,
        HttpContext context,
        [FromBody] SuspendCustomerRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        [FromServices] ILogger<Program> logger,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Required"] });

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        await tenantStore.UpdateStatusAsync(customerId, TenantStatus.Suspended, ct);
        tierCache.Remove(customerId);
        featureGateCache.Remove(customerId);
        await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantSuspendedAsync(customerId, ct), logger);
        await audit.RecordAsync(
            new TenantId(callerTenantId), category: "admin", action: "partner.customer.suspended", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: customerId, targetType: "tenant",
            changes: new AuditChanges(
                Before: new { Status = customer.Status.ToString() },
                After: new { Status = "Suspended", Reason = body.Reason }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["reason"] = body.Reason,
            },
            ct: ct);

        return Results.Ok(new StatusUpdateResponse(customerId, "Suspended"));
    }

    // ── Activate ─────────────────────────────────────────────────────────────

    private static async Task<IResult> ActivateCustomer(
        string customerId,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        await tenantStore.UpdateStatusAsync(customerId, TenantStatus.Active, ct);
        tierCache.Remove(customerId);
        featureGateCache.Remove(customerId);
        await audit.RecordAsync(
            new TenantId(callerTenantId), category: "admin", action: "partner.customer.activated", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: customerId, targetType: "tenant",
            changes: new AuditChanges(Before: new { Status = customer.Status.ToString() }, After: new { Status = "Active" }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return Results.Ok(new StatusUpdateResponse(customerId, "Active"));
    }

    // ── Settings GET ─────────────────────────────────────────────────────────

    private static async Task<IResult> GetCustomerSettings(
        string customerId,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(customerId, tenantStore, authConfigStore,
            quotaStore, retentionStore, addOnStore, dunningStore, featureGateService, brandingStore, ct);

        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    // ── Settings PUT ─────────────────────────────────────────────────────────

    private static async Task<IResult> UpdateCustomerSettings(
        string customerId,
        HttpContext context,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        [FromServices] FeatureGateCache featureGateCache,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (callerTenantId is null)
            return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        // Partner cannot write quotas or RateLimitTier for customers
        var sanitized = body with { Quotas = null, RateLimitTier = null };

        // Enforce plan hierarchy ceiling on plan changes
        if (sanitized.Plan is { } requestedPlan)
        {
            var partner = await tenantStore.GetAsync(callerTenantId, ct);
            if (partner is not null && requestedPlan > partner.GetPlan())
                return Results.BadRequest(new ErrorResponse($"Customer plan '{requestedPlan}' exceeds partner plan '{partner.GetPlan()}'."));
        }

        var actorName = context.User.Identity?.Name ?? "unknown";
        var error = await TenantSettingsEndpoints.ApplyUpdates(customerId, sanitized, tenantStore, authConfigStore,
            quotaStore, retentionStore, tierCache, featureGateCache, addOnStore, brandingStore, ct,
            context.RequestServices, actorName);
        if (error is not null)
            return error;

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(customerId, tenantStore, authConfigStore,
            quotaStore, retentionStore, addOnStore, dunningStore, featureGateService, brandingStore, ct);

        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PartnerCustomerDto MapToDto(Tenant t) =>
        new(t.TenantId, t.Name, t.Status.ToString(), t.GetPlan().ToString(), t.CreatedAt);

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

internal sealed record PartnerCustomerDto(
    string TenantId,
    string Name,
    string Status,
    string Plan,
    DateTimeOffset CreatedAt);

internal sealed record CreatePartnerCustomerRequest(
    string TenantId,
    string Name,
    string? Plan = null,
    string? Template = null);

internal sealed record UpdatePartnerCustomerRequest(
    string? Name = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null);

internal sealed record SuspendCustomerRequest(string Reason);
