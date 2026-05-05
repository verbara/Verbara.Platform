# Tenant Login Resolution — Design Spec

**Date:** 2026-03-30
**Scope:** Verbara.Platform (backend) + Verbara.Platform.Web (frontend) + demo scripts
**Goal:** Make login work across all clients (frontend, demo scripts, API) by resolving tenant from multiple sources with a clear priority chain.

## Problem Statement

The login endpoint (`POST /api/auth/login`) requires `tenantId` in the request body (line 61 of `AuthEndpoints.cs`), but:

1. **Frontend** (`login-page.tsx:101`) sends `{ email, password }` — no tenantId
2. **Demo script** (`demo-reset.sh:90`) sends `{ email, password }` — no tenantId
3. **TenantResolutionMiddleware** already resolves tenant from `X-Tenant-Id` header, but the login endpoint ignores `context.Items["TenantId"]` and only reads from the body

The result: login always fails with 400 "Tenant ID is required" unless the client explicitly includes `tenantId` in the JSON body.

## Design Constraints

1. **Email is unique per-tenant** — `IUserStore.GetByEmailAsync(tenantId, email)` requires both. Cannot search by email alone without schema changes.
2. **TenantAuthConfig is per-tenant** — MFA policy, password rules, OIDC config vary by tenant. Must know tenant before auth.
3. **SSO requires pre-auth tenant** — OIDC redirect needs tenant to load IdP config. Tenant must be resolved before auth (from subdomain, header, or body).
4. **Roadmap** — Subdomain routing (v1.1.1), SAML (v1.1.1), custom domains (v1.2.0).

## Solution: Progressive Tenant Resolution Chain

### Resolution Priority (backend)

Login endpoint accepts tenant from multiple sources, first match wins:

```
1. body.TenantId              → Explicit (API clients, scripts, forms)
2. context.Items["TenantId"]  → From middleware (X-Tenant-Id header, subdomain, URL slug)
3. None found                 → 400 "Tenant identification required"
```

### Middleware Resolution Priority

`TenantResolutionMiddleware` resolves tenant from these sources:

```
1. Webhook path:    /api/webhooks/{tenantId}/{channel}     (existing)
2. Subdomain:       acme.platform.com → "acme"             (NEW — prep for v1.1.1)
3. X-Tenant-Id:     header value                            (existing)
```

### Frontend Tenant Source

The frontend resolves tenant from environment config, then sends it as header:

```
1. VITE_DEFAULT_TENANT_ID env var  → For single-tenant / demo deployments
2. Subdomain extraction            → For multi-tenant SaaS (when deployed with subdomains)
```

The resolved tenant goes into `X-Tenant-Id` header on the login fetch call and all subsequent API calls (already done via `client.ts:82` for authenticated requests, needs to be added to login).

## Changes Required

### 1. Backend: Login Endpoint Fallback (Platform)

**File:** `src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs`

Current (line 61-64):
```csharp
if (string.IsNullOrWhiteSpace(body.TenantId))
    return Results.BadRequest(new { error = "Tenant ID is required" });

var tenantId = new TenantId(body.TenantId);
```

New:
```csharp
// Resolve tenant: body > middleware context (header/subdomain)
var rawTenantId = body.TenantId;
if (string.IsNullOrWhiteSpace(rawTenantId) && context.Items.TryGetValue("TenantId", out var ctxTenant))
    rawTenantId = ctxTenant?.ToString();

if (string.IsNullOrWhiteSpace(rawTenantId))
    return Results.BadRequest(new { error = "Tenant identification required. Provide tenantId in body or X-Tenant-Id header." });

var tenantId = new TenantId(rawTenantId);
```

Same pattern applies to `ForgotPassword` (line 309).

**Note on the TenantId storage:** The middleware currently stores `tenantId.Value` (the string) in `context.Items["TenantId"]`. When auth handlers run, they store a `TenantId` struct. The login handler must handle both types. Since login is `AllowAnonymous`, only the middleware value will be present (not the auth handler value). The `ctxTenant?.ToString()` handles the `TenantId` struct case via its implicit string conversion.

### 2. Backend: Subdomain Resolution in Middleware (Platform)

**File:** `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs`

Add subdomain extraction between webhook and header checks:

```csharp
// Subdomain: acme.platform.com → "acme"
if (tenantId is null)
{
    var host = context.Request.Host.Host;
    var dotIndex = host.IndexOf('.');
    if (dotIndex > 0)
    {
        var subdomain = host[..dotIndex];
        // Exclude common non-tenant subdomains
        if (subdomain is not ("www" or "api" or "localhost"))
            tenantId = new TenantId(subdomain);
    }
}
```

This is a no-op in current deployments (localhost, direct IP) but activates automatically when deployed with wildcard subdomains.

### 3. Backend: LoginRequest DTO (Platform)

**File:** `src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs` line 535

Make TenantId nullable in the record:

Current:
```csharp
internal sealed record LoginRequest(string TenantId, string Email, string Password);
```

New:
```csharp
internal sealed record LoginRequest(string? TenantId, string Email, string Password);
```

Same for ForgotPasswordRequest (line 538) — TenantId is already used optionally.

### 4. Frontend: Tenant Resolution Utility (Platform.Web)

**File:** Create `src/core/tenant/resolve-tenant.ts`

```typescript
/**
 * Resolve the current tenant ID from available sources.
 * Priority: env var > subdomain extraction
 */
export function resolveDefaultTenant(): string | null {
  // 1. Explicit env var (demo, single-tenant deployments)
  const envTenant = import.meta.env.VITE_DEFAULT_TENANT_ID;
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

### 5. Frontend: Login Page Sends Tenant (Platform.Web)

**File:** `src/core/auth/login-page.tsx` lines 101-106

Current:
```typescript
const res = await fetch('/api/auth/login', {
  method: 'POST',
  credentials: 'include',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email, password }),
});
```

New:
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

Sends tenant in BOTH body and header — belt and suspenders. Backend prefers body, falls back to header via middleware.

### 6. Frontend: Environment Config (Platform.Web)

**File:** `.env` (demo/development default)

```
VITE_DEFAULT_TENANT_ID=demo
```

**File:** `.env.production` (multi-tenant SaaS — no default, uses subdomain)

```
# No default tenant — resolved from subdomain
```

### 7. Demo Script Fix (Platform)

**File:** `docker/demo/demo-reset.sh` line 90-92

Current:
```bash
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"email":"admin@demo.local","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
```

New:
```bash
curl -sf -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"demo","email":"admin@demo.local","password":"DemoAdmin2026!"}' > /dev/null 2>&1 || true
```

## Roadmap: Complete Tenant Resolution

### Done (Plan 24 + this spec)

- [x] InMemory RBAC stores (DI bug fixed)
- [x] AGI/ARI hosted services (SDK 1.5.2)
- [x] 0 warnings, TreatWarningsAsErrors=true

### Now: Plan 25 — Tenant Login Resolution

- [ ] Login endpoint accepts tenant from body OR middleware context
- [ ] LoginRequest.TenantId nullable
- [ ] ForgotPassword same treatment
- [ ] Middleware adds subdomain resolution (no-op on localhost, activates on subdomains)
- [ ] Frontend `resolveDefaultTenant()` utility
- [ ] Frontend login sends tenantId in body + X-Tenant-Id header
- [ ] Frontend `.env` with `VITE_DEFAULT_TENANT_ID=demo`
- [ ] Demo script curl includes tenantId
- [ ] Tests updated

### v1.1.1 — Full Subdomain Routing (future)

- [ ] Wildcard DNS + TLS provisioning
- [ ] Tenant validation in middleware (verify tenant exists in ITenantStore)
- [ ] Login page loads TenantAuthConfig from subdomain → shows SSO button if OIDC enabled
- [ ] SAML SP integration per-tenant
- [ ] Custom domain support (CNAME + domain-to-tenant table)
- [ ] IP allowlisting per-tenant (checked in middleware before auth)
- [ ] Frontend tenant selector for super-admins with multi-tenant access

### v1.2.0 — Advanced Identity (future)

- [ ] SCIM provisioning (auto-create users from IdP)
- [ ] LDAP directory sync
- [ ] WebAuthn / passkeys
- [ ] Email-domain discovery (HRD — Home Realm Discovery for SSO routing)
- [ ] Tenant switching without re-login (for multi-tenant users)

## Verification

### Backend Tests

1. Login with `tenantId` in body → 200 (existing behavior preserved)
2. Login without `tenantId` in body, with `X-Tenant-Id` header → 200 (new fallback)
3. Login without `tenantId` in body, without header → 400 with clear error message
4. ForgotPassword with same fallback behavior
5. Subdomain extraction: `acme.platform.com` → tenant "acme"
6. Subdomain exclusion: `www.platform.com` → no tenant
7. Localhost: `localhost` → no tenant (no dots, no subdomain)

### Frontend Tests

1. `resolveDefaultTenant()` returns env var when set
2. `resolveDefaultTenant()` extracts subdomain from `acme.platform.com`
3. `resolveDefaultTenant()` returns null on `localhost`
4. Login request includes tenantId in body and X-Tenant-Id header

### Integration

1. Demo environment: frontend at `localhost` with `VITE_DEFAULT_TENANT_ID=demo` → login works
2. Demo script curl with tenantId → login works
3. API client with `X-Tenant-Id: demo` header → login works

## Files Changed Summary

| Repo | File | Change |
|------|------|--------|
| Platform | `AuthEndpoints.cs` | Login/ForgotPassword fallback to middleware context |
| Platform | `AuthEndpoints.cs` | LoginRequest.TenantId nullable |
| Platform | `TenantResolutionMiddleware.cs` | Add subdomain resolution |
| Platform | `demo-reset.sh` | Add tenantId to curl body |
| Platform | Tests | New tests for fallback + subdomain |
| Platform.Web | `resolve-tenant.ts` | New utility (env var + subdomain extraction) |
| Platform.Web | `login-page.tsx` | Send tenant in body + header |
| Platform.Web | `.env` | Add VITE_DEFAULT_TENANT_ID=demo |
