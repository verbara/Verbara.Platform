# Sprint 0: Multi-Tenant Security Fixes

**Date:** 2026-04-07
**Version:** Pre-v1.4.0 (blocker)
**Scope:** Sdk.Pro (1 package) + Platform (8 packages)

## Problem

Five security vulnerabilities discovered during multi-tenant product analysis. All stem from the same root cause: the platform was built single-tenant-first and multi-tenant isolation was added at the storage layer (tenant_id filters) but not at the runtime/operational layer.

These must be fixed before any v1.4.0 feature work begins.

## Vulnerabilities

### V1: Cross-Tenant Analytics Live Data Leak (CRITICAL)

**Files:** `AnalyticsLiveEndpoints.cs`, `AnalyticsQueryService.cs` (Sdk.Pro), `LiveStateProvider.cs` (Sdk.Pro)

**Root cause:** `GetAllLiveStates()` returns metrics from all Asterisk queues across all servers. `LiveState` has no tenant metadata. Any authenticated supervisor sees all tenants' queue metrics.

**Fix:** Add overloads to `AnalyticsQueryService` and `LiveStateProvider` that accept `IReadOnlySet<string>? allowedQueues`. Platform endpoint loads the tenant's queue names from `IQueueStore` and passes them as filter. Sdk.Pro stays tenant-agnostic.

**Changes:**
- `Sdk.Pro.Analytics/AnalyticsQueryService.cs` — new overload `GetAllLiveStates(IReadOnlySet<string>? allowedQueues)`, `GetCurrentInterval(string queueName, IReadOnlySet<string>? allowedQueues)`
- `Sdk.Pro.Analytics/LiveStateProvider.cs` — new overload `GetAllLiveStates(IReadOnlySet<string>? allowedQueues)`, `GetLiveState(string queueName, IReadOnlySet<string>? allowedQueues)`
- `Platform.Api/Endpoints/AnalyticsLiveEndpoints.cs` — inject `IQueueStore`, load tenant queue names, pass to service
- Sdk.Pro version bump + NuGet pack

### V2: Recording Path Traversal (CRITICAL)

**File:** `RecordingEndpoints.cs`

**Root cause:** `Path.Combine(BasePath, recordingName + ext)` without sanitization. `recordingName` from DB could contain `../` sequences. No per-tenant directory isolation.

**Fix:** Three-layer defense:
1. `Path.GetFileName(recordingName)` — strip directory components
2. `Path.GetFullPath()` + `StartsWith()` — verify resolved path stays within allowed directory
3. Per-tenant subdirectory with legacy fallback

```csharp
static string? ResolveRecordingPath(string basePath, string tenantId, string recordingName, string ext)
{
    var safeName = Path.GetFileName(recordingName);
    if (string.IsNullOrEmpty(safeName)) return null;

    // Try tenant-isolated path first
    var tenantDir = Path.GetFullPath(Path.Combine(basePath, tenantId));
    var tenantPath = Path.GetFullPath(Path.Combine(tenantDir, safeName + ext));
    if (File.Exists(tenantPath) && tenantPath.StartsWith(tenantDir, StringComparison.Ordinal))
        return tenantPath;

    // Fallback to legacy flat structure (bounds-checked)
    var baseDir = Path.GetFullPath(basePath);
    var legacyPath = Path.GetFullPath(Path.Combine(baseDir, safeName + ext));
    if (File.Exists(legacyPath) && legacyPath.StartsWith(baseDir, StringComparison.Ordinal))
        return legacyPath;

    return null;
}
```

**Changes:**
- `Platform.Api/Endpoints/RecordingEndpoints.cs` — replace inline path logic with `ResolveRecordingPath` helper

### V3: Asterisk Realtime Context Isolation (HIGH)

**File:** `RealtimeEndpoints.cs`

**Root cause:** `CreateProfile` sets `Context = body.Context ?? "from-internal"` without tenant isolation. `TenantOptions.DialplanContextPrefix` exists but is never applied. All tenants share the same Asterisk dialplan context.

**Fix:** If tenant has `DialplanContextPrefix` configured, auto-prefix the context. Otherwise, validate against a whitelist of safe defaults.

```csharp
// In CreateProfile and UpdateProfile:
var tenant = await tenantStore.GetAsync(tenantId, ct);
var prefix = tenant?.Options.DialplanContextPrefix;
var requestedContext = body.Context ?? "from-internal";

if (prefix is not null)
{
    requestedContext = $"{prefix}-{requestedContext}";
}
else
{
    ReadOnlySpan<string> allowed = ["from-internal", "from-external", "default"];
    if (!allowed.Contains(requestedContext))
        return Results.BadRequest(new ErrorResponse(
            $"Context must be one of: {string.Join(", ", allowed)}. " +
            "Configure DialplanContextPrefix on the tenant for custom contexts."));
}
```

**Changes:**
- `Platform.Api/Endpoints/RealtimeEndpoints.cs` — inject `ITenantStore`, add context validation in `CreateProfile` and `UpdateProfile`

### V4: Partner Ownership Bypass (HIGH)

**File:** `ManagementTenantEndpoints.cs`

**Root cause:** `CreateTenant` handler does not verify that a Partner caller creates children only under itself. A Partner admin can specify `ParentTenantId = platform-host-id`.

**Fix:** After resolving parent, check caller's tenant type. Non-Platform callers can only create children under their own tenant.

```csharp
// After resolving parentId:
var callerTenantId = context.User.FindFirst("tid")?.Value;
if (callerTenantId is not null)
{
    var hostTenant = await store.GetHostTenantAsync(ct);
    if (!string.Equals(callerTenantId, hostTenant?.TenantId, StringComparison.OrdinalIgnoreCase))
    {
        // Non-platform caller: must create under own tenant
        if (!string.Equals(parentId, callerTenantId, StringComparison.OrdinalIgnoreCase))
            return Results.Problem("Non-platform tenants can only create children under their own tenant.", statusCode: 403);
    }
}

// Also: parent must be Active
if (parent.Status != TenantStatus.Active)
    return Results.BadRequest(new ErrorResponse("Cannot create children under an inactive tenant."));

// Also: Customers cannot have children
if (body.Type == TenantType.Customer && parent.Type == TenantType.Customer)
    return Results.BadRequest(new ErrorResponse("Customer tenants cannot have sub-tenants."));
```

**Changes:**
- `Platform.Api/Endpoints/ManagementTenantEndpoints.cs` — add ownership + status + hierarchy validation in `CreateTenant`

### V5: Webhook Inbound Security (HIGH)

**Files:** `WebhookEndpoints.cs`, `WhatsAppWebhookHandler.cs`, `MessengerWebhookHandler.cs`, `InstagramWebhookHandler.cs`, `TelegramWebhookHandler.cs`, `TwitterWebhookHandler.cs`

**Root cause:** Two issues:
1. No check that the channel is active (`IsActive`) for the tenant before processing webhooks
2. HMAC validation uses global `IOptions<T>.AppSecret` instead of per-tenant credentials from `ITenantChannelConfigStore`

The infrastructure for per-tenant secrets already exists (`TenantChannelConfig.Credentials` dict) but handlers never read from it.

**Fix:** Two layers:

**Layer 1 — Pre-validation in WebhookEndpoints (all channels):**
```csharp
// Before calling handler:
var channelConfig = await configStore.GetAsync(tid, channelType, ct);
if (channelConfig is null || !channelConfig.IsActive)
    return Results.NotFound();
```

**Layer 2 — Per-tenant HMAC in 5 handlers with signatures:**
Each handler: inject `ITenantChannelConfigStore`, load per-tenant secret with fallback to global.

```csharp
// In WhatsAppWebhookHandler.HandleAsync:
var config = await _configStore.GetAsync(tenantId, Channel, ct);
var appSecret = config?.Credentials.GetValueOrDefault("AppSecret") ?? _options.AppSecret;
if (!ValidateSignature(body, headers, appSecret))
    return Ignored();
```

Handlers change from `IOptions<T>` only to `IOptions<T>` + `ITenantChannelConfigStore`. `ValidateSignature` methods gain an `appSecret` parameter instead of reading from `_options`.

**Changes:**
- `Platform.Api/Endpoints/WebhookEndpoints.cs` — inject `ITenantChannelConfigStore`, add IsActive pre-check
- `Platform.Channels.WhatsApp/WhatsAppWebhookHandler.cs` — inject store, per-tenant secret with fallback
- `Platform.Channels.Messenger/MessengerWebhookHandler.cs` — same pattern
- `Platform.Channels.Instagram/InstagramWebhookHandler.cs` — same pattern
- `Platform.Channels.Telegram/TelegramWebhookHandler.cs` — same pattern (WebhookSecret credential key)
- `Platform.Channels.Twitter/TwitterWebhookHandler.cs` — same pattern (ApiSecret credential key)
- DI registrations: handlers change from Singleton to Transient (async store access)

SMS, Email, Video, WebChat, RCS have no HMAC — they only benefit from Layer 1 (IsActive gate).

## Non-Goals

- No new tests for webhook E2E (requires external service mocking, deferred to Sprint 4)
- No migration of existing recordings to per-tenant directories (legacy fallback covers this)
- No changes to Asterisk dialplan files (context prefixing is at the API level)
- No changes to Sdk (MIT) — only Sdk.Pro and Platform

## Testing

### Unit tests (new)
- `AnalyticsLiveEndpoints` — verify filtered results match tenant's queues only
- `RecordingEndpoints` — path traversal attempts return 404/403
- `RealtimeEndpoints` — context validation rejects invalid contexts, applies prefix
- `ManagementTenantEndpoints` — Partner cannot create under Platform tenant
- `WebhookEndpoints` — inactive channel returns 404

### Build verification
```sh
# Sdk.Pro
cd ../Verbara.Sdk.Pro
dotnet build && dotnet test
dotnet pack -c Release -o ../local-nuget-feed/

# Platform
cd ../Verbara.Platform
rm -rf ~/.nuget/packages/asterisk.sdk.pro*
dotnet restore && dotnet build Verbara.Platform.slnx && dotnet test Verbara.Platform.slnx
```

## Repo Impact

| Repo | Changes | Version Bump |
|------|---------|-------------|
| Sdk.Pro | 2 files (AnalyticsQueryService, LiveStateProvider) | 1.1.2-pro |
| Platform | ~12 files across 8 packages | Stays 1.3.1 (security patch) |
| Sdk (MIT) | None | None |
| Platform.Web | None | None |
