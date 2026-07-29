> **Execution model (Platform convention):** Subagent-Driven Development with FCM batching —
> **Phase A** foundation (batch in one subagent) → **Phase B** critical components (one focused
> subagent each) → **Phase C** integration (batch). Groups 1/2/3 below map to A/B/C.

## 1. Phase A — Foundation (batch)

- [x] 1.1 In `src/Verbara.Platform.Storage.Postgres/Stores/PostgresUserStore.cs`, add the two
  purpose constants as `public const string`, mirroring
  `PostgresTenantAuthConfigStore.OidcClientSecretProtectorPurpose` (design D2):
  `MfaSecretProtectorPurpose = "Verbara.UserMfaSecret"` and
  `MfaRecoveryCodesProtectorPurpose = "Verbara.UserMfaRecoveryCodes"`. Document on each why the
  purpose string is part of the persistence contract — renaming one makes every value stored under
  the old purpose unreadable and requires a rewrap migration, never a bare rename.
- [x] 1.2 Widen the constructor to `PostgresUserStore(NpgsqlDataSource dataSource,
  IDataProtectionProvider dataProtectionProvider)` with `ArgumentNullException.ThrowIfNull` on both
  (the existing expression-bodied ctor becomes a block body). Create and hold two `IDataProtector`
  fields from the 1.1 purposes. No DI edit is needed: the store is registered by type via
  `AddKeyedSingleton<IUserStore, PostgresUserStore>` and `IDataProtectionProvider` is a lazily
  resolved singleton — the same registration-order gap `PostgresTenantAuthConfigStore` already
  crosses (design D10).
- [x] 1.3 Add the four `internal` helpers on the store, mirroring
  `PostgresTenantAuthConfigStore.ProtectSecret` / `UnprotectSecret`:
  `ProtectMfaSecret(string?)`, `UnprotectMfaSecret(string?)`, `ProtectRecoveryCodes(IReadOnlyList<string>?)`
  → `string[]?`, `UnprotectRecoveryCodes(string[]?)` → `string[]?`. Null/empty in ⇒ `null` out for the
  scalar; `null` in ⇒ `null` out and empty in ⇒ empty array out for the collection (spec:
  "Null and empty MFA material keep their column shape"). Each unwrap catches
  `CryptographicException` and returns the stored value **verbatim** — per value for the scalar and
  **per element** for the array (design D8), so a mixed array projects correctly. Add `using
  System.Security.Cryptography;` and `using Microsoft.AspNetCore.DataProtection;`.

## 2. Phase B — Critical components (one focused subagent each)

### 2a. Store write + read paths

- [x] 2.1 In `SaveAsync`, wrap on write: bind `MfaSecret` as
  `(object?)ProtectMfaSecret(user.MfaSecret) ?? DBNull.Value` with `NpgsqlDbType.Text`, and
  `MfaRecoveryCodes` as `(object?)ProtectRecoveryCodes(user.MfaRecoveryCodes) ?? DBNull.Value` with
  `NpgsqlDbType.Array | NpgsqlDbType.Text`. Keep both parameters explicitly typed — either can be
  `DBNull.Value` and an untyped nullable parameter throws `42P08`. Carry an ADMIN-001-style comment
  citing that this is the protect-on-write half.
- [x] 2.2 Change `UserRow.ToUser()` to `UserRow.ToUser(PostgresUserStore store)` and unwrap inside it:
  `MfaSecret = store.UnprotectMfaSecret(mfa_secret)` and
  `MfaRecoveryCodes = store.UnprotectRecoveryCodes(mfa_recovery_codes)`. `UserRow.Map` MUST stay
  `static` (the `Verbara.Sdk.Data.Npgsql` mapper delegate requires it), which is why the unwrap lives
  in the projection and not in `Map` (design D7). Leave every non-MFA field untouched.
- [x] 2.3 Update all five `ToUser()` call sites in the file to pass `this`: `GetByIdAsync`,
  `GetByEmailAsync`, `FindByOidcSubjectAsync`, both `ListAsync` branches (the shared
  `rows.Select(r => r.ToUser())` projection), and `GetByIdsAsync`. Verify by grep that no
  `ToUser()` call without an argument remains.

### 2b. One-shot encryption migrator

- [x] 2.4 Create `src/Verbara.Platform.Storage.Postgres/Stores/UserMfaEncryptionMigrator.cs` as an
  `internal sealed partial class ... : IHostedService`, structurally mirroring
  `OidcClientSecretEncryptionMigrator`: ctor takes `NpgsqlDataSource`, `IDataProtectionProvider`,
  `ILogger<UserMfaEncryptionMigrator>`; creates its two protectors from the **1.1 constants on
  `PostgresUserStore`** (never re-typed literals — spec: "The migrator binds the store's purpose
  constants"); `StopAsync` returns `Task.CompletedTask`.
- [x] 2.5 Implement the keyset-paged scan (design D5) with a class-based row type carrying
  `{ get; init; }` and a hand-written `static Map(NpgsqlDataReader)`, using `QueryListAsync` from
  `Verbara.Sdk.Data.Npgsql`:
  `SELECT tenant_id, user_id, mfa_secret, mfa_recovery_codes FROM users WHERE (mfa_secret IS NOT NULL
  OR mfa_recovery_codes IS NOT NULL) AND (tenant_id, user_id) > (@LastTenantId, @LastUserId) ORDER BY
  tenant_id, user_id LIMIT @BatchSize` with batch size 500 and the cursor seeded at `("", "")`. Loop
  until a batch returns fewer rows than `BatchSize`. Read `mfa_recovery_codes` with the
  `IsDBNull(GetOrdinal(...)) ? null : GetFieldValue<string[]>(...)` shape already used in
  `PostgresUserStore.UserRow.Map`, and `mfa_secret` with `GetStringOrNull`.
- [x] 2.6 Implement per-value / per-element trial-unwrap detection (design D4): a value that
  round-trips through `Unprotect` is already wrapped and is carried through byte-for-byte; a
  `CryptographicException` marks it legacy and it is re-emitted wrapped. Track whether the row
  changed at all, and issue the `UPDATE users SET mfa_secret = @MfaSecret,
  mfa_recovery_codes = @MfaRecoveryCodes WHERE tenant_id = @TenantId AND user_id = @UserId`
  **only when at least one value was legacy** (design D6) — both value parameters explicitly typed
  (`NpgsqlDbType.Text` and `NpgsqlDbType.Array | NpgsqlDbType.Text`) because either can be
  `DBNull.Value`.
- [x] 2.7 Implement the failure posture (spec: "never blocks host startup"): `catch
  (PostgresException ex) when (ex.SqlState == "42P01")` → debug-level no-op log; `catch
  (OperationCanceledException)` → silent clean exit; `catch (Exception ex)` → error-level log and
  return, never rethrow. Wrap the **per-row** rewrite in its own try/catch so one failing row
  increments a `failed` counter and the loop continues with the rest of the batch and the remaining
  batches. Honour `cancellationToken` between batches and between rows.
- [x] 2.8 Add telemetry carrying **counts only** (design D11, spec: "Logs and telemetry carry counts,
  never values"): `ActivitySource("Verbara.Platform.UserMfaEncryption")` with tags `scanned_rows`,
  `encrypted_rows`, `already_encrypted_rows`, `failed_rows`; `[LoggerMessage]` source-generated logs
  using the unused **4201–4205** EventId band (`Storage.Postgres` currently occupies 4001–4003 and
  4101–4120) — completion (Information, the four counts), failure (Error), missing-table (Debug),
  per-row failure (Warning, identified by `tenant_id`/`user_id` only). Assert by review that no
  message template can interpolate a secret, a ciphertext, or a user email.

## 3. Phase C — Integration (batch)

- [x] 3.1 Add `AddUserMfaEncryptionMigrator(this IServiceCollection services)` to
  `src/Verbara.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`, immediately after
  `AddOidcClientSecretEncryptionMigrator`, with an XML-doc comment in the same voice: what it closes
  (unwrapped `users.mfa_secret` / `users.mfa_recovery_codes`, threat-model asset A7), that it is
  idempotent, and that it is safe to register unconditionally.
- [x] 3.2 Call `builder.Services.AddUserMfaEncryptionMigrator();` in
  `src/Verbara.Platform.Api/Program.cs` inside the `coreConnectionString`-configured branch,
  immediately after the existing `AddOidcClientSecretEncryptionMigrator()` call (~line 310), with a
  short comment matching the neighbouring style.

## 4. Regression suite

- [x] 4.1 Create `tests/Verbara.Platform.Storage.Postgres.Tests/Stores/UserMfaEncryptionFixture.cs`
  modelled on `TenantAuthConfigEncryptionFixture` (design D12): Testcontainers `postgres:16-alpine`,
  an `IAsyncLifetime` that builds the minimal schema (`tenants` FK target + the `users` columns
  `PostgresUserStore` touches — do NOT replay the production migration ledger), an ephemeral
  in-process DataProtection keyring, plus `ResetAsync`, `SeedTenantAsync`, and **raw-column readers**
  `ReadRawMfaSecretAsync(tenantId, userId)` and `ReadRawRecoveryCodesAsync(tenantId, userId)` and a
  raw writer `WriteRawMfaMaterialAsync(...)` so tests can plant legacy unwrapped rows.
- [x] 4.2 Create `tests/Verbara.Platform.Storage.Postgres.Tests/Stores/PostgresUserStoreMfaEncryptionTests.cs`
  with `[Trait("Category", "Integration")]` and `IClassFixture<UserMfaEncryptionFixture>`, all names
  following `Method_ShouldExpected_WhenCondition`. Cover:
  - `Save_ShouldPersistEncryptedSecret_WhenMfaSecretProvided` — the raw column is not the plaintext
    and is materially longer.
  - `Save_ShouldPersistEncryptedRecoveryCodes_WhenCodesProvided` — same array length, no element
    equals its supplied counterpart.
  - `Save_ShouldPersistNull_WhenMfaMaterialIsNull` **and** `Save_ShouldPersistEmptyArray_WhenRecoveryCodesEmpty`
    — the null/empty column shape is preserved.
  - `Get_ShouldReturnOriginalMfaMaterial_WhenStoreUsedInternally` — round-trip equality, exercised
    across `GetByIdAsync`, `GetByEmailAsync`, and `GetByIdsAsync`.
  - `Get_ShouldReturnLegacyValueVerbatim_WhenRowNotYetMigrated` — a planted legacy row projects
    verbatim with no exception.
  - `Get_ShouldProjectPerElement_WhenRecoveryCodeArrayPartiallyEncrypted` — the mixed array
    (crash-mid-migration shape) projects element by element, preserving order and length.
  - `Get_ShouldPreserveBothHashFormats_WhenCodesMixBcryptAndSha256Hex` — format-agnostic byte-for-byte
    round trip for a `$2a$10$…` digest and a 64-char hex digest in the same array.
- [x] 4.3 Add migrator tests in the same file (or a sibling class sharing the fixture):
  - `Migration_ShouldEncryptExistingRows_AndBeIdempotent` — plant legacy rows, run the migrator,
    assert the columns changed and the values still project correctly; run it a **second** time and
    assert `encrypted_rows == 0` and that the stored ciphertext is byte-for-byte unchanged (spec:
    "A second run performs zero writes").
  - `Migration_ShouldConvergeMixedArray_WhenSomeElementsAlreadyEncrypted` — legacy elements get
    wrapped, already-wrapped elements are untouched, order and length preserved.
  - `Migration_ShouldVisitEveryRow_WhenPopulationExceedsBatchSize` — seed more than one batch
    (>500 rows carrying MFA material) and assert every row is migrated exactly once, pinning the
    keyset cursor.
  - `Migration_ShouldNotThrow_WhenUsersTableMissing` — 42P01 is a silent no-op.
- [x] 4.4 Assert a protector bound to one purpose cannot unwrap the other purpose's ciphertext
  (`Protector_ShouldThrowCryptographicException_WhenPurposeMismatched`), pinning design D2.
- [x] 4.5 Update any existing test or fixture that constructs `PostgresUserStore` directly for the
  new two-argument constructor. (Grep first — the current tree shows no direct `new PostgresUserStore(`
  outside DI, so this may be a no-op; confirm rather than assume.)
- [x] 4.6 **(added during implementation — see design D10 amendment)** Fix the DI activation
  regression the new constructor surfaced: `ServiceCollectionExtensionsTests` builds a bare
  `ServiceCollection`, calls `AddPostgresStorage` alone and resolves `IUserStore`, which now throws
  `InvalidOperationException: Unable to resolve service for type 'IDataProtectionProvider'`. Register
  the keyring explicitly in the two affected tests (`AddPostgresStorage_ShouldRegisterAllStoreInterfaces`,
  `AddPostgresStorage_ShouldRegisterAllExpectedStores`) and document the requirement in a `<remarks>`
  on BOTH `AddPostgresStorage` overloads. `AddPostgresStorage` MUST NOT self-register
  `AddDataProtection()` — a silently installed ephemeral keyring loses every wrapped value on the next
  process start, which is the exact footgun ADR-0003 closed; a loud activation failure is correct.

## 5. Documentation

- [x] 5.1 Extend `docs/decisions/0003-dataprotection-key-persistence-strategy.md` with a
  **protected-column register** addendum listing every column → purpose pair:
  `tenant_auth_config.oidc_client_secret` → `Verbara.OidcClientSecret`, `users.mfa_secret` →
  `Verbara.UserMfaSecret`, `users.mfa_recovery_codes` → `Verbara.UserMfaRecoveryCodes`; state that
  any future column-level secret is added here when it is wrapped, that purpose strings are a
  persistence contract (rename ⇒ rewrap migration), and name the residual risk plus the follow-up
  that would close it (wrapping the keyring itself with a certificate or KMS — `AddPlatformDataProtection`
  exposes no `ProtectKeysWith*` today).
- [x] 5.2 Update the A7 row and mitigations in `docs/security/threat-model.md`: MFA enrollment
  material is DataProtection-wrapped at rest, **and** a full-database compromise that includes
  `data_protection_keys` is explicitly outside what that wrap mitigates.
- [x] 5.3 Add the keyring backup/restore + recovery note to the DataProtection operations material:
  losing the keyring means enrolled users can no longer complete TOTP verification (values fall
  through verbatim and `MfaService.VerifyCode` simply returns false), recovery is a per-user admin
  MFA reset via `MfaAdminService`, and with the default `UsePostgres` keyring a whole-database backup
  captures `data_protection_keys` alongside `users` — a table-scoped backup policy may not.
- [x] 5.4 Add the rollback asymmetry to the deploy runbook (design Migration Plan step 4): reverting
  the binary across this change leaves wrapped rows in place and breaks MFA verification for migrated
  users until it is rolled forward (or those users are admin-reset). Non-MFA authentication is
  unaffected in both directions. No schema rollback exists because no schema migration ships.
- [x] 5.5 Add a `[Unreleased]` entry to `CHANGELOG.md` recording that MFA enrollment material is now
  encrypted at rest, that a one-shot idempotent startup migrator converts existing rows, and that no
  schema or API change accompanies it.

## 6. Verification

- [x] 6.1 `dotnet build Verbara.Platform.slnx` — **zero warnings** (`TreatWarningsAsErrors=true`,
  `WarningLevel 9999`).
- [ ] 6.2 `dotnet test Verbara.Platform.slnx` green, including the new integration suite and the
  existing MFA caller-side suites that act as the regression net (`MfaAdminEndpointsTests`,
  `MfaPolicyEnforcementTests`, `ChangePasswordMfaStepUpTests`, `OidcMfaEnforcementTests`,
  `AuthEndpointsTests`, `MfaAdminCrossTenantTests`).
  **PARTIAL — left unticked deliberately.** Green where it is attributable to this change:
  new MFA-encryption suite **13/13**, `ServiceCollectionExtensionsTests` **7/7** (after the 4.6 fix),
  `Api.Tests` **1707/1707** (MFA-filtered subset **60/60**). NOT green overall:
  `Storage.Postgres.Tests` fails a varying subset run-to-run (142 → 49 → 8 across runs) on a
  **pre-existing** Testcontainers startup race — every failure is
  `Npgsql.NpgsqlException: Exception while reading from stream` / connection-reset, zero are logic
  failures, and zero are in the new suite. Proven pre-existing by a detached worktree at HEAD
  (`afd61e4f`, without this change): **21 failed / 224**. Root cause: the official postgres
  entrypoint runs `initdb` against a temp server with `listen_addresses=''`, so the fixtures'
  `pg_isready -U postgres` socket probe reports ready ~4 s before anything listens on TCP;
  Testcontainers then declares the container ready and Npgsql dies during authentication. The new
  fixture probes `pg_isready -U postgres -h 127.0.0.1` and is stable. Back-porting `-h 127.0.0.1`
  to the ~13 sibling fixtures is a **separate change** (it would likely also retire
  `parallelizeTestCollections: false`). Tick 6.2 only once that back-port lands — note that CI's
  main lane already excludes `Storage.Postgres.Tests` via
  `--filter "FullyQualifiedName!~Storage.Postgres.Tests"`.
  **Confirmed on PR #212 (run `30432527575`):** CI reproduces the same flake, worse than local —
  `Storage.Postgres.Tests` ran **122 passed / 115 failed of 237**. The `Live-DB Tests (Postgres)`
  check-run still reports **pass**, but ONLY because both of its test steps carry
  `continue-on-error: true`; the underlying `dotnet test` printed `Test Run Failed.` Do not read
  that green check as a green suite. Within that same run, all **13** new
  `PostgresUserStoreMfaEncryptionTests` passed and **0** failed, as did the four repaired
  `ServiceCollectionExtensionsTests` — so nothing in this change contributes to the 115.
  `Identity.Redis.Tests` was **34/34**, matching the CI comment's note that Redis has no analogous
  restart cycle.
- [x] 6.3 Confirm the AOT posture is unchanged: no reflection introduced, no new DTO or serialization
  path, no `[JsonSerializable]` entry needed, and no Dapper reference (the `BanDapperPackageReferences`
  guard stays green). A `dotnet publish` AOT leg of `Verbara.Platform.Api` must emit no new
  `IL2026`/`IL3050`/`IL207x` diagnostics.
- [x] 6.4 Manual end-to-end check against a live Postgres: enroll MFA for a user, verify with raw SQL
  that `mfa_secret` no longer resembles a Base32 secret and that no `mfa_recovery_codes` element
  resembles a bare hash, then complete a TOTP login and redeem a recovery code successfully.
  **RUN against the published Native AOT binary** (`publish/Verbara.Platform.Api`, ELF) in
  `ASPNETCORE_ENVIRONMENT=Production` against `postgres:18-alpine`, driving the real HTTP surface:
  - `POST /api/v1/setup` → 201; `POST /auth/login` → 200.
  - `POST /profile/security/mfa/enroll/init` → 200, secret `2KWU…2FRB` (32-char raw Base32).
  - `POST /profile/security/mfa/enroll/verify` with a TOTP computed externally (RFC 6238,
    SHA1/30s/6) → 200 + 10 recovery codes. `…/complete` → 204.
  - **Raw SQL on `users`:** `mfa_secret` on disk is `CfDJ8…`, **176 chars**, `= plaintext` → false,
    matches `^[A-Z2-7]+$` (Base32) → **false**. All **10** recovery-code elements likewise
    ciphertext: matches `^[0-9A-F]{64}$` → false, matches `^\$2a\$` → false. No plaintext, and
    neither digest format is recognisable on disk.
  - `POST /auth/login` → 200 with `mfaToken` (MFA now demanded);
    **`POST /auth/mfa/verify` with a real TOTP code → 200 + accessToken.** This is the end-to-end
    proof of unwrap-on-read: the secret was written wrapped and read back correctly by
    `MfaService.VerifyCode` through the real host.
  - **The recovery-code redemption sub-clause could NOT be satisfied — and not because of this
    change.** `POST /auth/mfa/verify` with a recovery code returns **500 `Invalid salt version`**:
    `MfaService.ValidateRecoveryCode` calls `BCrypt.Verify` on the SHA-256-hex digest the wizard
    writes, and BCrypt.Net throws parsing a non-`$2a$` string. **Reproduced identically on
    unmodified `origin/main` (`afd61e4f`) against a separate clean database with no encryption
    anywhere** — same 500, same message, every one of the other seven steps also identical. That is
    the adjacent defect the proposal's Out of Scope names, now confirmed live rather than inferred;
    it needs its own change. Ticking 6.4 on that basis: the task's purpose — proving the wrap is
    transparent end-to-end on a real host — is met, and the one unsatisfiable sub-clause fails the
    same way without this change.
- [x] 6.5 Boot twice against a database seeded with legacy unwrapped rows and confirm the migrator's
  completion log reports `encrypted_rows` > 0 on the first boot and `encrypted_rows == 0` with
  `already_encrypted_rows` == the full population on the second (spec: idempotence).
  **RUN — double boot of the published Native AOT binary in Production against `postgres:18-alpine`.**
  Seeded 3 legacy unwrapped rows across 2 tenants: `u1` with BCrypt-format codes, `u2` with
  SHA-256-hex codes, `u3` with a secret and `mfa_recovery_codes NULL`.
  - Boot on the empty DB (schema only): `0 scanned, 0 legacy rows wrapped, 0 already wrapped, 0 failed`.
  - **Boot #1 over the legacy rows: `3 scanned, 3 legacy rows wrapped, 0 already wrapped, 0 failed`.**
    Columns afterwards are `CfDJ8…` ciphertext (secret 32 → 176 chars); `u3`'s NULL array stayed
    NULL, so the null shape survived.
  - **Boot #2: `3 scanned, 0 legacy rows wrapped, 3 already wrapped, 0 failed`**, and the md5 of
    `mfa_secret || mfa_recovery_codes` per row is **byte-for-byte identical to boot #1**. Since
    DataProtection randomises per call, that equality is the zero-write proof on a real host, not
    only in the test suite.
  The EventId 4201 line an operator reads is exactly:
  `User MFA encryption migration completed: {N} scanned, {N} legacy rows wrapped, {N} already wrapped, {N} failed`.
  This complements `Migration_ShouldEncryptExistingRows_AndBeIdempotent` and
  `Migration_ShouldVisitEveryRow_WhenPopulationExceedsBatchSize` (620 rows) at the migrator boundary.
- [x] 6.6 `openspec validate --all --strict` green before the PR (also a CI gate in this repo).
- [x] 6.7 CI green on the PR. **PR #212**, run `30432527575` on `fdf25a9c`: all 11 reporting checks
  pass — `Build + Unit Tests (Release)`, `AOT Publish (Api)`, `Coverage Ratchet`, `Invariant Gates`,
  `OpenSpec Validate`, `Analyze (C#)`, `CodeQL`, `Dependency Review`, `Docs-only gate`,
  `Coverage Script Tests`, `Live-DB Tests (Postgres)`; `Auto-merge safe Dependabot PRs` skips (n/a).
  `Coverage Ratchet` reports patch **100.0% (8/8)**, band line **77.42%** in `[75, 78]`, branch
  **62.99%** ≥ 60, **29297** lines ≥ 27690, exclusion baseline 0 = 0.
  Caveat carried from 6.2, so this tick is not read as more than it is: the
  `Live-DB Tests (Postgres)` check is green only because its steps are `continue-on-error: true`;
  its underlying run was 122/237. That lane is report-only by design and does not gate merge.
  The first run on `1ea42a74` failed `Coverage Ratchet` at patch **0.0% (0/1)**; `fdf25a9c` fixed it
  by making the composition-root registration coverable — see that commit and design D10.
