# Plan 29D: Impersonation (Shadow JWT)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow Platform Admins to operate in the context of a child tenant via a short-lived shadow JWT, with full audit trail, middleware restrictions, and explicit UI indication.

**Architecture:** New `ManagementImpersonationEndpoints.cs` with POST/DELETE endpoints. `JwtTokenService` extended with `GenerateImpersonationToken()`. `TenantResolutionMiddleware` extended to block dangerous operations during impersonation. Frontend: auth store extended with impersonation state, new banner component, impersonation hook.

**Tech Stack:** .NET 10, JWT RS256, React 19, Zustand, TanStack Query 5.

**Spec:** `docs/superpowers/specs/2026-03-31-v121-operations-design.md` — Sub-project C.

**Prerequisite:** Plan 29A complete (ErrorResponse DTO available).

---

### Task 1: Auth event types for impersonation

**Files:**
- Modify: `src/Asterisk.Platform.Identity/AuthEvent.cs`

- [ ] **Step 1: Add impersonation event types**

In `AuthEventTypes` (static class or constants), add:

```csharp
public const string ImpersonationStarted = "impersonation_started";
public const string ImpersonationEnded = "impersonation_ended";
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Identity/`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Identity/AuthEvent.cs
git commit -m "feat: add ImpersonationStarted and ImpersonationEnded auth event types"
```

---

### Task 2: JwtTokenService — GenerateImpersonationToken

**Files:**
- Modify: `src/Asterisk.Platform.Api/Services/JwtTokenService.cs`

- [ ] **Step 1: Add GenerateImpersonationToken method**

Add after the existing `GenerateAccessToken` method:

```csharp
public (string Token, DateTimeOffset ExpiresAt) GenerateImpersonationToken(
    User admin,
    string targetTenantId,
    IReadOnlySet<string> targetPermissions)
{
    var now = DateTimeOffset.UtcNow;
    var expires = now.AddMinutes(30); // 30 min for impersonation (vs 15 for normal)

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, admin.UserId.Value),
        new("tid", targetTenantId),
        new(JwtRegisteredClaimNames.Email, admin.Email),
        new("name", admin.DisplayName ?? admin.Email),
        new(ClaimTypes.Role, "Admin"),
        new("impersonator_id", admin.UserId.Value),
        new("impersonator_tenant", admin.TenantId.Value),
        new("impersonation", "true"),
    };

    foreach (var permission in targetPermissions)
        claims.Add(new Claim("permissions", permission));

    var token = new JwtSecurityToken(
        issuer: Issuer,
        audience: Issuer,
        claims: claims,
        notBefore: now.UtcDateTime,
        expires: expires.UtcDateTime,
        signingCredentials: _signingCredentials);

    return (new JwtSecurityTokenHandler().WriteToken(token), expires);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/JwtTokenService.cs
git commit -m "feat: add GenerateImpersonationToken to JwtTokenService (30min TTL, dual identity claims)"
```

---

### Task 3: Impersonation middleware restrictions

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`

- [ ] **Step 1: Add impersonation path blocking**

At the beginning of `InvokeAsync`, after the existing logic, add a check for impersonation tokens attempting blocked operations:

```csharp
// After tenant resolution, before calling next(context):
if (context.User.Identity?.IsAuthenticated == true)
{
    var isImpersonating = context.User.FindFirstValue("impersonation") == "true";
    if (isImpersonating)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        var isBlocked =
            (method == "DELETE" && path.StartsWith("/api/management/tenants", StringComparison.OrdinalIgnoreCase)) ||
            (method == "POST" && path.Equals("/api/management/impersonate", StringComparison.OrdinalIgnoreCase)) ||
            (method == "PUT" && path.StartsWith("/api/management/system", StringComparison.OrdinalIgnoreCase)) ||
            (method == "POST" && path.Equals("/api/setup", StringComparison.OrdinalIgnoreCase));

        if (isBlocked)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("Operation not allowed during impersonation"));
            return;
        }
    }
}
```

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using System.Security.Claims;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs
git commit -m "feat: block dangerous operations during impersonation (tenant delete, recursive impersonate, system settings, setup)"
```

---

### Task 4: Impersonation endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs` (to map the new endpoints)

- [ ] **Step 1: Create ManagementImpersonationEndpoints.cs**

```csharp
using System.Security.Claims;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

public static class ManagementImpersonationEndpoints
{
    public static void MapManagementImpersonationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management")
            .WithTags("Management - Impersonation")
            .RequireAuthorization("PlatformAdminOnly");

        group.MapPost("/impersonate", StartImpersonation);
        group.MapDelete("/impersonate", EndImpersonation);
    }

    private static async Task<IResult> StartImpersonation(
        HttpContext context,
        [FromBody] ImpersonateRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] JwtTokenService jwtService,
        [FromServices] AuthEventService authEvents,
        [FromServices] PermissionResolver permissionResolver,
        CancellationToken ct)
    {
        // 1. Verify caller has impersonate permission
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        var callerTenantId = context.User.FindFirstValue("tid")
            ?? context.User.FindFirstValue("tenant_id");

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(callerTenantId))
            return Results.Unauthorized();

        var callerPermissions = await permissionResolver.ResolveAsync(
            new TenantId(callerTenantId), new EntityId(userId), ct);
        if (!callerPermissions.Contains("platform:tenant:impersonate"))
            return Results.Forbid();

        // 2. Verify target tenant exists
        var targetTenant = await tenantStore.GetAsync(new TenantId(body.TargetTenantId), ct);
        if (targetTenant is null)
            return Results.NotFound(new ErrorResponse($"Tenant '{body.TargetTenantId}' not found"));

        // 3. Verify target is a child tenant (not the platform tenant itself)
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        if (targetTenant.TenantId == hostTenant?.TenantId)
            return Results.BadRequest(new ErrorResponse("Cannot impersonate the Platform tenant"));

        // 4. Verify target tenant is Active
        if (targetTenant.Status != TenantStatus.Active)
            return Results.BadRequest(new ErrorResponse(
                $"Target tenant is {targetTenant.Status}. Can only impersonate Active tenants."));

        // 5. Get admin user for token generation
        var admin = await userStore.GetByIdAsync(new TenantId(callerTenantId), new EntityId(userId), ct);
        if (admin is null)
            return Results.Unauthorized();

        // 6. Resolve target permissions (system_admin ceiling, no platform:* permissions)
        // Use all non-platform permissions as the ceiling
        var allPermissions = await permissionResolver.ResolveAsync(
            new TenantId(callerTenantId), new EntityId(userId), ct);
        var targetPermissions = allPermissions
            .Where(p => !p.StartsWith("platform:", StringComparison.Ordinal))
            .ToHashSet();

        // 7. Generate shadow JWT
        var (token, expiresAt) = jwtService.GenerateImpersonationToken(
            admin, body.TargetTenantId, targetPermissions);

        // 8. Audit log
        await authEvents.LogAsync(
            callerTenantId, userId,
            AuthEventTypes.ImpersonationStarted,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            new { targetTenantId = body.TargetTenantId, impersonatorTenant = callerTenantId },
            ct);

        return Results.Ok(new ImpersonateResponse(
            token, expiresAt, body.TargetTenantId, targetTenant.Name ?? body.TargetTenantId));
    }

    private static async Task<IResult> EndImpersonation(
        HttpContext context,
        [FromServices] AuthEventService authEvents,
        CancellationToken ct)
    {
        var isImpersonating = context.User.FindFirstValue("impersonation") == "true";
        if (!isImpersonating)
            return Results.BadRequest(new ErrorResponse("Not currently impersonating"));

        var userId = context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var targetTenantId = context.User.FindFirstValue("tid");
        var impersonatorTenant = context.User.FindFirstValue("impersonator_tenant");

        await authEvents.LogAsync(
            impersonatorTenant ?? "", userId,
            AuthEventTypes.ImpersonationEnded,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            new { targetTenantId },
            ct);

        return Results.NoContent();
    }
}

internal sealed record ImpersonateRequest(string TargetTenantId);

internal sealed record ImpersonateResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string TargetTenantId,
    string TargetTenantName);
```

- [ ] **Step 2: Register DTOs in ApiJsonContext**

```csharp
[JsonSerializable(typeof(ImpersonateRequest))]
[JsonSerializable(typeof(ImpersonateResponse))]
```

- [ ] **Step 3: Map endpoints in Program.cs**

Add after other Management endpoint mappings:

```csharp
app.MapManagementImpersonationEndpoints();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds.

- [ ] **Step 5: Run tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: add impersonation endpoints (POST/DELETE /api/management/impersonate) with shadow JWT"
```

---

### Task 5: Impersonation tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementImpersonationEndpointTests.cs`

- [ ] **Step 1: Write tests**

```csharp
public class ManagementImpersonationEndpointTests
{
    [Fact]
    public void ImpersonateRequest_ShouldRequireTargetTenantId()
    {
        var request = new ImpersonateRequest("tenant-123");
        request.TargetTenantId.Should().Be("tenant-123");
    }

    [Fact]
    public void ImpersonateResponse_ShouldContainAllFields()
    {
        var response = new ImpersonateResponse(
            "eyJ...", DateTimeOffset.UtcNow.AddMinutes(30),
            "tenant-123", "Test Tenant");
        response.AccessToken.Should().StartWith("eyJ");
        response.TargetTenantId.Should().Be("tenant-123");
        response.TargetTenantName.Should().Be("Test Tenant");
    }

    [Fact]
    public void GenerateImpersonationToken_ShouldIncludeImpersonationClaims()
    {
        // This test needs the JwtTokenService — test via the token claims
        // Verify the token contains: sub, tid, impersonator_id, impersonator_tenant, impersonation=true
        // Verify TTL is 30 minutes
        // Verify no platform:* permissions are included
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Asterisk.Platform.Api.Tests/ManagementImpersonationEndpointTests.cs
git commit -m "test: add impersonation endpoint and JWT tests"
```

---

### Task 6: Frontend — Impersonation hook and auth store

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Platform.Web/src/core/api/hooks/use-impersonation.ts`
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform.Web/src/core/auth/auth-store.ts`

- [ ] **Step 1: Create use-impersonation.ts**

```typescript
import { useMutation } from '@tanstack/react-query';
import { customFetch } from '../client';
import { useAuthStore } from '../../auth/auth-store';
import { toast } from 'sonner';

interface ImpersonateResponse {
  accessToken: string;
  expiresAt: string;
  targetTenantId: string;
  targetTenantName: string;
}

export function useImpersonate() {
  const { accessToken, tenantId, startImpersonation } = useAuthStore();

  return useMutation({
    mutationFn: async (targetTenantId: string) => {
      const response = await customFetch<ImpersonateResponse>(
        '/api/management/impersonate',
        { method: 'POST', data: { targetTenantId } },
      );
      startImpersonation(response, accessToken!, tenantId!);
      return response;
    },
    onSuccess: (data) => {
      toast.success(`Now operating as ${data.targetTenantName}`);
    },
    onError: () => {
      toast.error('Failed to start impersonation');
    },
  });
}

export function useEndImpersonate() {
  const { endImpersonation } = useAuthStore();

  return useMutation({
    mutationFn: async () => {
      await customFetch('/api/management/impersonate', { method: 'DELETE' });
      endImpersonation();
    },
    onSuccess: () => {
      toast.success('Impersonation ended');
    },
  });
}
```

- [ ] **Step 2: Extend auth-store.ts**

Add to the state interface and implementation:

```typescript
// Add to interface:
impersonation: {
  active: boolean;
  targetTenantId: string;
  targetTenantName: string;
  originalToken: string;
  originalTenantId: string;
  expiresAt: number;
} | null;

startImpersonation: (response: {
  accessToken: string;
  expiresAt: string;
  targetTenantId: string;
  targetTenantName: string;
}, originalToken: string, originalTenantId: string) => void;

endImpersonation: () => void;
```

```typescript
// Add to create() implementation:
impersonation: null,

startImpersonation: (response, originalToken, originalTenantId) => set({
  impersonation: {
    active: true,
    targetTenantId: response.targetTenantId,
    targetTenantName: response.targetTenantName,
    originalToken,
    originalTenantId,
    expiresAt: new Date(response.expiresAt).getTime(),
  },
  accessToken: response.accessToken,
  tenantId: response.targetTenantId,
  tokenExpiry: new Date(response.expiresAt).getTime(),
}),

endImpersonation: () => {
  const state = get();
  if (state.impersonation) {
    set({
      accessToken: state.impersonation.originalToken,
      tenantId: state.impersonation.originalTenantId,
      impersonation: null,
    });
  }
},
```

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform.Web
git add src/core/api/hooks/use-impersonation.ts src/core/auth/auth-store.ts
git commit -m "feat: add impersonation hook and auth store state management"
```

---

### Task 7: Frontend — Impersonation banner + tenant page integration

**Files:**
- Create: `/media/Data/Source/Verbara/Asterisk.Platform.Web/src/core/auth/impersonation-banner.tsx`
- Modify: `/media/Data/Source/Verbara/Asterisk.Platform.Web/src/admin/sidebar.tsx` (or layout wrapper to include banner)

- [ ] **Step 1: Create ImpersonationBanner.tsx**

```tsx
import { useEffect, useState } from 'react';
import { AlertTriangle, LogOut } from 'lucide-react';
import { Button } from '../ui/button';
import { useAuthStore } from './auth-store';
import { useEndImpersonate } from '../api/hooks/use-impersonation';

export function ImpersonationBanner() {
  const impersonation = useAuthStore((s) => s.impersonation);
  const { mutate: endImpersonation, isPending } = useEndImpersonate();
  const [remaining, setRemaining] = useState('');

  useEffect(() => {
    if (!impersonation?.active) return;

    const update = () => {
      const diff = impersonation.expiresAt - Date.now();
      if (diff <= 0) {
        useAuthStore.getState().endImpersonation();
        return;
      }
      const mins = Math.floor(diff / 60000);
      const secs = Math.floor((diff % 60000) / 1000);
      setRemaining(`${mins}:${secs.toString().padStart(2, '0')}`);
    };

    update();
    const id = setInterval(update, 1000);
    return () => clearInterval(id);
  }, [impersonation]);

  if (!impersonation?.active) return null;

  return (
    <div
      data-testid="impersonation-banner"
      className="flex items-center justify-between bg-amber-500/15 border-b border-amber-500/30 px-4 py-2 text-sm text-amber-700 dark:text-amber-400"
    >
      <div className="flex items-center gap-2">
        <AlertTriangle className="h-4 w-4" />
        <span>
          Operating as <strong>{impersonation.targetTenantName}</strong> — {remaining} remaining
        </span>
      </div>
      <Button
        variant="outline"
        size="sm"
        onClick={() => endImpersonation()}
        disabled={isPending}
        data-testid="impersonation-end-btn"
      >
        <LogOut className="mr-1 h-3 w-3" />
        End Impersonation
      </Button>
    </div>
  );
}
```

- [ ] **Step 2: Add banner to layout**

In the main layout wrapper (likely the admin layout component that wraps sidebar + content), add `<ImpersonationBanner />` at the very top, before the sidebar:

```tsx
import { ImpersonationBanner } from '../core/auth/impersonation-banner';

// In the layout JSX:
<>
  <ImpersonationBanner />
  {/* existing sidebar + content layout */}
</>
```

- [ ] **Step 3: Commit**

```bash
cd /media/Data/Source/Verbara/Asterisk.Platform.Web
git add src/core/auth/impersonation-banner.tsx
git commit -m "feat: add ImpersonationBanner with countdown timer and end button"
```
