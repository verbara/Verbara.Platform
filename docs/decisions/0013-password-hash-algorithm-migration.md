# ADR-0013: Password hash algorithm migration (BCrypt → Argon2id)

**Status:** Accepted
**Date:** 2026-04-27
**Context:** AHH Phase 4 (v1.14.0)

## Context

AHH Phase 0 measured BCrypt with `workFactor=12` at **162 ms / verify** on
AMD Ryzen 9 9900X — the dominant per-request cost on `POST /auth/login`,
accounting for ≥99.9 % of the measurable crypto wall time. The R5.5
sustainable throughput knee of 75 req/s recovered exactly under the
single-axis CPU model `162 ms × 75 / 12 cores ≈ saturation`. Phase 4 is
the perf-mover of the AHH train: replace BCrypt with a faster algorithm
without weakening the security posture.

Phase 0 also validated **Argon2id** at the OWASP-2025 floor parameters
(m=19 MiB, t=2, p=1) at **33 ms / verify** — 4.9× faster — and
confirmed the candidate library (`Isopoh.Cryptography.Argon2 2.0.0`)
publishes AOT-clean (zero IL trim/AOT warnings under
`PublishAot=true`). The AOT gate was a hard blocker; with it cleared, the
algorithmic switch is unblocked.

R5.5 capacity-planning baseline (knee 75 req/s, p99 250 ms target) gives
us the success criterion: post-Phase-4 the single-replica knee should
move to **≥ 220 req/s** at p99 ≤ 250 ms — the math: at Argon2id verify
~33 ms × 12 cores ÷ 1.8 (overhead headroom) ≈ 220 req/s. Phase 5
horizontal validation will confirm the projection.

## Decision

**Make Argon2id (m=19 MiB, t=2, p=1) the canonical password hash. Keep
BCrypt as a verify-only legacy path so already-deployed credentials keep
working. Migrate users transparently on next successful login via the
AuthWriteQueue (Phase 2 reuse) — one-shot `~30 ms` synchronous cost on
the migrating login, zero cost thereafter.**

Concrete shape:

1. **`Isopoh.Cryptography.Argon2 2.0.0`** — added as a `PackageReference`
   in `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj`. Locked
   per Phase 0 AOT validation.

2. **`PasswordService` (rewritten,
   `src/Asterisk.Platform.Api/Services/PasswordService.cs`)**:
   - `HashPassword(password)` — always emits Argon2id at the OWASP-2025
     floor parameters. Uses `RandomNumberGenerator` for the 16-byte salt
     and `Argon2Config.EncodeString(...)` for the canonical
     `$argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>` output.
   - `VerifyPassword(password, hash)` — dispatches by hash prefix:
     `$argon2id$…` → `Argon2.Verify`, otherwise BCrypt verify (legacy
     `$2a$/$2b$` hashes). Catches `BCrypt.Net.SaltParseException` to
     return `false` on malformed input rather than leak hash shape via
     exception type.
   - `IsBcryptHash(hash)` — public discriminator (`hash[0..2] == "$2"`).
     Login handler calls this after a successful verify to decide
     whether to enqueue a rehash.
   - `ValidatePolicy(...)` — unchanged from R5.4.

3. **`PasswordRehashCommand`** (added to
   `src/Asterisk.Platform.Api/Services/AuthWriteCommand.cs`):
   - Sealed record `(string TenantId, string UserId, string NewHash)`.
   - `TypeName = "password_rehash"` for meter dimensioning.
   - The new hash is computed synchronously inside the request before
     enqueuing — the queue carries the precomputed Argon2id string, NOT
     plaintext. Plaintext stays in process memory only as long as the
     login handler runs, matching the existing security envelope.

4. **`AuthWriteQueue.ProcessBatchAsync` extension**: the consumer's
   user-coalescing structure (`UserMutation` struct) gains a
   `NewPasswordHash` field. When set, the consumer overwrites
   `user.PasswordHash` before calling `IUserStore.SaveAsync`. Coalesces
   naturally with `UpdateLastLoginAtCommand` and
   `ResetLockoutCountersCommand` — a single user receiving all three
   commands in a batch yields one DB read + one DB write.

5. **`AuthEndpoints.Login`** (the single call site that drives the
   migration): immediately after `PasswordService.VerifyPassword`
   returns true, if `IsBcryptHash(user.PasswordHash)` AND the
   `AuthWriteQueue` is registered, compute the new Argon2id hash and
   enqueue a `PasswordRehashCommand`. Both conditions are required —
   tests + single-process bootstraps that don't register the queue
   (which is the AHH Phase 2 ship surface) skip the migration silently
   and the user continues to log in via the legacy BCrypt path until
   the queue is wired.

## Why synchronous rehash + queued persistence

Considered alternatives:

- **Plaintext-on-queue + hash in consumer.** Rejected: the queue is
  in-process and short-lived (250 ms flush interval), but adding even a
  brief plaintext residence on a non-request-scoped object widens the
  exposure beyond what the existing login handler does. The marginal
  ~30 ms request latency on the rehash login is a one-time, per-user
  cost; users perceive it as a normal-feeling login.
- **Background "rehash everyone" cron.** Rejected: requires storing
  plaintext on disk or running with elevated key access. Both worse than
  the on-login model. Inactive users stay on BCrypt indefinitely until
  they log in — acceptable; an account that never logs in is not
  authenticating, so its hash strength is irrelevant.
- **Rehash on every successful login (no detection).** Rejected:
  computing a fresh Argon2id hash for already-Argon2id users is wasted
  work (~30 ms × every login). The `IsBcryptHash` check costs ~20 ns;
  trivially cheap.

## Why the queue MUST NOT drop `PasswordRehashCommand`

The AuthWriteQueue uses `BoundedChannelFullMode.Wait` with producer-side
drop counting (ADR-0011). Under sustained > 10× knee load, some commands
are dropped. For `UpdateLastLoginAtCommand` and
`LogSuccessEventCommand` the drop is acceptable (timestamp / log gap).
**For `PasswordRehashCommand`, a drop leaves the user permanently on
BCrypt until their next login.**

The migration converges geometrically: each successful login is another
chance to enqueue the rehash. A drop today means another attempt
tomorrow. The aggregate bound is "100 % of pre-deploy BCrypt hashes
migrated within ~3 logins per user" — which holds even at 25 % drop
rate (typical web-cohort first-week login frequency × 3 = nearly all
active users).

For dormant users, the BCrypt hash persists. Acceptable — they're not
authenticating. When they do come back, login still verifies correctly
(the BCrypt path is preserved).

## OWASP-2025 floor parameters

Argon2id at m=19 MiB, t=2, p=1, hashLength=32 bytes. Source:
[OWASP Password Storage Cheat Sheet — Argon2id Configuration](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#argon2id).

Phase 0 measured the per-verify cost at this parameter set: 33 ms mean,
0.35 ms σ. The bench artifact lives at
[`docs/research/2026-04-27-auth-hotpath-baseline.md`](../research/2026-04-27-auth-hotpath-baseline.md)
§2 and is reproducible via
`scripts/profiling/run-benchmarks.sh`.

## Memory pressure footprint

Argon2id is memory-hard by design. Each verify allocates a ~19 MiB
working buffer that gets collected after the verify completes:

- At the post-Phase-4 sustained rate (~220 req/s) the ephemeral
  allocation rate is ~4 GB/sec — comfortably handled by .NET 10's
  Server GC + concurrent workstation GC on a 60 GB host.
- **Production must use Server GC**:
  `<ServerGarbageCollection>true</ServerGarbageCollection>` in
  `Asterisk.Platform.Api.csproj`. Verify before Phase 4 ship.
- **Production alert**: if `dotnet_collection_count_total{generation="2"}`
  climbs > 0.5 / sec under sustained load, retune Argon2id parameters
  (drop memory cost to 12 MiB, raise time cost to 3) — not a security
  regression at OWASP's "memory-or-time" tradeoff axis.
- **Container RAM headroom**: reserve at least
  `2 × max_concurrent_logins × 19 MiB` per replica. At the 220 req/s
  knee with verify wall ≈ 33 ms, in-flight ≈ 7 verifies × 19 MiB ≈
  130 MB. Negligible vs 60 GB host; noteworthy for 4 GB containers.

These items are tracked in
[`docs/operations/capacity-planning.md`](../operations/capacity-planning.md)
under the "Argon2id memory budget" subsection (added in Phase 5).

## Tested invariants

`PasswordServiceTests` (5 new):
- `HashPassword_ShouldEmitArgon2id_AfterPhase4Migration`
- `VerifyPassword_ShouldReturnTrue_WhenLegacyBcryptHashMatches`
- `VerifyPassword_ShouldReturnFalse_WhenLegacyBcryptHashDoesNotMatch`
- `IsBcryptHash_ShouldReturnTrue_ForLegacyBcryptHash`
- `IsBcryptHash_ShouldReturnFalse_ForArgon2idHash`
- `VerifyPassword_ShouldReturnFalse_WhenBcryptHashIsMalformed` (defensive)

`AuthWriteQueueTests` (2 new):
- `Consumer_ShouldUpdatePasswordHash_WhenPasswordRehashCommandEnqueued`
- `Consumer_ShouldCoalesceRehashAndLastLogin_WhenSameUserHasBoth`

Existing pre-Phase-4 tests pass unchanged: 853 / 853 Api.Tests PASS.

## Considered alternatives

- **PBKDF2** — ASP.NET Core Identity default. Rejected: not
  memory-hard, defeats the GPU-resistance design goal Argon2id
  satisfies.
- **scrypt** — also memory-hard. Rejected: less battle-tested in .NET
  AOT than Argon2id; Isopoh.Cryptography.Argon2 already validated.
- **Lower BCrypt cost factor (12 → 10)** — rejected in Phase 0 / ADR
  baseline. Trades security for perf without architectural improvement.
- **Pre-rehash all users at deploy time** — requires plaintext access;
  not available. The on-login transparent rehash is the only non-
  security-regressing path.
- **Per-tenant algorithm choice** — out of scope; deferred to
  v1.16+ if a tenant requests it.

## Forward compatibility

- **Future algorithm rotation** (e.g. Argon2id → next-gen winner):
  the prefix-discriminator pattern in `VerifyPassword` extends naturally.
  Add a new branch + new `IsXxxHash` predicate; the on-login rehash
  loop detects + migrates.
- **OWASP parameter drift** (e.g. m=19 → m=64 by 2030): `HashPassword`
  emits encoded params in the hash string itself; older hashes still
  verify with their original params. New hashes use updated params.
  No data migration needed.
- **JWKS-style password-hash exchange**: not a concept; passwords stay
  server-side. No interop pressure here.

## Related ADRs

- ADR-0011 — Auth write-path deferral (the AuthWriteQueue this phase
  reuses).
- ADR-0012 — JWT rotation pool wire-up (Phase 3 sister; both ride v1.14.0).
- Phase 0 baseline doc — empirical justification for Argon2id parameters
  and the AOT gate.

---

## Addendum 2026-07-29 — the prefix discriminator also governs recovery-code digests

**Source change:** `fix-recovery-code-redemption` (OpenSpec, tier MEDIANO, `decision_ref`
Platform/ADR-0013).

Append-only addendum. The **Decision** above is unchanged: Argon2id remains the canonical password
hash, BCrypt remains verify-only for passwords, and the on-login rehash is untouched. This records a
**second column** that the same pattern now governs, and — because the two cases are not symmetric —
the one part of the password decision that deliberately does **not** carry over.

The *"Forward compatibility"* section above pre-authorises this: *"the prefix-discriminator pattern
in `VerifyPassword` extends naturally. Add a new branch + new `IsXxxHash` predicate."* This is that
case, applied to a different column.

### The second column

`users.mfa_recovery_codes` carries **two digest families**, written by two sets of mint paths:

| Family | Written by | Shape |
|---|---|---|
| BCrypt cost-10 | `MfaService.HashRecoveryCodes` — the legacy MFA endpoints (`POST /auth/mfa/setup`, `POST /auth/mfa/recovery-codes/regenerate`) | `$2a$…`, 60 chars |
| Salted SHA-256 | `IRecoveryCodeService.Hash(code, salt)` — the profile enrollment wizard (`POST /profile/security/mfa/enroll/verify`, `POST /profile/security/recovery-codes/regenerate`) | 64 uppercase hex chars |

There is exactly one redemption path — `AuthEndpoints.MfaVerify` → `MfaService.ValidateRecoveryCode`
— and until this change it called `BCrypt.Verify` unconditionally. A SHA-256 element therefore threw
rather than failing to match.

### The dispatch table

`MfaService.ValidateRecoveryCode` now classifies **each stored element on its own shape**, exactly as
`PasswordService.VerifyPassword` classifies a password hash:

| Stored element | Verifier |
|---|---|
| starts with `$2` (the `PasswordService.IsBcryptHash` discriminator — `$2a$` and `$2b$` alike) | `BCrypt.Net.BCrypt.Verify` |
| anything else | `IRecoveryCodeService.Verify(code, salt, storedHash)`, salt = `user.UserId.Value` |

Two details are load-bearing:

- **Per element, never once per array.** A mixed array — reachable through a partially-applied
  migration or a manual database edit — verifies every element on its own terms instead of failing
  at the first one of the unexpected family.
- **The SHA-256 branch delegates; it does not reimplement.** `IRecoveryCodeService.Verify` already
  normalises the input the same way `Hash` does and compares with
  `CryptographicOperations.FixedTimeEquals`. It had zero callers in `src/` before this change — a
  dead seam beside a live defect. Wiring it is both the fix and the removal of the shape that caused
  the defect. The salt is a parameter because `Hash` salts with the user's id, which is not
  recoverable from the digest.

### The `SaltParseException` guard is a standing requirement, not a local nicety

`PasswordService.VerifyPassword` has carried
`catch (BCrypt.Net.SaltParseException) { return false; }` since this ADR, with the stated rationale
of not leaking hash shape through the exception type. `MfaService.ValidateRecoveryCode` lacked it,
and that omission is what turned a format mismatch into an **HTTP 500** carrying the crypto library's
raw `"Invalid salt version"` message in `ProblemDetails.Detail` — `SaltParseException` derives
directly from `Exception`, so `ErrorHandlingMiddleware` maps it through its fallthrough arm.

**Therefore, as a standing requirement of this ADR: every verifier that reads stored credential
material MUST treat a malformed or unrecognised stored value as a non-match, never as an exception
and never as a match.** The guard is not a defensive flourish attached to the password path — it is
the property that makes a credential verifier structurally incapable of 500-ing on the contents of
its own column.

**Catching `SaltParseException` alone does NOT satisfy that requirement**, which the implementation
of `fix-recovery-code-redemption` established by measurement rather than assumption. BCrypt.Net-Next
raises `SaltParseException` only when the stored value does not begin with `$`. A digest corrupt
*inside* the `$2` family raises something else entirely:

| Stored value | BCrypt.Net-Next raises | Mapped by `ErrorHandlingMiddleware` to |
|---|---|---|
| `code1`, a 64-char hex digest | `SaltParseException` | 500 (fallthrough arm) |
| `$2a$10$truncated` | `ArgumentOutOfRangeException` | 400 |
| `$2a$xx$…` (non-numeric cost) | `FormatException` | **500** |
| `$2`, `$2a$` | `IndexOutOfRangeException` | **500** |

Both credential verifiers therefore funnel every BCrypt comparison through a single guarded helper,
`Verbara.Platform.Api.Services.BcryptVerifyGuard.SafeVerify`, whose exception filter covers
`SaltParseException`, `ArgumentException` (and so `ArgumentOutOfRangeException`), `FormatException`
and `IndexOutOfRangeException` — and deliberately nothing wider, so an
`OperationCanceledException` or an `OutOfMemoryException` still propagates. The filter is pinned by
`MfaServiceTests.ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsCorruptInsideTheBcryptFamily`
and `PasswordServiceTests.VerifyPassword_ShouldReturnFalse_WhenStoredHashIsCorruptInsideTheBcryptFamily`,
both of which exercise the real library rather than a stub. Any future verifier must route through
that helper rather than write its own `try`/`catch`.

### What does NOT carry over: there is no rehash-on-verify here

The password migration converges — every successful login rehashes a BCrypt user to Argon2id, so the
legacy family drains over time and the ADR's job is to keep it verifiable *until then*. **The
recovery-code case has no such convergence and is not meant to.** Both families are kept verifiable
**indefinitely**:

- Unifying them means re-hashing every element of the array, and a recovery code is only ever
  presented in plaintext **at the moment it is redeemed and consumed**. The other nine elements are
  only ever known as digests, so they cannot be converted — the only honest "migration" is to mint a
  fresh set, which **invalidates every outstanding code**, including the ones users have already
  filed in a password manager or printed.
- The digest a user holds is the credential. Rewriting the column is therefore a user-visible
  revocation, not a transparent upgrade. That is the whole difference from a password hash, which the
  user re-supplies on every login.

A future reader should not assume symmetry with the password migration: there is no `IsXxxHash`-then-
rehash loop on this path, no write-back on successful verification, and no drain. A recovery-code
family retires only when a deliberate change chooses to invalidate outstanding codes — and that is a
decision of its own, not a consequence of this one.

This addendum also does **not** claim the salted-SHA-256 form is strong. It is a single round over a
small keyspace and deserves a stretched KDF; `fix-recovery-code-redemption` neither unifies the
families nor strengthens that digest. It makes both readable. The at-rest exposure of the column is
addressed separately by `encrypt-mfa-secrets-at-rest` (see the protected-column register in
ADR-0003), which is byte-for-byte transparent to the dispatch described here.

### Out of this addendum's scope

The same change gave `POST /auth/mfa/verify` the lockout and audit bookkeeping it never had on the
failure path (`AuthEventTypes.MfaVerificationFailure`). That is a correction to the endpoint's
audit posture, not to the hash-dispatch decision recorded here; it is documented in
`docs/security/threat-model.md` (A7 status update, 2026-07-29) and in `CHANGELOG.md`.
