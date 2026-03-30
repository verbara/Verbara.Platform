# Platform Administration — Sub-project A: Host Tenant Identity + Management API

## Overview

Introduce a platform-level administration layer that transcends individual tenants. Platform admins manage the entire deployment: tenant lifecycle, Asterisk servers, licensing, cluster operations, and global configuration.

**Deployment scenarios supported:**

| Scenario | How it works |
|---|---|
| On-prem (single tenant) | Customer installs. Setup wizard creates host tenant + first platform admin. Their operational tenant is a child of host. |
| SaaS (vendor-managed) | Vendor operates from host tenant. Each customer is a child tenant. Management API on private network. |
| Reseller/Partner | Partner is child of host with `platform:tenant:create` + `platform:tenant:impersonate`. Creates customer tenants as their children. Billing rolls up. |
| White-label | Same as reseller + branding config on partner tenant. Customers authenticate against partner subdomain. |

**Key constraint:** `TenantId` remains a non-nullable `readonly record struct` (1,168 references unchanged). Platform admins live inside the host tenant — not outside the tenant system.

---

## 1. Host Tenant & TenantType

### 1.1 Tenant Model Changes (SDK Pro.MultiTenant)

`Tenant` gains two fields:

```csharp
public sealed class Tenant
{
    // existing fields unchanged
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public TenantOptions Options { get; init; } = new();
    public Dictionary<string, string>? Metadata { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // NEW
    public string? ParentTenantId { get; init; }
    public TenantType Type { get; init; } = TenantType.Customer;
}
```

```csharp
public enum TenantType
{
    Platform = 0,   // Host tenant — exactly ONE allowed
    Partner = 1,    // Reseller/white-label — child of Platform
    Customer = 2,   // Operational tenant — child of Platform or Partner
}
```

### 1.2 Business Rules

- Exactly **one** tenant with `Type = Platform` may exist. `ITenantStore.UpsertAsync` rejects duplicates.
- `ParentTenantId = null` is only valid for `TenantType.Platform`.
- `TenantType.Partner` must have `ParentTenantId` pointing to the Platform tenant.
- `TenantType.Customer` must have `ParentTenantId` pointing to Platform or a Partner tenant.
- Maximum hierarchy depth: **3 levels** (Platform -> Partner -> Customer). A Partner's children cannot create sub-children.
- Suspending a parent **does not** cascade to children (children continue operating; billing/licensing may restrict independently).
- Deleting a parent is **blocked** if active children exist.

### 1.3 ITenantStore — New Operations

```csharp
public interface ITenantStore
{
    // Existing (unchanged)
    ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken ct = default);
    ValueTask UpsertAsync(Tenant tenant, CancellationToken ct = default);
    ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken ct = default);

    // New
    ValueTask<Tenant?> GetHostTenantAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken ct = default);
}
```

### 1.4 Backward Compatibility

- Existing tenants (e.g. `"demo"`) default to `TenantType.Customer` with `ParentTenantId = null`.
- The `POST /api/setup` endpoint, after creating the host tenant, runs a one-time migration: all existing tenants with `ParentTenantId = null` and `Type = Customer` get `ParentTenantId` set to the new host tenant's ID. This is idempotent — subsequent calls return 409 before reaching this logic.
- All 39 existing endpoint groups continue operating with TenantId as before — the hierarchy is metadata, not a query filter change.

---

## 2. Platform Permissions & Authorization

### 2.1 New `platform:*` Permission Domain

8 new permissions added to the existing 52-permission catalog:

| Permission ID | Description |
|---|---|
| `platform:tenant:create` | Create tenants (Customer or Partner) |
| `platform:tenant:manage` | Edit tenant config, limits, metadata |
| `platform:tenant:suspend` | Suspend/reactivate tenants |
| `platform:tenant:delete` | Soft-delete tenants |
| `platform:tenant:impersonate` | Operate in context of a child tenant (Sub-project C) |
| `platform:server:manage` | CRUD Asterisk servers, health monitoring (Sub-project B) |
| `platform:license:manage` | View/activate licenses, feature flags |
| `platform:cluster:manage` | Cluster nodes, drain, failover. The existing `system:cluster:manage` permission is **retained** for tenant-scoped cluster visibility; `platform:cluster:manage` is required for cross-tenant cluster operations (drain, failover). |

### 2.2 `PlatformAdminOnly` Authorization Policy

New policy in Program.cs:

```csharp
options.AddPolicy("PlatformAdminOnly", policy =>
    policy.AddRequirements(new PlatformAdminRequirement()));
```

### 2.3 PlatformAdminAuthorizationHandler

A **new, separate** authorization handler (does not modify the existing `PermissionAuthorizationHandler`):

```csharp
public sealed class PlatformAdminAuthorizationHandler
    : AuthorizationHandler<PlatformAdminRequirement>
```

Validation logic:

1. Extract `tid` claim from JWT (or `tenant_id` from API key auth).
2. Check if the user's tenant is the host tenant (cached singleton lookup).
3. If host tenant: resolve effective permissions via `PermissionResolver` and check for the required `platform:*` permission.
4. If not host tenant: check if user's tenant is a Partner with the required `platform:*` permission AND the target tenant (extracted from route parameter `{id}`) is a direct child of that Partner. For endpoints without a target tenant (e.g. `GET /api/management/tenants` list), the handler scopes results to the user's children only.
5. Management API Keys (`key_type=management` claim) bypass steps 2-4 — they are always authorized for `PlatformAdminOnly`.

### 2.4 Role Template: `platform_admin`

New role template added to `RoleTemplateSeeder`:

- Includes all 52 existing permissions + all 8 `platform:*` permissions.
- `IsSystem = true` (cannot be deleted).
- The existing `system_admin` template is **unchanged** — it remains tenant-scoped and does NOT receive `platform:*` permissions.

### 2.5 Impact on Existing Auth

- `PermissionAuthorizationHandler` — **no changes**. Continues resolving tenant-scoped permissions.
- `AdminOnly`, `SupervisorPlus`, `Authenticated` policies — **no changes**.
- `PlatformAdminAuthorizationHandler` is an **additional** handler registered alongside the existing one. It only activates for `PlatformAdminRequirement`.

---

## 3. Management API Surface

### 3.1 Route Structure

All endpoints require `PlatformAdminOnly` policy.

```
── Tenant Lifecycle ──────────────────────────────────────────
GET    /api/management/tenants                — List all tenants (filter: parent, status, type)
GET    /api/management/tenants/{id}           — Tenant detail + stats (users, channels, campaigns)
POST   /api/management/tenants                — Create tenant (Customer or Partner)
PUT    /api/management/tenants/{id}           — Update config, limits, metadata
POST   /api/management/tenants/{id}/suspend   — Suspend tenant
POST   /api/management/tenants/{id}/activate  — Reactivate tenant
DELETE /api/management/tenants/{id}           — Soft-delete (blocked if active children)

── Platform Info ─────────────────────────────────────────────
GET    /api/management/system/info            — Version, features, host tenant info
GET    /api/management/system/license         — Active license, tier, feature limits
PUT    /api/management/system/license         — Activate/update license key
GET    /api/management/system/settings        — Global platform config
PUT    /api/management/system/settings        — Update global platform config

── Cluster ───────────────────────────────────────────────────
GET    /api/management/cluster/status         — Overall cluster health
GET    /api/management/cluster/nodes          — List all nodes
GET    /api/management/cluster/nodes/{id}     — Node detail
POST   /api/management/cluster/nodes/{id}/drain — Graceful drain

── Management API Keys ───────────────────────────────────────
GET    /api/management/api-keys               — List Management API Keys
POST   /api/management/api-keys               — Create new Management API Key
POST   /api/management/api-keys/{id}/rotate   — Rotate key (new secret)
DELETE /api/management/api-keys/{id}          — Revoke key

── Setup (one-time) ──────────────────────────────────────────
POST   /api/setup                             — Create host tenant + first platform admin
```

### 3.2 Endpoint Migration

The following existing endpoints are **replaced** by Management equivalents:

| Old (removed) | New (replacement) |
|---|---|
| `TenantEndpoints` (`/api/admin/tenants/*`) | `ManagementTenantEndpoints` (`/api/management/tenants/*`) |
| `SystemEndpoints` (`/api/admin/system/*`) | `ManagementSystemEndpoints` (`/api/management/system/*`) |
| `ClusterEndpoints` (`/api/admin/cluster/*`) | `ManagementClusterEndpoints` (`/api/management/cluster/*`) |

All other `/api/admin/*` endpoints (users, queues, agents, teams, RBAC, auth config) remain tenant-scoped and unchanged.

### 3.3 Setup Endpoint

`POST /api/setup` — no authentication required. Only functions when no host tenant exists.

**Request:**
```json
{
  "email": "admin@company.com",
  "password": "SecurePassword123!",
  "displayName": "Platform Admin",
  "platformName": "My Contact Center"
}
```

**Behavior:**
1. Check `GetHostTenantAsync() == null`. If host tenant exists -> `409 Conflict`.
2. Create host tenant: `TenantId = "platform"`, `Type = TenantType.Platform`, `ParentTenantId = null`.
3. Create first user: `UserRole.Admin`, `TenantId = "platform"`, password hashed with BCrypt.
4. Assign `platform_admin` role template to the user.
5. Generate a Management API Key (SHA-256 hashed, `KeyType = Management`).
6. Return `201 Created`:

```json
{
  "tenantId": "platform",
  "userId": "usr_...",
  "accessToken": "eyJ...",
  "refreshToken": "...",
  "managementApiKey": "mgmt_..."
}
```

The `managementApiKey` is returned in plaintext **only once**. Subsequent calls return `409`.

---

## 4. Management API Key Authentication

### 4.1 ApiKey Model Extension

```csharp
public sealed class ApiKey : ITenantScoped
{
    // all existing fields unchanged
    public ApiKeyType KeyType { get; init; } = ApiKeyType.Standard;  // NEW
}

public enum ApiKeyType
{
    Standard = 0,    // Tenant-scoped (current behavior)
    Management = 1,  // Platform-scoped (host tenant, platform:* permissions)
}
```

### 4.2 Auth Handler Extension

The existing `ApiKeyAuthenticationHandler` gains a branch for Management keys:

```csharp
if (apiKey.KeyType == ApiKeyType.Management)
{
    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
    claims.Add(new Claim("key_type", "management"));
}
```

The `PlatformAdminAuthorizationHandler` recognizes `key_type=management` as full authorization for `PlatformAdminOnly` endpoints.

### 4.3 Lifecycle

- **Creation**: Only via `POST /api/setup` (first boot) or `POST /api/management/api-keys` (by existing platform admin).
- **Rotation**: `POST /api/management/api-keys/{id}/rotate` — generates new hash, invalidates previous.
- **Revocation**: `DELETE /api/management/api-keys/{id}`.
- **Storage**: Same `IApiKeyStore` — Management keys have `TenantId = host tenant ID` and `KeyType = Management`.

### 4.4 Impact

- `IApiKeyStore` interface — **no changes**. Management keys are stored identically.
- `ApiKeyAuthenticationHandler` — one `if` branch added for `KeyType.Management`.
- SHA-256 hashing, expiration, revocation — all unchanged.

---

## 5. Files Inventory

### New Files

| File | Purpose |
|---|---|
| `Asterisk.Sdk.Pro.MultiTenant/TenantType.cs` | `TenantType` enum (Platform, Partner, Customer) |
| `Platform.Identity/ApiKeyType.cs` | `ApiKeyType` enum (Standard, Management) — alongside `ApiKey.cs` |
| `Platform.Api/Auth/PlatformAdminRequirement.cs` | Authorization requirement |
| `Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs` | Authorization handler |
| `Platform.Api/Endpoints/SetupEndpoints.cs` | `POST /api/setup` |
| `Platform.Api/Endpoints/ManagementTenantEndpoints.cs` | Tenant CRUD |
| `Platform.Api/Endpoints/ManagementSystemEndpoints.cs` | System info, license, settings |
| `Platform.Api/Endpoints/ManagementClusterEndpoints.cs` | Cluster nodes |
| `Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs` | Management API key CRUD |
| `Platform.Api.Tests/PlatformAdminApiFactory.cs` | Test factory with host tenant + platform admin |
| `Platform.Api.Tests/SetupEndpointTests.cs` | Setup wizard tests |
| `Platform.Api.Tests/ManagementEndpointTests.cs` | Management API integration tests |

### Modified Files

| File | Change |
|---|---|
| `Asterisk.Sdk.Pro.MultiTenant/Tenant.cs` | Add `ParentTenantId`, `Type` fields |
| `Asterisk.Sdk.Pro.MultiTenant/ITenantStore.cs` | Add `GetHostTenantAsync`, `GetChildrenAsync` |
| `Platform.Identity/ApiKey.cs` | Add `KeyType` field |
| `Platform.Api/Auth/ApiKeyAuthenticationHandler.cs` | Management key branch |
| `Platform.Api/Program.cs` | Register `PlatformAdminOnly` policy + handler, map new endpoints |
| `Platform.Storage.Postgres/Seeds/PermissionSeeder.cs` | Add 8 `platform:*` permissions |
| `Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs` | Add `platform_admin` template |

### Removed Files

| File | Replaced By |
|---|---|
| `Platform.Api/Endpoints/TenantEndpoints.cs` | `ManagementTenantEndpoints.cs` |
| `Platform.Api/Endpoints/SystemEndpoints.cs` | `ManagementSystemEndpoints.cs` |
| `Platform.Api/Endpoints/ClusterEndpoints.cs` | `ManagementClusterEndpoints.cs` |

---

## 6. Testing Strategy

### New Tests (~30)

**Host Tenant & TenantType (7 tests):**
- `Tenant_WithPlatformType_MustHaveNullParent`
- `Tenant_WithPartnerType_MustHaveParentId`
- `Tenant_WithCustomerType_MustHaveParentId`
- `TenantStore_GetHostTenant_ReturnsOnlyPlatformType`
- `TenantStore_GetChildren_ReturnsDirectChildrenOnly`
- `TenantStore_Upsert_RejectsDuplicatePlatformTenant`
- `TenantStore_Upsert_RejectsDepthBeyondThreeLevels`

**Platform Auth (4 tests):**
- `PlatformAdminHandler_GrantsAccess_WhenUserInHostTenant_WithPlatformPermission`
- `PlatformAdminHandler_DeniesAccess_WhenUserInCustomerTenant`
- `PlatformAdminHandler_DeniesAccess_WhenPartner_AccessesSiblingChildren`
- `PlatformAdminHandler_GrantsAccess_WhenPartner_AccessesOwnChildren`

**Permission Seeding (2 tests):**
- `PermissionSeeder_IncludesPlatformPermissions`
- `RoleTemplateSeeder_IncludesPlatformAdminTemplate`

**Management API Keys (4 tests):**
- `ApiKeyAuth_ManagementKey_SetsManagementClaims`
- `ApiKeyAuth_StandardKey_DoesNotSetManagementClaims`
- `ManagementApiKeys_Create_RequiresPlatformAdmin`
- `ManagementApiKeys_Rotate_GeneratesNewHash`

**Setup Endpoint (4 tests):**
- `Setup_CreatesHostTenant_WhenNoneExists`
- `Setup_Returns409_WhenHostTenantAlreadyExists`
- `Setup_CreatesUserWithPlatformAdminRole`
- `Setup_ReturnsManagementApiKey`

**Management Endpoints — Integration (9 tests):**
- `ManagementTenants_List_RequiresPlatformAdmin`
- `ManagementTenants_List_ReturnsByParentFilter`
- `ManagementTenants_Create_CreatesChildOfHostTenant`
- `ManagementTenants_Create_RejectsDepthViolation`
- `ManagementTenants_Suspend_UpdatesStatus`
- `ManagementTenants_Delete_BlockedIfActiveChildren`
- `ManagementTenants_Delete_SoftDeletes`
- `ManagementSystem_License_ReturnsCurrentLicense`
- `ManagementCluster_Status_ReturnsNodes`

### Test Factory

`PlatformAdminApiFactory` extends `AuthenticatedPlatformApiFactory`:
- Pre-seeds host tenant (`TenantType.Platform`, `TenantId = "platform"`)
- Pre-seeds platform admin user with `platform_admin` role
- Pre-seeds Management API Key
- `CreatePlatformAdminClient()` returns HttpClient with Management API Key header
- Reuses `StubAsteriskHostedServices()` and `RegisterInMemoryStores()`

### Existing Tests

All 1,036 existing tests pass without changes. The removed endpoint files (`TenantEndpoints`, `SystemEndpoints`, `ClusterEndpoints`) have their tests replaced by new Management endpoint tests.

---

## 7. Out of Scope (Future Sub-projects)

| Sub-project | What it covers |
|---|---|
| **B: Asterisk Server Management** | CRUD servers, health monitoring, AMI/ARI connectivity checks. Uses `platform:server:manage` permission defined here. |
| **C: Impersonation + Partner Hierarchy** | `POST /api/management/impersonate`, short-lived JWT with target tenant context, `impersonator_id` audit claim. Uses `platform:tenant:impersonate` permission defined here. |
| **D: Billing/Metering** | Per-tenant consumption tracking, usage aggregation, invoice generation, billing roll-up through hierarchy. |
| **E: K8s Operator** | CRD-based tenant provisioning, GitOps-compatible, reconciliation loop. Complementary to Management API. |
