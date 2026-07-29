## Context

`POST /api/v1/auth/mfa/verify` (`AuthEndpoints.MfaVerify`) is the **only** redemption surface for MFA
recovery codes in the entire product. It is `AllowAnonymous` — the caller presents a one-shot
`mfaToken` challenge minted by `/auth/login`, not a bearer token.

The handler's recovery-code branch is:

```csharp
var (isValid, index) = MfaService.ValidateRecoveryCode(body.RecoveryCode, user.MfaRecoveryCodes);
```

and `MfaService.ValidateRecoveryCode` is a bare loop with no guard:

```csharp
for (var i = 0; i < hashedCodes.Count; i++)
    if (BCrypt.Net.BCrypt.Verify(code, hashedCodes[i]))   // throws on a non-"$2…" stored value
        return (true, i);
return (false, -1);
```

**Two digest families are written into that column.** `MfaService.HashRecoveryCodes` produces BCrypt
cost-10 (`$2a$…`, 60 chars) for the legacy paths; `IRecoveryCodeService.Hash(code, salt)` produces
`Convert.ToHexString(SHA256("{salt}:{TRIM+UPPER(code)}"))` — 64 uppercase hex chars, salt =
`user.UserId.Value` — for the wizard paths. `BCrypt.Verify` on the hex form fails its
`hash[0] == '$' && hash[1] == '2'` precondition and throws `SaltParseException`, which derives
**directly from `Exception`**, so `ErrorHandlingMiddleware`'s type switch drops it into the `_` arm:
HTTP 500, `ProblemDetails.Detail` = the raw `"Invalid salt version"`.

`IRecoveryCodeService.Verify` — the matching salted-SHA-256 verifier, complete with
`CryptographicOperations.FixedTimeEquals` and 16 unit tests — has **zero callers in `src/`**. The
seam was never wired; only `Generate` and `Hash` are used.

**The repo has already solved this exact class of problem.** Platform/ADR-0013 (password-hash
migration, Accepted 2026-04-27) faced two hash families in one column and chose a prefix
discriminator, with the legacy branch guarded:

```csharp
if (hash.StartsWith(Argon2idPrefix, StringComparison.Ordinal))
    return Argon2.Verify(hash, password);
try { return BCrypt.Net.BCrypt.Verify(password, hash); }
catch (BCrypt.Net.SaltParseException) { return false; }
```

ADR-0013's "Forward compatibility" section states the pattern *"extends naturally. Add a new branch +
new `IsXxxHash` predicate."* This change is the case that sentence anticipated. Notably, the
`SaltParseException` guard the password path has carried for three months is precisely what
`ValidateRecoveryCode` lacks.

**Constraints.** Native AOT (Platform/ADR-0022): no reflection, source-generated logging,
`[JsonSerializable]` for every DTO. `TreatWarningsAsErrors` with `WarningLevel 9999`. The CI patch
coverage floor is 85% and both edited files live in the coverage-measured `Verbara.Platform.Api`
assembly.

**Stakeholders.** Any user who loses their authenticator; tenant security owners; compliance —
`docs/security/audit-checklist.md` Scope 3.4 requires MFA verification to be audited, and today the
handler emits nothing on failure.

## Goals / Non-Goals

**Goals:**
- Make a recovery code from any of the four mint paths redeemable, without rewriting or invalidating
  a single stored value.
- Make `POST /auth/mfa/verify` incapable of returning 500 for any content of the column, and stop it
  leaking a cryptography library's message through `ProblemDetails.Detail`.
- Wire `IRecoveryCodeService.Verify` into production so the dead seam that caused this stops being
  dead.
- Give the handler the lockout and audit bookkeeping it has never had on the failure path.
- Add the cross-seam tests whose absence let this ship, so a single-family regression fails loudly.

**Non-Goals:**
- Unifying the two digest families or upgrading the SHA-256 form to a stretched KDF. That
  invalidates every outstanding code and is its own change; `encrypt-mfa-secrets-at-rest` already
  mitigates the at-rest exposure by wrapping the column.
- Changing the one-shot semantics of the `mfaToken` challenge (see D8).
- Fixing the frontend's "render any non-429/400 as *invalid code*" behaviour (see D8).
- Wiring the enrollment wizard into frontend navigation — which MUST NOT happen before this ships.
- `PREPUB-2026-05-09-MFA-002` (no step-up on MFA re-enrollment), open and unrelated.

## Decisions

**D1 — Per-element prefix dispatch, mirroring `PasswordService.VerifyPassword`.**
Each stored element is classified by its own shape: `$2` prefix → BCrypt; anything else → salted
SHA-256. *Alternatives considered:* (a) **try-both** — run BCrypt, and on failure try SHA-256:
rejected, it doubles the crypto work on every element of every attempt and turns a malformed value
into two failures instead of a classified one; (b) **a format-marker column** — rejected, it needs a
schema migration and can desynchronise from the actual bytes, the same argument that rejected a
marker in `encrypt-mfa-secrets-at-rest` D4; (c) **decide once for the whole array** — rejected,
because a mixed array (possible via a manual DB edit or a partially-applied migration) would fail on
its first element. Per-element costs nothing and is strictly safer.

Direction matters: the **new** branch gets the explicit test and BCrypt stays the fallback, exactly
as ADR-0013 arranged it. Here the natural discriminator is the BCrypt `$2` prefix, since the
SHA-256 form has no prefix at all — so the test is `StartsWith("$2")` → BCrypt, `else` → SHA-256.

**D2 — The SHA-256 branch delegates to `IRecoveryCodeService.Verify`; it does not reimplement it.**
The method exists, is unit-tested, normalises the input (`Trim().ToUpperInvariant()`) exactly as
`Hash` does, and compares with `CryptographicOperations.FixedTimeEquals`. *Alternative:* inline the
digest next to the BCrypt branch. *Rejected:* it would duplicate a constant-time comparison on an
authentication path and leave the original dead seam in place — the very shape that produced this
defect. Wiring the interface is both the fix and the root-cause removal.

**D3 — Thread the salt explicitly; do not derive it.**
`Hash` salts with `user.UserId.Value`, so verification needs it. The handler passes
`user.UserId.Value`; the signature becomes
`ValidateRecoveryCode(IRecoveryCodeService recoveryCodes, string code, IReadOnlyList<string> hashedCodes, string salt)`.
*Alternative:* store the salt alongside the digest. *Rejected:* it changes the persisted format, which
is exactly what this change promises not to do — and `IRecoveryCodeService`'s doc comment already
fixes the per-user-id salt as the contract.

**D4 — `MfaService` stays static; the service arrives as a parameter.**
`MfaService` is a static utility (`Program.cs` has a comment stating it needs no DI registration) and
every other call site is static. Passing `IRecoveryCodeService` as the first parameter keeps that
shape, keeps the method trivially unit-testable with a real `RecoveryCodeService` instance, and
avoids converting a static utility into a DI'd service across unrelated call sites.
*Alternative:* make `MfaService` an injected instance. *Rejected:* wide, unrelated churn for one method.

**D5 — Catch `SaltParseException` in the BCrypt branch, return "no match".**
Verbatim the ADR-0013 posture, whose stated rationale is to avoid leaking hash shape through the
exception type. This makes the endpoint structurally incapable of 500-ing on stored material: every
branch either positively verifies or returns no-match. Note this guard alone would have converted the
production 500 into a 401 — but a 401 for a code the user typed correctly is still a lockout, which
is why D1–D3 are the actual fix and D5 is the safety net.

**D6 — Failure bookkeeping reuses the services already in the handler's signature.**
`MfaVerify` already receives `AccountLockoutService lockoutService` and `AuthEventService authEvents`
— it passes them to `IssueTokensAsync` on success and ignores them on failure. The failure path gains
`lockoutService.RecordFailedAttemptAsync(user, ip, ua, ct)` and
`authEvents.LogAsync(..., AuthEventTypes.MfaVerificationFailure, ...)`, mirroring `Login`'s
invalid-password branch line for line, with `ip`/`ua` from the existing `GetIpAddress` /
`GetUserAgent` helpers. `AuthEventTypes` gains one new constant —
`MfaVerificationFailure = "mfa_verification_failure"` — since the closest existing values
(`LoginFailure`, `MfaEnroll`) would both misreport what happened. **Verify at apply time** whether
any check constraint or consumer enumerates auth-event types before adding it.

**D7 — Elevate `MfaVerify` from `private` to `internal` for testability.**
It is currently `private static`, so `Api.Tests` cannot invoke it even with `InternalsVisibleTo`.
`Login`, `Refresh`, `MfaConfirm`, `MfaDisable` and `RegenerateRecoveryCodes` are all already
`internal` with comments recording that the visibility was elevated *specifically* for tests. This
follows that established precedent rather than inventing a new test seam. Prefer full HTTP tests
through an existing `WebApplicationFactory` where one fits; fall back to direct handler invocation as
those siblings do.

**D8 — Deliberately NOT changing challenge-token consumption or the frontend.**
`mfaCache.TakeAsync` destroys the one-shot token 18 lines before verification, so today every 500
costs the user a full re-login. That is real, but once this change lands a failed redemption is an
ordinary 401 — the same cost as mistyping a TOTP code — and altering one-shot replay semantics is a
security decision that deserves its own analysis. Likewise the web client renders any non-429/non-400
as "invalid code"; correct to fix, but this change removes the 500 entirely, so the misleading branch
becomes unreachable in practice. Both are recorded rather than silently absorbed.

**D9 — No data migration, in either direction.**
Nothing on disk is read, rewritten, or re-hashed. The fix is entirely in the reader. That is what
makes rollback safe (see Migration Plan) and what keeps codes already sitting in users' password
managers valid.

## Risks / Trade-offs

- **[The edit is on an anonymous authentication endpoint; a defect here is a second-factor bypass]**
  → Strictly worse than the lockout being fixed, so the structure is fail-closed by construction:
  every branch returns no-match unless it positively verifies, and the `catch` converts an exception
  into no-match, never into success. The constant-time comparison is not re-written — it is reused
  from the already-tested `RecoveryCodeService`. The cross-seam tests assert both the positive and
  the negative case per family.
- **[Lockout on MFA verification goes from "not enforced" to "enforced"]** → A user who repeatedly
  fumbles a recovery code can now lock the account, which operators may notice as new behaviour.
  This is the intended correction — the endpoint was an unthrottled, unaudited guessing surface — but
  it is a behavioural change and belongs in the CHANGELOG rather than buried.
- **[A new auth-event type could hit an unknown consumer or constraint]** → D6 flags an explicit
  apply-time check of any enumeration or check constraint over auth-event types before the constant
  lands.
- **[Adding the salt parameter changes a public-ish signature]** → `MfaService` is `internal` to the
  Api assembly; only `AuthEndpoints` and `MfaServiceTests` call `ValidateRecoveryCode`. Both are
  updated in this change; the compiler enforces there are no others.
- **[Patch-coverage floor]** → Unlike `encrypt-mfa-secrets-at-rest`, every edited file here is in the
  coverage-measured `Verbara.Platform.Api` assembly and every new line is directly exercised by the
  new tests, so the 85% patch floor should be comfortable. Verify locally with
  `scripts/check-patch-coverage.py` before pushing rather than assuming.
- **[Textual conflict with PR #212]** → `encrypt-mfa-secrets-at-rest` touches the same test project
  and the same column's story in the threat model and CHANGELOG. Rebase this change onto it. There is
  no semantic dependency in either direction: the wrap is byte-for-byte transparent, proven by
  `Get_ShouldPreserveBothHashFormats_WhenCodesMixBcryptAndSha256Hex` and by both hosts behaving
  identically in the live run.

## Migration Plan

1. **Ship reader-only.** No schema change, no data rewrite, no format migration. Deploying is a
   binary swap.
2. **Deploy.** Both digest families are verifiable from the first request. Users holding wizard-minted
   codes can redeem immediately; users holding legacy codes see no change.
3. **Verify.** Reproduce the original failure as a regression check: mint through
   `POST /profile/security/mfa/enroll/verify`, then redeem at `POST /auth/mfa/verify` and assert 200
   where `origin/main` returns 500 `Invalid salt version`. Confirm a wrong code yields 401 with no
   crypto message in the body, and that the failure now appears in the auth-event log.
4. **Rollback.** Reverting the binary restores the previous behaviour exactly — including the defect.
   Nothing on disk changed, so there is nothing to undo and no risk of a one-way door. The only
   consequence of a rollback is that wizard-minted codes 500 again.
5. **Follow-up gate.** Do not wire the enrollment wizard into frontend navigation until this has
   shipped.

## Open Questions

- **Should the SHA-256 form be retired in favour of a single stretched-KDF family?** A single round
  over a ~40-bit keyspace is weak on its own; `encrypt-mfa-secrets-at-rest` mitigates it at rest by
  wrapping the column, which is why this is not urgent. Retiring it invalidates outstanding codes
  unless paired with rehash-on-successful-redeem — the ADR-0013 move, which is available here because
  a redeemed code arrives in plaintext. Deferred deliberately: it is a distinct decision and this
  change is a defect fix.
- **Should `POST /auth/mfa/verify` carry its own rate-limit policy?** It has none today beyond the
  global bucket, and it is anonymous. The lockout added in D6 substantially covers the guessing case,
  but a dedicated policy may still be warranted. Not decided here.
