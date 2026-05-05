# Plan 25: Tenant Login Resolution — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make login work across all clients by resolving tenant from body OR middleware context (header/subdomain), with prep for v1.1.1 subdomain routing.

**Architecture:** Login endpoint falls back to `context.Items["TenantId"]` when body.TenantId is null. Middleware gains subdomain extraction. Frontend sends tenant from env var via body + header. Demo script fixed.

**Tech Stack:** .NET 10, xunit, FluentAssertions, NSubstitute, React (Vite)

**Spec:** `docs/superpowers/specs/2026-03-30-tenant-login-resolution-design.md`

---

## File Structure

| File | Responsibility | Action |
|------|---------------|--------|
| `src/.../Middleware/TenantResolutionMiddleware.cs` | Resolve tenant from webhook path, subdomain, header | Modify: add subdomain |
| `src/.../Endpoints/AuthEndpoints.cs` | Login + ForgotPassword handlers, DTOs | Modify: fallback + nullable DTO |
| `tests/.../TenantResolutionMiddlewareTests.cs` | Unit tests for middleware resolution chain | Create |
| `tests/.../AuthIntegrationTests.cs` | Integration tests for login with various tenant sources | Modify: add new test cases |
| `docker/demo/demo-reset.sh` | Demo warmup curl | Modify: add tenantId to body |

**Platform.Web (separate repo: `/media/Data/Source/Verbara/Asterisk.Platform.Web/`):**

| File | Responsibility | Action |
|------|---------------|--------|
| `src/core/tenant/resolve-tenant.ts` | Resolve tenant from env var or subdomain | Create |
| `src/core/auth/login-page.tsx` | Login form submit | Modify: send tenant in body + header |
| `.env` | Dev/demo defaults | Modify: add VITE_DEFAULT_TENANT_ID |

---

## Phase A: Backend — Middleware + Auth Endpoint (Platform repo)

> **Working directory:** `/media/Data/Source/Verbara/Asterisk.Platform/`

### Task 1: Add Subdomain Resolution to TenantResolutionMiddleware

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/TenantResolutionMiddlewareTests.cs`

- [ ] **Step 1: Write tests for subdomain resolution**

Create `tests/Asterisk.Platform.Api.Tests/TenantResolutionMiddlewareTests.cs`:

```csharp
using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantResolutionMiddlewareTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public TenantResolutionMiddlewareTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("acme.platform.com", "acme")]
    [InlineData("demo.myapp.io", "demo")]
    [InlineData("tenant-1.example.com", "tenant-1")]
    public async Task Subdomain_ShouldResolveTenant(string host, string expectedTenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        var response = await _client.SendAsync(request);

        // Health endpoint doesn't require tenant, but we verify the middleware
        // doesn't break the pipeline. Tenant resolution is tested via login in Task 2.
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Theory]
    [InlineData("www.platform.com")]
    [InlineData("api.platform.com")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("platform.com")]
    public async Task Subdomain_ShouldNotResolveTenant_ForExcludedHosts(string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Header_ShouldResolveTenant()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Tenant-Id", "header-tenant");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass (middleware doesn't break pipeline)**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter TenantResolutionMiddlewareTests -v q`
Expected: All pass (health endpoint is unaffected by tenant resolution)

- [ ] **Step 3: Add subdomain resolution to middleware**

In `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`, replace the entire `InvokeAsync` method:

```csharp
    public async Task InvokeAsync(HttpContext context)
    {
        TenantId? tenantId = null;

        // Webhook routes: /api/webhooks/{tenantId}/{channel}
        if (context.Request.Path.StartsWithSegments("/api/webhooks", out var remaining))
        {
            var segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is { Length: >= 1 } && !string.IsNullOrWhiteSpace(segments[0]))
                tenantId = new TenantId(segments[0]);
        }

        // Subdomain: acme.platform.com → "acme"
        if (tenantId is null)
        {
            var host = context.Request.Host.Host;
            var dotIndex = host.IndexOf('.');
            if (dotIndex > 0)
            {
                var subdomain = host[..dotIndex];
                if (subdomain is not ("www" or "api" or "localhost"))
                    tenantId = new TenantId(subdomain);
            }
        }

        // API routes: X-Tenant-Id header
        if (tenantId is null &&
            context.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue))
        {
            tenantId = new TenantId(headerValue.ToString());
        }

        if (tenantId is not null)
            context.Items["TenantId"] = tenantId.Value;

        await _next(context);
    }
```

- [ ] **Step 4: Verify build and tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter TenantResolutionMiddlewareTests -v q`
Expected: Build succeeded, all tests pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs tests/Asterisk.Platform.Api.Tests/TenantResolutionMiddlewareTests.cs
git commit -m "feat: add subdomain resolution to TenantResolutionMiddleware

Middleware now resolves tenant from: webhook path > subdomain > X-Tenant-Id header.
Excluded subdomains: www, api, localhost. Prep for v1.1.1 subdomain routing."
```

### Task 2: Login Endpoint — Fallback to Middleware Context

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs:61-64,93-94,309,535,538`
- Modify: `tests/Asterisk.Platform.Api.Tests/AuthIntegrationTests.cs`

- [ ] **Step 1: Write failing tests for login with header-only tenant**

Add these tests to `tests/Asterisk.Platform.Api.Tests/AuthIntegrationTests.cs`. Find the class and add:

```csharp
    [Fact]
    public async Task Login_ShouldAcceptTenantFromHeader_WhenBodyOmitsTenantId()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                email = "nonexistent@test.com",
                password = "wrong"
            })
        };
        request.Headers.Add("X-Tenant-Id", "demo");

        var response = await _anonClient.SendAsync(request);

        // 401 = tenant resolved, user not found (not 400 = tenant missing)
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_ShouldReturn400_WhenNoTenantProvided()
    {
        var response = await _anonClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = "test@test.com",
            password = "wrong"
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Tenant identification required");
    }

    [Fact]
    public async Task Login_ShouldPreferBodyTenant_OverHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                tenantId = "demo",
                email = "nonexistent@test.com",
                password = "wrong"
            })
        };
        request.Headers.Add("X-Tenant-Id", "other-tenant");

        var response = await _anonClient.SendAsync(request);

        // Should use "demo" from body, not "other-tenant" from header
        // Either way returns 401 (user not found), but proves body takes precedence
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_ShouldAcceptTenantFromSubdomain()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                email = "nonexistent@test.com",
                password = "wrong"
            })
        };
        request.Headers.Host = "demo.platform.com";

        var response = await _anonClient.SendAsync(request);

        // 401 = tenant resolved via subdomain, user not found
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "Login_ShouldAcceptTenantFromHeader_WhenBodyOmitsTenantId|Login_ShouldReturn400_WhenNoTenantProvided|Login_ShouldPreferBodyTenant_OverHeader|Login_ShouldAcceptTenantFromSubdomain" -v q`
Expected: `Login_ShouldAcceptTenantFromHeader_WhenBodyOmitsTenantId` FAILS (returns 400 instead of 401), `Login_ShouldReturn400_WhenNoTenantProvided` FAILS (error message doesn't match), `Login_ShouldAcceptTenantFromSubdomain` FAILS (returns 400)

- [ ] **Step 3: Make LoginRequest.TenantId nullable**

In `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs`, change line 535:

From:
```csharp
internal sealed record LoginRequest(string TenantId, string Email, string Password);
```

To:
```csharp
internal sealed record LoginRequest(string? TenantId, string Email, string Password);
```

- [ ] **Step 4: Implement tenant fallback in Login handler**

In `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs`, replace lines 61-64:

From:
```csharp
        if (string.IsNullOrWhiteSpace(body.TenantId))
            return Results.BadRequest(new { error = "Tenant ID is required" });

        var tenantId = new TenantId(body.TenantId);
```

To:
```csharp
        // Resolve tenant: body > middleware context (header/subdomain)
        var rawTenantId = body.TenantId;
        if (string.IsNullOrWhiteSpace(rawTenantId) && context.Items.TryGetValue("TenantId", out var ctxTenant))
            rawTenantId = ctxTenant?.ToString();

        if (string.IsNullOrWhiteSpace(rawTenantId))
            return Results.BadRequest(new { error = "Tenant identification required. Provide tenantId in body or X-Tenant-Id header." });

        var tenantId = new TenantId(rawTenantId);
```

Also update line 71 and 82 where `body.TenantId` is passed to `authEvents.LogAsync` — replace with `rawTenantId`:

Line 71: `await authEvents.LogAsync(rawTenantId!, null, ...`
Line 82: `await authEvents.LogAsync(rawTenantId!, user.UserId.Value, ...`
Line 94: `TenantId = rawTenantId!,`

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "Login_Should" -v q`
Expected: All pass

- [ ] **Step 6: Apply same pattern to ForgotPassword**

In `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs`, add `HttpContext context` parameter to ForgotPassword and implement fallback.

Change ForgotPassword signature (line 303) from:
```csharp
    private static async Task<IResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest body,
        [FromServices] IUserStore userStore,
        CancellationToken ct)
```

To:
```csharp
    private static async Task<IResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest body,
        HttpContext context,
        [FromServices] IUserStore userStore,
        CancellationToken ct)
```

Replace line 309 from:
```csharp
        if (!string.IsNullOrWhiteSpace(body.TenantId) && !string.IsNullOrWhiteSpace(body.Email))
```

To:
```csharp
        var forgotTenantId = body.TenantId;
        if (string.IsNullOrWhiteSpace(forgotTenantId) && context.Items.TryGetValue("TenantId", out var ctxForgotTenant))
            forgotTenantId = ctxForgotTenant?.ToString();

        if (!string.IsNullOrWhiteSpace(forgotTenantId) && !string.IsNullOrWhiteSpace(body.Email))
```

And update line 311 from `new TenantId(body.TenantId)` to `new TenantId(forgotTenantId!)`, and line 318 from `TenantId = body.TenantId` to `TenantId = forgotTenantId!`.

- [ ] **Step 7: Full build + test**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: Build succeeded, 0 warnings, all tests pass

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs tests/Asterisk.Platform.Api.Tests/AuthIntegrationTests.cs
git commit -m "feat: login accepts tenant from body OR middleware context (header/subdomain)

LoginRequest.TenantId is now nullable. When missing, falls back to
context.Items[\"TenantId\"] set by TenantResolutionMiddleware
(X-Tenant-Id header or subdomain). Same for ForgotPassword.
Body tenant takes precedence over header/subdomain."
```

### Task 3: Fix Demo Script

**Files:**
- Modify: `docker/demo/demo-reset.sh:90-92`

- [ ] **Step 1: Add tenantId to demo login curl**

In `docker/demo/demo-reset.sh`, change lines 90-92:

From:
```bash
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"email":"admin@demo.local","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
```

To:
```bash
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"demo","email":"admin@demo.local","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
```

- [ ] **Step 2: Commit**

```bash
git add docker/demo/demo-reset.sh
git commit -m "fix(demo): add tenantId to login curl in demo-reset.sh"
```

---

## Phase B: Frontend — Tenant Resolution (Platform.Web repo)

> **Working directory:** `/media/Data/Source/Verbara/Asterisk.Platform.Web/`

### Task 4: Create resolveDefaultTenant Utility

**Files:**
- Create: `src/core/tenant/resolve-tenant.ts`

- [ ] **Step 1: Create the utility**

```typescript
/**
 * Resolve the current tenant ID from available sources.
 * Priority: env var > subdomain extraction.
 */
export function resolveDefaultTenant(): string | null {
  // 1. Explicit env var (demo, single-tenant deployments)
  const envTenant = import.meta.env.VITE_DEFAULT_TENANT_ID as string | undefined;
  if (envTenant) return envTenant;

  // 2. Subdomain extraction (multi-tenant SaaS)
  const host = window.location.hostname;
  const parts = host.split('.');
  if (parts.length >= 3) {
    const subdomain = parts[0];
    if (!['www', 'api', 'localhost'].includes(subdomain)) {
      return subdomain;
    }
  }

  return null;
}
```

- [ ] **Step 2: Verify it builds**

Run: `npx tsc --noEmit`
Expected: No type errors

### Task 5: Update Login Page to Send Tenant

**Files:**
- Modify: `src/core/auth/login-page.tsx:95-106`

- [ ] **Step 1: Import resolveDefaultTenant and update login fetch**

At the top of `src/core/auth/login-page.tsx`, add the import:

```typescript
import { resolveDefaultTenant } from '../tenant/resolve-tenant';
```

Then change the `handleEmailLogin` function's fetch call (around lines 101-106):

From:
```typescript
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
      });
```

To:
```typescript
      const tenant = resolveDefaultTenant();
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          ...(tenant && { 'X-Tenant-Id': tenant }),
        },
        body: JSON.stringify({ tenantId: tenant, email, password }),
      });
```

- [ ] **Step 2: Verify build**

Run: `npx tsc --noEmit`
Expected: No type errors

### Task 6: Add Environment Config and Commit

**Files:**
- Modify: `.env` (or create if not exists)

- [ ] **Step 1: Add VITE_DEFAULT_TENANT_ID to .env**

Add this line to the `.env` file in the Platform.Web root:

```
VITE_DEFAULT_TENANT_ID=demo
```

If a `.env.production` exists, do NOT add this line there (production uses subdomain resolution).

- [ ] **Step 2: Verify frontend builds**

Run: `npm run build`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/core/tenant/resolve-tenant.ts src/core/auth/login-page.tsx .env
git commit -m "feat: send tenant on login from env var or subdomain

resolveDefaultTenant() reads VITE_DEFAULT_TENANT_ID (demo/single-tenant)
or extracts subdomain (multi-tenant SaaS). Login sends tenant in both
body and X-Tenant-Id header for belt-and-suspenders compatibility."
```

---

## Phase C: Platform Docs Update

### Task 7: Update CLAUDE.md and Plan

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update test count**

Run: `dotnet test Asterisk.Platform.slnx -v q 2>&1 | grep -oP 'Passed:\s+\K\d+' | paste -sd+ | bc`

Update `CLAUDE.md` header with the new test count.

- [ ] **Step 2: Add Plan 25 to CLAUDE.md**

After the Plan 24 section, add:

```markdown
## Plan 25: Tenant Login Resolution -- COMPLETE (2026-03-30)

**Spec:** `docs/superpowers/specs/2026-03-30-tenant-login-resolution-design.md`
**Plan:** `docs/superpowers/plans/2026-03-30-plan25-tenant-login.md`

Progressive tenant resolution chain:
1. **Login fallback** -- accepts tenant from body OR middleware context (X-Tenant-Id header, subdomain)
2. **Subdomain prep** -- TenantResolutionMiddleware extracts subdomain (no-op on localhost, activates on wildcard DNS)
3. **Frontend env** -- VITE_DEFAULT_TENANT_ID for demo/single-tenant, subdomain extraction for SaaS
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/superpowers/plans/2026-03-30-plan25-tenant-login.md
git commit -m "docs: update CLAUDE.md — Plan 25 complete, tenant login resolution"
```
