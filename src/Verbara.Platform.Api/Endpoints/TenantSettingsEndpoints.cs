using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Endpoints;

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
    DunningStatusDto? Dunning,
    BrandingSettingsDto? Branding);

internal sealed record OperationalSettingsDto(
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    string? DialplanContextPrefix,
    List<string>? NodeAffinity,
    List<int>? AllowedDialingModes,
    // 3B.2d — tenant-level outbound caller ID for agent click-to-dial + external blind transfer.
    string? OutboundCallerId,
    // W6-A7 — per-tenant default channel capacity; agents inherit these unless overridden.
    int MaxVoiceDefault,
    int MaxChatDefault,
    int MaxEmailDefault,
    int MaxSmsDefault,
    int MaxTotalDefault);

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
    string OidcDefaultRole,
    // R5.2 PB.2 / C.7 — per-tenant impersonation session policy.
    int ImpersonationMaxConcurrentSessions,
    int ImpersonationAutoTimeoutMinutes,
    // §4.1 IP allowlist toggle
    bool IpAllowlistEnabled);

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
    IReadOnlyList<PlanFeature>? AddOns = null,
    UpdateBrandingSettingsDto? Branding = null);

internal sealed record UpdateOperationalSettingsDto(
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    string? DialplanContextPrefix = null,
    List<string>? NodeAffinity = null,
    List<int>? AllowedDialingModes = null,
    string? OutboundCallerId = null,
    // W6-A7 — per-tenant default channel capacity (null = leave unchanged).
    int? MaxVoiceDefault = null,
    int? MaxChatDefault = null,
    int? MaxEmailDefault = null,
    int? MaxSmsDefault = null,
    int? MaxTotalDefault = null);

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
    string? OidcDefaultRole = null,
    // R5.2 PB.2 / C.7 — per-tenant impersonation session policy.
    int? ImpersonationMaxConcurrentSessions = null,
    int? ImpersonationAutoTimeoutMinutes = null,
    // §4.1 IP allowlist toggle
    bool? IpAllowlistEnabled = null);

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

// ── Branding DTOs ───────────────────────────────────────────────────────────

internal sealed record BrandingSettingsDto(
    string? DisplayName, string? LogoUrl, string? FaviconUrl,
    string? PrimaryColor, string? SecondaryColor, string? AccentColor,
    string? Locale, string? Timezone, string? Subdomain,
    string? SupportEmail, string? SupportUrl,
    string? EmailFromName, string? EmailFromAddress);

internal sealed record UpdateBrandingSettingsDto(
    string? DisplayName = null, string? LogoUrl = null, string? FaviconUrl = null,
    string? PrimaryColor = null, string? SecondaryColor = null, string? AccentColor = null,
    string? Locale = null, string? Timezone = null,
    string? SupportEmail = null, string? SupportUrl = null,
    string? EmailFromName = null, string? EmailFromAddress = null);

internal sealed record UpdateManagementBrandingSettingsDto(
    string? DisplayName = null, string? LogoUrl = null, string? FaviconUrl = null,
    string? PrimaryColor = null, string? SecondaryColor = null, string? AccentColor = null,
    string? Locale = null, string? Timezone = null, string? Subdomain = null,
    string? SupportEmail = null, string? SupportUrl = null,
    string? EmailFromName = null, string? EmailFromAddress = null);

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

    private static async Task<Results<Ok<TenantSettingsDto>, UnauthorizedHttpResult, NotFound>> GetSettings(
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
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return TypedResults.Unauthorized();

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);
        if (dto is null)
            return TypedResults.NotFound();
        return TypedResults.Ok(dto);
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
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
                    ?? context.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
            return Results.Unauthorized();

        // AdminOnly cannot write quotas, rateLimitTier, plan, or addOns — strip them
        var sanitized = body with { Quotas = null, RateLimitTier = null, Plan = null, AddOns = null };

        var actorName = context.User.Identity?.Name ?? "unknown";
        var error = await ApplyUpdates(tenantId, sanitized, tenantStore, authConfigStore, quotaStore, retentionStore,
            tierCache, featureGateCache, addOnStore, brandingStore, ct,
            context.RequestServices, actorName);
        if (error is not null)
            return error;

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore,
            addOnStore, dunningStore, featureGateService, brandingStore, ct);
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
        ITenantBrandingStore? brandingStore,
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

        // Load branding with 3-tier inheritance: Customer → Partner → Platform defaults
        BrandingSettingsDto? brandingDto = null;
        if (brandingStore is not null)
        {
            var branding = await brandingStore.GetAsync(tenantId, ct);

            TenantBranding? parentBranding = null;
            if (tenant.ParentTenantId is not null)
                parentBranding = await brandingStore.GetAsync(tenant.ParentTenantId, ct);

            brandingDto = new BrandingSettingsDto(
                DisplayName: branding?.DisplayName ?? parentBranding?.DisplayName,
                LogoUrl: branding?.LogoUrl ?? parentBranding?.LogoUrl,
                FaviconUrl: branding?.FaviconUrl ?? parentBranding?.FaviconUrl,
                PrimaryColor: branding?.PrimaryColor ?? parentBranding?.PrimaryColor,
                SecondaryColor: branding?.SecondaryColor ?? parentBranding?.SecondaryColor,
                AccentColor: branding?.AccentColor ?? parentBranding?.AccentColor,
                Locale: branding?.Locale ?? parentBranding?.Locale,
                Timezone: branding?.Timezone ?? parentBranding?.Timezone,
                Subdomain: branding?.Subdomain, // Subdomain does NOT inherit
                SupportEmail: branding?.SupportEmail ?? parentBranding?.SupportEmail,
                SupportUrl: branding?.SupportUrl ?? parentBranding?.SupportUrl,
                EmailFromName: branding?.EmailFromName ?? parentBranding?.EmailFromName,
                EmailFromAddress: branding?.EmailFromAddress ?? parentBranding?.EmailFromAddress);
        }

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
                AllowedDialingModes: tenant.Options.AllowedDialingModes,
                OutboundCallerId: tenant.Options.OutboundCallerId,
                // W6-A7 — per-tenant default channel capacity lives on the auth config.
                MaxVoiceDefault: auth.MaxVoiceDefault,
                MaxChatDefault: auth.MaxChatDefault,
                MaxEmailDefault: auth.MaxEmailDefault,
                MaxSmsDefault: auth.MaxSmsDefault,
                MaxTotalDefault: auth.MaxTotalDefault),
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
                OidcDefaultRole: auth.OidcDefaultRole,
                ImpersonationMaxConcurrentSessions: auth.ImpersonationMaxConcurrentSessions,
                ImpersonationAutoTimeoutMinutes: auth.ImpersonationAutoTimeoutMinutes,
                IpAllowlistEnabled: auth.IpAllowlistEnabled),
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
                IsPaused: dunning.IsPaused),
            Branding: brandingDto);
    }

    internal static async Task<IResult?> ApplyUpdates(
        string tenantId,
        UpdateTenantSettingsRequest body,
        ITenantStore tenantStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantQuotaStore quotaStore,
        ITenantRetentionPolicyStore retentionStore,
        TenantTierCache? tierCache,
        FeatureGateCache? featureGateCache,
        ITenantAddOnStore? addOnStore,
        ITenantBrandingStore? brandingStore,
        CancellationToken ct,
        IServiceProvider? sp = null,
        string actorName = "unknown")
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
                            return null; // Silently skip — validation should be done at endpoint level
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
                        OutboundCallerId = op?.OutboundCallerId ?? tenant.Options.OutboundCallerId,
                    },
                    Metadata = newMetadata,
                    CreatedAt = tenant.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                await tenantStore.UpsertAsync(updated, ct);

                if (body.RateLimitTier is { } newTier && tierCache is not null)
                    tierCache.SetTier(tenantId, newTier);

                // Invalidate feature cache so the next request re-resolves features from the new plan/add-ons
                if (body.Plan is not null)
                    featureGateCache?.Remove(tenantId);
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
            // R5.2 PB.2 / C.7 — bound the impersonation knobs to sensible
            // ranges so a fat-fingered admin can't disable revocation by
            // setting timeout to int.MaxValue.
            if (a.ImpersonationMaxConcurrentSessions is { } mcs)
                auth.ImpersonationMaxConcurrentSessions = Math.Clamp(mcs, 0, 100);
            if (a.ImpersonationAutoTimeoutMinutes is { } at)
                auth.ImpersonationAutoTimeoutMinutes = Math.Clamp(at, 0, 7 * 24 * 60); // ≤ 7 days

            // §4.1 IP allowlist toggle — cannot enable an empty allowlist
            if (a.IpAllowlistEnabled is { } ipAllowlistEnabled)
            {
                var ipAllowlistStore = sp?.GetService(typeof(ITenantIpAllowlistStore)) as ITenantIpAllowlistStore;
                if (ipAllowlistEnabled && ipAllowlistStore is not null)
                {
                    var count = await ipAllowlistStore.CountAsync(tenantId, ct);
                    if (count == 0)
                        return Results.BadRequest(new ErrorResponse("ip_allowlist_enable_requires_entries"));
                }
                auth.IpAllowlistEnabled = ipAllowlistEnabled;
                var audit = sp?.GetService(typeof(IAuditService)) as IAuditService;
                if (audit is not null)
                {
                    await audit.RecordAsync(
                        tenantId: new TenantId(tenantId),
                        category: "auth",
                        action: ipAllowlistEnabled ? "auth.ip_allowlist.enabled" : "auth.ip_allowlist.disabled",
                        severity: ipAllowlistEnabled ? "info" : "warning",
                        actorId: actorName,
                        actorType: "user",
                        ct: ct);
                }
            }

            auth.UpdatedAt = DateTimeOffset.UtcNow;
            await authConfigStore.SaveAsync(auth, ct);
        }

        // W6-A7 — per-tenant default channel capacity. Carried in the Operational
        // DTO (UI surface) but persisted on the auth config (the resolver's
        // tenant-defaults source of truth). Saving via authConfigStore.SaveAsync
        // goes through CachedTenantAuthConfigStore, which evicts the hot-path cache
        // so the capacity resolver/provider observes the new defaults on next read.
        var capOp = body.Operational;
        if (capOp is not null && (capOp.MaxVoiceDefault is not null
            || capOp.MaxChatDefault is not null || capOp.MaxEmailDefault is not null
            || capOp.MaxSmsDefault is not null || capOp.MaxTotalDefault is not null))
        {
            // Validate BEFORE persisting (mirror A6 style: 400 + plain string).
            // MaxVoice is pinned to 1 until the W5b ARI mixing-bridge lands.
            if (capOp.MaxVoiceDefault is { } voiceDefault && voiceDefault != 1)
                return Results.BadRequest(
                    "MaxVoiceDefault must equal 1. Concurrent voice is a single exclusive lane until the ARI mixing-bridge lands.");

            if (CapacityRangeError(capOp.MaxChatDefault, nameof(capOp.MaxChatDefault)) is { } chatErr)
                return Results.BadRequest(chatErr);
            if (CapacityRangeError(capOp.MaxEmailDefault, nameof(capOp.MaxEmailDefault)) is { } emailErr)
                return Results.BadRequest(emailErr);
            if (CapacityRangeError(capOp.MaxSmsDefault, nameof(capOp.MaxSmsDefault)) is { } smsErr)
                return Results.BadRequest(smsErr);
            if (CapacityRangeError(capOp.MaxTotalDefault, nameof(capOp.MaxTotalDefault)) is { } totalErr)
                return Results.BadRequest(totalErr);

            var cap = await authConfigStore.GetAsync(tenantId, ct)
                   ?? new TenantAuthConfig { TenantId = tenantId };

            var oldVoice = cap.MaxVoiceDefault;
            var oldChat = cap.MaxChatDefault;
            var oldEmail = cap.MaxEmailDefault;
            var oldSms = cap.MaxSmsDefault;
            var oldTotal = cap.MaxTotalDefault;

            if (capOp.MaxVoiceDefault is { } nv) cap.MaxVoiceDefault = nv;
            if (capOp.MaxChatDefault is { } nc) cap.MaxChatDefault = nc;
            if (capOp.MaxEmailDefault is { } ne) cap.MaxEmailDefault = ne;
            if (capOp.MaxSmsDefault is { } ns) cap.MaxSmsDefault = ns;
            if (capOp.MaxTotalDefault is { } nt) cap.MaxTotalDefault = nt;

            var changed = cap.MaxVoiceDefault != oldVoice
                || cap.MaxChatDefault != oldChat
                || cap.MaxEmailDefault != oldEmail
                || cap.MaxSmsDefault != oldSms
                || cap.MaxTotalDefault != oldTotal;

            if (changed)
            {
                cap.UpdatedAt = DateTimeOffset.UtcNow;
                await authConfigStore.SaveAsync(cap, ct);

                // Best-effort audit (mirror A6/ForceAgentOffline: a failed audit
                // write must NEVER fail the operator's settings update).
                var audit = sp?.GetService(typeof(IAuditService)) as IAuditService;
                if (audit is not null)
                {
                    try
                    {
                        await audit.RecordAsync(
                            tenantId: new TenantId(tenantId),
                            category: "operational",
                            action: "tenant.capacity_default_changed",
                            severity: "info",
                            actorId: actorName,
                            actorType: "user",
                            targetId: tenantId,
                            targetType: "Tenant",
                            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["actor"] = actorName,
                                ["tenant_id"] = tenantId,
                                ["old_max_voice_default"] = oldVoice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["old_max_chat_default"] = oldChat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["old_max_email_default"] = oldEmail.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["old_max_sms_default"] = oldSms.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["old_max_total_default"] = oldTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["new_max_voice_default"] = cap.MaxVoiceDefault.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["new_max_chat_default"] = cap.MaxChatDefault.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["new_max_email_default"] = cap.MaxEmailDefault.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["new_max_sms_default"] = cap.MaxSmsDefault.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["new_max_total_default"] = cap.MaxTotalDefault.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            },
                            ct: ct);
                    }
                    catch
                    {
                        // Swallow — the capacity-default write already succeeded; audit is advisory.
                    }
                }
            }
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

        if (body.Branding is not null && brandingStore is not null)
        {
            var existing = await brandingStore.GetAsync(tenantId, ct)
                        ?? new TenantBranding { TenantId = tenantId };

            var b = body.Branding;
            if (b.DisplayName is not null) existing.DisplayName = b.DisplayName;
            if (b.LogoUrl is not null) existing.LogoUrl = b.LogoUrl;
            if (b.FaviconUrl is not null) existing.FaviconUrl = b.FaviconUrl;
            if (b.PrimaryColor is not null) existing.PrimaryColor = b.PrimaryColor;
            if (b.SecondaryColor is not null) existing.SecondaryColor = b.SecondaryColor;
            if (b.AccentColor is not null) existing.AccentColor = b.AccentColor;
            if (b.Locale is not null) existing.Locale = b.Locale;
            if (b.Timezone is not null) existing.Timezone = b.Timezone;
            if (b.SupportEmail is not null) existing.SupportEmail = b.SupportEmail;
            if (b.SupportUrl is not null) existing.SupportUrl = b.SupportUrl;
            if (b.EmailFromName is not null) existing.EmailFromName = b.EmailFromName;
            if (b.EmailFromAddress is not null) existing.EmailFromAddress = b.EmailFromAddress;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await brandingStore.UpsertAsync(existing, ct);
        }

        // Invalidate feature cache after plan/add-on changes
        if (body.Plan is not null || body.AddOns is not null)
            featureGateCache?.Remove(tenantId);

        return null;
    }

    // W6-A7 — per-channel default capacity range (mirrors AdminEndpoints' agent-override bound):
    // null (leave unchanged) or [0, MaxCapacityDefaultValue].
    private const int MaxCapacityDefaultValue = 50;

    private static string? CapacityRangeError(int? value, string field) =>
        value is { } x && (x < 0 || x > MaxCapacityDefaultValue)
            ? $"{field} must be between 0 and {MaxCapacityDefaultValue}."
            : null;

    /// <summary>
    /// Applies management branding updates including Subdomain (PlatformAdminOnly).
    /// </summary>
    internal static async Task ApplyManagementBrandingUpdates(
        string tenantId,
        UpdateManagementBrandingSettingsDto branding,
        ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var existing = await brandingStore.GetAsync(tenantId, ct)
                    ?? new TenantBranding { TenantId = tenantId };

        if (branding.DisplayName is not null) existing.DisplayName = branding.DisplayName;
        if (branding.LogoUrl is not null) existing.LogoUrl = branding.LogoUrl;
        if (branding.FaviconUrl is not null) existing.FaviconUrl = branding.FaviconUrl;
        if (branding.PrimaryColor is not null) existing.PrimaryColor = branding.PrimaryColor;
        if (branding.SecondaryColor is not null) existing.SecondaryColor = branding.SecondaryColor;
        if (branding.AccentColor is not null) existing.AccentColor = branding.AccentColor;
        if (branding.Locale is not null) existing.Locale = branding.Locale;
        if (branding.Timezone is not null) existing.Timezone = branding.Timezone;
        if (branding.Subdomain is not null) existing.Subdomain = branding.Subdomain;
        if (branding.SupportEmail is not null) existing.SupportEmail = branding.SupportEmail;
        if (branding.SupportUrl is not null) existing.SupportUrl = branding.SupportUrl;
        if (branding.EmailFromName is not null) existing.EmailFromName = branding.EmailFromName;
        if (branding.EmailFromAddress is not null) existing.EmailFromAddress = branding.EmailFromAddress;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await brandingStore.UpsertAsync(existing, ct);
    }
}
