# ADR-0028 — Setup creates a mandatory Customer tenant

**Status:** Accepted
**Date:** 2026-05-30
**Supersedes:** —
**Superseded by:** —
**Related:** [ADR-0027](0027-tenant-type-operational-gate.md) (tenant-type operational gate — the gap this closes); [ADR-0026](0026-queue-membership-executive-routing.md) (membership routing). Spec: [docs/specs/2026-05-30-setup-multitenant-platform-customer.md](../specs/2026-05-30-setup-multitenant-platform-customer.md).

---

## Context

The platform recognizes 3 tenant types ([`TenantType`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/TenantType.cs)): `Platform` (0, host admin-only), `Partner` (1, reseller, optional), `Customer` (2, the only **operational** tenant — agents, queues, conversations).

ADR-0027 enforces at the endpoint surface that **only `Customer` tenants can operate** (`RequireOperationalTenant()` → HTTP 409 for Platform/Partner callers). But `POST /api/setup` created **only** the `platform` tenant + a Platform Admin. After a clean SMB Docker install the instance had exactly one tenant (`platform`) and zero Customers — and the Platform Admin could not create agents/queues/conversations (ADR-0027 returns 409). The operator had to manually create a Customer via `/management/tenants` before the contact center was usable. For the SMB target topology (`Platform + 1×Customer`), this was a missing, undocumented manual step that made a fresh install appear broken.

Two pre-existing weaknesses compounded it: setup never validated passwords against the tenant password policy (only non-empty), and the frontend zod schema enforced `min 8` while the backend default policy is `min 12`.

## Decision

**`POST /api/setup` now creates, in one operation, the `platform` tenant + Platform Admin AND a mandatory first `Customer` tenant + Customer Admin.** The Customer fields are a **hard requirement** — setup returns HTTP 400 if any is missing. This is a **breaking change** to the setup contract (an intentional one, made in the pre-customer window where blast radius is zero).

`SetupRequest` gains: `CustomerTenantId`, `CustomerName`, `CustomerAdminEmail`, `CustomerAdminPassword` (required) + `CustomerAdminDisplayName` (optional). `SetupResponse` gains: `CustomerTenantId`, `CustomerUserId`.

Validations (→ 400): any Customer field missing; `CustomerTenantId` not a valid lowercase slug or equal to `"platform"`; `CustomerAdminEmail` equal to the Platform Admin `Email` (case-insensitive); password policy (`PasswordService.ValidatePolicy`, platform defaults min 12 + uppercase + number) failing on **either** password. Emails are normalized (`Trim().ToLowerInvariant()`) for both comparison and persistence. The Customer is `Type=Customer`, `ParentTenantId="platform"`, `Status=Active`, default `TenantOptions`; the Customer Admin is `UserRole.Admin` in the Customer tenant with a best-effort clone of the `admin` role template.

The returned `accessToken` remains the **Platform Admin's** token; operators log in as the Customer Admin (or impersonate the Customer) to drive operational endpoints.

### Behavior notes

- **Non-atomic first-run.** The multi-write sequence (host tenant → orphan adoption → platform admin → mgmt key → customer tenant → customer admin) has no surrounding transaction. A storage fault mid-sequence can leave a partially-initialized install that the 409 sentinel then prevents re-running. Accepted trade-off (partial failure → 500); a future follow-up may make first-run idempotent/transactional. Documented in-code at the step-1 host-tenant write.
- **Impersonation unchanged.** A Platform Admin impersonating the Customer resolves the Customer tenant, so ADR-0027's gate passes naturally.

## Consequences

**Positive:** a fresh SMB install is immediately operational — `Platform + 1×Customer` out of the box, no manual tenant step. Password policy is now enforced at setup (was bypassed). Frontend/backend password rules aligned (12 + upper + number).

**Breaking:** every caller of `/api/setup` with the old 4-field body now gets HTTP 400. All in-repo callers were migrated in the same change set: `docker/demo/demo-reset.sh` (creates Customer "demo"), `scripts/seed-staging.sh` (creates Customer "staging"), `docs/getting-started.md`, `docs/manuales/smb/03-setup-inicial.md`, and the Api.Tests (`SetupEndpointTests`, `VersioningTests`). External/automation callers must add the Customer fields.

**Out of scope (deferred):** force-password-change on first login for the Customer Admin; full transactional/idempotent first-run.

## Alternatives considered

- **Optional Customer (create only if fields present).** Rejected — does not guarantee the "Customer mandatory" invariant; a fresh install could still land unusable, which is the whole problem.
- **Leave setup as-is, document the manual `/management/tenants` step.** Rejected — a product that looks broken on first run (every operational click → 409) is unacceptable for the SMB track; documentation does not fix the UX.
