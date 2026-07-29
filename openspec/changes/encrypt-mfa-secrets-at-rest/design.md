## Context

`users.mfa_secret TEXT` and `users.mfa_recovery_codes TEXT[]` (declared in
`src/Verbara.Platform.Storage.Postgres/Migrations/001_Baseline.sql`) are written and read by exactly
one component: `PostgresUserStore`. Today the values pass straight through — `SaveAsync` binds
`user.MfaSecret` and `user.MfaRecoveryCodes?.ToArray()` directly as Npgsql parameters, and
`UserRow.ToUser()` projects the columns back with no transformation.

**Current state of what those columns hold:**

| Column | Content today | Reversible by an attacker holding the column? |
|---|---|---|
| `mfa_secret` | raw Base32 TOTP shared secret | trivially — it *is* the secret |
| `mfa_recovery_codes` (legacy path, `MfaService.HashRecoveryCodes`) | BCrypt cost-10 digests | stretched; expensive |
| `mfa_recovery_codes` (wizard path, `RecoveryCodeService.Hash`) | single-round SHA-256 hex, salt = `users.user_id` | ~40-bit keyspace, unstretched, salt in the same row — cheap offline |

Both recovery-code formats coexist in the same column because two mint paths are live:
`AuthEndpoints` (legacy) writes BCrypt; `MfaEnrollEndpoints` and `ProfileRecoveryCodesEndpoints`
(wizard) write SHA-256 hex.

**The precedent this follows.** `PREPUB-2026-05-09-ADMIN-001` closed the identical gap for
`tenant_auth_config.oidc_client_secret`, and the shipped shape is the template here:

- `PostgresTenantAuthConfigStore` takes `IDataProtectionProvider`, exposes
  `public const string OidcClientSecretProtectorPurpose = "Verbara.OidcClientSecret"`, and carries
  an internal `ProtectSecret` / `UnprotectSecret` pair; `SaveAsync` wraps and
  `TenantAuthConfigRow.ToTenantAuthConfig(store)` unwraps.
- `UnprotectSecret` catches `CryptographicException` and returns the value verbatim — the
  transitional guard for rows the migrator has not yet rewritten.
- `OidcClientSecretEncryptionMigrator : IHostedService` runs once per boot, detects
  already-encrypted by trial `Unprotect`, is idempotent, treats SQLSTATE `42P01` as a no-op,
  swallows every other failure so startup is never blocked, and logs counts only.
- `AddOidcClientSecretEncryptionMigrator()` in `ServiceCollectionExtensions`, called from
  `Program.cs` inside the `coreConnectionString`-configured branch.

**Constraints.** Native AOT (Platform/ADR-0022): no reflection, `[LoggerMessage]` source-generated
logging, `Verbara.Sdk.Data.Npgsql` for all data access (Dapper permanently banned),
`TreatWarningsAsErrors` with `WarningLevel 9999`. DataProtection is already AOT-clean in this host
via `NpgsqlXmlRepository` (Platform/ADR-0003 + ADR-0022 Phase B).

**Stakeholders.** Self-host / SMB Docker operators (the primary product track) and tenant security
owners. No frontend or SDK stakeholder: the values never cross the HTTP boundary.

## Goals / Non-Goals

**Goals:**
- Wrap `users.mfa_secret` and every element of `users.mfa_recovery_codes` with `IDataProtection`
  under concern-specific purposes, on every Postgres write, with transparent unwrap on read.
- Convert existing unwrapped rows with a one-shot, idempotent, non-blocking startup migrator that
  pages over `users`.
- Change nothing a caller can observe: no schema migration, no API/DTO change, no hash-format
  change, no caller edits, no cross-repo pin movement.
- Pin the contract with a Testcontainers regression suite matching the ADMIN-001 suite's shape.
- Record the protected-column register in Platform/ADR-0003 and state the residual-risk boundary
  honestly in the threat model.

**Non-Goals:**
- Encrypting the DataProtection keyring itself. `AddPlatformDataProtection` has no
  `ProtectKeysWith*` option; `data_protection_keys` holds key XML in the clear in the same database.
  A complete database dump defeats this change. That is a separate change against ADR-0003.
- Re-hashing recovery codes with a stretched KDF, or unifying the two coexisting hash formats.
- Fixing the adjacent redemption-format defect (login verifies BCrypt only via
  `MfaService.ValidateRecoveryCode`, while the wizard mints SHA-256 hex and
  `RecoveryCodeService.Verify` has no caller in `src/`). Documented in the proposal; its own change.
- Touching `InMemoryUserStore` or changing `CachedUserStore`'s trust boundary (Platform/ADR-0010).
- A bulk rewrap job on DataProtection key rotation.

## Decisions

**D1 — Wrap in the store, not in a Postgres-side mechanism (pgcrypto / TDE / a column trigger).**
The protect/unprotect pair lives in `PostgresUserStore` because that is where ADMIN-001 put it and
because it keeps the key material outside the database engine's reach. *Alternatives considered:*
`pgcrypto` column encryption — rejected, the key would have to be supplied to Postgres per query
(landing in `pg_stat_statements` and logs) or stored in the database, which defeats the purpose;
filesystem/volume-level encryption — rejected, it protects a stolen disk but not a `pg_dump`,
a replica, or a SQL-injection read, which are the exposures this change targets.

**D2 — Two purpose strings, not one.**
`Verbara.UserMfaSecret` and `Verbara.UserMfaRecoveryCodes`, as `public const string` on
`PostgresUserStore` (mirroring `PostgresTenantAuthConfigStore.OidcClientSecretProtectorPurpose`), so
the migrator binds the same symbols instead of re-typed literals. *Alternative:* one shared
`Verbara.UserMfa` purpose — rejected: ADR-0003's convention is concern-specific purposes so each
concern rotates independently and cross-purpose decryption is impossible, and the two columns have
genuinely different lifecycles (the secret is written once at enroll; the array is rewritten on
every code redemption).

**D3 — Element-wise wrapping for the `TEXT[]` column.**
Each recovery-code hash is protected individually and the column stays `TEXT[]`. *Alternatives:*
(a) serialize the whole list to JSON, protect once, store as a single-element array — rejected: it
silently changes the column's shape, breaks any ad-hoc SQL that reasons about array length, and
makes a crash mid-migration unrecoverable without special-casing; (b) protect the joined string into
a new `TEXT` column — rejected: requires a schema migration and a dual-read window for no benefit.
Element-wise also makes partial migration self-healing: each element carries its own
already-wrapped/legacy verdict, so a row interrupted mid-rewrite converges on the next boot.

**D4 — Detection by trial `Unprotect`, not by a format sniff or a marker column.**
A value that round-trips is already wrapped; a `CryptographicException` means legacy. *Alternatives:*
a `mfa_encrypted BOOLEAN` marker column — rejected: needs a schema migration and can desynchronise
from reality (a row rewritten by an older binary would lie); prefix sniffing (`$2a$` for BCrypt, 64
hex chars for SHA-256, Base32 for the TOTP secret) — rejected: it hard-codes knowledge of the hash
formats into the encryption layer, which is exactly the coupling the spec's format-agnostic
requirement forbids, and it would break the moment a third format appears. Trial unwrap is also what
ADMIN-001 already does, so operators reason about one mechanism.

**D5 — Keyset-paged batches in the migrator, unlike the OIDC migrator's single `QueryListAsync`.**
`tenant_auth_config` has one row per tenant, so the OIDC migrator materialises everything.
`users` is unbounded — a mid-size deployment can hold six figures of rows — so this migrator walks
`ORDER BY tenant_id, user_id` in batches (500 rows) using a keyset cursor:

```sql
SELECT tenant_id, user_id, mfa_secret, mfa_recovery_codes
FROM users
WHERE (mfa_secret IS NOT NULL OR mfa_recovery_codes IS NOT NULL)
  AND (tenant_id, user_id) > (@LastTenantId, @LastUserId)
ORDER BY tenant_id, user_id
LIMIT @BatchSize
```

*Alternative:* `LIMIT`/`OFFSET` — rejected: `OFFSET` re-scans and, because the migrator mutates rows
inside the predicate's own result set, offset paging can skip rows if the plan reorders. The keyset
cursor is stable under concurrent writes and monotonic. The first batch seeds the cursor with the
empty-string sentinel (`tenant_id`/`user_id` are non-null `TEXT`), so `(tenant_id, user_id) > ('','')`
matches every row.

**D6 — Rewrite only rows carrying at least one legacy value; skip fully-wrapped rows.**
The `UPDATE` fires only when the row's scan found something to change:

```sql
UPDATE users
SET mfa_secret = @MfaSecret, mfa_recovery_codes = @MfaRecoveryCodes
WHERE tenant_id = @TenantId AND user_id = @UserId
```

with `@MfaSecret` bound `NpgsqlDbType.Text` and `@MfaRecoveryCodes` bound
`NpgsqlDbType.Array | NpgsqlDbType.Text`, both explicitly typed because either can be `DBNull.Value`
(the repo's standing Npgsql rule — an untyped nullable parameter throws `42P08`). Skipping
already-wrapped rows is what makes the second run a genuine zero-write no-op rather than a
ciphertext-churning rewrite, and keeps the WAL footprint of an idempotent re-run at zero.

**D7 — The row projection takes the store, mirroring `ToTenantAuthConfig(store)`.**
`UserRow.Map` must stay a `static` method to satisfy the `Verbara.Sdk.Data.Npgsql` mapper delegate,
so the unwrap cannot live in `Map`. `UserRow.ToUser()` becomes `UserRow.ToUser(PostgresUserStore store)`
and calls the store's internal unwrap helpers. Five call sites inside the file update
(`GetByIdAsync`, `GetByEmailAsync`, `FindByOidcSubjectAsync`, both `ListAsync` branches,
`GetByIdsAsync`). This is the exact shape `PostgresTenantAuthConfigStore` already uses; no new
pattern is introduced. *Alternative:* unwrap in each public method after mapping — rejected: six
call sites each able to forget, versus one projection that cannot.

**D8 — Verbatim fallback on `CryptographicException`, evaluated per value and per array element.**
`UnprotectMfaSecret(string?)` and `UnprotectRecoveryCodes(string[]?)` return the stored value when
unwrap fails. This makes deploy order irrelevant: rows the migrator has not reached still
authenticate. *Trade-off accepted:* it also means a lost keyring degrades silently into
"ciphertext returned as the TOTP secret", so `MfaService.VerifyCode` simply returns false rather than
raising an alarm. Mitigated by D9's operational documentation plus the migrator's counters — a
keyring loss shows up as a boot where the migrator suddenly reports a large legacy count on a table
it already migrated, which is a usable detection signal.

**D9 — State the residual risk instead of implying full protection.**
With the default `UsePostgres` keyring, `data_protection_keys` sits in the same database as `users`
and holds key XML unencrypted. A complete dump therefore yields both. This change mitigates
*partial* exposure — `pg_dump -t users`, a CSV/report extract, a read-replica scoped to application
tables, a SQL-injection read that reaches `users` but not `data_protection_keys`. Rather than leave
that unstated, the spec makes it a requirement, the threat model's A7 row records it, and
Platform/ADR-0003 grows a protected-column register listing every column → purpose pair. A follow-up
to wrap the keyring at rest (certificate or KMS) is named but not built here.

**D10 — Registration position: alongside the OIDC migrator, inside the Postgres branch.**
`AddUserMfaEncryptionMigrator()` is called in `Program.cs` immediately after
`AddOidcClientSecretEncryptionMigrator()`. DI needs no ordering work: `AddPostgresStorage` registers
the store roughly 450 lines before `AddPlatformDataProtection` runs, but both are singletons resolved
lazily, and `PostgresTenantAuthConfigStore` already takes `IDataProtectionProvider` across the same
gap. Because both migrators are `IHostedService`, they start in registration order after the schema
runner (`DatabaseMigrationService.ApplyMigrations`) has already executed eagerly at line ~304.

> **Amended during implementation.** "No DI edit is needed" holds for *registration order* and for
> the Api host, but it was too broad: it missed that `AddPostgresStorage` now carries an **implicit
> `IDataProtectionProvider` dependency reachable through `IUserStore`**. `ServiceCollectionExtensionsTests`
> builds a bare `ServiceCollection`, calls `AddPostgresStorage` alone, and resolves `IUserStore` — which
> began throwing `InvalidOperationException: Unable to resolve service for type
> 'IDataProtectionProvider' while attempting to activate 'PostgresUserStore'`. The dependency was
> latent in the package before this change (`PostgresTenantAuthConfigStore` has taken
> `IDataProtectionProvider` since ADMIN-001) but no test resolved a store that needed it.
>
> **Resolution:** register the keyring explicitly in those tests, and document the requirement on both
> `AddPostgresStorage` overloads. `AddPostgresStorage` deliberately does **NOT** call
> `AddDataProtection()` itself — that would silently install an **ephemeral in-memory keyring** when a
> host forgot to configure one, losing every wrapped value on the next process start. That is the
> precise footgun ADR-0003 was written to close ("keys regenerated on each process start… currently in
> use by accident"), so a loud activation failure is the correct behaviour and the fix belongs on the
> caller. Recorded here because the same trap awaits the next column-level secret added to the register.

**D11 — Telemetry shape: `ActivitySource` + `[LoggerMessage]`, counts only.**
`ActivitySource("Verbara.Platform.UserMfaEncryption")` with tags `scanned_rows`,
`encrypted_rows`, `already_encrypted_rows`, `failed_rows`; `[LoggerMessage]` EventIds in the unused
**4201–4205** band (`Storage.Postgres` currently occupies 4001–4003 and 4101–4120). No value, no
ciphertext, no email is ever emitted — the migrator's log surface is the count quadruple plus the
missing-table debug line and the failure error line.

**D12 — Test the store boundary against a real Postgres, not a mock.**
New `UserMfaEncryptionFixture` (Testcontainers `postgres:16-alpine`, minimal `tenants` + `users`
schema covering only the columns `PostgresUserStore` touches, ephemeral in-process DataProtection
keyring) + `PostgresUserStoreMfaEncryptionTests`, both modelled directly on
`TenantAuthConfigEncryptionFixture` / `PostgresTenantAuthConfigStoreEncryptionTests` and carrying
`[Trait("Category", "Integration")]`. The fixture exposes raw-column readers so a test can assert
"the bytes on disk are not the plaintext" — the assertion that actually proves the requirement.
*Alternative:* unit-test the protect/unprotect helpers in isolation — rejected: it would pass even if
`SaveAsync` forgot to call them, which is precisely the regression worth guarding.

## Risks / Trade-offs

- **[A defect in the projection breaks all authentication, not just MFA]** → `PostgresUserStore` is
  the hottest store on the auth path. Mitigation: the projection change is mechanical and confined to
  two fields; the round-trip test covers all five read methods; every non-MFA field is untouched;
  `dotnet test` green plus the existing MFA suites (`MfaAdminEndpointsTests`,
  `MfaPolicyEnforcementTests`, `ChangePasswordMfaStepUpTests`, `OidcMfaEnforcementTests`,
  `AuthEndpointsTests`) act as the caller-side regression net.
- **[Keyring loss now costs MFA as well as OIDC secrets and AgentAssist credentials]** → With the
  default Postgres-backed keyring, `data_protection_keys` and `users` are captured by the same
  whole-database backup, so the common backup/restore path keeps them together. Recovery when the
  keyring is genuinely gone: per-user admin MFA reset via `MfaAdminService` (already audit-emitting).
  Documented in the ops material and stated in the spec.
- **[Rollback to the previous binary reads wrapped rows as opaque strings]** → The old binary does
  not throw — it hands the ciphertext to `MfaService.VerifyCode`, which returns false — so MFA
  verification fails for already-migrated users until the binary rolls forward. Mitigation: the
  deploy runbook must state that rollback across this change requires rolling forward again to
  restore MFA, or admin MFA reset for affected users. Non-MFA authentication is unaffected in both
  directions.
- **[A crash mid-migration leaves a partially wrapped array]** → Per-element trial detection makes
  the next boot converge the row with no manual step; the spec pins this as a scenario and the test
  suite exercises the mixed array explicitly.
- **[Migrator cost on a large `users` table at every boot]** → After the first run every value
  unwraps successfully, so steady state is one keyset-paged scan of rows with non-null MFA material
  and zero writes. Batch size 500 bounds memory; the scan runs on the host's shared
  `NpgsqlDataSource` (ADR-0015) and does not block startup. If the scan ever becomes material, the
  natural follow-up is a completion marker — deliberately not built now, because a marker is exactly
  the thing that can lie (D4).
- **[Ciphertext growth in `users`]** → Each value grows to roughly 3–4× its plaintext length; a
  fully enrolled user costs on the order of a couple of kilobytes more. No index covers either
  column, so no index bloat. Negligible against Postgres TOAST behaviour for `TEXT`/`TEXT[]`.
- **[The change buys nothing against a full-database compromise]** → Accepted and stated (D9), not
  papered over. The follow-up that would close it — wrapping the keyring with a certificate or KMS —
  is named in the ADR addendum as the next step.
- **[The adjacent recovery-code redemption defect could be blamed on this change]** → The wrap is
  byte-for-byte transparent and format-agnostic, and the test suite proves round-trip equality for
  both hash formats, so the pre-existing behaviour is demonstrably unchanged. The defect is recorded
  in the proposal's Out of Scope so the audit trail is unambiguous.

## Migration Plan

1. **Ship the store wrap and the migrator together.** Protect-on-write and the migrator must land in
   the same binary: writes start producing ciphertext immediately, and the migrator converts the
   pre-existing rows on that same boot.
2. **Deploy.** `DatabaseMigrationService.ApplyMigrations` runs eagerly (no new `.sql` file), then the
   hosted services start: the OIDC migrator, then the MFA migrator. Legacy rows authenticate through
   the D8 verbatim fallback for the seconds the scan takes.
3. **Verify.** Confirm the migration-completed log line reports `encrypted_rows` > 0 on the first
   boot and `encrypted_rows` == 0 with `already_encrypted_rows` == the full population on the
   second. Spot-check with raw SQL that `mfa_secret` no longer resembles a Base32 secret, and that a
   TOTP login still succeeds for an enrolled user.
4. **Rollback.** No schema change means no schema rollback. Reverting the binary leaves wrapped rows
   in place and breaks MFA verification for migrated users (see Risks) — the supported response is to
   roll forward, or to admin-reset MFA for affected users. Non-MFA authentication is unaffected
   either way. This asymmetry is why the runbook note is a shipped task, not an afterthought.
5. **Backup posture.** Reconfirm that operator backups capture `data_protection_keys` together with
   the application tables — with the default `UsePostgres` keyring a whole-database `pg_dump` does,
   but a table-scoped backup policy might not.

## Open Questions

- **Should a `KeyringUnavailable` signal be surfaced (a health-check degradation or a metric) when
  the migrator's legacy count spikes on an already-migrated table?** That is the cleanest detection
  for a keyring loss, since D8's fallback is silent by design. Not built here — it needs a stored
  baseline to compare against, which is the completion marker D4 argues against. Recorded as a
  follow-up candidate; the counter in the completion log is the interim signal.
- **Should the follow-up that wraps the keyring at rest (certificate or KMS) be an ADR-0003 amendment
  or its own ADR?** Leaning amendment, since ADR-0003 already owns the persistence-strategy decision
  space — but it changes the deployment contract for every operator, which is ADR-sized. Deferred to
  when that change is proposed; it does not block this one.
