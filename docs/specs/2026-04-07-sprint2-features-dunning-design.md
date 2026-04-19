# Sprint 2: Feature Flags Per-Tenant + Billing-Lifecycle Dunning

**Date:** 2026-04-07
**Version:** v1.4.0 Sprint 2
**Scope:** Sdk.Pro (1 enum change) + Platform (all new code)
**Depends on:** Sprint 1 (suspension enforcement + TenantSettings facade — COMPLETE)

## Problem

Two gaps block multi-tenant monetization:

1. **No feature gating.** All tenants have access to all platform features regardless of pricing tier. `RateLimitTier` controls request throughput but nothing else. There is no concept of "plans" (Starter/Pro/Enterprise), no add-ons, and no way to restrict features like Dialer, Bot, OIDC SSO, or Recordings based on what a tenant pays for.

2. **No billing lifecycle.** When a tenant doesn't pay, the only recourse is manual suspension via `POST /management/tenants/{id}/suspend`. There is no automatic progression from overdue → warning → degradation → suspension → deletion. Invoices have `InvoiceStatus` (Draft/Issued/Paid/Void) but no `PaymentStatus` tracking. `TenantStatus` only has Active/Suspended/Deleted — no intermediate states for gradual enforcement.

## Approved Decisions

| Decision | Choice |
|----------|--------|
| Plan as source of truth | Plan defines rate limit tier + features + quotas. RateLimitTier override still possible by platform admin. |
| Add-ons model | On/off only. Add-on activates a feature regardless of plan. No per-add-on quotas (YAGNI). |
| Hierarchical inheritance | Ceiling of parent. Customer cannot have plan higher than its Partner parent. Enforcement automatic. |
| Dunning progression | Automatic by time: Warning (day 0) → Degraded (day 7) → Suspended (day 14) → PendingDeletion (day 30). |
| Feature categories | 13 flags across 11 categories (Channels, Dialer, Bot, Analytics, Flows, Webhooks, OIDC, Audit, Reports, KB, Recordings). |

## Deliverable 1: Tenant Plans + Feature Flags

### 1.1 TenantPlan Enum (Platform.Core)

```csharp
public enum TenantPlan
{
    Starter = 0,
    Pro = 1,
    Enterprise = 2
}
```

Stored in `Tenant.Metadata["Plan"]` (same pattern as `RateLimitTier`). Default: `Starter` when not set.

### 1.2 PlanFeature Enum (Platform.Core)

```csharp
public enum PlanFeature
{
    Dialer,
    BotBasic,
    BotAdvanced,
    AgentAssist,
    CallAnalytics,
    AnalyticsExport,
    Flows,
    Webhooks,
    OidcSso,
    ScheduledReports,
    KnowledgeBase,
    Recordings,
    RecordingTranscription
}
```

### 1.3 PlanDefinition (Platform.Core)

Static class that maps each `TenantPlan` to its capabilities:

```csharp
public static class PlanDefinition
{
    public static IReadOnlySet<PlanFeature> GetFeatures(TenantPlan plan);
    public static RateLimitTier GetDefaultTier(TenantPlan plan);
    public static int GetMaxChannels(TenantPlan plan);
    public static int GetAuditRetentionDays(TenantPlan plan);
    public static int GetMaxWebhookSubscriptions(TenantPlan plan);
    public static int GetMaxScheduledReports(TenantPlan plan);
}
```

**Plan matrix:**

| Capability | Starter | Pro | Enterprise |
|-----------|---------|-----|------------|
| Rate Limit Tier | Standard (300/min) | Professional (600/min) | Enterprise (1200/min) |
| Max Channels | 3 | 7 | 11 (all) |
| Features | (none) | Dialer, BotBasic, Flows, Webhooks, KnowledgeBase, Recordings, AnalyticsExport, ScheduledReports | All 13 features |
| Audit Retention | 7 days | 30 days | 90 days |
| Max Webhook Subs | 0 | 5 | int.MaxValue |
| Max Scheduled Reports | 0 | 5 | int.MaxValue |

### 1.4 TenantExtensions (Platform.Core — modify existing)

Add helper methods alongside existing `GetRateLimitTier()` / `SetRateLimitTier()`:

```csharp
public static TenantPlan GetPlan(this Tenant tenant)
    => tenant.Metadata?.GetValueOrDefault("Plan") is string s
        && Enum.TryParse<TenantPlan>(s, out var plan) ? plan : TenantPlan.Starter;

public static void SetPlan(this Tenant tenant, TenantPlan plan) { ... }
```

### 1.5 TenantAddOn Record (Platform.Core)

```csharp
public sealed class TenantAddOn
{
    public string TenantId { get; init; } = default!;
    public PlanFeature Feature { get; init; }
    public DateTimeOffset EnabledAt { get; init; }
}
```

### 1.6 ITenantAddOnStore (Platform.Core)

```csharp
public interface ITenantAddOnStore
{
    Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default);
}
```

### 1.7 IFeatureGateService (Platform.Core)

```csharp
public interface IFeatureGateService
{
    bool IsFeatureEnabled(string tenantId, PlanFeature feature);
    IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId);
    int GetMaxChannels(string tenantId);
    int GetAuditRetentionDays(string tenantId);
    int GetMaxWebhookSubscriptions(string tenantId);
    int GetMaxScheduledReports(string tenantId);
}
```

### 1.8 FeatureGateCache + DefaultFeatureGateService (Platform.Api)

**FeatureGateCache** (singleton):

```csharp
internal sealed class FeatureGateCache
{
    private readonly ConcurrentDictionary<string, ResolvedFeatures> _cache = new();

    public ResolvedFeatures? Get(string tenantId) => _cache.GetValueOrDefault(tenantId);
    public void Set(string tenantId, ResolvedFeatures features) => _cache[tenantId] = features;
    public void Remove(string tenantId) => _cache.TryRemove(tenantId, out _);
}

internal sealed record ResolvedFeatures(
    TenantPlan EffectivePlan,
    IReadOnlySet<PlanFeature> Features,
    int MaxChannels,
    int AuditRetentionDays,
    int MaxWebhookSubscriptions,
    int MaxScheduledReports);
```

**DefaultFeatureGateService** reads from `FeatureGateCache` for sync access. Cache is populated by `TenantStatusMiddleware` on each request.

**Resolution logic (in TenantStatusMiddleware):**

```
1. Read tenant.GetPlan() → base features from PlanDefinition
2. Load add-ons from ITenantAddOnStore → union with base features
3. If tenant.Status == Degraded → force to Starter features (ignore plan + add-ons)
4. Hierarchy ceiling: if tenant has parent, load parent.GetPlan()
   → intersect child features with parent plan features
5. Store in FeatureGateCache
```

### 1.9 RequirePlanFeature Endpoint Filter (Platform.Api)

```csharp
internal static class PlanFeatureFilterExtensions
{
    public static RouteGroupBuilder RequirePlanFeature(
        this RouteGroupBuilder group, PlanFeature feature) { ... }
}
```

**Behavior:** Reads `FeatureGateCache` for the resolved tenant. If feature not in enabled set → 403:

```json
{
    "type": "feature_not_available",
    "title": "Feature Not Available",
    "detail": "This feature requires Pro plan or higher. Current plan: Starter"
}
```

**Applied to these endpoint groups:**

| Endpoint Group | Required Feature |
|---------------|-----------------|
| CampaignEndpoints | Dialer |
| CallAttemptEndpoints | Dialer |
| DncListEndpoints | Dialer |
| CallerIdPoolEndpoints | Dialer |
| HolidayCalendarEndpoints | Dialer |
| DialerSettingsEndpoints | Dialer |
| BotEndpoints | BotBasic |
| AgentAssistEndpoints | AgentAssist |
| FlowEndpoints | Flows |
| WebhookSubscriptionEndpoints | Webhooks |
| OidcEndpoints | OidcSso |
| ScheduledReportEndpoints | ScheduledReports |
| KnowledgeBaseEndpoints | KnowledgeBase |
| RecordingEndpoints | Recordings |

Platform tenant (host) bypasses all feature gates — same pattern as `RequireLicenseFeature`.

### 1.10 Hierarchical Inheritance Enforcement

When assigning a plan to a Customer tenant via management settings:

```
1. Load parent tenant (via ParentTenantId)
2. If parent exists and parent.GetPlan() < requested plan → reject with 400:
   "Cannot assign Enterprise plan to tenant under a Pro partner"
3. If parent plan is downgraded later → children are NOT auto-downgraded
   (logged as warning, platform admin resolves manually — auto-cascade deferred to Sprint 3+)
```

### 1.11 Changes

**Files:**
- **Sdk.Pro** (no changes for feature flags — only for dunning, see §2)
- Create: `src/Asterisk.Platform.Core/TenantPlan.cs`
- Create: `src/Asterisk.Platform.Core/PlanFeature.cs`
- Create: `src/Asterisk.Platform.Core/PlanDefinition.cs`
- Create: `src/Asterisk.Platform.Core/TenantAddOn.cs`
- Create: `src/Asterisk.Platform.Core/ITenantAddOnStore.cs`
- Create: `src/Asterisk.Platform.Core/IFeatureGateService.cs`
- Modify: `src/Asterisk.Platform.Core/TenantExtensions.cs` — add GetPlan/SetPlan
- Create: `src/Asterisk.Platform.Api/Services/FeatureGateCache.cs`
- Create: `src/Asterisk.Platform.Api/Services/DefaultFeatureGateService.cs`
- Create: `src/Asterisk.Platform.Api/Filters/PlanFeatureFilterExtensions.cs`
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs` — populate FeatureGateCache
- Modify: `src/Asterisk.Platform.Api/Program.cs` — register services, apply filters to endpoint groups
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAddOnStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

## Deliverable 2: Billing-Lifecycle Dunning

### 2.1 New TenantStatus Values (Sdk.Pro)

```csharp
public enum TenantStatus
{
    Active = 0,
    Warning = 1,
    Degraded = 2,
    Suspended = 3,
    PendingDeletion = 4,
    Deleted = 5
}
```

This is the ONLY change in Sdk.Pro. Requires pack + NuGet feed update.

### 2.2 PaymentStatus Enum (Platform.Billing)

```csharp
public enum PaymentStatus
{
    Current,
    Overdue,
    Delinquent,
    WrittenOff
}
```

Added as properties to `Invoice`:

```csharp
public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Current;
public DateTimeOffset? DueDate { get; set; }
```

- `Current` — paid or not yet due
- `Overdue` — past DueDate, dunning started
- `Delinquent` — 14+ days, tenant suspended
- `WrittenOff` — 30+ days, no recovery expected

### 2.3 DunningConfig (Platform.Billing)

```csharp
public sealed class DunningConfig
{
    public int WarningDays { get; init; } = 0;
    public int DegradedDays { get; init; } = 7;
    public int SuspendedDays { get; init; } = 14;
    public int PendingDeletionDays { get; init; } = 30;
    public int CheckIntervalHours { get; init; } = 6;
}
```

Registered as `IOptions<DunningConfig>`. Platform admin can adjust via `appsettings.json` or environment variables.

### 2.4 DunningRecord (Platform.Billing)

```csharp
public sealed class DunningRecord
{
    public string DunningId { get; init; } = default!;
    public string TenantId { get; init; } = default!;
    public string InvoiceId { get; init; } = default!;
    public TenantStatus CurrentStage { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool IsPaused { get; set; }
    public bool IsActive { get; set; } = true;
}
```

### 2.5 IDunningStore (Platform.Billing)

```csharp
public interface IDunningStore
{
    Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default);
    Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default);
    Task UpsertAsync(DunningRecord record, CancellationToken ct = default);
}
```

### 2.6 DunningService (Platform.Billing — IHostedService)

Background service that runs every `DunningConfig.CheckIntervalHours`:

**Phase 1 — Detect new overdue invoices:**
```
For each invoice with Status=Issued and DueDate < now and no active DunningRecord:
  1. Create DunningRecord (stage=Warning, StartedAt=DueDate)
  2. Invoice.PaymentStatus → Overdue
  3. Tenant.Status → Warning (via ITenantStore.UpdateStatusAsync)
  4. Log warning event (no ITenantLifecycleHandler dispatch — interface only has OnCreated/OnSuspended/OnDeleted)
```

**Phase 2 — Escalate existing dunning records:**
```
For each active, non-paused DunningRecord:
  daysSinceStart = (now - record.StartedAt).TotalDays

  If daysSinceStart >= PendingDeletionDays and stage < PendingDeletion:
    → stage = PendingDeletion, tenant.Status = PendingDeletion
    → Invoice.PaymentStatus = WrittenOff
  Else if daysSinceStart >= SuspendedDays and stage < Suspended:
    → stage = Suspended, tenant.Status = Suspended
    → Invoice.PaymentStatus = Delinquent
    → Dispatch ITenantLifecycleHandler.OnTenantSuspendedAsync (cleans up Realtime rows)
  Else if daysSinceStart >= DegradedDays and stage < Degraded:
    → stage = Degraded, tenant.Status = Degraded
```

**Error handling:** Each tenant escalation is wrapped in try/catch. One tenant failure does not block others.

### 2.7 Dunning Resolution

When `ManagementBillingEndpoints` marks an invoice as `Paid`:

```
1. Invoice.Status → Paid
2. Invoice.PaymentStatus → Current
3. DunningRecord.IsActive → false, ResolvedAt → now
4. Tenant.Status → Active (via ITenantStore.UpdateStatusAsync)
5. FeatureGateCache.Remove(tenantId) — force re-resolution on next request
6. TenantTierCache.Remove(tenantId) — force re-resolution on next request
```

### 2.8 TenantStatusMiddleware Expansion

Current behavior (Sprint 1): Active → pass, Suspended → 403, Deleted → 404.

Expanded behavior:

| Status | HTTP | Headers | Feature Gate |
|--------|------|---------|-------------|
| Active | pass | (none) | Normal |
| Warning | pass | `X-Tenant-Warning: payment_overdue` | Normal |
| Degraded | pass | `X-Tenant-Warning: payment_overdue` | Forced to Starter |
| Suspended | 403 | — | Blocked |
| PendingDeletion | 403 | — | Blocked (different message) |
| Deleted | 404 | — | Blocked |

**403 for PendingDeletion:**
```json
{
    "type": "tenant_pending_deletion",
    "title": "Tenant Pending Deletion",
    "detail": "This tenant account is pending deletion due to prolonged non-payment. Contact your administrator immediately."
}
```

### 2.9 Management Dunning Endpoints (2 new)

Added to `ManagementBillingEndpoints`:

- `GET /api/v1/management/tenants/{id}/dunning` — returns active DunningRecord or 404
- `POST /api/v1/management/tenants/{id}/dunning/pause` — toggles `IsPaused` on active dunning (prevents escalation while admin negotiates)

### 2.10 Changes

**Files:**
- Modify: `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/TenantStatus.cs` — add Warning, Degraded, PendingDeletion
- Create: `src/Asterisk.Platform.Billing/PaymentStatus.cs`
- Create: `src/Asterisk.Platform.Billing/DunningConfig.cs`
- Create: `src/Asterisk.Platform.Billing/DunningRecord.cs`
- Create: `src/Asterisk.Platform.Billing/IDunningStore.cs`
- Create: `src/Asterisk.Platform.Billing/DunningService.cs`
- Modify: `src/Asterisk.Platform.Billing/Invoice.cs` — add PaymentStatus property + DueDate
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs` — handle Warning, Degraded, PendingDeletion
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` — dunning endpoints + invoice payment resolution
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryDunningStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` — register new DTOs
- Modify: `src/Asterisk.Platform.Api/Program.cs` — register DunningService, DunningConfig

## Deliverable 3: TenantSettings Facade Expansion

### 3.1 New Sections in TenantSettingsDto

```csharp
// Added to TenantSettingsDto
TenantPlan Plan,                              // read-only for AdminOnly
IReadOnlyList<PlanFeature> EnabledFeatures,   // calculated, read-only
IReadOnlyList<PlanFeature> AddOns,            // read-only for AdminOnly
DunningStatusDto? Dunning                     // read-only, null if no active dunning

// New DTO
internal sealed record DunningStatusDto(
    string InvoiceId,
    TenantStatus CurrentStage,
    DateTimeOffset StartedAt,
    DateTimeOffset? EscalatedAt,
    bool IsPaused);
```

### 3.2 New Sections in UpdateTenantSettingsRequest

```csharp
// Added to UpdateTenantSettingsRequest (PlatformAdminOnly only)
TenantPlan? Plan,
IReadOnlyList<PlanFeature>? AddOns
```

AdminOnly PUT strips `Plan` and `AddOns` (same pattern as Quotas/RateLimitTier).

### 3.3 Plan Assignment with Hierarchy Validation

When PlatformAdmin sets a plan via management settings PUT:

```
1. If tenant has ParentTenantId:
   a. Load parent tenant
   b. If parent.GetPlan() < requested plan → 400 "Cannot assign {plan} to tenant under a {parentPlan} partner"
2. tenant.SetPlan(plan)
3. Recalculate RateLimitTier (if no manual override exists):
   a. PlanDefinition.GetDefaultTier(plan) → TenantTierCache.SetTier()
4. FeatureGateCache.Remove(tenantId) — force re-resolution
5. Upsert tenant
```

### 3.4 Add-On Management

When PlatformAdmin sets add-ons via management settings PUT:

```
1. Compare requested list with current add-ons
2. For each new add-on → ITenantAddOnStore.UpsertAsync(new TenantAddOn { ... })
3. For each removed add-on → ITenantAddOnStore.DeleteAsync(tenantId, feature)
4. FeatureGateCache.Remove(tenantId)
```

## Non-Goals

- **No auto-cascade on parent plan downgrade** — platform admin resolves manually. Deferred to Sprint 3+ with Partner Portal.
- **No Partner plan catalog** — Partners can't define which plans they offer to Customers. Deferred to Sprint 3.
- **No add-ons with quotas** — add-ons are on/off only. Add per-add-on limits in Sprint 3+ if needed.
- **No Stripe/payment gateway** — dunning changes tenant status but doesn't charge cards. Deferred to v2.0.
- **No self-service plan upgrade** — only PlatformAdmin assigns plans. Self-service deferred to v2.0.
- **No notification delivery** — dunning changes status and headers. Email/in-app notifications for overdue tenants deferred to Sprint 4.
- **No Postgres dunning/add-on stores** — InMemory only in Sprint 2. Postgres in Docker stabilization sprint.
- **No Frontend changes** — Sprint 2 is backend-only.

## Future Roadmap (from Sprint 2 decisions)

- **Sprint 3+:** Partner plan catalog (Partner defines which plans/add-ons to sell)
- **Sprint 3+:** Add-ons with quotas (e.g., "Dialer Basic" = max 2 campaigns)
- **Sprint 3+:** Auto-cascade parent plan downgrade to children
- **Sprint 4:** Dunning notifications (email + in-app SSE)
- **v2.0:** Stripe payment gateway integration (dunning triggers real charges)
- **v2.0:** Self-service plan upgrade from frontend

## Testing

### Unit Tests

**TenantPlan + PlanDefinition:**
- `GetFeatures_ShouldReturnEmpty_WhenStarter`
- `GetFeatures_ShouldReturn8Features_WhenPro`
- `GetFeatures_ShouldReturn13Features_WhenEnterprise`
- `GetDefaultTier_ShouldReturnStandard_WhenStarter`
- `GetDefaultTier_ShouldReturnProfessional_WhenPro`
- `GetPlan_ShouldReturnStarter_WhenMetadataNull`
- `GetPlan_ShouldReturnPlan_WhenMetadataSet`

**FeatureGateService:**
- `IsFeatureEnabled_ShouldReturnFalse_WhenStarterAndDialer`
- `IsFeatureEnabled_ShouldReturnTrue_WhenProAndDialer`
- `IsFeatureEnabled_ShouldReturnTrue_WhenStarterWithAddOn`
- `IsFeatureEnabled_ShouldReturnFalse_WhenDegradedEvenIfEnterprise`
- `GetEnabledFeatures_ShouldIntersectWithParentPlan`

**RequirePlanFeature filter:**
- `Filter_ShouldReturn403_WhenFeatureNotEnabled`
- `Filter_ShouldPassThrough_WhenFeatureEnabled`
- `Filter_ShouldBypass_WhenPlatformTenant`

**TenantStatusMiddleware (expanded):**
- `Invoke_ShouldAddWarningHeader_WhenTenantWarning`
- `Invoke_ShouldAddWarningHeader_WhenTenantDegraded`
- `Invoke_ShouldReturn403_WhenTenantPendingDeletion`

**DunningService:**
- `Execute_ShouldCreateDunningRecord_WhenInvoiceOverdue`
- `Execute_ShouldEscalateToWarning_WhenNewOverdue`
- `Execute_ShouldEscalateToDegraded_WhenPast7Days`
- `Execute_ShouldEscalateToSuspended_WhenPast14Days`
- `Execute_ShouldEscalateToPendingDeletion_WhenPast30Days`
- `Execute_ShouldSkipPausedRecords`
- `Execute_ShouldNotBlockOnSingleTenantFailure`

**Dunning Resolution:**
- `PayInvoice_ShouldResolveDunning_WhenPaid`
- `PayInvoice_ShouldRestoreActiveStatus_WhenPaid`

**TenantSettings facade (expanded):**
- `GetSettings_ShouldIncludePlanAndFeatures`
- `UpdateSettings_ShouldRejectHigherPlanThanParent`
- `UpdateSettings_ShouldStripPlanAndAddOns_WhenAdminOnly`
- `UpdateSettings_ShouldUpdateAddOns_WhenPlatformAdmin`

### Build Verification

```sh
# Sdk.Pro (TenantStatus enum change)
cd /media/Data/Source/IPcom/Asterisk.Sdk.Pro
dotnet build && dotnet test
dotnet pack -c Release -o /media/Data/Source/IPcom/local-nuget-feed/

# Platform
cd /media/Data/Source/IPcom/Asterisk.Platform
rm -rf ~/.nuget/packages/asterisk.sdk.pro*
dotnet restore && dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx
```

## Repo Impact

| Repo | Changes | Version Bump |
|------|---------|-------------|
| Sdk.Pro | 1 file modified (TenantStatus.cs) | 1.1.1-pro → 1.1.2-pro |
| Platform | ~20 files (10 new, 10 modified) | Stays 1.3.1 |
| Sdk (MIT) | None | None |
| Platform.Web | None (backend-only) | None |
