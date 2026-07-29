# mfa-secret-encryption-at-rest Specification

## Purpose

Threat-model asset **A7 — MFA enrollment material** must not sit unwrapped in a Postgres column.
This capability holds the contract for how `users.mfa_secret` (the Base32 TOTP shared secret) and
`users.mfa_recovery_codes` (a `TEXT[]` of digests) are protected at rest: wrapped on write and
unwrapped on read through `IDataProtection`, under **one purpose per column** so no concern can
decrypt another's ciphertext, with the array wrapped **element-wise** so the column keeps its shape.

**How to read the durable content.** The value here is the *shape*, not the specific columns:
protect-on-write / unprotect-on-read at the store boundary; per-value and per-element verbatim
fallback on `CryptographicException`, which is what lets a not-yet-migrated row keep authenticating
across a deploy; trial-unwrap rather than a marker column or a format sniff to decide what is already
wrapped; and a one-shot, idempotent, non-blocking startup migrator that writes only rows carrying a
legacy value, so a re-run costs zero writes. That shape is re-instantiated for every column added to
ADR-0003's protected-column register — this capability is the register's behavioural half. The
purpose-string literals are a **persistence contract**: renaming one makes every value stored under
it unreadable, so a rename requires a rewrap migration and never a bare edit.

**Read the last requirement as load-bearing, not as a caveat.** With the default `UsePostgres`
keyring, `data_protection_keys` holds key XML unencrypted in the same database as the wrapped
columns, so a complete database dump defeats this entirely. What it mitigates is *partial* exposure —
a table-scoped dump, a report extract, a read-replica, a SQL-injection read that reaches `users` but
not the keyring. Anyone citing this capability as "secrets are encrypted at rest" without that
qualifier is misreading it.

Deliberately outside this capability: unifying or strengthening the recovery-code digest families
(both are wrapped as opaque strings and neither is inspected), and wrapping the keyring itself —
named as the follow-up in the ADR-0003 addendum.

## Requirements
### Requirement: MFA enrollment material is wrapped with DataProtection before it reaches a Postgres column

`PostgresUserStore` MUST wrap `User.MfaSecret` and every element of `User.MfaRecoveryCodes` through
`IDataProtector.Protect` before binding them as Npgsql parameters, so neither the `users.mfa_secret`
column nor any element of the `users.mfa_recovery_codes` array ever holds the caller-supplied value
verbatim. This closes the standing rule stated by the pre-public security-review audit checklist
item 5.1 — *"Every secret column has matching Protect/Unprotect pair. Plaintext persistence = P0."*
— for threat-model asset **A7 (MFA enrollment material)**, using the same protect-on-write /
unprotect-on-read pattern already shipped for `tenant_auth_config.oidc_client_secret` under
`PREPUB-2026-05-09-ADMIN-001` (Platform/ADR-0003's concern-specific-purpose convention).

Wrapping MUST be applied on **every** write path into those columns. `PostgresUserStore.SaveAsync`
is the sole SQL writer of `users` in `src/` and therefore the sole enforcement point; any future
writer of either column MUST route through the same protectors. A `null` or empty `MfaSecret` MUST
persist as SQL `NULL`, not as the ciphertext of an empty string; a `null` `MfaRecoveryCodes` MUST
persist as SQL `NULL`, and an empty collection MUST persist as an empty array — the wrap MUST NOT
change the null/empty shape of either column.

Encryption (not hashing) is required for `mfa_secret` because TOTP verification needs the shared
secret itself: `MfaService.VerifyCode` recomputes the code from the secret, so a one-way digest is
not an available option for that column.

#### Scenario: A saved TOTP secret is unreadable in the column

- **GIVEN** a `PostgresUserStore` constructed with an `IDataProtectionProvider`
- **WHEN** a `User` carrying a Base32 TOTP shared secret is passed to `SaveAsync`
- **THEN** reading `users.mfa_secret` for that row with raw SQL returns a value that is NOT equal to
  the supplied secret
- **AND** the stored value is materially longer than the supplied secret, because DataProtection
  ciphertext carries a key header and is base64url-encoded

#### Scenario: A saved recovery-code array is unreadable in the column

- **GIVEN** a `PostgresUserStore` constructed with an `IDataProtectionProvider`
- **WHEN** a `User` carrying a list of recovery-code hashes is passed to `SaveAsync`
- **THEN** reading `users.mfa_recovery_codes` for that row with raw SQL returns an array of the same
  length whose every element differs from the corresponding supplied hash
- **AND** no element of the stored array is equal to any element of the supplied list

#### Scenario: Null and empty MFA material keep their column shape

- **GIVEN** a `PostgresUserStore` constructed with an `IDataProtectionProvider`
- **WHEN** a `User` with `MfaSecret` `null` and `MfaRecoveryCodes` `null` is passed to `SaveAsync`
- **THEN** `users.mfa_secret` is SQL `NULL` and `users.mfa_recovery_codes` is SQL `NULL`
- **AND** saving a `User` whose `MfaRecoveryCodes` is an empty collection stores an empty array, not
  `NULL` and not a one-element array of ciphertext

### Requirement: Reads transparently unwrap, falling back verbatim for not-yet-migrated values

The `UserRow` → `User` projection in `PostgresUserStore` MUST unwrap `mfa_secret` and every element
of `mfa_recovery_codes` through `IDataProtector.Unprotect` so that every existing caller —
`MfaService.VerifyCode`, `MfaService.ValidateRecoveryCode`, `MfaEnrollEndpoints`,
`ProfileRecoveryCodesEndpoints`, `MfaAdminService` — receives exactly the values it received before
this change, with no caller-side edit. When `Unprotect` throws `CryptographicException` — which
means the stored value is a legacy unwrapped row the migrator has not yet rewritten — the projection
MUST return that stored value verbatim rather than throwing or nulling the field, mirroring
`PostgresTenantAuthConfigStore.UnprotectSecret`.

The verbatim fallback MUST be evaluated **per value**: a row whose `mfa_secret` is wrapped but whose
`mfa_recovery_codes` array is still legacy (or an array with a mix of wrapped and legacy elements,
which a crash mid-migration can produce) MUST project correctly, element by element.

The transformation MUST be transparent in both directions: a value written through `SaveAsync` and
read back through any of `GetByIdAsync`, `GetByEmailAsync`, `FindByOidcSubjectAsync`, `ListAsync`,
or `GetByIdsAsync` MUST equal the original.

#### Scenario: A wrapped secret round-trips unchanged

- **GIVEN** a `User` whose TOTP secret was persisted through `SaveAsync`
- **WHEN** the same user is fetched via `GetByIdAsync`
- **THEN** the returned `User.MfaSecret` equals the originally supplied secret exactly
- **AND** the returned `User.MfaRecoveryCodes` equals the originally supplied list, in order

#### Scenario: A legacy unwrapped row still authenticates before the migrator reaches it

- **GIVEN** a `users` row whose `mfa_secret` column was written directly as a legacy unwrapped
  Base32 secret and whose `mfa_recovery_codes` array holds legacy unwrapped hashes
- **WHEN** that user is fetched through `PostgresUserStore`
- **THEN** `User.MfaSecret` is the legacy value verbatim and `User.MfaRecoveryCodes` holds the legacy
  hashes verbatim
- **AND** no exception surfaces to the caller, so TOTP verification and recovery-code redemption keep
  working during the window between deploy and migrator completion

#### Scenario: A partially wrapped recovery-code array projects element by element

- **GIVEN** a `users` row whose `mfa_recovery_codes` array holds some wrapped elements and some
  legacy unwrapped elements (the shape a crash mid-migration leaves behind)
- **WHEN** that user is fetched through `PostgresUserStore`
- **THEN** each wrapped element is returned unwrapped and each legacy element is returned verbatim
- **AND** the returned list preserves the stored order and length

### Requirement: Each protected column binds its own concern-specific DataProtection purpose

`PostgresUserStore` MUST create two distinct `IDataProtector` instances — one for `mfa_secret` and
one for `mfa_recovery_codes` — from concern-specific purpose strings, so a protector for one concern
cannot decrypt another concern's ciphertext and each concern can rotate independently. The purpose
strings MUST be `Verbara.UserMfaSecret` and `Verbara.UserMfaRecoveryCodes`, exposed as
`public const string` members on `PostgresUserStore` so the migrator binds the identical values
rather than duplicating string literals (the shape `PostgresTenantAuthConfigStore.OidcClientSecretProtectorPurpose`
already establishes).

Purpose strings are part of the persistence contract: once rows exist, changing a purpose string
renders every value under the old purpose unreadable. A future change that renames one MUST ship a
rewrap migration, never a bare rename.

#### Scenario: A protector for one purpose cannot read the other purpose's ciphertext

- **GIVEN** a value wrapped under the `Verbara.UserMfaSecret` purpose
- **WHEN** a protector created from the `Verbara.UserMfaRecoveryCodes` purpose attempts to unwrap it
- **THEN** the attempt throws `CryptographicException` rather than returning the value

#### Scenario: The migrator binds the store's purpose constants

- **GIVEN** the one-shot MFA encryption migrator
- **WHEN** it creates its protectors
- **THEN** it does so from the `public const string` purpose members declared on `PostgresUserStore`,
  not from re-typed literals, so store and migrator can never drift apart

### Requirement: A one-shot idempotent startup migrator wraps every legacy unwrapped row

Platform MUST register a hosted service that, once per process boot, scans `users` for rows where
`mfa_secret IS NOT NULL OR mfa_recovery_codes IS NOT NULL` and rewrites any value that is not
already wrapped, mirroring `OidcClientSecretEncryptionMigrator`. Detection MUST be by **trial
unwrap**: a value that round-trips through `Unprotect` is already wrapped and MUST be left untouched;
a value whose `Unprotect` throws `CryptographicException` MUST be treated as legacy and rewritten
wrapped. Detection MUST be per value and, for the array column, per element.

The migrator MUST be idempotent — a second run over an already-migrated table finds every value
wrapped and performs **zero** writes — so it is safe to leave registered indefinitely. It MUST issue
an `UPDATE` only for rows carrying at least one legacy value; a fully wrapped row MUST NOT be
rewritten (a no-op write would churn the ciphertext and the row's WAL footprint for nothing).

Unlike the OIDC migrator, which materialises a single row per tenant, `users` is an unbounded table:
the migrator MUST walk it in bounded keyset-ordered batches rather than loading every matching row
into memory at once.

The migrator MUST be registered through a dedicated
`AddUserMfaEncryptionMigrator()` extension on
`Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions`, invoked from `Program.cs` inside
the Postgres-configured branch alongside the existing `AddOidcClientSecretEncryptionMigrator()` call.

#### Scenario: Legacy rows are wrapped on the next boot

- **GIVEN** `users` rows written before this change, holding an unwrapped `mfa_secret` and unwrapped
  `mfa_recovery_codes` elements
- **WHEN** the host starts and the migrator runs
- **THEN** every one of those columns holds ciphertext afterwards, and no column equals its previous
  value
- **AND** fetching each affected user through `PostgresUserStore` returns the same MFA material it
  returned before the migration

#### Scenario: A second run performs zero writes

- **GIVEN** a `users` table in which every MFA value is already wrapped
- **WHEN** the migrator runs again
- **THEN** it reports every scanned value as already-wrapped and issues no `UPDATE` statement
- **AND** the ciphertext stored in every column is byte-for-byte unchanged

#### Scenario: A mixed array converges in one pass

- **GIVEN** a `users` row whose `mfa_recovery_codes` array holds a mix of wrapped and legacy elements
- **WHEN** the migrator runs
- **THEN** the legacy elements are rewritten wrapped and the already-wrapped elements are left
  byte-for-byte unchanged
- **AND** the array's length and element order are preserved

#### Scenario: A large table is walked in bounded batches

- **GIVEN** a `users` table with more rows carrying MFA material than one batch holds
- **WHEN** the migrator runs
- **THEN** it advances through keyset-ordered batches rather than materialising all matching rows at
  once
- **AND** every matching row is visited exactly once

### Requirement: The migrator never blocks host startup and never logs a secret value

The migrator MUST NOT prevent the host from starting: any failure — a Postgres error, an unwrap
failure on an individual row, or an unexpected exception — MUST be logged and swallowed so the next
deploy retries, exactly as `OidcClientSecretEncryptionMigrator` does. A missing `users` table
(SQLSTATE `42P01`, which a fresh install can present when the boot order puts the migrator ahead of
the schema runner) MUST be treated as a silent no-op. Cancellation during host shutdown MUST exit
cleanly without an error-level log.

A failure on one row MUST NOT abandon the remaining rows: the migrator counts the failure and
continues.

The migrator MUST NOT emit any MFA secret, recovery-code value, ciphertext, or user email to logs or
telemetry. It MAY emit counts (scanned, newly wrapped, already wrapped, failed) via `[LoggerMessage]`
source-generated logs and an `Activity` carrying the same counts as tags — no values.

#### Scenario: A Postgres failure does not stop the host

- **GIVEN** a boot in which the migrator's query fails with a Postgres error
- **WHEN** the host starts
- **THEN** the failure is logged at error level and the host continues starting normally
- **AND** the next deploy retries the migration

#### Scenario: A missing users table is a silent no-op

- **GIVEN** a boot where `users` does not yet exist and the query fails with SQLSTATE `42P01`
- **WHEN** the migrator runs
- **THEN** it exits without an error-level log and the host starts normally

#### Scenario: One bad row does not abandon the rest

- **GIVEN** a batch in which one row's rewrite fails
- **WHEN** the migrator processes that batch
- **THEN** the failure is counted and the migrator continues with the remaining rows in the batch and
  the remaining batches

#### Scenario: Logs and telemetry carry counts, never values

- **GIVEN** a migration run that wraps at least one legacy row
- **WHEN** its logs and its `Activity` tags are inspected
- **THEN** they contain only counts (scanned / newly wrapped / already wrapped / failed)
- **AND** they contain no MFA secret, no recovery-code value, no ciphertext, and no user email

### Requirement: The wrap is format-agnostic and changes no schema, API, or hash format

The wrap MUST treat every recovery-code element as an **opaque string**. `users.mfa_recovery_codes`
holds two coexisting hash formats today — BCrypt cost-10 digests from `MfaService.HashRecoveryCodes`
and salted SHA-256 hex digests from `RecoveryCodeService.Hash` — and this change MUST NOT inspect,
normalise, re-hash, or migrate between them. Whatever string the caller supplies is what is wrapped;
whatever string was wrapped is what is returned.

The change MUST NOT alter the database schema: `mfa_secret TEXT` and `mfa_recovery_codes TEXT[]` are
unbounded and hold the longer ciphertext without a SQL migration, and no index covers either column.

The change MUST NOT alter any API contract. `MfaSecret` and `MfaRecoveryCodes` are already never
returned over HTTP, so no DTO, no `ApiJsonContext` entry, no endpoint, and no
`Verbara.Platform.Web` client changes. The change MUST NOT touch `Verbara.Sdk` or
`Verbara.Sdk.Pro` and MUST NOT require a package pin bump — the cross-repo chain
`Sdk → Sdk.Pro → Platform ← Platform.Web` is unaffected in both directions.

The requirement binds the **Postgres** persistence path only. `InMemoryUserStore` has no at-rest
surface and MUST remain unchanged. `CachedUserStore` continues to cache the unwrapped `User` in
process memory under ADR-0010's existing trust boundary, and MUST NOT begin serializing MFA
material to Redis.

#### Scenario: Both recovery-code hash formats survive a round trip byte-for-byte

- **GIVEN** a `User` whose `MfaRecoveryCodes` mixes BCrypt cost-10 digests and salted SHA-256 hex
  digests
- **WHEN** the user is saved and fetched back
- **THEN** every element returns byte-for-byte identical to what was supplied
- **AND** neither format was re-hashed, normalised, or converted

#### Scenario: No schema migration accompanies the change

- **GIVEN** the change as shipped
- **WHEN** `src/Verbara.Platform.Storage.Postgres/Migrations/` is inspected
- **THEN** no new `.sql` file is required for the columns to hold ciphertext
- **AND** an existing deployment takes the change without a schema change

#### Scenario: The InMemory backend is untouched

- **GIVEN** a Platform boot configured with `Storage.InMemory`
- **WHEN** MFA enrollment and verification run
- **THEN** behaviour is identical to before this change, with no DataProtection dependency introduced
  into the InMemory store

### Requirement: The residual-risk boundary is stated, not implied

The change MUST document that wrapping these columns does **not** defend against a compromise that
includes the DataProtection keyring. With the default `UsePostgres` keyring, key XML is persisted
unencrypted in the `data_protection_keys` table in the same database as `users`
(`AddPlatformDataProtection` exposes no `ProtectKeysWith*` option), so an attacker holding a complete
database dump holds both keyring and ciphertext. The mitigation this change delivers targets
**partial** exposure: a table-scoped dump or export of `users`, a read-replica or CSV extract, or a
SQL-injection read that reaches `users` but not `data_protection_keys`.

The operational consequence MUST be documented too: losing the keyring now costs MFA as well as
OIDC. Unwrappable values fall through verbatim, `MfaService.VerifyCode` then fails for every enrolled
user, and recovery is a per-user admin MFA reset through `MfaAdminService` (already audit-emitting).
The mitigating property — with the default Postgres-backed keyring, `data_protection_keys` and
`users` are captured by the same whole-database backup — MUST be stated alongside it.

Platform/ADR-0003 MUST carry the canonical **protected-column register**: the list of
column → purpose-string pairs, which this change grows from `tenant_auth_config.oidc_client_secret`
alone to include `users.mfa_secret` and `users.mfa_recovery_codes`. Any future column-level secret
MUST be added to that register when it is wrapped.

#### Scenario: The register lists every protected column and its purpose

- **GIVEN** Platform/ADR-0003 after this change
- **WHEN** its protected-column register is read
- **THEN** it lists `tenant_auth_config.oidc_client_secret` → `Verbara.OidcClientSecret`,
  `users.mfa_secret` → `Verbara.UserMfaSecret`, and `users.mfa_recovery_codes` →
  `Verbara.UserMfaRecoveryCodes`

#### Scenario: The keyring-loss recovery path is documented

- **GIVEN** an operator who has lost the DataProtection keyring
- **WHEN** they consult the operations material
- **THEN** it states that enrolled users can no longer complete TOTP verification, that the recovery
  path is a per-user admin MFA reset, and that a whole-database backup captures the keyring together
  with the wrapped columns

#### Scenario: The threat model records the A7 mitigation and its limit

- **GIVEN** `docs/security/threat-model.md` after this change
- **WHEN** the A7 row and its mitigations are read
- **THEN** they record that MFA enrollment material is DataProtection-wrapped at rest
- **AND** they state explicitly that a full-database compromise including `data_protection_keys` is
  outside what that wrap mitigates

