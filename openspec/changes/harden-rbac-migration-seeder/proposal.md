---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform operators; every tenant whose users depend on the boot-time role migration
decision_ref: Platform/ADR-0037
---

## Why

`canonicalize-rbac-permission-vocabulary` (#225) fixed the three aborts that were killing RBAC
seeding on every boot. It did **not** fix the structure that let one bad row take down every tenant
queued behind it, and while verifying it we found a validation gap on the endpoint that writes the
same table. Both were recorded as Out of Scope there; this change is where they live so they are
visible to the backlog rather than buried in an archived proposal.

**1 — `RbacMigrationSeeder` still has no fault isolation.** The loop over tenants carries no
transaction and no per-tenant `try`/`catch`. The three concrete aborts are gone, but the blast
radius is unchanged: any *future* unexpected error still takes down every tenant that has not been
reached yet. That is not hypothetical — it is exactly what happened twice in #225, once through
`23503` and once through `23505`, and in the second case the `platform` tenant was never processed
at all because `demo` failed first. `RoleTemplateSeeder.ReseedExistingTenantsAsync` already wraps
each tenant in its own transaction and is the model to follow.

**2 — A skipped template clone is silent.** When a tenant already owns a same-named role, the clone
is skipped by design (untargeted `ON CONFLICT DO NOTHING`). Nothing records it. That is precisely
the event an operator wants when asking why a tenant's `admin` role does not carry the template's
grants — and it is invisible today. The seeder has no `ILogger`; threading one through
`Storage.Postgres` is the work.

**3 — The resolved user assignment is idempotent but not convergent.** The assignment resolves the
exact template `role_id` first and falls back to the tenant's same-named role. For unchanged data —
the case that runs on every boot — it is idempotent. But if the tenant later creates the exact
template `role_id`, or renames the equivalent role, a subsequent run resolves through the other
branch and inserts a **second** row for the same user. `ON CONFLICT (tenant_id, user_id, role_id)`
cannot catch it, because the `role_id` differs. Severity is low — both roles are admin-equivalent
and `PermissionResolver` unions them — but the seeder claims to be re-runnable and in this shape it
is not.

**4 — `POST /admin/users/{id}/roles/{roleId}` accepts a user id that matches no user.** It returns
204 and writes an orphan `user_roles` row: the table has a foreign key onto `tenant_roles` but none
onto `users`. Found while verifying #225 — it is what made a probe report a false negative, because
`GET /admin/users/{id}/permissions` happily resolves the orphan row while login, which looks the
user up by email, resolves the real user's empty set. Two endpoints disagreeing about whether a user
has a role is a bad failure mode for an RBAC surface, and the divergence is silent.

## What Changes

- **Per-tenant fault isolation in `RbacMigrationSeeder`** — a transaction and a `try`/`catch` per
  tenant, so one tenant's failure is logged and skipped rather than aborting the rest of the loop.
- **Log the skipped clone** — the seeder gains an `ILogger` (or an equivalent seam that does not
  drag ASP.NET logging into `Storage.Postgres`) and records which tenant/template pair was skipped
  and why.
- **Make the resolved assignment convergent** — either pin the resolution to one branch, or
  reconcile the user's admin-equivalent rows so a second run cannot leave two.
- **Validate the user id on role assignment** — reject an id matching no user in the tenant instead
  of writing an orphan row, and decide whether `user_roles` should carry a foreign key onto `users`
  (a migration, hence a decision rather than an assumption).

## Capabilities

### New Capabilities

- `rbac-migration-seeder-resilience`: the boot-time role migration survives a single tenant's
  failure, records what it skipped, converges across re-runs, and cannot be handed a user that does
  not exist.

### Modified Capabilities

<!-- None. `rbac-permission-vocabulary` owns the catalog-integrity invariant and the seed's
     visibility; none of its requirements change here. This change is about the migration loop's
     blast radius, its observability, its convergence, and the endpoint that writes the same
     table. -->

## Impact

- **Seeds:** `src/Verbara.Platform.Storage.Postgres/Seeds/RbacMigrationSeeder.cs` — the tenant loop,
  the clone-skip path and the resolving assignment. `RoleTemplateSeeder.ReseedExistingTenantsAsync`
  is the reference implementation for per-tenant transactions.
- **Logging seam:** whatever `Storage.Postgres` adopts to log from a seeder. The startup path in
  `Program.cs` already logs a failed seed at Error (ADR-0037); this is the finer-grained event.
- **API host:** `src/Verbara.Platform.Api/Endpoints/RbacEndpoints.cs` — the role-assignment
  endpoint's validation.
- **Data:** a foreign key from `user_roles` onto `users` would be a migration and would fail on any
  database that already carries orphan rows, so the change must decide whether to add it and, if so,
  clean up first. Everything else is code-only.
- **Tests:** `tests/Verbara.Platform.Storage.Postgres.Tests/Seeds/` — `PostgresRbacFixture` already
  carries the `users` table and the `idx_tenant_roles_name` index added by #225, so the fixture can
  express both the fault-isolation and the convergence cases.
- **Cross-repo:** none. No `Verbara.Sdk` / `Verbara.Sdk.Pro` change, no pin movement.

## Architectural Risk

Adding a foreign key from `user_roles` onto `users` is the only genuinely risky part. It is the
right shape — the absence of that key is what let the orphan row exist — but it will fail to apply
on any deployment that already has orphans, and those are invisible until the migration runs. The
cleanup and the key have to land together, and the cleanup has to be decided rather than assumed:
an orphan row is indistinguishable from a row whose user was hard-deleted.

Per-tenant transactions change the failure mode from "everything after the first failure is missing"
to "one tenant is missing and the rest are fine". That is strictly better, but it also means a
partial failure stops being loud by omission — which is why the logging requirement is part of the
same change rather than a follow-up to it.

### Out of Scope (explicit)

- **The `Admin`/`SystemAdmin` role shortcut** in `PlatformAdminAuthorizationHandler` — unchanged by
  #225 and unchanged here; whether a role should bypass a permission check is its own decision.
- **Per-tenant `platform_admin` roles.** `tools/RbacReseed` matches `role_id = 'platform_admin'`
  while `SetupEndpoints` provisions `platform-admin-{tenant}`, so the CLI does not reach them. A
  pre-existing R5.2 PC.3 gap, named in ADR-0037's Consequences, not resolved here.
- **Grants on suffixed template-derived roles.** The clone only refreshes grants for roles whose
  `role_id` equals the template id, so a tenant whose role is `admin-demo` keeps what it was
  provisioned with. Also named in ADR-0037; it is a provisioning question, not a migration-loop one.
