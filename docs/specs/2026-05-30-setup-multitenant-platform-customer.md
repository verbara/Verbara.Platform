# Spec — Setup multi-tenant: Platform + Customer obligatorio

**Status:** Approved
**Date:** 2026-05-30
**Scope:** Backend (`Verbara.Platform.Api`) + Web wizard (`Verbara.Platform.Web`)
**Related:** [ADR-0027 tenant-type operational gate](../decisions/0027-tenant-type-operational-gate.md), [ADR-0026 Phase A.6](../decisions/0026-queue-membership-executive-routing.md), [project_tenant_architecture memory](../../../.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_tenant_architecture.md)

---

## Context

The platform recognizes 3 tenant types ([`TenantType`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/TenantType.cs)):

| Value | Name | Role | Parent |
|---|---|---|---|
| 0 | `Platform` | Host único de la instancia Verbara (administrativo puro) | NULL |
| 1 | `Partner` | Reseller / white-label | Platform (siempre) |
| 2 | `Customer` | Tenant operativo (agentes, colas, conversaciones) | Platform (venta directa) o Partner |

ADR-0027 (shipped 2026-05-28) enforces at the endpoint surface that **only `Customer` tenants can operate** agents/queues/conversations (`RequireOperationalTenant()` → HTTP 409 for Platform/Partner callers).

### The gap

`POST /api/setup` ([`SetupEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs)) currently creates **only the `platform` tenant** + a Platform Admin user. It does NOT create any `Customer`. After a clean SMB Docker single-host install, the instance has only the `platform` tenant and zero Customers — and the Platform Admin cannot operate (ADR-0027 returns 409). The operator must manually create a Customer via `/management/tenants` before the contact center is usable. For the SMB target topology (`Platform + 1×Customer`), this is a missing step the setup should resolve.

Two pre-existing weaknesses also surfaced and are corrected here:
- The setup never validates passwords against the tenant password policy (only checks non-empty). The canonical validator `PasswordService.ValidatePolicy` exists ([`PasswordService.cs:148`](../../src/Verbara.Platform.Api/Services/PasswordService.cs#L148)) but setup bypasses it.
- The frontend zod schema enforces `min 8` ([`setup-page.tsx:18`](../../../Verbara.Platform.Web/src/core/auth/setup-page.tsx#L18)) while the backend default policy is `MinLength=12` — a silent mismatch.

## Goals

1. `POST /api/setup` creates, in one atomic operation: `platform` tenant + Platform Admin + **`Customer` tenant + Customer Admin**.
2. Customer fields are a **hard requirement** — setup returns `400` if any are missing.
3. Enforce the tenant password policy on **both** admin passwords.
4. Web setup wizard collects the Customer data and is **fully i18n** (no hardcoded strings), matching the platform's CI-enforced EN/ES/PT parity.

## Non-goals

- No change to RBAC, ADR-0027, or the tenant hierarchy invariants (already enforced).
- No "force password change on first login" (out of scope, can be a follow-up).
- No change to orphan-adoption logic in setup (left as-is; the new Customer has an explicit `parent=platform`).
- No new Customer-creation path; reuses the existing model pattern from [`ManagementTenantEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs).

---

## Design

### Approach

Extend the single `Setup` handler in [`SetupEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs). No new service, no new AOT JSON types — `SetupRequest`/`SetupResponse` are already registered in [`ApiJsonContext.cs:250-251`](../../src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs#L250); we only add fields to the existing records. Customer creation reuses the field-for-field pattern already proven in `ManagementTenantEndpoints.CreateTenant` (default `TenantOptions`, plan Starter, `Type=Customer`, `ParentTenantId=platform`).

### Contract

**`SetupRequest`** (new fields in **bold**; all Customer fields required except the optional display name):

```
Email                       string   // Platform Admin (existing)
Password                    string   // Platform Admin (existing)
DisplayName                 string?  // Platform Admin (existing)
PlatformName                string?  // (existing)
CustomerTenantId            string   // NEW — slug, must be unique and != "platform"
CustomerName                string   // NEW
CustomerAdminEmail          string   // NEW
CustomerAdminPassword       string   // NEW
CustomerAdminDisplayName    string?  // NEW (optional)
```

**`SetupResponse`** (extended):

```
TenantId            string   // "platform" (existing)
UserId              string   // Platform Admin user id (existing)
AccessToken         string   // Platform Admin JWT (existing)
ManagementApiKey    string   // mgmt_... (existing)
CustomerTenantId    string   // NEW
CustomerUserId      string   // NEW — Customer Admin user id
```

The access token returned remains the **Platform Admin's** token (the operator lands on the platform admin context first; can log in as the Customer Admin separately).

### Validation (order)

1. Guard: `GetHostTenantAsync` non-null → `409 Conflict` (existing).
2. Required fields: `Email`, `Password`, `CustomerTenantId`, `CustomerName`, `CustomerAdminEmail`, `CustomerAdminPassword` all non-blank → else `400 BadRequest`.
3. `CustomerTenantId` must be a valid slug and **`!= "platform"`** → else `400`.
4. `CustomerAdminEmail` (normalized, case-insensitive) **`!=`** `Email` → else `400` ("Platform admin and customer admin must use different emails").
5. Password policy on **both** passwords via `PasswordService.ValidatePolicy(pwd, config)` where `config = new TenantAuthConfig { TenantId = ... }` (platform defaults: MinLength=12, RequireUppercase, RequireNumber). On failure → `400` with `ErrorDetailResponse("Password does not meet policy", errors)` (same shape as [`AuthEndpoints.cs:456`](../../src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs#L456)).

> Note: at setup time no tenant-specific `TenantAuthConfig` exists yet, so policy uses platform defaults. This is intentional and matches the `?? new TenantAuthConfig` fallback used elsewhere.

### Execution flow

1. Guard + validate (above).
2. Create `platform` tenant (`Type=Platform`, `ParentTenantId=null`) — existing.
3. Create Platform Admin user + clone `platform_admin` role template (best-effort try/catch) + Management API Key + Platform Admin JWT — existing.
4. Create Customer tenant (`Type=Customer`, `ParentTenantId="platform"`, `Status=Active`, default `TenantOptions { MaxConcurrentChannels=100, MaxActiveCampaigns=10 }`, plan Starter metadata) — mirrors `ManagementTenantEndpoints.CreateTenant`.
5. Create Customer Admin user (in the Customer tenant, `UserRole.Admin`) + best-effort clone the tenant `admin` role template (same try/catch tolerance as the Platform Admin RBAC wiring).
6. Return extended `SetupResponse`.

Failure semantics: steps 2–6 are sequential `UpsertAsync`/`SaveAsync` calls against the same stores as today; no transaction wrapper exists in the current setup and none is added (consistent with existing behavior — the 409 guard prevents re-entry, and a partial failure surfaces as a 500 the operator can diagnose). RBAC role-clone steps remain best-effort (the `UserRole.Admin` fallback grants day-1 admin perms).

### Tests (backend)

Update [`SetupEndpointTests.cs`](../../tests/Verbara.Platform.Api.Tests/SetupEndpointTests.cs) — the 3 existing tests will break under the hard requirement (expected) and are updated to supply Customer fields. New coverage:

- `Setup_ShouldCreateBothTenantsAndAdmins_WhenValid` — asserts `platform` + Customer tenants exist (via `ITenantStore`), both admin users exist (via `IUserStore`), Customer has `Type=Customer` + `ParentTenantId="platform"`, response carries `CustomerTenantId`/`CustomerUserId`.
- `Setup_ShouldReturn400_WhenCustomerFieldsMissing` (parametrized per missing field).
- `Setup_ShouldReturn400_WhenCustomerTenantIdIsPlatform`.
- `Setup_ShouldReturn400_WhenEmailsMatch`.
- `Setup_ShouldReturn400_WhenPasswordBelowPolicy` (e.g. 8-char password fails MinLength=12).
- Existing `Setup_ShouldReturn409_WhenHostTenantAlreadyExists` unchanged.

Target: Api.Tests green (1017 → ~1023), 0 warnings, AOT-clean.

### Web wizard (producto final, i18n completo)

[`setup-page.tsx`](../../../Verbara.Platform.Web/src/core/auth/setup-page.tsx) today hardcodes all strings in English (does NOT use `t()`), unlike [`login-page.tsx`](../../../Verbara.Platform.Web/src/core/auth/login-page.tsx). For a production-grade result:

- Migrate `setup-page.tsx` to `useTranslation()`.
- Add a `setup` block (existing strings + new Customer field labels + validation messages) to **`common.json` in all 3 locales** (`en-US`, `es-419`, `pt-BR`) under `public/locales/` — i18n parity is CI-enforced.
- Add a 3rd fieldset "Customer / Empresa": Company Name, Tenant Id, Customer Admin Email, Customer Admin Password, each with `data-testid` (`setup-customer-name`, `setup-customer-tenant-id`, `setup-customer-admin-email`, `setup-customer-admin-password`).
- Update the zod schema: align password rule to the real policy (min 12 + uppercase + number), add Customer fields, cross-field rule for distinct emails.
- Extend `SetupInput`/`SetupResponse` in [`use-system.ts`](../../../Verbara.Platform.Web/src/core/api/hooks/use-system.ts).
- Update [`setup-page.test.tsx`](../../../Verbara.Platform.Web/src/core/auth/setup-page.test.tsx) for the new fields + aligned validation.

### Docs

Refresh [`docs/manuales/smb/03-setup-inicial.md`](../manuales/smb/03-setup-inicial.md) — the SMB manual documents the setup wizard; the new Customer step MUST be reflected there in the same change set (per the `docs/manuales/smb/` lifecycle rule in CLAUDE.md).

---

## Risks & mitigations

- **Breaking the setup contract:** acceptable — pre-customer window (2026-05-25 pivot), zero live installs. The hard requirement is the whole point.
- **i18n parity CI failure:** mitigated by adding keys to all 3 locales in the same commit.
- **Frontend/backend password-rule drift:** eliminated by aligning the zod rule to the documented policy defaults.

## Verification

- `dotnet build Verbara.Platform.slnx -c Release` → 0 warnings.
- `dotnet test` Api.Tests → green.
- `npx vitest run src/core/auth/setup-page.test.tsx` → green.
- `npx eslint .` + i18n parity check → green.
