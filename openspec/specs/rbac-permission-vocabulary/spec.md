# rbac-permission-vocabulary Specification

## Purpose

Governs how RBAC permission ids are named, catalogued and seeded, so that an id which is granted or
gated is an id a principal can actually hold.

The rules here exist because their absence was not cosmetic. `role_template_permissions` has a
foreign key onto `permissions` and the seeders insert row-by-row without a transaction, so a single
uncatalogued grant aborted RBAC seeding on every boot — silently, behind a `Console.WriteLine` —
leaving 5 of 11 role templates in place, zero partner roles across 103 tenants, and six admin
surfaces reachable only through the `Admin`/`SystemAdmin` role shortcut that skips the permission
check. See [ADR-0037](../../docs/decisions/0037-canonical-rbac-permission-vocabulary.md).

This spec therefore covers four things together, because each one hid the next: a single
`domain:resource:action` vocabulary; catalog membership enforced at build time for both template
grants and authorization gates; a seed failure that is loud; and a per-tenant clone that tolerates a
tenant already owning an equivalent role. It also pins the client-side `RoleDefaultPermissions`
fallback to the admin template it claims to mirror, since that fallback is what masked the empty
RBAC state from anyone logging in.

Out of scope: whether the `Admin`/`SystemAdmin` shortcut should exist at all, and the migration
loop's blast radius, observability and convergence — the latter tracked as
`harden-rbac-migration-seeder`.

## Requirements
### Requirement: Every granted or gated permission id exists in the catalog

Every permission id referenced anywhere in the RBAC system MUST be a member of the catalog produced
by `PermissionSeeder`. This applies to both sources of reference: the grants in `RoleTemplateSeeder`
(`AllPermissions()` and every explicit per-template list) and the ids named by authorization gates
(`PlatformAdminRequirement`).

This is not a style rule. `role_template_permissions.permission_id` carries a foreign key onto
`permissions`, and `RoleTemplateSeeder.SeedAsync` inserts row-by-row **without a transaction**, so a
single orphan grant raises `23503` and aborts the remainder of the seeding loop — leaving a partially
seeded role model rather than a failed one. A gate naming an uncatalogued id is equally unsatisfiable:
`PermissionResolver.HasPermission` is an exact set-membership test with no alias or implication layer.

The invariant MUST be enforced by tests that fail the build, not by review.

#### Scenario: A template granting an uncatalogued id fails the build

- **GIVEN** a permission id added to `RoleTemplateSeeder.AllPermissions()` or to an explicit template list
- **WHEN** that id is not present in the catalog produced by `PermissionSeeder`
- **THEN** the test suite fails and names the offending id

#### Scenario: A gate naming an uncatalogued id fails the build

- **GIVEN** an authorization policy constructed with `new PlatformAdminRequirement("<id>")`
- **WHEN** `<id>` is not present in the catalog produced by `PermissionSeeder`
- **THEN** the test suite fails and names the offending id

#### Scenario: Seeding completes for every template

- **GIVEN** a database seeded by `RbacSeederOrchestrator`
- **WHEN** seeding finishes
- **THEN** all eleven role templates exist
- **AND** the `admin` template holds every id in `AllPermissions()` except its two documented exclusions

### Requirement: Permission ids use the canonical `domain:resource:action` form

Permission ids MUST use the canonical `domain:resource:action` vocabulary. The dot-notation ids
introduced by R5.2 Phase 0 P0.9 are retired: `security.mfa.admin`, `audit.read`, `audit.export`,
`security.impersonation.manage`, `retention.read`, `retention.manage`, `tenant.settings.write`.

Six of the seven were named by gates and MUST move to their canonical replacement together with the
gate that names them, so no surface is left gated on a retired id:

| Retired | Canonical replacement |
|---|---|
| `security.mfa.admin` | `system:mfa:manage` |
| `audit.read` | `system:audit:view` |
| `audit.export` | `system:audit:export` |
| `security.impersonation.manage` | `system:impersonation:manage` |
| `retention.read` | `system:retention:view` |
| `retention.manage` | `system:retention:manage` |

`tenant.settings.write` is gated nowhere and is dropped without replacement;
`system:tenant:configure` already covers tenant settings.

`security.jwt.rotate` is exempt and keeps its spelling. It is the one dot-notation id that is
catalogued, granted and gated coherently; renaming it would churn existing `tenant_role_permissions`
rows for no behavioural gain. The exemption is recorded in Platform/ADR-0037 rather than left as an
unexplained outlier.

Impersonation retains two distinct ids by design: `platform:tenant:impersonate` authorises *starting*
a session, `system:impersonation:manage` authorises *administering* sessions (list active, revoke,
read history). `system_admin` is excluded from the former and MUST retain the latter, preserving the
grant set the retired id had.

#### Scenario: No retired id remains in the seeders or the gates

- **GIVEN** the Platform source tree
- **WHEN** it is searched for the seven retired ids
- **THEN** none appears in `PermissionSeeder`, `RoleTemplateSeeder`, or any `PlatformAdminRequirement`

#### Scenario: system_admin keeps impersonation-session administration

- **GIVEN** the `system_admin` role template
- **THEN** it grants `system:impersonation:manage`
- **AND** it does not grant `platform:tenant:impersonate`

### Requirement: A failed RBAC seed is visible

A failure of `RbacSeederOrchestrator` at startup MUST be logged at **Error** level through the host
logger, including the exception. It MUST NOT be reported only via `Console.WriteLine` — that is how
a fully broken role model went unnoticed from 2026-07-29 until this change.

The host MUST still complete startup. A transient database fault at boot should not brick the API,
and the systematic cause of seed failure is now caught at build time by the catalog-integrity tests.

#### Scenario: A seeding failure is logged at Error

- **GIVEN** an API host whose RBAC seeding throws
- **WHEN** startup completes
- **THEN** an Error-level entry carrying the exception is emitted through the host logger
- **AND** the host is running

### Requirement: Per-tenant role cloning tolerates a tenant that already owns an equivalent role

`RbacMigrationSeeder.MigrateExistingUsersAsync` clones the role templates into every tenant on each
boot and MUST be idempotent against **every** unique constraint on `tenant_roles`, not only the
primary key. The table carries two: `tenant_roles_pkey (tenant_id, role_id)` and
`idx_tenant_roles_name (tenant_id, lower(name))`.

A tenant provisioned outside this seeder may already hold an equivalent role under a different id.
Cloning a template whose name collides then misses a `(tenant_id, role_id)` conflict target and
raises `23505`; the loop has no transaction and no handler, so it aborts and every tenant queued
behind it is skipped.

Where a clone is skipped, dependent writes MUST NOT assume the template id exists as a role —
`tenant_role_permissions` and `user_roles` both have foreign keys onto `tenant_roles`, so a blind
insert raises `23503`. The user assignment MUST resolve to the tenant's equivalent same-named role
rather than be skipped: a user whose legacy `users.role` reads `Admin` but who holds no role at all
resolves to zero permissions and falls back to `RoleDefaultPermissions`, which is the silent
degradation this capability exists to remove.

#### Scenario: A name collision does not abort the clone loop

- **GIVEN** a tenant that already owns a role named `"Admin"` under an id other than `admin`
- **AND** a second tenant with no roles
- **WHEN** `MigrateExistingUsersAsync` runs
- **THEN** it does not throw
- **AND** the second tenant receives all seven cloned roles

#### Scenario: A user is assigned the tenant's equivalent role

- **GIVEN** a tenant whose only admin-equivalent role is named `"Admin"` under a different id
- **AND** a user in that tenant whose legacy `users.role` is `Admin`
- **WHEN** `MigrateExistingUsersAsync` runs
- **THEN** the user is assigned that existing role
- **AND** no `tenant_role_permissions` or `user_roles` row references a nonexistent role id

### Requirement: The client permission fallback mirrors the admin template

`RoleDefaultPermissions.Admin` in `AuthEndpoints` is served to a client when the RBAC resolver
returns nothing, and documents itself as mirroring the `admin` role template. When canonical ids
replace retired ones in that template, the fallback list MUST move with them.

A stale fallback is not inert: it is the list the frontend's route guards consume, so a fallback that
disagrees with the template silently grants or hides admin surfaces for exactly the users whose RBAC
state is already degraded.

#### Scenario: The fallback carries the canonical ids

- **GIVEN** a user whose RBAC resolution returns no permissions and whose role is `Admin`
- **WHEN** they log in
- **THEN** the returned permission list contains the canonical ids for audit, MFA administration,
  impersonation-session administration and retention
- **AND** contains none of the retired dot-notation ids

