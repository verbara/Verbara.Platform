# ADR-0027 — Tenant-type operational gate

**Status:** Accepted
**Date:** 2026-05-28
**Supersedes:** —
**Superseded by:** —
**Related:** [ADR-0002 multi-tenant RBAC topology] (referenced via `PlatformAdminAuthorizationHandler` design); [ADR-0026](0026-queue-membership-executive-routing.md) Phase A.6 introduced the editor that exposes membership to whichever tenant is logged in — directly motivated this gap audit.

---

## Context

The platform recognizes 3 tenant types ([`TenantType` enum](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/TenantType.cs)):

| Value | Name | Conceptual role |
|---|---|---|
| 0 | `Platform` | Host único de la instancia Verbara. Administrativo puro — gestiona tenants hijos, licensing, system config, cluster, impersonation, audit cross-tenant. |
| 1 | `Partner` | Reseller / white-label. Comercial-administrativo — gestiona SUS Customers, rate cards, revenue, settings de branding. |
| 2 | `Customer` | Tenant operativo. Aquí viven agentes, colas, conversaciones, campañas, channels, skills, bots, flows. |

The hierarchy invariants are enforced today:

- `platform` is DB-enforced unique root (`ix_tenants_platform_unique WHERE type=0` in migration [`007_TenantsAndScheduledReports.sql:21`](../../src/Verbara.Platform.Storage.Postgres/Migrations/007_TenantsAndScheduledReports.sql#L21)).
- `Partner` must be a child of `Platform`, `Customer` must be a child of `Platform` or `Partner` ([`ManagementTenantEndpoints.cs:104-108`](../../src/Verbara.Platform.Api/Endpoints/ManagementTenantEndpoints.cs#L104)).
- `platform` cannot be suspended, deleted, or impersonated.
- Max depth 3 (no sub-customers) enforced in [`PostgresTenantStore.cs:49-65`](../../src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantStore.cs#L49).
- Cross-tenant `X-Tenant-Id` override is honored only for `Platform` and `Partner` callers ([`TenantBoundaryValidationMiddleware.cs:93`](../../src/Verbara.Platform.Api/Middleware/TenantBoundaryValidationMiddleware.cs#L93)).
- `platform` bypasses plan feature gates ([`PlanFeatureGateExtensions.cs:22`](../../src/Verbara.Platform.Api/Endpoints/PlanFeatureGateExtensions.cs#L22)).

### The gap

**Operational endpoints do not enforce `TenantType`.** Every `/admin/*`, `/conversations`, `/queues/{queueId}/members`, `/operations/*` endpoint group uses only `RequireAuthorization("AdminOnly")` / `Authenticated` / `SupervisorPlus`. The 68 `RequireAuthorization` sites across `src/Verbara.Platform.Api/Endpoints/` do **not** discriminate by `TenantType`.

Concretely: a user authenticated against `tenant=platform` (or any `Partner` tenant) with an Admin role can:

- `POST /api/v1/admin/queues` and create a queue called "Atención General" on the `platform` tenant.
- `POST /api/v1/admin/agents` and attach the platform-admin user as an agent on the `platform` tenant.
- `POST /api/v1/admin/channels/webchat` and configure a WebChat widget pointing customers to the `platform` tenant.
- `POST /api/v1/queues/{queueId}/members` and materialize a `queue_memberships` row that the SDK Pro `IRealtimeSyncService` will gladly push to Asterisk's `queue_members`.

This is **not blocked anywhere**. The contract "`platform` is administrative-only, `Partner` is commercial-only, only `Customer` operates" is honored today only by convention (operator discipline + UI navigation) — not by code. The same applies to a Partner: nothing prevents a Partner Admin from creating an agent under the Partner tenant directly via `/admin/agents`, which is semantically equivalent to turning the reseller into a contact center.

### Why fix it now (and not later)

1. **Pre-customer window.** The 2026-05-25 strategic pivot froze cloud spend until the first paying customer. SMB Docker is the primary track. No paying customers exist; semantic changes that tighten guards have zero blast radius today and grow exponentially once any tenant has live data accumulated under the wrong type.

2. **First Partner onboarding is the hard deadline.** The current SMB Docker target is a `Platform + 1×Customer` topology (single-tenant install). The instant a Partner-tier customer is provisioned — even as a design-partner pilot — the implicit guarantee is exposed. A Partner Admin with normal admin privileges can mistakenly (or deliberately) materialize state on the Partner tenant that should live on a child Customer.

3. **Phase A.6 surfaced it.** While building [ADR-0026](0026-queue-membership-executive-routing.md) Phase A.6 (the channel-aware membership editor at `/admin/agents/{id}/queues`), the audit revealed that the editor accepts whichever tenant the caller is currently scoped to. The editor is correct; the missing guard is what makes the editor unsafe at the Platform/Partner level.

4. **Cheap fix, durable contract.** One `EndpointFilter` + ~30 application sites + tests. Converts the implicit contract to enforced. Removes a class of operator mistakes that would otherwise require manual data cleanup.

---

## Decision

**Introduce a `RequireOperationalTenant()` endpoint-filter extension that returns HTTP 409 (Conflict) when the resolved tenant's `Type` is not `Customer`. Apply it to every endpoint group whose semantics are operational (agentes, colas, conversaciones, channels, campañas, skills, bots, flows, knowledge base, surveys, agent-assist, queue members, holiday calendars, canned responses, dispositions, outbound routes, trunks, caller-id pools, DNC lists, call attempts, scheduled reports).**

The `Platform` and `Partner` tenants get HTTP 409 instead of HTTP 403 deliberately:

- 403 implies "you do not have permission" — misleading, since the user may indeed have the Admin role.
- 409 ("Conflict") signals "the operation conflicts with the current tenant's type" — operator-actionable: switch to a Customer tenant (or impersonate into one) to perform the operation.

### Scope (operational ⇒ requires gate)

The gate applies to endpoints where the resource lives **inside** a Customer tenant (its agents, queues, conversations, etc.). Resources scoped to the tenant itself (e.g. tenant settings, RBAC, audit log readers, billing of the tenant) do NOT get the gate — they are administrative for every tenant type.

| Apply gate | Don't apply gate |
|---|---|
| `/admin/agents`, `/admin/queues`, `/admin/teams` | `/admin/users` (tenant directory) |
| `/admin/campaigns`, `/admin/caller-id-pools`, `/admin/dnc`, `/admin/trunks`, `/admin/outbound-routes` | `/admin/api-keys` (tenant's own keys) |
| `/admin/channels`, `/admin/webchat`, `/admin/whatsapp`, etc. | `/admin/tenant-settings` |
| `/admin/skills`, `/admin/agents/{id}/skills` | `/admin/rbac`, `/admin/auth` |
| `/admin/bots`, `/admin/flows`, `/admin/articles`, `/admin/surveys`, `/admin/agent-assist` | `/admin/audit` (auditoría del propio tenant) |
| `/admin/canned-responses`, `/admin/dispositions`, `/admin/holiday-calendars` | `/admin/scheduled-reports` for non-operational reports — to evaluate case-by-case |
| `/admin/dialer-settings` | `/admin/setup` (one-shot, doesn't matter) |
| `/admin/agent-assist` config | `/admin/features/agent-assist` (it's a plan-tier feature flag, not operational data) |
| `/conversations`, `/conversations/{id}/*` | `/notifications`, `/users/me`, `/agents/me` (per-user surface) |
| `/queues/{queueId}/members`, `/queues/{queueId}/members/{agentId}/*` | `/analytics/*` (read-only, fine to view across types if RBAC allows) |
| `/operations/*` (live wallboard, supervisor controls) | `/management/*` (already Platform/Partner-only by design) |
| `/admin/recordings`, `/admin/media` | `/partner/*` (already Partner-only by policy) |

The complete list is enumerated in the plan ([`docs/plans/active/2026-05-28-tenant-type-operational-gate.md`](../plans/active/2026-05-28-tenant-type-operational-gate.md)) §Application sites.

### Behavior under impersonation

When a Platform Admin or Partner Admin **impersonates** a Customer, the `Tenant` resolved into `HttpContext.Items["Tenant"]` is the **impersonated Customer** (already the case today — see [`ManagementImpersonationEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs)). The gate naturally passes because `Type == Customer`. No special case needed; impersonation remains the canonical way for a Platform Admin to drive operational endpoints.

### What the error shape looks like

```http
HTTP/1.1 409 Conflict
Content-Type: application/json

{
  "type": "https://verbara.platform/errors/tenant-type-mismatch",
  "title": "Operational endpoint not available on this tenant type",
  "status": 409,
  "detail": "Operational endpoints are only available on Customer tenants (this is a Platform tenant). Switch to a Customer tenant or use the Management API to administrate it.",
  "tenantType": "Platform",
  "expectedType": "Customer",
  "remediation": "POST /api/v1/management/impersonate {\"tenantId\":\"<customer-id>\"} to drive operational endpoints as that Customer."
}
```

### What this does NOT do

- Does NOT change RBAC. `AdminOnly` / `SupervisorPlus` continue to gate role-based access **within** a Customer tenant.
- Does NOT block read-only Management endpoints from listing/inspecting Customer state.
- Does NOT add a new permission. Tenant-type is a tenant attribute, not a user permission; the gate runs ahead of RBAC and short-circuits when the type mismatches.
- Does NOT touch the Partner endpoints (`/partner/*`) — those already restrict to `PartnerAdminOnly`.

---

## Alternatives considered

### A. Status quo — leave it as convention

Cheap, but every Partner / Platform Admin onboarding becomes a security review. As soon as a Partner exists, a single bad click materializes operational data on the wrong tenant. Cleanup is manual (Postgres-level) and surfaces in audit only retroactively.

### B. Encode tenant-type as an RBAC permission and add it to the role templates

E.g., a `tenant.type:customer:operate` permission seeded on the `Admin` template only for Customer tenants. Closes the gap, but conflates two orthogonal concepts: tenant type (architectural) and user permissions (RBAC). Makes role audits harder to read and requires permission-resolver changes per template per tenant type. Rejected.

### C. New `RequireCustomerTenant()` policy via `AuthorizationHandler`

Equivalent semantics to the chosen filter, but introduces a new authorization scheme that interacts with the existing `AdminOnly` / `SupervisorPlus` policies. The endpoint filter approach is simpler — pure middleware, no DI interactions, easy to add/remove without recompiling policy registration. Chosen.

### D. Block at the `TenantBoundaryValidationMiddleware` level

Move the check into the existing middleware that already inspects `tenant.Type`. Pros: single source of truth. Cons: middleware runs before endpoint routing, so it doesn't know **which** endpoint is being hit — it would have to maintain a route-shape allowlist, which is brittle. Endpoint filter applies per-group, which is the right granularity. Rejected.

### E. Treat the `platform` tenant as a special "system" pseudo-tenant with its own endpoint set

This is what Management endpoints partially do today. Extending to a full system tenant model means duplicating every operational endpoint under `/system/*` to keep the Platform-admin UX. High effort, low payoff vs. the chosen filter. Rejected.

---

## Consequences

### Positive

- Closes a class of operator-error bugs at the API surface, not at the UI navigation level.
- Makes the tenant-type contract auditable: `git grep RequireOperationalTenant` lists every endpoint that enforces the rule.
- Plays nicely with impersonation — the existing path for "Platform Admin drives an operational endpoint" continues to work.
- Surfaces a clear, operator-actionable error (409 + remediation hint) instead of mysterious data ending up in the wrong tenant.

### Negative

- Adds one filter execution per operational request. Negligible overhead (a `HttpContext.Items` lookup + enum comparison), but worth measuring against the v2.5.4 latency baseline.
- Tests for every operational endpoint group must add a "rejects when Tenant=Platform" / "rejects when Tenant=Partner" case. Mechanical work, ~30 new tests.
- Cannot land until `Tenant` is reliably populated into `HttpContext.Items["Tenant"]` for every authenticated request — already true today, but the assumption must be documented + tested.

### Migration

- Existing data created on `platform` or any `Partner` tenant under operational tables (`agents`, `queues`, `queue_memberships`, `conversations`, etc.) is NOT migrated by this ADR. The plan calls for a one-shot inventory script (`scripts/tenant-type-misplaced-data.sh`) that lists offenders for manual triage before the gate ships. On the SMB Docker happy path, the only known misplaced rows are the platform-admin-as-agent left over from pre-Phase A wizard installs.

---

## Validation criteria

- `dotnet test` adds a tenant-type test family covering each operational endpoint group; suite stays at 100%.
- Manual smoke: log in as `admin@verbara.local` (Platform tenant), attempt `POST /api/v1/admin/queues` → expect 409 with the JSON shape above; impersonate into a Customer → expect 201.
- Living-docs Day 1 spec (`01-day1-setup-and-webchat`) continues to pass — the wizard implicitly operates on the seeded Customer tenant. If the spec instead runs on Platform, it fails fast at the queue-creation step (which is the desired behavior).
- `docs/specs/...` snippet documenting the contract + the application-site list ships alongside the implementation PR.

---

## References

- Plan: [`docs/plans/active/2026-05-28-tenant-type-operational-gate.md`](../plans/active/2026-05-28-tenant-type-operational-gate.md)
- Tenant model: [`Tenant.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/Tenant.cs), [`TenantType.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/TenantType.cs)
- Hierarchy enforcement: [`PostgresTenantStore.cs`](../../src/Verbara.Platform.Storage.Postgres/Stores/PostgresTenantStore.cs)
- Cross-tenant override middleware: [`TenantBoundaryValidationMiddleware.cs`](../../src/Verbara.Platform.Api/Middleware/TenantBoundaryValidationMiddleware.cs)
- Authorization handlers: [`PlatformAdminAuthorizationHandler.cs`](../../src/Verbara.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs), [`PartnerAdminAuthorizationHandler.cs`](../../src/Verbara.Platform.Api/Auth/PartnerAdminAuthorizationHandler.cs)
- Related ADR: [ADR-0026](0026-queue-membership-executive-routing.md) Phase A.6 (channel-aware membership editor) surfaced the gap.
