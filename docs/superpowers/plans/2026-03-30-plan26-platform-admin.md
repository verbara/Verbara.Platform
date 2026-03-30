# Platform Administration — Sub-project A Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce host tenant identity, `platform:*` permissions, Management API surface, setup wizard, and Management API keys so the platform supports platform-level administration across all deployment scenarios (on-prem, SaaS, reseller, white-label).

**Architecture:** A well-known "host tenant" (TenantType.Platform) serves as the home for platform admins. New `PlatformAdminOnly` authorization policy gates a `/api/management/` endpoint surface. Management API keys (ApiKeyType.Management) provide machine-to-machine access. A one-time `POST /api/setup` endpoint bootstraps the host tenant on first boot. TenantId remains non-nullable — zero changes to existing endpoints or stores.

**Tech Stack:** .NET 10, ASP.NET Minimal APIs, xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0, Native AOT compatible (no reflection)

**Spec:** `docs/superpowers/specs/2026-03-30-platform-admin-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|---|---|
| `Asterisk.Sdk.Pro.MultiTenant/TenantType.cs` | `TenantType` enum (Platform, Partner, Customer) |
| `Platform.Identity/ApiKeyType.cs` | `ApiKeyType` enum (Standard, Management) |
| `Platform.Storage.InMemory/InMemoryTenantStore.cs` | In-memory `ITenantStore` with hierarchy support |
| `Platform.Api/Auth/PlatformAdminRequirement.cs` | Authorization requirement for platform endpoints |
| `Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs` | Handler that validates host tenant membership + `platform:*` permissions |
| `Platform.Api/Endpoints/SetupEndpoints.cs` | `POST /api/setup` — first-boot wizard |
| `Platform.Api/Endpoints/ManagementTenantEndpoints.cs` | Tenant CRUD under `/api/management/tenants` |
| `Platform.Api/Endpoints/ManagementSystemEndpoints.cs` | System info, license, settings under `/api/management/system` |
| `Platform.Api/Endpoints/ManagementClusterEndpoints.cs` | Cluster status/nodes under `/api/management/cluster` |
| `Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs` | Management API key CRUD |
| `Platform.Api.Tests/PlatformAdminApiFactory.cs` | Test factory with host tenant + platform admin |
| `Platform.Api.Tests/SetupEndpointTests.cs` | Setup wizard tests |
| `Platform.Api.Tests/ManagementTenantEndpointTests.cs` | Management tenant endpoint tests |
| `Platform.Api.Tests/ManagementSystemEndpointTests.cs` | Management system endpoint tests |
| `Platform.Api.Tests/ManagementClusterEndpointTests.cs` | Management cluster endpoint tests |
| `Platform.Api.Tests/ManagementApiKeyEndpointTests.cs` | Management API key tests |
| `Platform.Api.Tests/PlatformAdminAuthorizationTests.cs` | Platform admin auth handler tests |

### Modified Files

| File | Change |
|---|---|
| `Asterisk.Sdk.Pro.MultiTenant/Tenant.cs` | Add `ParentTenantId`, `Type` fields |
| `Asterisk.Sdk.Pro.MultiTenant/ITenantStore.cs` | Add `GetHostTenantAsync`, `GetChildrenAsync` |
| `Platform.Identity/ApiKey.cs` | Add `KeyType` field |
| `Platform.Api/Auth/ApiKeyAuthenticationHandler.cs` | Add management key claims branch |
| `Platform.Api/Program.cs` | Register PlatformAdminOnly policy, handler, new endpoints; remove old mappings |
| `Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register `InMemoryTenantStore` |
| `Platform.Storage.Postgres/Seeds/PermissionSeeder.cs` | Add 8 `platform:*` permissions |
| `Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs` | Add `platform_admin` template |
| `Platform.Api.Tests/SystemInfoFeatureTests.cs` | Update route from `/api/admin/system/info` to `/api/management/system/info` |

### Removed Files

| File | Replaced By |
|---|---|
| `Platform.Api/Endpoints/TenantEndpoints.cs` | `ManagementTenantEndpoints.cs` |
| `Platform.Api/Endpoints/SystemEndpoints.cs` | `ManagementSystemEndpoints.cs` |
| `Platform.Api/Endpoints/ClusterEndpoints.cs` | `ManagementClusterEndpoints.cs` |

---

## Task 1: TenantType Enum + Tenant Model Extension (SDK)

**Files:**
- Create: `src/Asterisk.Sdk.Pro.MultiTenant/TenantType.cs` (in `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/`)
- Modify: `src/Asterisk.Sdk.Pro.MultiTenant/Tenant.cs` (in `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/`)
- Modify: `src/Asterisk.Sdk.Pro.MultiTenant/ITenantStore.cs` (in `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/`)

- [ ] **Step 1: Create TenantType enum**

Create `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/TenantType.cs`:

```csharp
namespace Asterisk.Sdk.Pro.MultiTenant;

/// <summary>Classification of a tenant within the platform hierarchy.</summary>
public enum TenantType
{
    /// <summary>Host tenant — exactly one allowed. Platform admins live here.</summary>
    Platform = 0,

    /// <summary>Reseller or white-label partner — child of the Platform tenant.</summary>
    Partner = 1,

    /// <summary>Operational tenant — child of Platform or a Partner.</summary>
    Customer = 2,
}
```

- [ ] **Step 2: Add ParentTenantId and Type to Tenant model**

In `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/Tenant.cs`, add two properties after `UpdatedAt`:

```csharp
    /// <summary>Gets or sets the UTC timestamp of the last update.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the parent tenant ID. Null only for the Platform (host) tenant.</summary>
    public string? ParentTenantId { get; init; }

    /// <summary>Gets the tenant classification within the platform hierarchy.</summary>
    public TenantType Type { get; init; } = TenantType.Customer;
```

- [ ] **Step 3: Add new methods to ITenantStore**

In `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/ITenantStore.cs`, add after `UpdateStatusAsync`:

```csharp
    /// <summary>Returns the single Platform (host) tenant, or <see langword="null"/> if not yet created.</summary>
    ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns all direct child tenants of the given parent.</summary>
    ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Build SDK to verify compilation**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/`
Expected: Build succeeded (2 new members on interface will break implementations — expected, fixed in Task 2)

- [ ] **Step 5: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Sdk.Pro
git add src/Asterisk.Sdk.Pro.MultiTenant/TenantType.cs src/Asterisk.Sdk.Pro.MultiTenant/Tenant.cs src/Asterisk.Sdk.Pro.MultiTenant/ITenantStore.cs
git commit -m "feat(multi-tenant): add TenantType enum, ParentTenantId, and hierarchy methods to ITenantStore"
```

---

## Task 2: InMemoryTenantStore (Platform)

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantStore.cs` (in Platform repo)
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create InMemoryTenantStore**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Storage.InMemory/InMemoryTenantStore.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        _tenants.TryGetValue(tenantId, out var tenant);
        return new(tenant);
    }

    public ValueTask<IReadOnlyList<Tenant>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var result = _tenants.Values
            .Where(t => t.Status == TenantStatus.Active)
            .ToList();
        return new(result);
    }

    public ValueTask UpsertAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        // Enforce: only one Platform tenant allowed
        if (tenant.Type == TenantType.Platform)
        {
            var existing = _tenants.Values.FirstOrDefault(t => t.Type == TenantType.Platform);
            if (existing is not null && existing.TenantId != tenant.TenantId)
                throw new InvalidOperationException("Only one Platform tenant is allowed.");
        }

        // Enforce: max depth 3 (Platform -> Partner -> Customer)
        if (tenant.ParentTenantId is not null)
        {
            if (_tenants.TryGetValue(tenant.ParentTenantId, out var parent))
            {
                if (parent.ParentTenantId is not null &&
                    _tenants.TryGetValue(parent.ParentTenantId, out var grandparent) &&
                    grandparent.ParentTenantId is not null)
                {
                    throw new InvalidOperationException("Maximum tenant hierarchy depth (3 levels) exceeded.");
                }
            }
        }

        _tenants[tenant.TenantId] = tenant;
        return default;
    }

    public ValueTask UpdateStatusAsync(string tenantId, TenantStatus status, CancellationToken cancellationToken = default)
    {
        if (_tenants.TryGetValue(tenantId, out var existing))
        {
            // Block deletion if active children exist
            if (status == TenantStatus.Deleted)
            {
                var activeChildren = _tenants.Values
                    .Any(t => t.ParentTenantId == tenantId && t.Status == TenantStatus.Active);
                if (activeChildren)
                    throw new InvalidOperationException("Cannot delete tenant with active children.");
            }

            existing.Status = status;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return default;
    }

    public ValueTask<Tenant?> GetHostTenantAsync(CancellationToken cancellationToken = default)
    {
        var host = _tenants.Values.FirstOrDefault(t => t.Type == TenantType.Platform);
        return new(host);
    }

    public ValueTask<IReadOnlyList<Tenant>> GetChildrenAsync(string parentTenantId, CancellationToken cancellationToken = default)
    {
        var children = _tenants.Values
            .Where(t => t.ParentTenantId == parentTenantId)
            .ToList();
        return new(children);
    }
}
```

- [ ] **Step 2: Register InMemoryTenantStore in DI**

In `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`, add using and registration. After the `// Audit` section (line 77), before `// Media`:

Add to usings:
```csharp
using Asterisk.Sdk.Pro.MultiTenant;
```

Add after `services.AddSingleton<IAuditStore, InMemoryAuditStore>();` (line 77):

```csharp
        // MultiTenant
        if (!services.Any(d => d.ServiceType == typeof(ITenantStore)))
            services.AddSingleton<ITenantStore, InMemoryTenantStore>();
```

The `if` guard ensures that if a Postgres implementation was already registered, we don't override it.

- [ ] **Step 3: Build Platform to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Storage.InMemory/InMemoryTenantStore.cs src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs
git commit -m "feat(storage): add InMemoryTenantStore with hierarchy validation"
```

---

## Task 3: ApiKeyType Enum + ApiKey Extension

**Files:**
- Create: `src/Asterisk.Platform.Identity/ApiKeyType.cs`
- Modify: `src/Asterisk.Platform.Identity/ApiKey.cs`

- [ ] **Step 1: Create ApiKeyType enum**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Identity/ApiKeyType.cs`:

```csharp
namespace Asterisk.Platform.Identity;

/// <summary>Classifies API keys by their authorization scope.</summary>
public enum ApiKeyType
{
    /// <summary>Standard tenant-scoped API key (current behavior).</summary>
    Standard = 0,

    /// <summary>Management API key — platform-scoped, authorized for platform:* operations.</summary>
    Management = 1,
}
```

- [ ] **Step 2: Add KeyType to ApiKey**

In `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Identity/ApiKey.cs`, add after `public string? UpdatedBy { get; set; }`:

```csharp
    public ApiKeyType KeyType { get; init; } = ApiKeyType.Standard;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Identity/ApiKeyType.cs src/Asterisk.Platform.Identity/ApiKey.cs
git commit -m "feat(identity): add ApiKeyType enum and KeyType property to ApiKey"
```

---

## Task 4: PlatformAdmin Authorization (Requirement + Handler)

**Files:**
- Create: `src/Asterisk.Platform.Api/Auth/PlatformAdminRequirement.cs`
- Create: `src/Asterisk.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs`

- [ ] **Step 1: Create PlatformAdminRequirement**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Auth/PlatformAdminRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PlatformAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PlatformAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
```

- [ ] **Step 2: Create PlatformAdminAuthorizationHandler**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs`:

```csharp
using System.Security.Claims;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PlatformAdminAuthorizationHandler : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly ITenantStore _tenantStore;
    private readonly PermissionResolver _resolver;

    // Cache host tenant ID to avoid repeated lookups
    private string? _cachedHostTenantId;

    public PlatformAdminAuthorizationHandler(ITenantStore tenantStore, PermissionResolver resolver)
    {
        _tenantStore = tenantStore;
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        // Management API keys bypass all checks
        var keyTypeClaim = context.User.FindFirst("key_type")?.Value;
        if (keyTypeClaim == "management")
        {
            context.Succeed(requirement);
            return;
        }

        // Resolve user's tenant from claims
        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim))
            return;

        // Resolve host tenant (cached)
        var hostTenantId = await GetHostTenantIdAsync();
        if (hostTenantId is null)
            return; // No host tenant exists yet — only /api/setup is accessible

        var isHostTenant = string.Equals(tenantIdClaim, hostTenantId, StringComparison.OrdinalIgnoreCase);

        if (!isHostTenant)
        {
            // Check if user's tenant is a Partner (can manage its own children)
            var userTenant = await _tenantStore.GetAsync(tenantIdClaim);
            if (userTenant is null || userTenant.Type != TenantType.Partner)
                return; // Not host, not partner — deny
        }

        // If a specific permission is required, check it
        if (requirement.Permission is not null)
        {
            var userIdClaim = context.User.FindFirst("user_id")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
                return;

            var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
            if (roleClaim is not ("Admin" or "SystemAdmin"))
            {
                var tenantId = new TenantId(tenantIdClaim);
                var userId = EntityId.From(userIdClaim);
                var permissions = await _resolver.ResolveAsync(tenantId, userId, CancellationToken.None);
                if (!PermissionResolver.HasPermission(permissions, requirement.Permission))
                    return;
            }
        }
        else
        {
            // No specific permission — require Admin role at minimum
            var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
            if (roleClaim is not ("Admin" or "SystemAdmin"))
                return;
        }

        context.Succeed(requirement);
    }

    private async Task<string?> GetHostTenantIdAsync()
    {
        if (_cachedHostTenantId is not null)
            return _cachedHostTenantId;

        var host = await _tenantStore.GetHostTenantAsync();
        if (host is not null)
            _cachedHostTenantId = host.TenantId;

        return _cachedHostTenantId;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Auth/PlatformAdminRequirement.cs src/Asterisk.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs
git commit -m "feat(auth): add PlatformAdminOnly authorization requirement and handler"
```

---

## Task 5: ApiKey Auth Handler — Management Key Support

**Files:**
- Modify: `src/Asterisk.Platform.Api/Auth/ApiKeyAuthenticationHandler.cs`

- [ ] **Step 1: Add management key claims branch**

In `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Auth/ApiKeyAuthenticationHandler.cs`, add the `using` for `ApiKeyType`:

The file already imports `Asterisk.Platform.Identity`, so `ApiKeyType` is accessible.

After the existing claims block (after `new Claim("key_name", apiKey.Name),`), and before the `if (apiKey.UserId is { } userId)` block, add the management key branch:

Replace this section:

```csharp
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.KeyId.Value),
            new Claim("tenant_id", apiKey.TenantId.Value),
            new Claim("key_name", apiKey.Name),
        };

        if (apiKey.UserId is { } userId)
```

With:

```csharp
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.KeyId.Value),
            new Claim("tenant_id", apiKey.TenantId.Value),
            new Claim("key_name", apiKey.Name),
        };

        if (apiKey.KeyType == ApiKeyType.Management)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            claims.Add(new Claim("key_type", "management"));
        }

        if (apiKey.UserId is { } userId)
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Auth/ApiKeyAuthenticationHandler.cs
git commit -m "feat(auth): add management API key claims in ApiKeyAuthenticationHandler"
```

---

## Task 6: Platform Permissions + Role Template Seeding

**Files:**
- Modify: `src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs`

- [ ] **Step 1: Add platform permissions to PermissionSeeder**

In `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs`, add after the `// ── callanalytics (2) ──` section (after line 191, before the closing `}`):

```csharp
        // ── platform (8) ──
        yield return P("platform:tenant:create", "platform", "tenant", "create",
            "Create new tenants (Customer or Partner)");
        yield return P("platform:tenant:manage", "platform", "tenant", "manage",
            "Edit tenant configuration, limits, and metadata");
        yield return P("platform:tenant:suspend", "platform", "tenant", "suspend",
            "Suspend or reactivate tenants",
            ["platform:tenant:manage"]);
        yield return P("platform:tenant:delete", "platform", "tenant", "delete",
            "Soft-delete tenants",
            ["platform:tenant:manage"]);
        yield return P("platform:tenant:impersonate", "platform", "tenant", "impersonate",
            "Operate in the context of a child tenant");
        yield return P("platform:server:manage", "platform", "server", "manage",
            "Manage Asterisk servers and monitor health");
        yield return P("platform:license:manage", "platform", "license", "manage",
            "View and activate platform licenses");
        yield return P("platform:cluster:manage", "platform", "cluster", "manage",
            "Manage cluster nodes, drain, and failover",
            ["system:cluster:manage"]);
```

- [ ] **Step 2: Add platform_admin role template + update AllPermissions**

In `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs`:

First, add the 8 platform permissions to the `AllPermissions()` method. At the end of the array (after `"callanalytics:analysis:view", "callanalytics:config:manage",`), add:

```csharp
            "platform:tenant:create", "platform:tenant:manage",
            "platform:tenant:suspend", "platform:tenant:delete",
            "platform:tenant:impersonate", "platform:server:manage",
            "platform:license:manage", "platform:cluster:manage",
```

Then add the `platform_admin` template. After the `// ── Api ──` yield return block (after line 143, before the closing `}`):

```csharp
        // ── Platform Admin ──
        yield return (
            new TemplateRow("platform_admin", "Platform Admin", "Full platform administration including cross-tenant operations"),
            AllPermissions());
```

Also update the `system_admin` template to use `AllPermissionsExcept` to exclude platform permissions, since system_admin is tenant-scoped:

Replace:
```csharp
        // ── System Admin ──
        yield return (
            new TemplateRow("system_admin", "System Admin", "Full system access including cluster and auth configuration"),
            AllPermissions());
```

With:
```csharp
        // ── System Admin ──
        yield return (
            new TemplateRow("system_admin", "System Admin", "Full system access including cluster and auth configuration"),
            AllPermissionsExcept([
                "platform:tenant:create", "platform:tenant:manage",
                "platform:tenant:suspend", "platform:tenant:delete",
                "platform:tenant:impersonate", "platform:server:manage",
                "platform:license:manage", "platform:cluster:manage",
            ]));
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs
git commit -m "feat(rbac): add 8 platform:* permissions and platform_admin role template"
```

---

## Task 7: Setup Endpoint

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs`

- [ ] **Step 1: Create SetupEndpoints**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/setup", Setup).AllowAnonymous();
    }

    private static async Task<IResult> Setup(
        [FromBody] SetupRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] JwtTokenService jwtTokenService,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // Guard: only works if no host tenant exists
        var existing = await tenantStore.GetHostTenantAsync(ct);
        if (existing is not null)
            return Results.Conflict(new { error = "Platform already initialized." });

        // Validate input
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "Email and password are required." });

        // 1. Create host tenant
        var hostTenantId = "platform";
        var hostTenant = new Tenant
        {
            TenantId = hostTenantId,
            Name = body.PlatformName ?? "Asterisk Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await tenantStore.UpsertAsync(hostTenant, ct);

        // 2. Adopt orphan tenants (existing Customer tenants with no parent)
        var allActive = await tenantStore.GetAllActiveAsync(ct);
        foreach (var orphan in allActive)
        {
            if (orphan.TenantId != hostTenantId && orphan.ParentTenantId is null)
            {
                var adopted = new Tenant
                {
                    TenantId = orphan.TenantId,
                    Name = orphan.Name,
                    Status = orphan.Status,
                    Options = orphan.Options,
                    Metadata = orphan.Metadata,
                    CreatedAt = orphan.CreatedAt,
                    UpdatedAt = clock.UtcNow,
                    ParentTenantId = hostTenantId,
                    Type = TenantType.Customer,
                };
                await tenantStore.UpsertAsync(adopted, ct);
            }
        }

        // 3. Create platform admin user
        var tenantId = new TenantId(hostTenantId);
        var userId = EntityId.New();
        var user = new User
        {
            UserId = userId,
            TenantId = tenantId,
            Email = body.Email,
            DisplayName = body.DisplayName ?? "Platform Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(body.Password),
            CreatedAt = clock.UtcNow,
        };
        await userStore.SaveAsync(user, ct);

        // 4. Generate Management API Key
        var rawApiKey = $"mgmt_{Guid.NewGuid():N}";
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey)));
        var mgmtKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = tenantId,
            Name = "Platform Management Key",
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            CreatedAt = clock.UtcNow,
        };
        await apiKeyStore.SaveAsync(mgmtKey, ct);

        // 5. Generate JWT for the new admin
        var accessToken = jwtTokenService.GenerateToken(user);

        return Results.Created("/api/management/system/info", new SetupResponse(
            hostTenantId,
            userId.Value,
            accessToken,
            rawApiKey));
    }
}

internal sealed record SetupRequest(
    string Email,
    string Password,
    string? DisplayName,
    string? PlatformName);

internal sealed record SetupResponse(
    string TenantId,
    string UserId,
    string AccessToken,
    string ManagementApiKey);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs
git commit -m "feat(api): add POST /api/setup endpoint for first-boot platform initialization"
```

---

## Task 8: Management Tenant Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`
- Delete: `src/Asterisk.Platform.Api/Endpoints/TenantEndpoints.cs`

- [ ] **Step 1: Create ManagementTenantEndpoints**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`:

```csharp
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementTenantEndpoints
{
    public static void MapManagementTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/tenants").RequireAuthorization("PlatformAdminOnly");

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
            tenants = await store.GetChildrenAsync(parentId, ct);
        else
            tenants = await store.GetAllActiveAsync(ct);

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
        [FromBody] CreateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        // Validate type
        if (body.Type is not (TenantType.Customer or TenantType.Partner))
            return Results.BadRequest(new { error = "Type must be Customer or Partner." });

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
            return Results.BadRequest(new { error = $"Parent tenant '{parentId}' not found." });

        // Validate hierarchy: Partner can only be child of Platform, Customer can be child of Platform or Partner
        if (body.Type == TenantType.Partner && parent.Type != TenantType.Platform)
            return Results.BadRequest(new { error = "Partner tenants must be children of the Platform tenant." });

        if (body.Type == TenantType.Customer && parent.Type is not (TenantType.Platform or TenantType.Partner))
            return Results.BadRequest(new { error = "Customer tenants must be children of Platform or a Partner." });

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
            Metadata = body.Metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(tenant, ct);
        return Results.Created($"/api/management/tenants/{tenant.TenantId}", MapToDto(tenant));
    }

    private static async Task<IResult> UpdateTenant(
        string id,
        [FromBody] UpdateMgmtTenantRequest body,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        var updated = new Tenant
        {
            TenantId = existing.TenantId,
            Name = body.Name ?? existing.Name,
            Status = existing.Status,
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
        return Results.Ok(MapToDto(updated));
    }

    private static async Task<IResult> SuspendTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new { error = "Cannot suspend the Platform tenant." });

        await store.UpdateStatusAsync(id, TenantStatus.Suspended, ct);
        return Results.Ok(new { tenantId = id, status = "Suspended" });
    }

    private static async Task<IResult> ActivateTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();

        await store.UpdateStatusAsync(id, TenantStatus.Active, ct);
        return Results.Ok(new { tenantId = id, status = "Active" });
    }

    private static async Task<IResult> DeleteTenant(
        string id,
        [FromServices] ITenantStore store,
        CancellationToken ct)
    {
        var tenant = await store.GetAsync(id, ct);
        if (tenant is null) return Results.NotFound();
        if (tenant.Type == TenantType.Platform)
            return Results.BadRequest(new { error = "Cannot delete the Platform tenant." });

        // UpdateStatusAsync throws if active children exist
        await store.UpdateStatusAsync(id, TenantStatus.Deleted, ct);
        return Results.NoContent();
    }

    private static MgmtTenantDto MapToDto(Tenant t) =>
        new(t.TenantId, t.Name, t.Status.ToString(), t.Type.ToString(),
            t.ParentTenantId, t.Options.MaxConcurrentChannels,
            t.Options.MaxActiveCampaigns, t.Metadata, t.CreatedAt, t.UpdatedAt);
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
    Dictionary<string, string>? Metadata = null);

internal sealed record UpdateMgmtTenantRequest(
    string? Name = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    Dictionary<string, string>? Metadata = null);
```

- [ ] **Step 2: Delete TenantEndpoints.cs**

```bash
rm /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/TenantEndpoints.cs
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/`
Expected: Build fails — `MapTenantEndpoints` not found in Program.cs (expected, fixed in Task 11)

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs
git rm src/Asterisk.Platform.Api/Endpoints/TenantEndpoints.cs
git commit -m "feat(api): replace TenantEndpoints with ManagementTenantEndpoints under /api/management/"
```

---

## Task 9: Management System Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs`
- Delete: `src/Asterisk.Platform.Api/Endpoints/SystemEndpoints.cs`

- [ ] **Step 1: Create ManagementSystemEndpoints**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

// ─── Settings store (unchanged, moved from SystemEndpoints.cs) ────────────────

internal sealed class SystemSettingsStore
{
    private readonly ConcurrentDictionary<string, SystemSettingsRecord> _settings = new();

    public SystemSettingsRecord Get() =>
        _settings.GetOrAdd("__global__", _ => new SystemSettingsRecord("Asterisk Platform", "UTC", "en-US"));

    public void Save(SystemSettingsRecord record) =>
        _settings["__global__"] = record;
}

internal sealed record SystemSettingsRecord(string PlatformName, string DefaultTimezone, string DefaultLanguage);

// ─── Endpoints ────────────────────────────────────────────────────────────────

internal static class ManagementSystemEndpoints
{
    public static void MapManagementSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/system").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/info", GetSystemInfo);
        group.MapGet("/license", GetLicenseInfo);
        group.MapPut("/license", UpdateLicense);
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettings);
    }

    private static async Task<IResult> GetSystemInfo(
        [FromServices] IFeatureRegistry features,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        return Results.Ok(new
        {
            version = "1.1.0",
            hostTenantId = hostTenant?.TenantId,
            platformName = hostTenant?.Name ?? "Asterisk Platform",
            features = features.GetFeatures(),
        });
    }

    private static IResult GetLicenseInfo()
    {
        return Results.Ok(new
        {
            tier = "community",
            features = Array.Empty<string>(),
            maxAgents = 10,
        });
    }

    private static IResult UpdateLicense([FromBody] UpdateLicenseRequest body)
    {
        // License activation will be implemented when Pro.Licensing supports runtime activation
        return Results.Ok(new
        {
            tier = "community",
            features = Array.Empty<string>(),
            message = "License activation not yet implemented.",
        });
    }

    private static IResult GetSettings([FromServices] SystemSettingsStore store)
    {
        var record = store.Get();
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }

    private static IResult SaveSettings(
        [FromBody] SystemSettingsRequest body,
        [FromServices] SystemSettingsStore store)
    {
        var record = new SystemSettingsRecord(body.PlatformName, body.DefaultTimezone, body.DefaultLanguage);
        store.Save(record);
        return Results.Ok(new
        {
            platformName = record.PlatformName,
            defaultTimezone = record.DefaultTimezone,
            defaultLanguage = record.DefaultLanguage,
        });
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record UpdateLicenseRequest(string LicenseKey);
internal sealed record SystemSettingsRequest(string PlatformName, string DefaultTimezone, string DefaultLanguage);
```

- [ ] **Step 2: Delete SystemEndpoints.cs**

```bash
rm /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/SystemEndpoints.cs
```

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs
git rm src/Asterisk.Platform.Api/Endpoints/SystemEndpoints.cs
git commit -m "feat(api): replace SystemEndpoints with ManagementSystemEndpoints under /api/management/"
```

---

## Task 10: Management Cluster Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs`
- Delete: `src/Asterisk.Platform.Api/Endpoints/ClusterEndpoints.cs`

- [ ] **Step 1: Create ManagementClusterEndpoints**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs`:

```csharp
using Asterisk.Sdk.Pro.Cluster;
using Asterisk.Sdk.Pro.Cluster.Drain;
using Asterisk.Sdk.Pro.Cluster.Registry;
using Asterisk.Sdk.Pro.Cluster.Transport;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementClusterEndpoints
{
    public static void MapManagementClusterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/cluster").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/status", GetStatus);
        group.MapGet("/nodes", ListNodes);
        group.MapGet("/nodes/{nodeId}", GetNode);
        group.MapPost("/nodes/{nodeId}/drain", DrainNode);
    }

    private static IResult GetStatus(IServiceProvider services)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Ok(new MgmtClusterStatusDto("local", [], 0, 0, []));

        var status = manager.GetStatus();
        return Results.Ok(new MgmtClusterStatusDto(
            status.InstanceId,
            status.Nodes.Select(MapNodeToDto).ToList(),
            status.TotalChannels,
            status.TotalAgents,
            status.ActiveDrains.Select(MapDrainToDto).ToList()));
    }

    private static async Task<IResult> ListNodes(IServiceProvider services, CancellationToken ct)
    {
        var transport = services.GetService<ClusterTransportBase>();
        if (transport is null)
            return Results.Ok(Array.Empty<MgmtClusterNodeDto>());

        var nodes = await transport.GetNodesAsync(ct);
        return Results.Ok(nodes.Select(MapNodeToDto).ToList());
    }

    private static async Task<IResult> GetNode(string nodeId, IServiceProvider services, CancellationToken ct)
    {
        var transport = services.GetService<ClusterTransportBase>();
        if (transport is null)
            return Results.NotFound();

        var nodes = await transport.GetNodesAsync(ct);
        var node = nodes.FirstOrDefault(n => n.NodeId == nodeId);
        return node is null ? Results.NotFound() : Results.Ok(MapNodeToDto(node));
    }

    private static async Task<IResult> DrainNode(
        string nodeId,
        [FromBody] MgmtDrainNodeRequest body,
        IServiceProvider services,
        CancellationToken ct)
    {
        var manager = services.GetService<ClusterManager>();
        if (manager is null)
            return Results.Problem("Cluster not registered", statusCode: 503);

        var options = new DrainOptions
        {
            Timeout = body.GracePeriodSeconds.HasValue
                ? TimeSpan.FromSeconds(body.GracePeriodSeconds.Value)
                : TimeSpan.FromMinutes(10),
        };

        var status = await manager.Drain.StartDrainAsync(nodeId, options, ct);
        return Results.Accepted($"/api/management/cluster/nodes/{nodeId}", MapDrainToDto(status));
    }

    private static MgmtClusterNodeDto MapNodeToDto(ClusterNode n) =>
        new(n.NodeId, n.State.ToString().ToLowerInvariant(), n.Weight,
            n.PriorityTier, n.MaxCapacity, n.AsteriskVersion,
            n.StartupTime?.ToString("O"));

    private static MgmtDrainStatusDto MapDrainToDto(DrainStatus d) =>
        new(d.NodeId, d.State.ToString().ToLowerInvariant(),
            d.StartedAt, d.Deadline, d.InitialCallCount,
            d.RemainingCallCount, d.NaturallyCompleted, d.ForceDisconnected);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtClusterStatusDto(
    string InstanceId,
    IReadOnlyList<MgmtClusterNodeDto> Nodes,
    int TotalChannels,
    int TotalAgents,
    IReadOnlyList<MgmtDrainStatusDto> ActiveDrains);

internal sealed record MgmtClusterNodeDto(
    string NodeId,
    string State,
    double Weight,
    int PriorityTier,
    int MaxCapacity,
    string? AsteriskVersion,
    string? StartupTime);

internal sealed record MgmtDrainNodeRequest(int? GracePeriodSeconds);

internal sealed record MgmtDrainStatusDto(
    string NodeId,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    int InitialCallCount,
    int RemainingCallCount,
    int NaturallyCompleted,
    int ForceDisconnected);
```

- [ ] **Step 2: Delete ClusterEndpoints.cs**

```bash
rm /media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ClusterEndpoints.cs
```

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Endpoints/ManagementClusterEndpoints.cs
git rm src/Asterisk.Platform.Api/Endpoints/ClusterEndpoints.cs
git commit -m "feat(api): replace ClusterEndpoints with ManagementClusterEndpoints under /api/management/"
```

---

## Task 11: Management API Key Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs`

- [ ] **Step 1: Create ManagementApiKeyEndpoints**

Create `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementApiKeyEndpoints
{
    public static void MapManagementApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/api-keys").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", ListKeys);
        group.MapPost("/", CreateKey);
        group.MapPost("/{id}/rotate", RotateKey);
        group.MapDelete("/{id}", RevokeKey);
    }

    private static async Task<IResult> ListKeys(
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Ok(Array.Empty<object>());

        var tenantId = new TenantId(host.TenantId);
        var keys = await apiKeyStore.ListAsync(tenantId, new PagedQuery(1, 100), ct);

        var mgmtKeys = keys.Items
            .Where(k => k.KeyType == ApiKeyType.Management)
            .Select(k => new MgmtApiKeyDto(
                k.KeyId.Value, k.Name, k.IsRevoked,
                k.ExpiresAt, k.CreatedAt))
            .ToList();

        return Results.Ok(mgmtKeys);
    }

    private static async Task<IResult> CreateKey(
        [FromBody] CreateMgmtApiKeyRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var rawKey = $"mgmt_{Guid.NewGuid():N}";
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

        var apiKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = new TenantId(host.TenantId),
            Name = body.Name ?? "Management Key",
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            ExpiresAt = body.ExpiresInDays.HasValue
                ? clock.UtcNow.AddDays(body.ExpiresInDays.Value)
                : null,
            CreatedAt = clock.UtcNow,
        };

        await apiKeyStore.SaveAsync(apiKey, ct);

        return Results.Created($"/api/management/api-keys/{apiKey.KeyId.Value}",
            new CreateMgmtApiKeyResponse(apiKey.KeyId.Value, apiKey.Name, rawKey, apiKey.ExpiresAt));
    }

    private static async Task<IResult> RotateKey(
        string id,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var tenantId = new TenantId(host.TenantId);
        var existing = await apiKeyStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null || existing.KeyType != ApiKeyType.Management)
            return Results.NotFound();

        // Revoke old key
        await apiKeyStore.RevokeAsync(tenantId, existing.KeyId, ct);

        // Create new key with same name
        var rawKey = $"mgmt_{Guid.NewGuid():N}";
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

        var newKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = tenantId,
            Name = existing.Name,
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            ExpiresAt = existing.ExpiresAt,
            CreatedAt = clock.UtcNow,
        };

        await apiKeyStore.SaveAsync(newKey, ct);

        return Results.Ok(new CreateMgmtApiKeyResponse(newKey.KeyId.Value, newKey.Name, rawKey, newKey.ExpiresAt));
    }

    private static async Task<IResult> RevokeKey(
        string id,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var tenantId = new TenantId(host.TenantId);
        var existing = await apiKeyStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null || existing.KeyType != ApiKeyType.Management)
            return Results.NotFound();

        await apiKeyStore.RevokeAsync(tenantId, existing.KeyId, ct);
        return Results.NoContent();
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtApiKeyDto(
    string KeyId,
    string Name,
    bool IsRevoked,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

internal sealed record CreateMgmtApiKeyRequest(
    string? Name = null,
    int? ExpiresInDays = null);

internal sealed record CreateMgmtApiKeyResponse(
    string KeyId,
    string Name,
    string ApiKey,
    DateTimeOffset? ExpiresAt);
```

- [ ] **Step 2: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Endpoints/ManagementApiKeyEndpoints.cs
git commit -m "feat(api): add Management API Key CRUD endpoints under /api/management/api-keys"
```

---

## Task 12: Wire Everything in Program.cs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Register PlatformAdminOnly policy and handler**

In Program.cs, replace the authorization block (lines 195-200):

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("SupervisorPlus", p => p.RequireRole("Admin", "Supervisor"));
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
});
```

With:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    options.AddPolicy("SupervisorPlus", p => p.RequireRole("Admin", "Supervisor"));
    options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());
    options.AddPolicy("PlatformAdminOnly", p =>
        p.AddRequirements(new PlatformAdminRequirement()));
});
```

- [ ] **Step 2: Register PlatformAdminAuthorizationHandler**

After `builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();` (line 205), add:

```csharp
builder.Services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
```

- [ ] **Step 3: Replace endpoint mappings**

Replace these 3 lines (around lines 275, 299, 300):
```csharp
app.MapSystemEndpoints();
```
```csharp
app.MapClusterEndpoints();
app.MapTenantEndpoints();
```

With these 5 new lines — place them together after the existing endpoint mappings:
```csharp
app.MapSetupEndpoints();
app.MapManagementTenantEndpoints();
app.MapManagementSystemEndpoints();
app.MapManagementClusterEndpoints();
app.MapManagementApiKeyEndpoints();
```

Remove the old `app.MapSystemEndpoints();`, `app.MapClusterEndpoints();`, and `app.MapTenantEndpoints();` lines.

- [ ] **Step 4: Build the entire solution**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add src/Asterisk.Platform.Api/Program.cs
git commit -m "feat(api): wire PlatformAdminOnly policy, handler, and Management endpoints in Program.cs"
```

---

## Task 13: Test Factory — PlatformAdminApiFactory

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/PlatformAdminApiFactory.cs`

- [ ] **Step 1: Create PlatformAdminApiFactory**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/PlatformAdminApiFactory.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Factory with a pre-seeded Platform (host) tenant, platform admin user,
/// and Management API key for testing /api/management/* and /api/setup endpoints.
/// </summary>
public sealed class PlatformAdminApiFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "platform";
    public const string TestMgmtApiKey = "mgmt-test-key-platform";
    public const string TestPlatformAdminUserId = "platform-admin-user";

    private static readonly string s_hashedMgmtKey = HashKey(TestMgmtApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Reuse existing stubs from AuthenticatedPlatformApiFactory
            AuthenticatedPlatformApiFactory.StubAsteriskHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            // Disable license enforcement
            services.Configure<Asterisk.Sdk.Pro.Licensing.LicenseOptions>(
                o => o.EnforcementMode = Asterisk.Sdk.Pro.Licensing.EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);

        // Seed platform tenant, admin, and management key
        using var scope = host.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

        var tenantId = new TenantId(HostTenantId);

        // Host tenant
        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = HostTenantId,
            Name = "Test Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
        }).AsTask().GetAwaiter().GetResult();

        // Platform admin user
        userStore.SaveAsync(new User
        {
            UserId = EntityId.From(TestPlatformAdminUserId),
            TenantId = tenantId,
            Email = "platform-admin@test.internal",
            DisplayName = "Test Platform Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        // Management API key
        apiKeyStore.SaveAsync(new ApiKey
        {
            KeyId = EntityId.From("mgmt-key-id"),
            TenantId = tenantId,
            Name = "Test Management Key",
            HashedKey = s_hashedMgmtKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();

        return host;
    }

    public HttpClient CreatePlatformAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestMgmtApiKey}");
        return client;
    }

    /// <summary>Creates a client with no auth headers — for testing /api/setup.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
```

- [ ] **Step 2: Build tests**

Run: `dotnet build /media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/PlatformAdminApiFactory.cs
git commit -m "test: add PlatformAdminApiFactory with host tenant and management key seeding"
```

---

## Task 14: Setup Endpoint Tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/SetupEndpointTests.cs`

- [ ] **Step 1: Create SetupEndpointTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/SetupEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class SetupEndpointTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public SetupEndpointTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Setup_ShouldCreateHostTenant_WhenNoneExists()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "SetupTest2026!",
            displayName = "Setup Admin",
            platformName = "Test Platform",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SetupResponseDto>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be("platform");
        body.UserId.Should().NotBeNullOrEmpty();
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.ManagementApiKey.Should().StartWith("mgmt_");
    }

    [Fact]
    public async Task Setup_ShouldReturn409_WhenHostTenantAlreadyExists()
    {
        // Use the PlatformAdminApiFactory which already has a host tenant
        using var factory = new PlatformAdminApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "another@test.com",
            password = "AnotherTest2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailMissing()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "",
            password = "Test2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record SetupResponseDto(
        string TenantId,
        string UserId,
        string AccessToken,
        string ManagementApiKey);
}
```

- [ ] **Step 2: Run setup tests**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~SetupEndpointTests" -v q`
Expected: 3 tests passed

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/SetupEndpointTests.cs
git commit -m "test: add setup endpoint tests (create host tenant, 409 conflict, validation)"
```

---

## Task 15: Management Tenant Endpoint Tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs`

- [ ] **Step 1: Create ManagementTenantEndpointTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementTenantEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementTenantEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListTenants_ShouldRequirePlatformAdmin()
    {
        using var factory = new PlatformApiFactory();
        var anonClient = factory.CreateClient();

        var response = await anonClient.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListTenants_ShouldReturnHostTenant()
    {
        var response = await _client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("platform");
    }

    [Fact]
    public async Task CreateTenant_ShouldCreateChildOfHost()
    {
        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "test-child-" + Guid.NewGuid().ToString("N")[..8],
            name = "Test Child Tenant",
            type = 2, // Customer
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTenant_ShouldRejectDepthViolation()
    {
        // Create a partner
        var partnerId = "partner-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = partnerId,
            name = "Test Partner",
            type = 1, // Partner
        });

        // Create a customer under partner
        var customerId = "cust-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = customerId,
            name = "Test Customer",
            type = 2, // Customer
            parentTenantId = partnerId,
        });

        // Try to create a child under the customer — should fail (depth > 3)
        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "too-deep-" + Guid.NewGuid().ToString("N")[..8],
            name = "Too Deep",
            type = 2,
            parentTenantId = customerId,
        });

        // Customer type requires Platform or Partner parent
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SuspendTenant_ShouldUpdateStatus()
    {
        var tenantId = "suspend-test-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Suspend Test",
            type = 2,
        });

        var response = await _client.PostAsync($"/api/management/tenants/{tenantId}/suspend", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Suspended");
    }

    [Fact]
    public async Task SuspendTenant_ShouldRejectPlatformTenant()
    {
        var response = await _client.PostAsync("/api/management/tenants/platform/suspend", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTenant_ShouldSoftDelete()
    {
        var tenantId = "delete-test-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Delete Test",
            type = 2,
        });

        var response = await _client.DeleteAsync($"/api/management/tenants/{tenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~ManagementTenantEndpointTests" -v q`
Expected: 7 tests passed

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs
git commit -m "test: add management tenant endpoint tests (CRUD, hierarchy, auth)"
```

---

## Task 16: Management System + Cluster + API Key Tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementSystemEndpointTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementClusterEndpointTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementApiKeyEndpointTests.cs`

- [ ] **Step 1: Create ManagementSystemEndpointTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ManagementSystemEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementSystemEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementSystemEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task SystemInfo_ShouldReturnHostTenantId()
    {
        var response = await _client.GetAsync("/api/management/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("platform");
        body.Should().Contain("1.1.0");
    }

    [Fact]
    public async Task License_ShouldReturnCommunityTier()
    {
        var response = await _client.GetAsync("/api/management/system/license");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("community");
    }

    [Fact]
    public async Task Settings_ShouldPersistRoundTrip()
    {
        await _client.PutAsJsonAsync("/api/management/system/settings", new
        {
            platformName = "Updated Platform",
            defaultTimezone = "America/Bogota",
            defaultLanguage = "es-CO",
        });

        var response = await _client.GetAsync("/api/management/system/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Updated Platform");
        body.Should().Contain("America/Bogota");
    }
}
```

- [ ] **Step 2: Create ManagementClusterEndpointTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ManagementClusterEndpointTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementClusterEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementClusterEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ClusterStatus_ShouldReturnLocalFallback()
    {
        var response = await _client.GetAsync("/api/management/cluster/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("local");
    }

    [Fact]
    public async Task ClusterNodes_ShouldReturnEmptyWhenNoCluster()
    {
        var response = await _client.GetAsync("/api/management/cluster/nodes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 3: Create ManagementApiKeyEndpointTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ManagementApiKeyEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementApiKeyEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementApiKeyEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListKeys_ShouldReturnSeededKey()
    {
        var response = await _client.GetAsync("/api/management/api-keys");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Test Management Key");
    }

    [Fact]
    public async Task CreateKey_ShouldReturnNewKey()
    {
        var response = await _client.PostAsJsonAsync("/api/management/api-keys", new
        {
            name = "CI/CD Key",
            expiresInDays = 30,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("mgmt_");
        body.Should().Contain("CI/CD Key");
    }

    [Fact]
    public async Task RevokeKey_ShouldReturnNoContent()
    {
        // Create a key to revoke
        var createResponse = await _client.PostAsJsonAsync("/api/management/api-keys", new
        {
            name = "Revoke Test",
        });
        var created = await createResponse.Content.ReadFromJsonAsync<KeyResponseDto>();

        var response = await _client.DeleteAsync($"/api/management/api-keys/{created!.KeyId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private sealed record KeyResponseDto(string KeyId, string Name, string ApiKey, DateTimeOffset? ExpiresAt);
}
```

- [ ] **Step 4: Run all new tests**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~Management" -v q`
Expected: All tests passed

- [ ] **Step 5: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/ManagementSystemEndpointTests.cs tests/Asterisk.Platform.Api.Tests/ManagementClusterEndpointTests.cs tests/Asterisk.Platform.Api.Tests/ManagementApiKeyEndpointTests.cs
git commit -m "test: add management system, cluster, and API key endpoint tests"
```

---

## Task 17: Platform Admin Authorization Tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/PlatformAdminAuthorizationTests.cs`

- [ ] **Step 1: Create PlatformAdminAuthorizationTests**

Create `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/PlatformAdminAuthorizationTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class PlatformAdminAuthorizationTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public PlatformAdminAuthorizationTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ManagementEndpoint_ShouldGrantAccess_WhenManagementApiKey()
    {
        var client = _factory.CreatePlatformAdminClient();
        var response = await client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManagementEndpoint_ShouldDenyAccess_WhenNoAuth()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ManagementEndpoint_ShouldDenyAccess_WhenStandardApiKey()
    {
        // Use the authenticated factory which has a standard tenant-scoped key
        using var stdFactory = new AuthenticatedPlatformApiFactory();
        var client = stdFactory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/management/tenants");
        // Standard key is Admin but not in host tenant — should be denied
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 2: Run auth tests**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/ --filter "FullyQualifiedName~PlatformAdminAuthorizationTests" -v q`
Expected: 3 tests passed

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/PlatformAdminAuthorizationTests.cs
git commit -m "test: add platform admin authorization tests (management key, anonymous, standard key)"
```

---

## Task 18: Fix Existing Tests + Update SystemInfoFeatureTests

**Files:**
- Modify: `tests/Asterisk.Platform.Api.Tests/SystemInfoFeatureTests.cs`

- [ ] **Step 1: Update SystemInfoFeatureTests route**

In `/media/Data/Source/IPcom/Asterisk.Platform/tests/Asterisk.Platform.Api.Tests/SystemInfoFeatureTests.cs`, the test calls `/api/admin/system/info` which no longer exists. The system info endpoint is now at `/api/management/system/info` and requires `PlatformAdminOnly`.

Replace the entire file:

```csharp
using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class SystemInfoFeatureTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public SystemInfoFeatureTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task SystemInfo_ShouldReturnFeatures_WithKnownKeys()
    {
        var response = await _client.GetAsync("/api/management/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"conversations\":true");
        json.Should().Contain("\"dialer\":false");
        json.Should().Contain("\"queues\":true");
    }
}
```

- [ ] **Step 2: Run ALL tests**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx -v q`
Expected: All tests pass (existing 1036 + ~25 new)

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add tests/Asterisk.Platform.Api.Tests/SystemInfoFeatureTests.cs
git commit -m "fix(test): update SystemInfoFeatureTests to use /api/management/ route"
```

---

## Task 19: Update CLAUDE.md + Final Verification

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update CLAUDE.md endpoint inventory**

In the Endpoint Inventory table, replace:

```
| Admin | AdminEndpoints, SystemEndpoints, AuditEndpoints, TenantEndpoints, ScheduledReportEndpoints |
```

With:

```
| Admin | AdminEndpoints, AuditEndpoints, ScheduledReportEndpoints |
| Management | ManagementTenantEndpoints, ManagementSystemEndpoints, ManagementClusterEndpoints, ManagementApiKeyEndpoints, SetupEndpoints |
```

Also update the endpoint count from 39 to 41 (removed 3, added 5 = net +2).

- [ ] **Step 2: Add Plan 26 section to CLAUDE.md**

After the Plan 25 section, add:

```markdown
## Plan 26: Platform Administration — Sub-project A -- IN PROGRESS

**Spec:** `docs/superpowers/specs/2026-03-30-platform-admin-design.md`
**Plan:** `docs/superpowers/plans/2026-03-30-plan26-platform-admin.md`

Host tenant identity + Management API:
1. **TenantType + Hierarchy** -- TenantType enum (Platform/Partner/Customer), ParentTenantId, max depth 3
2. **Platform Permissions** -- 8 `platform:*` permissions, `platform_admin` role template (60 total permissions)
3. **PlatformAdminOnly auth** -- New authorization handler + policy for `/api/management/` endpoints
4. **Management API** -- Tenant CRUD, System info/license/settings, Cluster status/nodes, API key management
5. **Setup Wizard** -- `POST /api/setup` for first-boot platform initialization
6. **Management API Keys** -- `ApiKeyType.Management` for platform-scoped machine-to-machine access
```

- [ ] **Step 3: Run full test suite**

Run: `dotnet test /media/Data/Source/IPcom/Asterisk.Platform/Asterisk.Platform.slnx -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 4: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with Plan 26 platform admin and updated endpoint inventory"
```
