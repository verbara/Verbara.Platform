## ADDED Requirements

### Requirement: One tenant's failure does not abort the migration for the others

`RbacMigrationSeeder.MigrateExistingUsersAsync` MUST isolate each tenant, so a failure while
processing one tenant is contained and the loop continues with the next.

Today the loop carries no transaction and no per-tenant `try`/`catch`. The three concrete aborts
that `canonicalize-rbac-permission-vocabulary` fixed are gone, but the structure that turned each of
them into a total outage is unchanged: the second of those defects left the `demo` tenant with 4 of
7 roles and never reached the `platform` tenant at all, because `demo` failed first.

Isolation MUST be transactional per tenant, not merely a caught exception: a tenant that fails
halfway through its clone loop would otherwise be left with a partial role set that the next boot
reads as "already cloned". `RoleTemplateSeeder.ReseedExistingTenantsAsync` already implements this
shape and is the reference.

A contained failure MUST be logged at Error with the tenant it belongs to. A migration that silently
skips a tenant is a worse failure mode than the abort it replaces, because the abort was at least
visible in aggregate.

#### Scenario: A failing tenant does not block the tenants behind it

- **GIVEN** three tenants queued for migration and the second one raises a database error
- **WHEN** the migration runs
- **THEN** the first and third tenants are migrated completely
- **AND** the second tenant's failure is logged at Error with its tenant id

#### Scenario: A failed tenant is not left half-migrated

- **GIVEN** a tenant whose migration fails after some of its roles were cloned
- **WHEN** the migration completes
- **THEN** that tenant's partial writes are rolled back
- **AND** a subsequent run retries the tenant from a clean state

### Requirement: A skipped template clone is recorded

The seeder MUST record when it skips cloning a template because the tenant already owns an
equivalent role under a different id.

The skip itself is correct and deliberate — the untargeted `ON CONFLICT DO NOTHING` exists precisely
so a tenant holding `admin-demo` named "Admin" does not collide with template `admin` on
`idx_tenant_roles_name`. What is wrong is that nothing says it happened. An operator asking why a
tenant's admin role does not carry the template's grants has no signal to work from.

The record MUST identify the tenant, the template that was skipped, and the existing role that
matched, so the answer is readable without querying the database.

`RbacMigrationSeeder` has no logger today, so this requires a logging seam in `Storage.Postgres`.
The seam MUST NOT drag a host-level logging dependency into the storage package.

#### Scenario: The skip names the role that caused it

- **GIVEN** a tenant that already owns a role named "Admin" under the id `admin-demo`
- **WHEN** the migration reaches template `admin` for that tenant
- **THEN** the clone is skipped
- **AND** the skip is recorded with the tenant id, the template id, and `admin-demo`

### Requirement: The resolved user assignment converges across re-runs

The user-to-role assignment MUST reach the same state on every subsequent run, not merely avoid
duplicating the row it wrote last time.

The assignment resolves the exact template `role_id` first and falls back to the tenant's same-named
role. For unchanged data — the case that runs on every boot — it is idempotent. It is not
convergent: if the tenant later creates the exact template `role_id`, or renames the equivalent
role, the next run resolves through the other branch and inserts a **second** row for the same user.
`ON CONFLICT (tenant_id, user_id, role_id)` cannot catch that, because the `role_id` differs.

Severity is low today — both roles are admin-equivalent and `PermissionResolver` unions them — but a
seeder that runs on every boot MUST NOT accumulate rows as the data around it changes.

#### Scenario: Creating the template id later does not double-assign

- **GIVEN** a user assigned to `admin-demo` by a previous run, because template `admin` had no clone
- **WHEN** the tenant creates a role with `role_id = admin` and the migration runs again
- **THEN** the user holds exactly one admin-equivalent role assignment

#### Scenario: Renaming the equivalent role does not double-assign

- **GIVEN** a user assigned through the same-named fallback
- **WHEN** that role is renamed and the migration runs again
- **THEN** the user holds exactly one admin-equivalent role assignment

### Requirement: Role assignment rejects a user that does not exist

`POST /api/v1/admin/users/{id}/roles/{roleId}` MUST reject a user id that matches no user in the
tenant, rather than returning 204 and writing an orphan `user_roles` row.

`user_roles` has a foreign key onto `tenant_roles` but none onto `users`, so the database accepts the
row. The result is two endpoints that disagree: `GET /admin/users/{id}/permissions` resolves the
orphan row and reports permissions, while login — which looks the user up by email — resolves the
real user's empty set. The divergence is silent, and during the verification of
`canonicalize-rbac-permission-vocabulary` it produced a false negative that was initially read as a
platform defect.

Whether `user_roles` should also carry a foreign key onto `users` MUST be decided in this change
rather than assumed. Adding it is the shape that prevents the class outright, but it will fail to
apply on any database that already holds orphan rows, and an orphan is indistinguishable from a row
whose user was hard-deleted — so the cleanup is a decision, not a mechanical step.

#### Scenario: An unknown user id is rejected

- **GIVEN** a user id that matches no user in the tenant
- **WHEN** a role assignment is posted for it
- **THEN** the request is rejected with a client error
- **AND** no `user_roles` row is written

#### Scenario: The two read paths agree

- **GIVEN** a user who has been assigned a role through the endpoint
- **WHEN** the permissions endpoint and the login path each resolve that user's permissions
- **THEN** both report the same set

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `RbacMigrationSeeder` — runs on every boot against every tenant. Changing its transaction shape
  changes what a partial failure leaves behind on real databases.
- A new logging seam in `Verbara.Platform.Storage.Postgres`, a package that deliberately has no host
  dependencies today.
- `user_roles` — a foreign key onto `users` is a migration that fails on any deployment already
  carrying orphan rows, and those are invisible until it runs.
- `RbacEndpoints` role assignment — tightening it from 204 to a client error is a contract change for
  any caller currently relying on the permissive behaviour, including provisioning scripts.

**Mitigation:**
- Per-tenant transactions follow `RoleTemplateSeeder.ReseedExistingTenantsAsync`, which already runs
  this shape in production, rather than inventing one.
- The logging requirement lands in the same change as the isolation requirement, so a contained
  failure cannot become a silent one.
- `PostgresRbacFixture` already carries the `users` table and the `idx_tenant_roles_name` unique
  index added by `canonicalize-rbac-permission-vocabulary`, so both the fault-isolation and the
  convergence cases can be expressed as real Postgres tests rather than in-memory approximations —
  the fixture-fidelity gap that hid both of that change's defects.
- The foreign key is explicitly framed as a decision with a required cleanup, not a mechanical
  addition, so the failure mode is confronted before it reaches a customer database.
