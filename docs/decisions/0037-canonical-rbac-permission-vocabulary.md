# ADR-0037: Canonical RBAC permission vocabulary and catalog-integrity invariant

- **Status:** Accepted
- **Date:** 2026-08-12
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - `src/Verbara.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs` (the catalog)
  - `src/Verbara.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs` (the grants)
  - `src/Verbara.Platform.Api/Program.cs` (`PlatformAdminRequirement` gates, ~L1438–1493)
  - `src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs` (`RoleDefaultPermissions` fallback)
  - Related ADRs: [ADR-0027 (tenant-type operational gate)](0027-tenant-type-operational-gate.md)

## Context

RBAC permissions are identified by a string id. The catalog lives in `PermissionSeeder`
(`permissions` table); grants live in `RoleTemplateSeeder` (`role_template_permissions`), which has a
foreign key onto the catalog. Gates name a permission id at the policy site
(`new PlatformAdminRequirement("<id>")`), and `PermissionResolver.HasPermission` is an exact set
membership test — there is no alias, prefix, or implication layer at check time.

Two vocabularies coexisted. The canonical one is `domain:resource:action` (83 ids). A second,
dot-notation set was introduced by R5.2 Phase 0 P0.9 — `audit.read`, `audit.export`,
`security.mfa.admin`, `security.impersonation.manage`, `retention.read`, `retention.manage`,
`tenant.settings.write` — granted to the `admin`, `system_admin` and `platform_admin` templates
**ahead of** the features that would consume them, with the catalog side deferred. The catalog side
never landed.

The consequence was not a naming inconsistency. It was a hard failure:

- `RoleTemplateSeeder.SeedAsync` inserts row-by-row with `ON CONFLICT DO NOTHING` and **no
  transaction**. On reaching the first orphan grant it raised `23503`
  (`role_template_permissions_permission_id_fkey`) and the whole seeding loop aborted.
- `Program.cs` wrapped the seeder in a `try/catch` that emitted a `Console.WriteLine`, so the failure
  was invisible in normal operation.
- Observed on a live database seeded 2026-07-29: **5 of 11 role templates existed**. Seeding died on
  the 5th (`admin`), which retained a partial 67 grants. `system_admin`, `api`, `platform_admin`,
  `partner_admin`, `partner_billing` and `partner_viewer` were never created at all — across 103
  tenants and 507 tenant roles there was not one partner role, and the single `Platform Admin` role
  held zero permissions.
- Six endpoint gates named orphan ids, so no principal could ever satisfy them by permission. The
  audit, MFA-admin, impersonation-session, retention and JWT-rotation surfaces stayed reachable only
  through the `Admin`/`SystemAdmin` role shortcut in `PlatformAdminAuthorizationHandler`, which skips
  the permission check entirely. Users whose permissions resolved to nothing fell back to the
  hardcoded `RoleDefaultPermissions.Admin` list, masking the empty RBAC state.

A permission id that is granted but not catalogued is therefore not a cosmetic defect: it is a
seed-time integrity violation that silently truncates the entire role model.

## Decision

**1 — One vocabulary.** Every permission id is `domain:resource:action`. The dot-notation ids from
R5.2 P0.9 are retired. Six of the seven were gated; each gets a canonical replacement, and the gate
moves with it:

| Retired (dot) | Canonical replacement |
|---|---|
| `security.mfa.admin` | `system:mfa:manage` |
| `audit.read` | `system:audit:view` *(already catalogued)* |
| `audit.export` | `system:audit:export` |
| `security.impersonation.manage` | `system:impersonation:manage` |
| `retention.read` | `system:retention:view` |
| `retention.manage` | `system:retention:manage` |
| `tenant.settings.write` | *dropped — gated nowhere; `system:tenant:configure` already covers it* |

`security.jwt.rotate` keeps its spelling. It is the one dot-notation id that **is** catalogued
(`PermissionSeeder`, category `security`), so it is gated, granted and functional end to end.
Renaming it would churn existing `tenant_role_permissions` rows for no behavioural gain; it is
recorded here as an accepted inconsistency rather than left unnoticed.

Impersonation deliberately keeps **two** distinct ids: `platform:tenant:impersonate` authorises
*starting* an impersonation session, `system:impersonation:manage` authorises *administering* them
(list active, revoke, read history). `system_admin` is excluded from the former by design
(`RoleTemplateSeeder`) and retains the latter, which preserves the grant set that
`security.impersonation.manage` had.

**2 — Catalog integrity is a build-time invariant, not a runtime hope.** Every permission id
referenced by a role template or named by an authorization gate MUST exist in the catalog produced by
`PermissionSeeder`. This is enforced by tests, so the class of defect that produced this ADR cannot
be reintroduced by review alone:

- every id in `RoleTemplateSeeder` (both `AllPermissions()` and the explicit per-template lists) is a
  member of the catalog;
- every id named by a `PlatformAdminRequirement` gate — held as constants rather than string
  literals at the policy site — is a member of the catalog.

**3 — A failed RBAC seed is loud.** The startup `catch` no longer writes to `Console`; it logs at
**Error** through the host logger. The boot is not aborted: a transient database fault should not
brick the API, and the systematic cause is now caught at build time.

**4 — The fallback mirrors the template.** `RoleDefaultPermissions.Admin` in `AuthEndpoints` is the
list served to a client when the RBAC resolver returns nothing. It documents itself as mirroring the
`admin` role template, so it moves with the canonical ids.

## Consequences

- A restart repairs an affected database with no migration script. Inserts are
  `ON CONFLICT DO NOTHING` and un-transacted, and `RbacMigrationSeeder` re-clones templates into
  every tenant on each boot, so once the FK cause is gone the missing templates, the missing `admin`
  grants and the missing per-tenant rows all fill in.
  Two limits on that self-repair, neither introduced here and neither reachable as a privilege
  problem, but both worth stating so the sentence is not read as more than it is. First, the clone
  only refreshes grants for roles whose `role_id` equals the template id; a tenant whose
  template-derived role carries a suffixed id (`admin-demo`, `role_admin_<tenant>` — the majority
  shape) keeps the grants it was provisioned with. That is invisible to the six surfaces this ADR
  is about, because `PlatformAdminRequirement` denies any tenant that is neither the host nor a
  `Partner` before it ever consults a permission. Second, per-tenant `platform_admin` roles remain
  the `tools/RbacReseed` CLI's job, and that CLI matches `role_id = 'platform_admin'` while
  `SetupEndpoints` provisions `platform-admin-{tenant}` — so it does not currently reach them. Both
  are pre-existing (R5.2 PC.3) and tracked as follow-ups.
- **The loud-log decision paid for itself on the first boot after the fix.** With `23503` resolved
  and the failure finally visible, the very next startup surfaced a second, independent abort that
  the `Console.WriteLine` had been swallowing all along: `RbacMigrationSeeder` raised `23505` against
  `idx_tenant_roles_name`, a unique index its `ON CONFLICT (tenant_id, role_id)` clause did not
  target, killing the per-tenant clone loop partway through. Two un-transacted seeders failing in
  sequence, each hidden by the one before it, is the strongest available argument that a swallowed
  startup exception is not a small defect. Fixed in the same change; the self-repair claim above
  depends on it.
- Two lessons generalise beyond RBAC and are worth applying to any seeder: `ON CONFLICT` with an
  explicit target silently ignores every *other* unique constraint on the table, and a test fixture
  that omits a constraint makes the entire class of defect it guards invisible — the `23503` hid
  behind a missing foreign key in one fixture and the `23505` behind a missing unique index in the
  same one.
- The `Admin`/`SystemAdmin` role shortcut in `PlatformAdminAuthorizationHandler` stops being
  load-bearing. It was the only reason six admin surfaces answered at all; after this change they are
  reachable on their permission, and the shortcut is a convenience.
- Clients gating on the retired ids break, by design — they were gating on ids no principal could
  hold. `Verbara.Platform.Web` moves in the same change under the hub rule (verbara-meta/ADR-0005).
- The catalog grows 83 → 88.

## Alternatives considered

**Catalogue the dot-notation ids instead.** Adding the seven missing rows to `PermissionSeeder` would
have cleared the FK violation with a smaller diff and no gate changes. Rejected: it would have made
the second vocabulary permanent and left every future reader to guess which spelling governs a given
surface — the ambiguity that produced the divergent frontend guards in the first place.

**Add an alias layer at check time.** Rejected: `PermissionResolver.HasPermission` is a set-membership
test on a hot path, and an alias map is a second source of truth that drifts exactly like the first.

**Fail fast on seed error.** Rejected as the default. A transient database fault at boot would brick
the host, and CLAUDE.md's standing guidance is to avoid crashing boots on external-dependency seams.
The build-time invariant covers the systematic case; the Error log covers the transient one.
