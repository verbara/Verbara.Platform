---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform operators (self-host / SMB Docker deployments); tenant security owners
decision_ref: Platform/ADR-0003
---

## Why

`users.mfa_secret` and `users.mfa_recovery_codes` are the last MFA enrollment material Platform
persists **unwrapped**. The threat model already classes them as asset **A7 — MFA enrollment
material (TOTP shared secrets, recovery codes), sensitivity High**
(`docs/security/threat-model.md`), yet:

- **`mfa_secret` (TEXT)** holds the raw Base32 TOTP shared secret verbatim. Anyone who reads that
  column can mint valid TOTP codes for that user indefinitely and silently — the second factor is
  fully cloned, with no signal to the user and no audit event.
- **`mfa_recovery_codes` (TEXT[])** holds hashes, but weak ones for the current mint path:
  `RecoveryCodeService` stores a **single-round salted SHA-256 hex** where the salt is
  `user.UserId.Value` — a value stored in the same row (`users.user_id`). Codes are 8 characters
  over a 32-glyph alphabet, i.e. ~40 bits. Salt + hash + a 2^40 keyspace + an unstretched hash means
  an attacker holding the column can recover the plaintext recovery codes offline; per-user salting
  only forbids a shared rainbow table, it does not make the search expensive. (The legacy
  `MfaService.HashRecoveryCodes` path stores BCrypt cost-10 hashes, which are stretched — but both
  formats coexist in the same column today.)

The remediation pattern is already shipped and proven in this repo. `PREPUB-2026-05-09-ADMIN-001`
closed the identical gap for `tenant_auth_config.oidc_client_secret` with a
protect-on-write / unprotect-on-read wrap in `PostgresTenantAuthConfigStore` plus a one-shot,
idempotent `OidcClientSecretEncryptionMigrator` hosted service. The pre-public security review's
audit checklist item 5.1 states the standing rule in general terms — *"Every secret column has
matching Protect/Unprotect pair. Plaintext persistence = P0."* — and `users.mfa_secret` currently
fails it. This change applies the same pattern to the MFA columns rather than inventing a second
one.

**Why now:** the SMB Docker self-host product is the primary track, and a self-host `pg_dump`,
table-scoped export, read-replica, or SQL-injection read of `users` is a realistic operator-side
exposure. Wrapping the columns is a contained, precedent-following change with no API surface
impact.

## What Changes

- **Protect-on-write / unprotect-on-read in `PostgresUserStore`** — the store takes an
  `IDataProtectionProvider` (exactly as `PostgresTenantAuthConfigStore` already does) and creates
  two concern-specific protectors. `SaveAsync` wraps `MfaSecret` and every element of
  `MfaRecoveryCodes` before binding the Npgsql parameters; the `UserRow` → `User` projection
  unwraps them. Plaintext continues to exist only in process memory and never lands in the column.
- **Two purpose strings, not one** — `Verbara.UserMfaSecret` and `Verbara.UserMfaRecoveryCodes`,
  exposed as `public const string` on `PostgresUserStore` so the migrator binds the same values.
  Separate purposes prevent cross-concern decryption and let each rotate independently
  (ADR-0003's concern-specific-purpose convention, as applied by the ADMIN-001 fix).
- **Element-wise wrapping for the `TEXT[]` column** — each recovery-code hash is protected
  individually, keeping the column a `TEXT[]` (no schema change) and making a partially-migrated
  array self-healing.
- **New one-shot `UserMfaEncryptionMigrator` hosted service** — mirrors
  `OidcClientSecretEncryptionMigrator`: trial-`Unprotect` detects "already encrypted"; a
  `CryptographicException` means legacy plaintext and the value is rewritten wrapped. Idempotent
  (a second run performs zero writes), never blocks host startup, treats a missing `users` table
  (SQLSTATE `42P01`) as a no-op, and never logs a secret value — only counts plus an `Activity`.
  Unlike the OIDC migrator it pages: `users` is an unbounded table, so it walks keyset-ordered
  batches instead of materialising every row.
- **New DI extension `AddUserMfaEncryptionMigrator()`** in
  `Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions`, wired in `Program.cs`
  immediately after the existing `AddOidcClientSecretEncryptionMigrator()` call inside the
  Postgres-configured branch.
- **Transitional read fallback** — on `CryptographicException` the store returns the stored value
  verbatim, so a legacy row that the migrator has not yet reached still authenticates. Same
  belt-and-suspenders posture as `PostgresTenantAuthConfigStore.UnprotectSecret`.
- **Regression suite** — a Testcontainers fixture + tests asserting the column on disk never equals
  the plaintext, that reads round-trip, that the migrator encrypts legacy rows and is idempotent,
  and that a mixed (partially wrapped) recovery-code array converges.
- **Docs** — extend `docs/decisions/0003-dataprotection-key-persistence-strategy.md` with a
  **protected-column register** (the canonical list of column → purpose-string pairs, which this
  change grows from one row to three), update the threat-model A7 row, and add a `[Unreleased]`
  CHANGELOG entry.
- **NOT a breaking change.** No API contract, DTO, or endpoint changes. `MfaSecret` and
  `MfaRecoveryCodes` are already never returned over HTTP.

## Capabilities

### New Capabilities
- `mfa-secret-encryption-at-rest`: MFA enrollment material (`users.mfa_secret`,
  `users.mfa_recovery_codes`) is wrapped with `IDataProtection` under concern-specific purposes on
  every Postgres write and transparently unwrapped on read, and a one-shot idempotent startup
  migrator converts any legacy unwrapped rows — with the residual-risk boundary (a full-database
  compromise that includes the keyring table is NOT mitigated) stated as part of the contract.

### Modified Capabilities
<!-- None. No existing living spec's requirements change. `tenant-auth-config-surface` covers the
     admin auth-config DTO surface, not user-row persistence, and its requirements are untouched:
     this change adds no field, alters no request/response shape, and modifies no endpoint. The
     ADMIN-001 OIDC-secret wrap was never captured as a living spec, so there is no existing
     encryption capability to extend — this change creates the register instead. -->

## Impact

- **Source (`src/Verbara.Platform.Storage.Postgres/`):**
  - `Stores/PostgresUserStore.cs` — new `IDataProtectionProvider` constructor dependency, two
    purpose constants, protect-on-write in `SaveAsync`, unprotect-on-read in the `UserRow` → `User`
    projection (the projection gains a store parameter, mirroring
    `TenantAuthConfigRow.ToTenantAuthConfig(store)`; all call sites in the file update).
  - `Stores/UserMfaEncryptionMigrator.cs` — **new** hosted service.
  - `ServiceCollectionExtensions.cs` — **new** `AddUserMfaEncryptionMigrator()` extension.
- **Composition root:** `src/Verbara.Platform.Api/Program.cs` — one added registration call inside
  the `coreConnectionString`-configured branch. DI resolves the new constructor argument with no
  ordering change required: both the store and `IDataProtectionProvider` are singletons resolved
  lazily, and `PostgresTenantAuthConfigStore` already proves the pattern across the same
  registration gap.
- **Schema:** none. `mfa_secret TEXT` and `mfa_recovery_codes TEXT[]` (`Migrations/001_Baseline.sql`)
  are unbounded and hold the longer ciphertext without a migration. No index touches either column.
- **Storage footprint:** each wrapped value grows to roughly 3–4× its plaintext length. A fully
  enrolled user costs on the order of a couple of kilobytes extra in `users` — negligible.
- **Tests (`tests/Verbara.Platform.Storage.Postgres.Tests/`):** new
  `Stores/UserMfaEncryptionFixture.cs` + `Stores/PostgresUserStoreMfaEncryptionTests.cs`, modelled
  on the existing `TenantAuthConfigEncryptionFixture` /
  `PostgresTenantAuthConfigStoreEncryptionTests` pair (Testcontainers Postgres, ephemeral
  DataProtection keyring, `[Trait("Category", "Integration")]`).
- **Docs:** `docs/decisions/0003-dataprotection-key-persistence-strategy.md` (protected-column
  register addendum), `docs/security/threat-model.md` (A7 mitigation), `CHANGELOG.md`
  (`[Unreleased]`), and a keyring backup/restore note in the DataProtection operations material.
- **Operations — the one real behavioural risk:** losing the DataProtection keyring now costs MFA
  as well as OIDC. Unwrappable values fall through verbatim, `MfaService.VerifyCode` then fails for
  every enrolled user, and recovery is a per-user admin MFA reset (`MfaAdminService`, already
  audited). Mitigating property: with the default `UsePostgres` keyring the `data_protection_keys`
  table lives in the same database as `users`, so any whole-database backup captures both.
- **Cross-repo:** none. No `Verbara.Sdk` / `Verbara.Sdk.Pro` change, no pin bump, no
  `Verbara.Platform.Web` change — the affected values never cross the HTTP boundary.
- **AOT:** unaffected. `IDataProtection` is already in the AOT-clean host
  (`NpgsqlXmlRepository`, ADR-0022 Phase B); no reflection, no new serialization, no new DTO.

### Out of Scope (explicit)

- **`InMemoryUserStore`** — the InMemory storage backend is the dev/test default and has no
  at-rest surface. Encryption here is a persistence concern; the requirement binds the Postgres
  store only.
- **`CachedUserStore`** — the in-process `IMemoryCache` decorator continues to cache the unwrapped
  `User` (as it already does for `PasswordHash`). ADR-0010's trust-boundary statement governs and
  is unchanged; nothing is added to the Redis pubsub payload, which still carries invalidation keys
  only.
- **Encrypting the DataProtection keyring itself.** `AddPlatformDataProtection` has no
  `ProtectKeysWith*` option today, so `data_protection_keys` holds key XML in the clear. An
  attacker with a **complete** database dump therefore gets keyring and ciphertext together and
  this change buys nothing against them — the mitigation targets partial exposure (table-scoped
  dump, `pg_dump -t users`, read-replica or CSV export, SQL-injection read of `users`). That
  residual risk is stated in the spec rather than papered over; wrapping the keyring at rest is a
  separate change against ADR-0003.
- **Re-hashing recovery codes with a stretched KDF.** Migrating the SHA-256-hex format to BCrypt or
  Argon2 would invalidate every outstanding code and needs its own change; this one wraps whatever
  hash format is present as an opaque string.
- **Adjacent defect discovered while scoping, deliberately NOT fixed here:** the login redemption
  path (`AuthEndpoints` → `MfaService.ValidateRecoveryCode`) verifies **only** BCrypt-format hashes
  via `BCrypt.Verify`, while the wizard mint paths (`MfaEnrollEndpoints`,
  `ProfileRecoveryCodesEndpoints`) write `RecoveryCodeService` SHA-256-hex hashes, and
  `RecoveryCodeService.Verify` has no caller in `src/`. Codes minted by the wizard therefore appear
  unredeemable at login. That is a pre-existing correctness bug independent of encryption and
  warrants its own change; this change must neither fix nor worsen it, which is exactly why the
  wrap treats every array element as an opaque string.
- **Rewrapping on key rotation.** DataProtection unwraps with any key still in the ring, so rotated
  keys keep old values readable; a value is rewrapped under the current key only when its row is
  next saved. No bulk rewrap job is built here.
