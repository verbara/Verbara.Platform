# Sprint 1: Suspension Enforcement + TenantSettings Facade

**Date:** 2026-04-07
**Version:** v1.4.0 Sprint 1
**Scope:** Platform only (no Sdk.Pro changes)
**Depends on:** Sprint 0 security fixes (COMPLETE)

## Problem

Two foundational gaps block multi-tenant production:

1. **No suspension enforcement.** `TenantStatus.Suspended` exists in the enum and can be set via `POST /management/tenants/{id}/suspend`, but suspended tenants continue making API calls normally. `ITenantLifecycleHandler` exists in Sdk.Pro with `OnCreated/OnSuspended/OnDeleted` methods but is NEVER invoked from Platform code. The only implementation (`RealtimeTenantLifecycleHandler`) cleans up Asterisk Realtime rows on suspend/delete — but never runs.

2. **No unified tenant settings.** Per-tenant configuration is scattered across 5 stores (`ITenantStore`, `ITenantAuthConfigStore`, `ITenantQuotaStore`, `ITenantRetentionPolicyStore`) with separate endpoints. Tenant admins must make 4+ API calls to view their full configuration. Additionally, `RateLimitTier` is hardcoded to `Standard` for all tenants — the middleware and enum exist but are disconnected from per-tenant storage.

## Deliverable 1: Tenant Suspension Enforcement

### 1.1 TenantStatusMiddleware

New middleware that runs after `TenantResolutionMiddleware` and `Authentication`:

```
ErrorHandling → CORS → RateLimiter → RateLimitHeaders → TenantResolution → Auth → TenantStatusMiddleware → ...endpoints
```

**Behavior:**
- If no TenantId in `HttpContext.Items["TenantId"]` → skip (management, health, setup endpoints)
- Load tenant from `ITenantStore.GetAsync(tenantId)`
- If tenant is `null` → skip (unauthenticated or external request)
- If `TenantStatus.Suspended` → return 403:
  ```json
  {"type": "tenant_suspended", "title": "Tenant Suspended", "detail": "This tenant account has been suspended. Contact your administrator."}
  ```
- If `TenantStatus.Deleted` → return 404:
  ```json
  {"type": "tenant_not_found", "title": "Not Found"}
  ```
- If `TenantStatus.Active` → store `Tenant` in `HttpContext.Items["Tenant"]` for downstream use
- Also populate `TenantTierCache` with the tenant's `RateLimitTier` (see §2.3)

**Bypass:** Platform tenant (host) is never blocked by this middleware — Platform admins use management endpoints which don't resolve a tenant through this path.

### 1.2 Lifecycle Handler Dispatch

Modify `ManagementTenantEndpoints` to invoke `IEnumerable<ITenantLifecycleHandler>` after status changes:

| Endpoint | After status change | Handler call |
|----------|-------------------|--------------|
| `CreateTenant` | After `store.UpsertAsync()` | `OnTenantCreatedAsync(tenant)` |
| `SuspendTenant` | After `store.UpdateStatusAsync()` | `OnTenantSuspendedAsync(tenantId)` |
| `DeleteTenant` | After `store.UpdateStatusAsync()` | `OnTenantDeletedAsync(tenantId)` |
| `ActivateTenant` | After `store.UpdateStatusAsync()` | None (interface has no `OnActivated`) |

**Error handling:** Each handler call is wrapped in try/catch. Handler failures are logged as warnings but do NOT block the management operation. The status change is already persisted before handlers run.

```csharp
// In SuspendTenant, after store.UpdateStatusAsync:
foreach (var handler in lifecycleHandlers)
{
    try
    {
        await handler.OnTenantSuspendedAsync(id, ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Lifecycle handler {Handler} failed for tenant {TenantId} suspension",
            handler.GetType().Name, id);
    }
}
```

### 1.3 Changes

**Files:**
- Create: `src/Verbara.Platform.Api/Middleware/TenantStatusMiddleware.cs`
- Modify: `src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs` — inject + invoke lifecycle handlers
- Modify: `src/Verbara.Platform.Api/Program.cs` — register middleware after auth

## Deliverable 2: TenantSettings Facade

### 2.1 Facade Endpoints

Two endpoint groups with different authorization levels:

**Tenant-scoped (AdminOnly):**
- `GET /api/v1/admin/tenant/settings` — read own tenant's aggregated settings
- `PUT /api/v1/admin/tenant/settings` — update own tenant's settings (CANNOT write `quotas` or `rateLimitTier`)

**Platform-scoped (PlatformAdminOnly):**
- `GET /api/v1/management/tenants/{id}/settings` — read any tenant's settings
- `PUT /api/v1/management/tenants/{id}/settings` — update any tenant's settings (ALL sections writable)

### 2.2 Unified DTO

```csharp
internal sealed record TenantSettingsDto(
    string TenantId,
    string Name,
    TenantType Type,
    TenantStatus Status,
    OperationalSettingsDto Operational,
    AuthSettingsDto Auth,
    QuotaSettingsDto Quotas,
    RetentionSettingsDto Retention,
    RateLimitTier RateLimitTier);

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
```

**Update request (partial — null sections are skipped):**

```csharp
internal sealed record UpdateTenantSettingsRequest(
    OperationalSettingsDto? Operational,
    AuthSettingsDto? Auth,
    QuotaSettingsDto? Quotas,
    RetentionSettingsDto? Retention,
    RateLimitTier? RateLimitTier);
```

### 2.3 RateLimitTier Per-Tenant

**Storage:** `Tenant.Metadata["RateLimitTier"]` — uses the existing `Dictionary<string, string>?` on the Tenant class. No new tables or Sdk.Pro changes.

**Read helper** (in Platform.Core):
```csharp
public static class TenantExtensions
{
    public static RateLimitTier GetRateLimitTier(this Tenant tenant)
        => tenant.Metadata?.GetValueOrDefault("RateLimitTier") is string s
            && Enum.TryParse<RateLimitTier>(s, out var tier) ? tier : RateLimitTier.Standard;
}
```

**TenantTierCache** (singleton):
```csharp
public sealed class TenantTierCache
{
    private readonly ConcurrentDictionary<string, RateLimitTier> _cache = new();

    public RateLimitTier GetTier(string tenantId)
        => _cache.GetValueOrDefault(tenantId, RateLimitTier.Standard);

    public void SetTier(string tenantId, RateLimitTier tier)
        => _cache[tenantId] = tier;

    public void Remove(string tenantId)
        => _cache.TryRemove(tenantId, out _);
}
```

**Wiring:**
- `TenantStatusMiddleware` loads the tenant on each request → calls `cache.SetTier(tenantId, tenant.GetRateLimitTier())`
- `TenantRateLimitPolicy` reads from `TenantTierCache.GetTier(tenantId)` (sync, fast)
- `RateLimitHeadersMiddleware` reads from `TenantTierCache.GetTier(tenantId)`
- First request from a new tenant uses `Standard` (cache miss), subsequent requests use correct tier
- When facade updates tier → writes to `Tenant.Metadata` + updates cache

### 2.4 Facade Read Flow

```
GET /admin/tenant/settings
  → resolve tenantId from claims
  → Task.WhenAll(
      tenantStore.GetAsync(tenantId),
      authConfigStore.GetAsync(tenantId),
      quotaStore.GetAsync(tenantId),
      retentionStore.GetAsync(tenantId))
  → map to TenantSettingsDto
  → return 200
```

### 2.5 Facade Write Flow

```
PUT /admin/tenant/settings
  → resolve tenantId from claims
  → for each non-null section in request body:
      if Operational → read tenant, update Options, upsert tenant
      if Auth → map to TenantAuthConfig, save
      if Quotas → SKIP (AdminOnly cannot write quotas)
      if Retention → map to TenantRetentionPolicy, save
      if RateLimitTier → SKIP (AdminOnly cannot write tier)
  → return 200 with updated TenantSettingsDto

PUT /management/tenants/{id}/settings (PlatformAdminOnly)
  → same as above but ALL sections writable:
      if Quotas → map to TenantQuota, upsert
      if RateLimitTier → update Tenant.Metadata, upsert tenant, update cache
```

### 2.6 Changes

**Files:**
- Create: `src/Verbara.Platform.Core/TenantExtensions.cs` — `GetRateLimitTier()` extension
- Create: `src/Verbara.Platform.Api/Services/TenantTierCache.cs`
- Create: `src/Verbara.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` — AdminOnly facade
- Create: `src/Verbara.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs` — PlatformAdminOnly facade
- Modify: `src/Verbara.Platform.Api/Middleware/TenantRateLimitPolicy.cs` — read from TenantTierCache
- Modify: `src/Verbara.Platform.Api/Middleware/RateLimitHeadersMiddleware.cs` — read from TenantTierCache
- Modify: `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — register new DTOs
- Modify: `src/Verbara.Platform.Api/Program.cs` — register TenantTierCache, map new endpoint groups

## Non-Goals

- **No new TenantStatus values** (Warning, Degraded, PendingDeletion) — Sprint 2 with billing dunning
- **No cascading suspension** (parent suspended → children auto-suspended) — Sprint 2
- **No automatic suspension triggers** — Sprint 2 with billing lifecycle
- **No channel configs in facade** — per-channel, already well-served by existing `/admin/channels/{channel}` endpoints
- **No Sdk.Pro changes** — all work in Platform
- **No OnTenantActivatedAsync** — interface doesn't have it; Realtime reconciler handles re-provisioning
- **No persistent tier storage migration** — uses existing Tenant.Metadata JSONB field

## Testing

### Unit Tests (new)

**TenantStatusMiddleware:**
- `Invoke_ShouldPassThrough_WhenNoTenantIdResolved`
- `Invoke_ShouldReturn403_WhenTenantSuspended`
- `Invoke_ShouldReturn404_WhenTenantDeleted`
- `Invoke_ShouldPassThrough_WhenTenantActive`
- `Invoke_ShouldPopulateTenantTierCache_WhenTenantActive`

**Lifecycle Dispatch (in ManagementTenantEndpoints tests):**
- `CreateTenant_ShouldInvokeOnTenantCreatedAsync`
- `SuspendTenant_ShouldInvokeOnTenantSuspendedAsync`
- `DeleteTenant_ShouldInvokeOnTenantDeletedAsync`
- `SuspendTenant_ShouldSucceed_WhenLifecycleHandlerThrows`

**TenantSettingsEndpoints:**
- `GetSettings_ShouldAggregateAllStores`
- `GetSettings_ShouldReturnDefaults_WhenStoresEmpty`
- `UpdateSettings_ShouldUpdateOnlyProvidedSections`
- `UpdateSettings_ShouldIgnoreQuotas_WhenAdminOnly`
- `UpdateSettings_ShouldUpdateQuotas_WhenPlatformAdmin`

**ManagementTenantSettingsEndpoints:**
- `GetSettings_ShouldReturnSettingsForAnyTenant`
- `UpdateSettings_ShouldUpdateAllSections`
- `UpdateSettings_ShouldUpdateRateLimitTier`

**RateLimitTier wiring:**
- `GetRateLimitTier_ShouldReturnStandard_WhenMetadataNull`
- `GetRateLimitTier_ShouldReturnTier_WhenMetadataSet`
- `TenantTierCache_ShouldReturnStandard_WhenNotCached`

### Build Verification

```sh
cd ../Verbara.Platform
dotnet build Verbara.Platform.slnx && dotnet test Verbara.Platform.slnx
```

## Repo Impact

| Repo | Changes | Version Bump |
|------|---------|-------------|
| Platform | ~12 files (4 new, 8 modified) | Stays 1.3.1 (Sprint 1 patch) |
| Sdk.Pro | None | None |
| Sdk (MIT) | None | None |
| Platform.Web | None (Sprint 1 is backend-only) | None |
