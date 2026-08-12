# Tasks — canonicalize-rbac-permission-vocabulary

## Phase A — Catalog and grants (P0, unblocks everything)

- [x] A1. `PermissionSeeder.cs`: add 5 canonical ids to the `system` section —
      `system:mfa:manage`, `system:audit:export` (implies `system:audit:view`),
      `system:impersonation:manage`, `system:retention:view`,
      `system:retention:manage` (implies `system:retention:view`). Update the `── system (6) ──`
      section comment to `(11)`. Catalog 83 → 88.
- [x] A2. `PermissionSeeder.cs`: expose the catalog ids for testing —
      a public read-only set derived from `GetPermissions()`, so both test projects can assert
      membership without duplicating the list.
- [x] A3. `RoleTemplateSeeder.AllPermissions()`: delete the 7 retired dot ids (keep
      `security.jwt.rotate`), add the 5 canonical ids, and replace the stale R5.2 P0.9 comment with
      one pointing at ADR-0037.
- [x] A4. Confirm `system_admin`'s exclusion list still denies `platform:tenant:impersonate` and does
      **not** deny `system:impersonation:manage`.

## Phase B — Gates and host (P0b + P1)

- [x] B1. New constants type for the `PlatformAdminRequirement` gate ids, so gates are testable
      rather than string literals at the policy site.
- [x] B2. `Program.cs`: move the 6 gates onto canonical ids —
      MFA admin → `system:mfa:manage`; audit query → `system:audit:view`; audit export →
      `system:audit:export`; impersonation sessions → `system:impersonation:manage`; retention read →
      `system:retention:view`; retention manage → `system:retention:manage`. Leave
      `security.jwt.rotate` untouched. Update the explanatory comments, which currently describe the
      retired ids as "seeded".
- [x] B3. `Program.cs` startup `catch`: `Console.WriteLine` → Error-level structured log carrying the
      exception. Boot still proceeds.
- [x] B4. `AuthEndpoints.RoleDefaultPermissions.Admin`: add the 5 canonical ids (57 → 62).
- [x] B5. Update the XML doc comments in `MfaAdminEndpoints.cs`, `AuditEndpoints.cs`,
      `RetentionAdminEndpoints.cs` and `ManagementImpersonationEndpoints.cs` that quote the retired
      ids.

## Phase C — Guard tests (the invariant)

- [x] C1. `Verbara.Platform.Storage.Postgres.Tests`: every id in `AllPermissions()` and in every
      explicit template list is a catalog member. Failure message names the offending ids.
      (`Seeds/PermissionCatalogIntegrityTests.cs`)
- [x] C2. `Verbara.Platform.Api.Tests`: every gate id constant is a catalog member.
      (`Auth/PlatformAdminPermissionsCatalogTests.cs`)
- [x] C3. Test that all 11 templates are produced and that `admin` holds `AllPermissions()` minus its
      two documented exclusions. (`Seeds/RbacSeedIntegrityTests.cs` — runs the real
      `PermissionSeeder.SeedAsync` + `RoleTemplateSeeder.SeedAsync` against the Testcontainers
      Postgres in `PostgresRbacFixture`, i.e. against the real FKs, so an orphan grant surfaces as
      `23503`.) Also re-pointed the now-inverted retired-id assertions in
      `Seeds/RoleTemplateSeederReseedTests.cs` at the canonical replacements.

## Phase D — Frontend (`Verbara.Platform.Web`)

- [x] D1. `src/admin/sidebar.tsx:443` — `audit.read` → `system:audit:view` (matches the router guard
      on the same destination, which already disagreed).
- [x] D2. `src/admin/security/audit/audit-viewer-page.tsx:113` — `audit.export` →
      `system:audit:export`.
- [x] D3. `src/router.tsx:784` — `security.mfa.admin` → `system:mfa:manage`.
- [x] D4. `src/router.tsx:819` — `retention.read` → `system:retention:view`.
- [x] D5. `src/admin/retention/retention-admin-page.tsx:24` — `retention.manage` →
      `system:retention:manage`.
- [x] D6. `src/router.tsx:809` — impersonation **admin** route → `system:impersonation:manage`.
      Verified: `ImpersonationAdminPage` has no start-impersonation surface; its only data import is
      `use-impersonation-sessions.ts`, which wraps exactly the three session-admin endpoints. The
      false comment claiming the seeder grants both spellings is gone.
- [x] D7. Update the affected component tests.

## Phase F — The second seeder abort (found by E4, once P0b made it visible)

- [x] F1. `RbacMigrationSeeder`: tenant-role clone → untargeted `ON CONFLICT DO NOTHING`, so the
      `idx_tenant_roles_name` unique index is covered as well as the primary key.
- [x] F2. `RbacMigrationSeeder`: `EXISTS` guard on the `tenant_role_permissions` insert — a skipped
      clone must not produce a `23503` on the grant.
- [x] F3. `RbacMigrationSeeder`: user assignment resolves the role (exact template id first, then the
      tenant's same-named role) instead of assuming the clone landed under the template id.
- [x] F4. `PostgresRbacFixture`: add the `idx_tenant_roles_name` unique index and a minimal `users`
      table. Without the index the whole defect class is invisible to the suite — the same fixture-
      fidelity gap that hid the `23503`.
- [x] F5. Regression tests in `RbacSeedIntegrityTests`, verified to have teeth against four separate
      mutations (full pre-fix SQL; `ORDER BY` direction flipped; blind `VALUES`; the skip-instead-of-
      resolve variant). Each mutation fails a different, specific test. 260 → 262 passing.
- [x] F6. Rebuild + restart: `RBAC seeder: permissions, role templates, and per-tenant role
      migration complete.` — no `23503`, no `23505`, first clean seed since at least 2026-07-29.
      Tenant `platform` went 1 → 8 roles (`admin` carrying all 85 grants); `tenant_roles` 507 → 520.
      The `demo` admin resolved onto the tenant's own `admin-demo`, confirming F3 empirically.

> **The two defects were causally chained, not independent.** When `RoleTemplateSeeder` aborted on
> the uncatalogued grant, `admin` and everything after it never reached `role_templates`. On the
> next boot the clone became a no-op and the name subquery resolved to `NULL`, so the pre-fix blind
> `VALUES` insert fired at a role that did not exist — a `23503` reached through an entirely
> different constraint. Fixing the catalog alone would have moved the failure, not removed it.

## Phase E — Verification

- [x] E1. `dotnet build Verbara.Platform.slnx` clean (TreatWarningsAsErrors).
- [x] E2. `dotnet test` — full suite, plus the new guard tests. Re-run after Phase F: 35 projects,
      **3811 passed / 0 failed / 0 warnings**, exit code 0.
- [x] E3. `openspec validate --all --strict`.
- [x] E4. Rebuild the API image, restart against the existing lab database, and confirm from the logs
      that seeding completes without `23503`. **Done — `23503` gone; templates 5 → 11, `admin` grants
      67 → 85, catalog 83 → 88. The rebuilt image also carries `org.opencontainers.image.revision`,
      closing the provenance gap. The now-visible Error log exposed a second, unrelated abort
      (`23505`) → Phase F.**
- [x] E5. Empirical probes against the rebuilt lab, all passing:
      - 11 templates present; `admin` holds 85 grants; catalog 88.
      - **Probe A** — a host-tenant user with `role: Agent` (so the `Admin`/`SystemAdmin` shortcut
        does **not** apply) plus the `admin` tenant role resolves 86 permissions including
        `system:audit:view`, and gets **200** from `/admin/audit/events`. This is the assertion that
        matters: the gate is now satisfiable *by permission*. Before the change the same probe
        returned 403.
      - **Probe B** — the same user shape without the grant falls back to the 4 Agent defaults and
        still gets **403**.
      - `platform@admin.local` now resolves **86 real permissions** instead of the hardcoded 57-id
        (now 62) `RoleDefaultPermissions.Admin` fallback — RBAC, not the mask.
      - Probe users, role assignments and one orphan `user_roles` row cleaned up; residue verified 0.

      > A first run of Probe A reported a false 403. Cause was the probe, not the platform:
      > `POST /admin/users` mints its own GUID and ignores a client-supplied `userId`, so the script
      > assigned the role to a literal id matching no user. `user_roles` has no foreign key onto
      > `users`, so that bogus assignment returned 204. Recorded as a follow-up in the proposal, and
      > it retires the "login resolver returns empty" item that was previously listed as an
      > unexplained open defect — there was no such defect.
- [x] E6. Frontend build + `npx vitest run`.
