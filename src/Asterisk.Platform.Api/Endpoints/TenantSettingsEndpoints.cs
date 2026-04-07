using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

// ── Read DTOs ────────────────────────────────────────────────────────────────

internal sealed record DunningStatusDto(
    string InvoiceId,
    string CurrentStage,
    DateTimeOffset StartedAt,
    DateTimeOffset? EscalatedAt,
    bool IsPaused);

internal sealed record TenantSettingsDto(
    string TenantId,
    string Name,
    string Type,
    string Status,
    OperationalSettingsDto Operational,
    AuthSettingsDto Auth,
    QuotaSettingsDto Quotas,
    RetentionSettingsDto Retention,
    RateLimitTier RateLimitTier,
    string Plan,
    IReadOnlyList<string> EnabledFeatures,
    IReadOnlyList<string> AddOns,
    DunningStatusDto? Dunning);

internal sealed record OperationalSettingsDto(
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    string? DialplanContextPrefix,
    List<string>? NodeAffinity,
    List<int>? AllowedDialingModes);

internal sealed record AuthSettingsDto(
    string MfaPolicy,
    IReadOnlyList<string> MfaRequiredRoles,
    int PasswordMinLength,
    bool PasswordRequireUppercase,
    bool PasswordRequireNumber,
    bool PasswordRequireSpecial,
    int LockoutThreshold,
    int LockoutDurationMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteTimeoutHours,
    bool OidcEnabled,
    string? OidcAuthority,
    string? OidcClientId,
    bool OidcAutoCreateUsers,
    string OidcDefaultRole);

internal sealed record QuotaSettingsDto(
    long? MaxMonthlyVoiceMinutes,
    long? MaxMonthlyMessages,
    long? MaxStorageBytes,
    int? MaxActiveAgents,
    string QuotaAction);

internal sealed record RetentionSettingsDto(
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);

// ── Update DTOs ──────────────────────────────────────────────────────────────

internal sealed record UpdateTenantSettingsRequest(
    UpdateOperationalSettingsDto? Operational = null,
    UpdateAuthSettingsDto? Auth = null,
    UpdateQuotaSettingsDto? Quotas = null,
    UpdateRetentionSettingsDto? Retention = null,
    RateLimitTier? RateLimitTier = null,
    TenantPlan? Plan = null,
    IReadOnlyList<PlanFeature>? AddOns = null);

internal sealed record UpdateOperationalSettingsDto(
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    string? DialplanContextPrefix = null,
    List<string>? NodeAffinity = null,
    List<int>? AllowedDialingModes = null);

internal sealed record UpdateAuthSettingsDto(
    string? MfaPolicy = null,
    IReadOnlyList<string>? MfaRequiredRoles = null,
    int? PasswordMinLength = null,
    bool? PasswordRequireUppercase = null,
    bool? PasswordRequireNumber = null,
    bool? PasswordRequireSpecial = null,
    int? LockoutThreshold = null,
    int? LockoutDurationMinutes = null,
    int? SessionIdleTimeoutMinutes = null,
    int? SessionAbsoluteTimeoutHours = null,
    bool? OidcEnabled = null,
    string? OidcAuthority = null,
    string? OidcClientId = null,
    string? OidcClientSecret = null,
    bool? OidcAutoCreateUsers = null,
    string? OidcDefaultRole = null);

internal sealed record UpdateQuotaSettingsDto(
    long? MaxMonthlyVoiceMinutes = null,
    long? MaxMonthlyMessages = null,
    long? MaxStorageBytes = null,
    int? MaxActiveAgents = null,
    string? QuotaAction = null);

internal sealed record UpdateRetentionSettingsDto(
    int? ConversationRetentionDays = null,
    int? AuthEventRetentionDays = null,
    int? AuditRetentionDays = null,
    int? UsageRecordRetentionDays = null);

// ── Endpoint handlers ────────────────────────────────────────────────────────

internal static class TenantSettingsEndpoints
{
    public static void MapTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/tenant")
            .RequireAuthorization("AdminOnly");

        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", UpdateSettings);
    }

    private static async Task<IResult> GetSettings(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSettings(
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
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        // AdminOnly cannot write quotas, rateLimitTier, plan, or addOns — strip them
        var sanitized = body with { Quotas = null, RateLimitTier = null, Plan = null, AddOns = null };

        await ApplyUpdates(tenantId, sanitized, tenantStore, authConfigStore, quotaStore, retentionStore,
            tierCache, featureGateCache, addOnStore, ct);

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    internal static async Task<TenantSettingsDto?> BuildSettingsDto(
        string tenantId,
        ITenantStore tenantStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantQuotaStore quotaStore,
        ITenantRetentionPolicyStore retentionStore,
        ITenantAddOnStore? addOnStore,
        IDunningStore? dunningStore,
        IFeatureGateService? featureGateService,
        CancellationToken ct)
    {
        var tenantTask = tenantStore.GetAsync(tenantId, ct).AsTask();
        var authTask = authConfigStore.GetAsync(tenantId, ct);
        var quotaTask = quotaStore.GetAsync(new TenantId(tenantId), ct);
        var retentionTask = retentionStore.GetAsync(tenantId, ct);

        await Task.WhenAll(tenantTask, authTask, quotaTask, retentionTask);

        var tenant = await tenantTask;
        if (tenant is null)
            return null;

        var auth = await authTask ?? new TenantAuthConfig { TenantId = tenantId };
        var quota = await quotaTask ?? new TenantQuota { TenantId = new TenantId(tenantId) };
        var retention = await retentionTask;

        var addOns = addOnStore is not null
            ? await addOnStore.GetAsync(tenantId, ct)
            : Array.Empty<TenantAddOn>();

        var dunning = dunningStore is not null
            ? await dunningStore.GetActiveAsync(tenantId, ct)
            : null;

        var plan = tenant.GetPlan();
        var enabledFeatures = featureGateService?.GetEnabledFeatures(tenantId)
            ?? (IReadOnlySet<PlanFeature>)PlanDefinition.GetFeatures(plan);

        return new TenantSettingsDto(
            TenantId: tenant.TenantId,
            Name: tenant.Name,
            Type: tenant.Type.ToString(),
            Status: tenant.Status.ToString(),
            Operational: new OperationalSettingsDto(
                MaxConcurrentChannels: tenant.Options.MaxConcurrentChannels,
                MaxActiveCampaigns: tenant.Options.MaxActiveCampaigns,
                DialplanContextPrefix: tenant.Options.DialplanContextPrefix,
                NodeAffinity: tenant.Options.NodeAffinity,
                AllowedDialingModes: tenant.Options.AllowedDialingModes),
            Auth: new AuthSettingsDto(
                MfaPolicy: auth.MfaPolicy,
                MfaRequiredRoles: auth.MfaRequiredRoles,
                PasswordMinLength: auth.PasswordMinLength,
                PasswordRequireUppercase: auth.PasswordRequireUppercase,
                PasswordRequireNumber: auth.PasswordRequireNumber,
                PasswordRequireSpecial: auth.PasswordRequireSpecial,
                LockoutThreshold: auth.LockoutThreshold,
                LockoutDurationMinutes: auth.LockoutDurationMinutes,
                SessionIdleTimeoutMinutes: auth.SessionIdleTimeoutMinutes,
                SessionAbsoluteTimeoutHours: auth.SessionAbsoluteTimeoutHours,
                OidcEnabled: auth.OidcEnabled,
                OidcAuthority: auth.OidcAuthority,
                OidcClientId: auth.OidcClientId,
                OidcAutoCreateUsers: auth.OidcAutoCreateUsers,
                OidcDefaultRole: auth.OidcDefaultRole),
            Quotas: new QuotaSettingsDto(
                MaxMonthlyVoiceMinutes: quota.MaxMonthlyVoiceMinutes,
                MaxMonthlyMessages: quota.MaxMonthlyMessages,
                MaxStorageBytes: quota.MaxStorageBytes,
                MaxActiveAgents: quota.MaxActiveAgents,
                QuotaAction: quota.QuotaAction.ToString()),
            Retention: new RetentionSettingsDto(
                ConversationRetentionDays: retention?.ConversationRetentionDays,
                AuthEventRetentionDays: retention?.AuthEventRetentionDays,
                AuditRetentionDays: retention?.AuditRetentionDays,
                UsageRecordRetentionDays: retention?.UsageRecordRetentionDays),
            RateLimitTier: tenant.GetRateLimitTier(),
            Plan: plan.ToString(),
            EnabledFeatures: enabledFeatures.Select(f => f.ToString()).ToList(),
            AddOns: addOns.Select(a => a.Feature.ToString()).ToList(),
            Dunning: dunning is null ? null : new DunningStatusDto(
                InvoiceId: dunning.InvoiceId,
                CurrentStage: dunning.CurrentStage.ToString(),
                StartedAt: dunning.StartedAt,
                EscalatedAt: dunning.EscalatedAt,
                IsPaused: dunning.IsPaused));
    }

    internal static async Task ApplyUpdates(
        string tenantId,
        UpdateTenantSettingsRequest body,
        ITenantStore tenantStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantQuotaStore quotaStore,
        ITenantRetentionPolicyStore retentionStore,
        TenantTierCache? tierCache,
        FeatureGateCache? featureGateCache,
        ITenantAddOnStore? addOnStore,
        CancellationToken ct)
    {
        if (body.Operational is not null || body.RateLimitTier is not null || body.Plan is not null)
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            if (tenant is not null)
            {
                var op = body.Operational;
                var newMetadata = tenant.Metadata is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(tenant.Metadata);

                if (body.RateLimitTier is { } tier)
                    newMetadata["RateLimitTier"] = tier.ToString();

                if (body.Plan is { } newPlan)
                {
                    // Hierarchy ceiling: child cannot exceed parent plan
                    if (tenant.ParentTenantId is not null)
                    {
                        var parent = await tenantStore.GetAsync(tenant.ParentTenantId, ct);
                        if (parent is not null && parent.GetPlan() < newPlan)
                            return; // Silently skip — validation should be done at endpoint level
                    }

                    newMetadata["Plan"] = newPlan.ToString();

                    // Derive tier from plan if no manual override
                    if (!newMetadata.ContainsKey("RateLimitTier"))
                    {
                        var derivedTier = PlanDefinition.GetDefaultTier(newPlan);
                        tierCache?.SetTier(tenantId, derivedTier);
                    }
                }

                var updated = new Tenant
                {
                    TenantId = tenant.TenantId,
                    Name = tenant.Name,
                    Status = tenant.Status,
                    Type = tenant.Type,
                    ParentTenantId = tenant.ParentTenantId,
                    Options = new TenantOptions
                    {
                        MaxConcurrentChannels = op?.MaxConcurrentChannels ?? tenant.Options.MaxConcurrentChannels,
                        MaxActiveCampaigns = op?.MaxActiveCampaigns ?? tenant.Options.MaxActiveCampaigns,
                        DialplanContextPrefix = op?.DialplanContextPrefix ?? tenant.Options.DialplanContextPrefix,
                        NodeAffinity = op?.NodeAffinity ?? tenant.Options.NodeAffinity,
                        AllowedDialingModes = op?.AllowedDialingModes ?? tenant.Options.AllowedDialingModes,
                    },
                    Metadata = newMetadata,
                    CreatedAt = tenant.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                await tenantStore.UpsertAsync(updated, ct);

                if (body.RateLimitTier is { } newTier && tierCache is not null)
                    tierCache.SetTier(tenantId, newTier);
            }
        }

        if (body.Auth is not null)
        {
            var auth = await authConfigStore.GetAsync(tenantId, ct)
                    ?? new TenantAuthConfig { TenantId = tenantId };

            var a = body.Auth;
            if (a.MfaPolicy is not null) auth.MfaPolicy = a.MfaPolicy;
            if (a.MfaRequiredRoles is not null) auth.MfaRequiredRoles = a.MfaRequiredRoles;
            if (a.PasswordMinLength is not null) auth.PasswordMinLength = a.PasswordMinLength.Value;
            if (a.PasswordRequireUppercase is not null) auth.PasswordRequireUppercase = a.PasswordRequireUppercase.Value;
            if (a.PasswordRequireNumber is not null) auth.PasswordRequireNumber = a.PasswordRequireNumber.Value;
            if (a.PasswordRequireSpecial is not null) auth.PasswordRequireSpecial = a.PasswordRequireSpecial.Value;
            if (a.LockoutThreshold is not null) auth.LockoutThreshold = a.LockoutThreshold.Value;
            if (a.LockoutDurationMinutes is not null) auth.LockoutDurationMinutes = a.LockoutDurationMinutes.Value;
            if (a.SessionIdleTimeoutMinutes is not null) auth.SessionIdleTimeoutMinutes = a.SessionIdleTimeoutMinutes.Value;
            if (a.SessionAbsoluteTimeoutHours is not null) auth.SessionAbsoluteTimeoutHours = a.SessionAbsoluteTimeoutHours.Value;
            if (a.OidcEnabled is not null) auth.OidcEnabled = a.OidcEnabled.Value;
            if (a.OidcAuthority is not null) auth.OidcAuthority = a.OidcAuthority;
            if (a.OidcClientId is not null) auth.OidcClientId = a.OidcClientId;
            if (a.OidcClientSecret is not null) auth.OidcClientSecret = a.OidcClientSecret;
            if (a.OidcAutoCreateUsers is not null) auth.OidcAutoCreateUsers = a.OidcAutoCreateUsers.Value;
            if (a.OidcDefaultRole is not null) auth.OidcDefaultRole = a.OidcDefaultRole;

            auth.UpdatedAt = DateTimeOffset.UtcNow;
            await authConfigStore.SaveAsync(auth, ct);
        }

        if (body.Quotas is not null)
        {
            var quota = await quotaStore.GetAsync(new TenantId(tenantId), ct)
                     ?? new TenantQuota { TenantId = new TenantId(tenantId) };

            var q = body.Quotas;
            if (q.MaxMonthlyVoiceMinutes is not null) quota.MaxMonthlyVoiceMinutes = q.MaxMonthlyVoiceMinutes;
            if (q.MaxMonthlyMessages is not null) quota.MaxMonthlyMessages = q.MaxMonthlyMessages;
            if (q.MaxStorageBytes is not null) quota.MaxStorageBytes = q.MaxStorageBytes;
            if (q.MaxActiveAgents is not null) quota.MaxActiveAgents = q.MaxActiveAgents;
            if (q.QuotaAction is not null && Enum.TryParse<QuotaAction>(q.QuotaAction, ignoreCase: true, out var qa))
                quota.QuotaAction = qa;

            await quotaStore.UpsertAsync(quota, ct);
        }

        if (body.Retention is not null)
        {
            var existing = await retentionStore.GetAsync(tenantId, ct);
            var r = body.Retention;

            var policy = new TenantRetentionPolicy
            {
                TenantId = tenantId,
                ConversationRetentionDays = r.ConversationRetentionDays ?? existing?.ConversationRetentionDays,
                AuthEventRetentionDays = r.AuthEventRetentionDays ?? existing?.AuthEventRetentionDays,
                AuditRetentionDays = r.AuditRetentionDays ?? existing?.AuditRetentionDays,
                UsageRecordRetentionDays = r.UsageRecordRetentionDays ?? existing?.UsageRecordRetentionDays,
            };

            await retentionStore.SaveAsync(policy, ct);
        }

        if (body.AddOns is not null && addOnStore is not null)
        {
            var existingAddOns = await addOnStore.GetAsync(tenantId, ct);
            var requestedSet = body.AddOns.ToHashSet();
            var existingSet = existingAddOns.Select(a => a.Feature).ToHashSet();

            foreach (var feature in requestedSet.Except(existingSet))
            {
                await addOnStore.UpsertAsync(new TenantAddOn
                {
                    TenantId = tenantId,
                    Feature = feature,
                    EnabledAt = DateTimeOffset.UtcNow,
                }, ct);
            }

            foreach (var feature in existingSet.Except(requestedSet))
                await addOnStore.DeleteAsync(tenantId, feature, ct);
        }

        // Invalidate feature cache after plan/add-on changes
        if (body.Plan is not null || body.AddOns is not null)
            featureGateCache?.Remove(tenantId);
    }
}
