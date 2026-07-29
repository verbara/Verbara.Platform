> **Execution model (Platform convention):** Subagent-Driven Development with FCM batching —
> **Phase A** foundation (batch) → **Phase B** critical components (focused, one per subagent) →
> **Phase C** integration (batch). Groups 1/2/3 map to A/B/C.
>
> **Rebase note (resolved).** Authored alongside `encrypt-mfa-secrets-at-rest`, which has since
> merged as **#212**; this branch is now rebased onto a `main` that also carries **#213**
> (role-default permission fallback on refresh) and **#216** (the #212 archive). Three conflicts
> resolved, none semantic: `CHANGELOG.md` and `docs/security/threat-model.md` were append-order
> collisions, and `MfaPolicyEnforcementTests` took #213's refactor — it extracted the fixture into a
> shared `AuthHandlerFixture`, so this change's task-4.7 annotation of the `"code1"` placeholder
> moved there with it, where it now covers every suite sharing that fixture. `AuthEndpoints.cs`
> auto-merged (#213 touches refresh, this change touches `MfaVerify`) and was re-verified rather
> than trusted: full lane **3512 passed / 0 failed**, auth+MFA suites **269/269**, patch coverage
> **100.0% (25/25)**.

## 1. Phase A — Foundation (batch)

- [x] 1.1 Add `MfaVerificationFailure = "mfa_verification_failure"` to `AuthEventTypes`
  (`src/Verbara.Platform.Identity/AuthEvent.cs`), beside the existing `LoginFailure` /
  `MfaEnroll` / `MfaDisable` constants. **First check** whether any check constraint, seeder, or
  consumer enumerates auth-event type strings (grep the migrations and any analytics/reporting
  reader); if one does, extend it in the same commit (design D6).
- [x] 1.2 Elevate `AuthEndpoints.MfaVerify` from `private static` to `internal static`, carrying a
  comment in the same voice as the existing elevations on `Login` / `Refresh` / `MfaConfirm` /
  `MfaDisable` / `RegenerateRecoveryCodes` — visibility raised so `Api.Tests` (which has
  `InternalsVisibleTo`) can invoke the handler directly (design D7).

## 2. Phase B — Critical components (one focused subagent each)

### 2a. Format-agnostic verification in `MfaService`

- [x] 2.1 Change the signature to
  `ValidateRecoveryCode(IRecoveryCodeService recoveryCodes, string code, IReadOnlyList<string> hashedCodes, string salt)`
  in `src/Verbara.Platform.Api/Services/MfaService.cs`, with `ArgumentNullException.ThrowIfNull` on
  the service. Keep the method `static` — `MfaService` is a static utility and every other call site
  is static (design D4).
- [x] 2.2 Dispatch **per element** (design D1), mirroring the shape of
  `PasswordService.VerifyPassword` (`src/Verbara.Platform.Api/Services/PasswordService.cs:108-127`)
  — read it first and match its structure and doc-comment `<list>` style:
  - element `StartsWith("$2", StringComparison.Ordinal)` → BCrypt branch;
  - otherwise → `recoveryCodes.Verify(code, salt, element)`.
  Do NOT decide once for the whole array: a mixed array must verify element by element.
- [x] 2.3 Wrap the BCrypt branch in `try { … } catch (BCrypt.Net.SaltParseException) { /* no match */ }`
  so no stored value can raise out of the method (design D5). Return the existing
  `(bool IsValid, int Index)` tuple shape so the caller's index-removal logic is untouched.
- [x] 2.4 Add a `private const string BcryptPrefix = "$2";` and an XML doc on the method spelling out
  the dispatch table and stating that a value that cannot be positively verified is a non-match,
  never an exception — citing Platform/ADR-0013 as the pattern's origin.
- [x] 2.9 **(added during implementation)** `catch (SaltParseException)` alone does NOT satisfy the
  spec's "MUST NOT return 500 for ANY content of `users.mfa_recovery_codes`". Measured against the
  real library: BCrypt.Net-Next raises `SaltParseException` only when the value does not begin with
  `$`; a digest corrupt *inside* the `$2` family raises `ArgumentOutOfRangeException` (→400),
  `FormatException` (→**500**) or `IndexOutOfRangeException` (→**500**) instead. Extract a single
  guarded helper `Services/BcryptVerifyGuard.SafeVerify` whose exception filter covers all four and
  **nothing wider** (an `OperationCanceledException` must still propagate), and route BOTH
  credential verifiers through it — `MfaService.ValidateRecoveryCode` **and**
  `PasswordService.VerifyPassword`. The latter is in scope because task 5.1 elevates the guard to a
  standing requirement on every stored-credential verifier in ADR-0013; leaving the password path
  non-compliant would make that ADR text false on the day it is written. Pin both with theories over
  the measured corrupt inputs.

### 2b. Redemption handler — wiring and bookkeeping

- [x] 2.5 In `AuthEndpoints.MfaVerify`, add `[FromServices] IRecoveryCodeService recoveryCodes` to the
  parameter list and pass it plus `user.UserId.Value` as the salt into the new
  `MfaService.ValidateRecoveryCode` call (design D3). Leave the success path — index removal,
  `SaveAsync`, `IssueTokensAsync` — exactly as it is.
- [x] 2.6 Add the failure bookkeeping the handler has never had (design D6, spec: "Every failed
  redemption attempt is audited and counted toward lockout"). On the `if (!verified)` path, before
  returning `Results.Unauthorized()`:
  - `var ip = GetIpAddress(context); var ua = GetUserAgent(context);`
  - `await lockoutService.RecordFailedAttemptAsync(user, ip, ua, ct);`
  - `await authEvents.LogAsync(pending.TenantId, user.UserId.Value, AuthEventTypes.MfaVerificationFailure, ip, ua, new Dictionary<string, string> { ["reason"] = … }, ct);`
  Mirror `AuthEndpoints.Login`'s invalid-password branch (`AuthEndpoints.cs:109-115`) line for line.
  `lockoutService` and `authEvents` are ALREADY in the handler's signature — no new DI parameter is
  needed for either.
- [x] 2.7 Distinguish the failure reason in the event payload (e.g. `invalid_totp`,
  `invalid_recovery_code`, `no_factor_supplied`) so the audit trail says which factor failed, without
  ever logging the submitted code, the stored digest, or the secret.
- [x] 2.8 Confirm the success path still resets the attempt counter and emits its success event via
  `IssueTokensAsync` — do not duplicate that bookkeeping on the success branch.

## 3. Phase C — Integration (batch)

- [x] 3.1 Update the only other caller of `ValidateRecoveryCode` —
  `tests/Verbara.Platform.Api.Tests/Services/MfaServiceTests.cs` — for the new signature. The
  compiler proves there are no further call sites; confirm by grep rather than assuming.
- [x] 3.2 Verify `IRecoveryCodeService` is resolvable wherever `MfaVerify` runs. It is registered
  singleton in `Program.cs` (~line 900); confirm the registration precedes nothing that would break
  and that no test factory stubs `IRecoveryCodeService` out.

## 4. Regression suite — cross the seam

> The defect shipped because every existing test verifies the family it minted. These tests are the
> control that would have caught it (spec: "The regression suite crosses the mint-to-redeem seam").

- [x] 4.1 `MfaService`-level tests in `tests/Verbara.Platform.Api.Tests/Services/MfaServiceTests.cs`,
  named `Method_ShouldExpected_WhenCondition`:
  - `ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesSha256Digest` — hash via a real
    `RecoveryCodeService` with a known salt, verify through `MfaService`. **This test fails on
    `origin/main`** — it is the direct regression pin.
  - `ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesBcryptDigest` — the existing behaviour,
    preserved.
  - `ValidateRecoveryCode_ShouldMatchPerElement_WhenArrayMixesBothFamilies` — one BCrypt element and
    one SHA-256 element in the same array; both plaintexts match their own element and the returned
    index is right.
  - `ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsMalformed` — a stored element that is
    neither family (e.g. `"code1"`, the very value the existing fixtures seed) returns no-match and
    **does not throw**.
  - `ValidateRecoveryCode_ShouldReturnFalse_WhenCodeMatchesNothing` — per family.
- [x] 4.2 End-to-end mint→redeem tests, one per mint path (spec: "Each mint path has an end-to-end
  redemption test"). Mint through the real endpoint, then redeem the returned plaintext through the
  real `POST /auth/mfa/verify`, asserting **200**:
  - `POST /auth/mfa/setup` (BCrypt) · `POST /auth/mfa/recovery-codes/regenerate` (BCrypt)
  - `POST /profile/security/mfa/enroll/verify` (SHA-256) · `POST /profile/security/recovery-codes/regenerate` (SHA-256)
  Prefer a full `WebApplicationFactory` flow where an existing factory fits; otherwise invoke the
  handlers directly, as the sibling auth tests already do (task 1.2 makes that possible).
- [x] 4.3 `MfaVerify_ShouldRejectReplay_WhenSameRecoveryCodeUsedTwice` — the second redemption of an
  already-used code fails, pinning one-time use across both families.
- [x] 4.4 `MfaVerify_ShouldReturn401NotServerError_WhenStoredDigestIsMalformed` — assert the status is
  401 and that the response body carries no `Invalid salt version` text and no cryptography-library
  message (spec: "A corrupt stored digest yields 401, not 500").
- [x] 4.5 Bookkeeping tests: a failed redemption writes an auth event of type
  `mfa_verification_failure` and records a lockout attempt; repeated failures lock the account per the
  tenant policy; a successful redemption resets the counter.
- [x] 4.6 Correct the misleading comment in
  `tests/Verbara.Platform.Api.Tests/Profile/MfaEnrollEndpointsTests.cs` claiming the test "proves the
  verify→persist branch works end-to-end" — it means TOTP verification, not recovery-code redemption.
  That sentence is where the coverage illusion was created; make it say so.
- [x] 4.7 Replace or annotate the `MfaRecoveryCodes = new[] { "code1", "code2" }` placeholder fixtures
  (`AuthEndpointsTests`, `ChangePasswordMfaStepUpTests`, `MfaPolicyEnforcementTests`) so they cannot
  be mistaken for redemption coverage — they are neither digest family and exist only to make
  `MfaEnabled == true` plausible.

## 5. Documentation

- [x] 5.1 Append an addendum to `docs/decisions/0013-password-hash-algorithm-migration.md` recording
  that the prefix-discriminator pattern now also governs **recovery-code digests** in
  `users.mfa_recovery_codes` (`$2` → BCrypt, otherwise salted SHA-256 via `IRecoveryCodeService`),
  and that the `SaltParseException`-as-non-match guard is a standing requirement on every stored-
  credential verifier. ADRs are append-only — do not rewrite the Decision.
- [x] 5.2 Add a status update to `docs/security/threat-model.md` under asset **A7**: recovery codes
  minted by the wizard paths were unredeemable and returned 500 with a crypto message; both families
  now verify; failed MFA verification is now audited and lockout-counted, closing the
  `docs/security/audit-checklist.md` Scope 3.4 gap for the verification half. Follow the file's
  append-only "Status update" convention rather than editing prior text.
- [x] 5.3 `CHANGELOG.md` `[Unreleased]` — a `### Fixed` entry for the redemption defect (naming the
  500 and which mint paths were affected) and a `### Security` note that failed MFA verification is
  now audited and counts toward lockout, which is a behavioural change operators may notice.

## 6. Verification

- [x] 6.1 `dotnet build Verbara.Platform.slnx` — **zero warnings** (`TreatWarningsAsErrors`,
  `WarningLevel 9999`).
- [x] 6.2 `dotnet test Verbara.Platform.slnx` green for every project the change touches, including
  the existing MFA suites (`MfaServiceTests`, `MfaEnrollEndpointsTests`, `AuthEndpointsTests`,
  `MfaPolicyEnforcementTests`, `ChangePasswordMfaStepUpTests`, `OidcMfaEnforcementTests`) and
  `RecoveryCodeServiceTests`. Note the standing caveat that `Storage.Postgres.Tests` is red on a
  pre-existing Testcontainers startup race unrelated to this change.
- [x] 6.3 **Reproduce the original defect as a regression check**, the way it was confirmed live:
  boot the host against a real Postgres, mint via `POST /profile/security/mfa/enroll/verify`, then
  redeem at `POST /auth/mfa/verify` — assert **200** where `origin/main` returns
  `500 {"detail": "Invalid salt version"}`. Two gotchas from that run, both pre-existing and
  unrelated: drive the host over **`localhost`, not `127.0.0.1`** (subdomain resolution takes `127`
  as the tenant and 403s every authenticated call), and export **`TZ=UTC`** (a
  `new DateTimeOffset(x, TimeSpan.Zero)` on a `Local`-kind value throws on a non-UTC machine).
  **Done.** Fresh `postgres:18-alpine`, host booted in Production, same `flow.py` driver that
  produced the 500 on `origin/main`: `/setup` 201 → login 200 → wizard enroll init/verify/complete
  200/200/204 → login demands MFA → `/auth/mfa/verify` with a **real TOTP** 200 → **`/auth/mfa/verify`
  with a wizard-minted RECOVERY CODE returns 200 + accessToken**, where `origin/main` returned
  `500 {"detail": "Invalid salt version"}`. Also verified live: replaying the consumed code → **401**
  (one-time use holds), a wrong code → **401 not 500**, and `auth_events` contains
  `mfa_verification_failure` rows with `reason = invalid_recovery_code` — the new bookkeeping writing
  end-to-end on a real host. Environment torn down afterwards.
- [x] 6.4 Run the coverage gate locally before pushing — `dotnet test` with
  `--collect:"XPlat Code Coverage" --settings coverlet.runsettings`, merge with `reportgenerator`,
  then `python3 scripts/check-patch-coverage.py coverage/report/Cobertura.xml coverage-floor.json`.
  Both edited source files are in the measured `Verbara.Platform.Api` assembly, so the 85% patch
  floor applies directly — do not assume, measure.
  **Measured:** patch **100.0% (25/25)**; band line **77.64%** inside `[75, 78]`, branch **63.39%**
  ≥ 60, **29310** lines ≥ 27690; exclusion baseline 0 = 0. All three gates pass locally.
- [x] 6.5 `openspec validate --all --strict` green.
- [ ] 6.6 CI green on the PR.
