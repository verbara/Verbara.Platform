---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform and partner operators; every tenant admin who reaches an admin surface
decision_ref: Platform/ADR-0037
---

## Why

The RBAC seeder has been failing on every API boot, silently, since at least 2026-07-29.

`RoleTemplateSeeder.AllPermissions()` grants seven dot-notation permission ids that
`PermissionSeeder` never catalogues. `role_template_permissions` has a foreign key onto
`permissions`, so the first orphan grant raises `23503` and aborts the seeding loop. `Program.cs`
catches it and emits a `Console.WriteLine`, so nothing surfaces.

Measured against the running lab database:

- **5 of 11 role templates exist.** Seeding dies on the 5th (`admin`), which keeps a partial 67
  grants out of ~81. `system_admin`, `api`, `platform_admin`, `partner_admin`, `partner_billing`
  and `partner_viewer` were never created.
- Across **103 tenants / 507 tenant roles** there is not one partner role, and the only
  `Platform Admin` role holds **zero** permissions.
- **Six endpoint gates name orphan ids** (`security.mfa.admin`, `audit.read`, `audit.export`,
  `security.impersonation.manage`, `retention.read`, `retention.manage`), so no principal can satisfy
  them by permission. Those surfaces answer only via the `Admin`/`SystemAdmin` shortcut in
  `PlatformAdminAuthorizationHandler`, which skips the permission check.
- A platform admin logging in receives the hardcoded `RoleDefaultPermissions.Admin` fallback (57
  ids), not RBAC — because RBAC resolves to nothing. The fallback masks the empty state.

Verified end to end against the lab: a user in the host tenant with `role: Agent` (so the shortcut
does not apply) receives **403** from `/admin/audit/events` and
`/management/impersonation/sessions/active`. A fresh Postgres deployment today cannot create a
`platform_admin` or any partner template at all — partner functionality is dead on arrival.

The frontend divergence that started this investigation is a symptom: `Verbara.Platform.Web` gates
some surfaces on canonical ids and others on the retired dot ids, and neither set matches the API,
because the ids the API names cannot exist.

## What Changes

- **Retire the dot-notation vocabulary.** Six gated ids move to `domain:resource:action`
  replacements; `tenant.settings.write` is dropped (gated nowhere). `security.jwt.rotate` is kept and
  its inconsistency recorded — it is the one dot id that is actually catalogued and functional.
- **Catalogue five new canonical ids** (`system:mfa:manage`, `system:audit:export`,
  `system:impersonation:manage`, `system:retention:view`, `system:retention:manage`), taking the
  catalog 83 → 88, and grant them where the retired ids were granted.
- **Hold gate ids as constants** rather than string literals at the policy site, so they are
  testable.
- **Enforce catalog integrity at build time**: every template grant and every gate id must be a
  catalog member. This is the invariant whose absence produced the bug.
- **Make a failed RBAC seed loud** — Error-level structured log instead of `Console.WriteLine`. The
  boot still proceeds; a transient database fault should not brick the host.
- **Fix a second seeder abort that the loud log immediately exposed** (see below):
  `RbacMigrationSeeder` raised `23505` against a unique index its `ON CONFLICT` clause did not
  target, aborting the per-tenant clone loop.
- **Stamp OCI provenance labels on the API image** so a running container is traceable to its commit.
- **Move `Verbara.Platform.Web`'s five stale guards** onto the canonical ids, hosted here under the
  hub rule (verbara-meta/ADR-0005).

### The second defect, found by the first fix

With `23503` resolved and the failure finally visible, the very next boot surfaced a different abort
in a different seeder — proof that the loud-logging change earns its place:

```
23505: duplicate key value violates unique constraint "idx_tenant_roles_name"
  at RbacMigrationSeeder.MigrateExistingUsersAsync
```

`tenant_roles` carries two unique constraints — `tenant_roles_pkey (tenant_id, role_id)` and
`idx_tenant_roles_name (tenant_id, lower(name))` — but the clone's `ON CONFLICT` targeted only the
first. A tenant provisioned outside this seeder can already hold an equivalent role under a
different id (the lab's `demo` tenant has `admin-demo` named `"Admin"`), so cloning template `admin`
misses the target and raises `23505`. Un-transacted and unhandled, it killed the loop: `demo`
received 4 of 7 roles and the `platform` tenant was never processed at all.

The clone now uses an untargeted `ON CONFLICT DO NOTHING`, and the user assignment **resolves** the
role instead of assuming the clone landed under the template id — falling back to the tenant's
same-named role. Skipping would have left a user whose legacy `users.role` reads `Admin` holding
zero permissions, i.e. back on the silent `RoleDefaultPermissions` fallback this change exists to
eliminate.

## Capabilities

### New Capabilities

- `rbac-permission-vocabulary`: one canonical permission vocabulary, with catalog membership enforced
  at build time for both template grants and authorization gates, and a seed failure that is visible.

### Modified Capabilities

<!-- None. No existing capability owns the permission-id vocabulary or the seeder's integrity
     contract; this change establishes it. Endpoint behaviour is unchanged for any principal that
     could already reach these surfaces. -->

## Impact

- **Seeds:** `PermissionSeeder.cs` (5 additions), `RoleTemplateSeeder.cs` (7 removals, 5 additions in
  `AllPermissions()`).
- **API host:** `Program.cs` — 6 `PlatformAdminRequirement` gates plus the startup `catch`; a new
  constants type for gate ids; `AuthEndpoints.RoleDefaultPermissions.Admin` fallback list.
- **Tests:** `Verbara.Platform.Storage.Postgres.Tests` (grants ⊆ catalog — the project already has
  `InternalsVisibleTo`), `Verbara.Platform.Api.Tests` (gate ids ⊆ catalog).
- **Frontend (`Verbara.Platform.Web`):** `src/admin/sidebar.tsx:443`,
  `src/admin/security/audit/audit-viewer-page.tsx:113`, `src/router.tsx:784`, `src/router.tsx:819`,
  `src/admin/retention/retention-admin-page.tsx:24`, and the impersonation-admin guard at
  `src/router.tsx:809` (which must name the id its endpoints gate on, not the start-impersonation id).
- **Seeds (second defect):** `RbacMigrationSeeder.cs` — untargeted `ON CONFLICT` on the tenant-role
  clone, an `EXISTS` guard on the grant insert, and a resolving `SELECT` on the user assignment.
- **Build tooling:** root `Dockerfile` (`VCS_REF` / `BUILD_DATE` / `VERSION` args →
  `org.opencontainers.image.*` labels) and `docker/docker-compose.full.yml` (passes the args
  through). Verified on the rebuilt image: `revision=977a261b-dirty`.
- **Data:** no migration. A restart repairs affected databases — inserts are
  `ON CONFLICT DO NOTHING` and un-transacted, and `RbacMigrationSeeder` re-clones per tenant each
  boot. Per-tenant `platform_admin` roles remain the `tools/RbacReseed` CLI's job. Note this
  self-repair claim only holds **with** the `23505` fix above; without it the clone loop aborts
  before most tenants are reached.
- **Cross-repo:** frontend only. No `Verbara.Sdk` / `Verbara.Sdk.Pro` change, no pin movement.

## Architectural Risk

The `Admin`/`SystemAdmin` shortcut currently masks this bug in production-shaped deployments, which
is why it went unnoticed. Once the permissions genuinely resolve, the shortcut stops being
load-bearing — but it also stops hiding regressions. Any future gate whose id is wrong will now fail
closed for non-Admin principals instead of being papered over, which is the intended behaviour and a
change in failure mode worth stating.

Retiring ids that some deployment may already have granted is safe here only because the FK
guarantees they were never inserted anywhere. That reasoning does not generalise to future
permission renames, which would need a data migration.

### Out of Scope (explicit)

- **The `Admin`/`SystemAdmin` role shortcut itself.** Whether a role should ever bypass a permission
  check is a separate decision; this change makes the permissions work, it does not remove the
  shortcut.
- **`POST /admin/users/{id}/roles/{roleId}` accepts a user id that matches no user**, returning 204
  and writing an orphan `user_roles` row — the table has a foreign key onto `tenant_roles` but none
  onto `users`. Found while verifying this change: it is what made an earlier probe report a false
  negative, since `GET /admin/users/{id}/permissions` happily resolves the orphan row while login,
  which looks the user up by email, resolves the real user's empty set. A validation gap, not a
  vocabulary problem. Tracked as `harden-rbac-migration-seeder`.
- **Renaming `security.jwt.rotate`** to canonical form — accepted inconsistency, recorded in
  ADR-0037.
- **Logging the skipped clone.** When `RbacMigrationSeeder` skips a template because the tenant
  already owns a same-named role, nothing records it. That is the event an operator would want, but
  the seeder has no `ILogger` and threading one through `Storage.Postgres` is plumbing beyond this
  change. Tracked as `harden-rbac-migration-seeder`.
- **Provenance labels on CI-built images.** `release.yml`'s `docker/build-push-action` step passes
  no `build-args`, so images built by CI carry `revision`/`created`/`version` = `unknown`; only a
  local build with the env vars exported gets real values. Still a strict improvement — the
  pre-change shipped image carried no provenance labels at all, only the Ubuntu base's
  `image.version=24.04` — but the traceability goal is only half met until the workflow passes them.
  Tracked as `stamp-ci-image-provenance`.
- **Convergence of the resolved assignment.** The name-resolving assignment is idempotent for
  unchanged data — the case that runs on every boot — but not convergent: if the tenant later
  creates the exact template `role_id`, or renames the equivalent role, a subsequent run resolves
  via the exact-id branch and inserts a *second* row for the same user, which
  `ON CONFLICT (tenant_id, user_id, role_id)` cannot catch because the `role_id` differs. Low
  severity (both roles are admin-equivalent and `PermissionResolver` unions them), but it is a real
  edge. Tracked as `harden-rbac-migration-seeder`.
- **Per-tenant fault isolation in `RbacMigrationSeeder`.** The loop still has no transaction and no
  per-tenant `try`/`catch`, so any *future* unexpected error still takes down every tenant queued
  behind it. The three concrete aborts are fixed; the structural blast radius is not.
  `RoleTemplateSeeder.ReseedExistingTenantsAsync` already has per-tenant transactions and is the
  model to follow. Tracked as `harden-rbac-migration-seeder`.
