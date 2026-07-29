---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Any user locked out of their second factor; tenant security owners; compliance (audit Scope 3.4)
decision_ref: Platform/ADR-0013
---

## Why

**MFA recovery codes minted by the enrollment wizard can never be redeemed. The endpoint returns
HTTP 500 and destroys the user's one-shot MFA challenge token on the way out.** Recovery codes exist
for exactly one situation — the user lost their authenticator — so the failure mode is a permanent
account lockout with no self-service path, and the only remedy is an admin MFA reset.

This was confirmed live, not inferred: `POST /api/v1/auth/mfa/verify` with a wizard-minted code
returns `500 {"detail": "Invalid salt version"}`, **reproduced identically on unmodified
`origin/main` (`afd61e4f`) against a clean database with no encryption anywhere**. It was discovered
while scoping `encrypt-mfa-secrets-at-rest`, deliberately left out of that change's scope, and
confirmed by its task 6.4.

**The mechanism.** The column carries two hash families and the reader only understands one:

| Mint path | Route | Stored digest |
|---|---|---|
| M1 legacy setup | `POST /auth/mfa/setup` | BCrypt cost-10 (`$2a$…`) |
| M2 legacy regenerate | `POST /auth/mfa/recovery-codes/regenerate` | BCrypt cost-10 |
| M3 wizard enroll | `POST /profile/security/mfa/enroll/verify` | **salted SHA-256, 64 hex chars** |
| M4 wizard regenerate | `POST /profile/security/recovery-codes/regenerate` | **salted SHA-256, 64 hex chars** |

There is exactly **one** redemption path — `AuthEndpoints.MfaVerify` →
`MfaService.ValidateRecoveryCode` → `BCrypt.Verify`, in a bare loop with no `try`/`catch`. Feeding
it a 64-char hex digest makes BCrypt.Net throw `SaltParseException` on the first element, which
`ErrorHandlingMiddleware` maps through its `_` fallthrough arm to a 500 whose `Detail` is the raw
crypto message. `RecoveryCodeService.Verify` — the matching salted-SHA-256 verifier, fully
implemented and unit-tested — **has zero callers in `src/`**. It is dead code; the seam was never
wired.

**Four things make this worse than a wrong-answer bug:**

1. **The challenge token is burned before the throw.** `MfaVerify` calls `mfaCache.TakeAsync` — an
   atomic, destructive, one-shot read — 18 lines *before* the failing verify. Every attempt costs
   the user a fresh login round-trip, and the token cannot be replayed.
2. **The user is told the wrong thing.** The web client special-cases only 429 and
   400-with-`expired`; a 500 falls through to the generic "invalid code" message. The user believes
   they mistyped, retries, and burns another token.
3. **Regenerating silently breaks working codes.** A user with functioning BCrypt codes who calls
   M4 converts the array to the unredeemable format and receives a success toast.
4. **The failure path writes nothing.** `MfaVerify` performs no lockout bookkeeping and emits no
   auth event on *any* failure — not for this crash, not for a plain wrong code. `Login` does both.
   So second-factor guessing against this endpoint is unaudited and unthrottled beyond the global
   rate-limit bucket. That contradicts `docs/security/audit-checklist.md` Scope 3.4, which requires
   MFA verification to be audited, and it is why this change is not a one-line fix.

**Blast radius today.** The shipped web UI wires the *legacy* endpoints, so a user enrolling through
it gets BCrypt codes that redeem correctly. The wizard routes are nonetheless live, guarded,
documented, and present in the generated OpenAPI surface — reachable by direct URL and by any
non-web API client. The exposure is therefore latent and grows on its own: **the moment the wizard
is linked from navigation, every new enrollee's recovery codes are unredeemable.** Fixing the reader
now is strictly cheaper than discovering this after a frontend release.

**Why now.** `encrypt-mfa-secrets-at-rest` just touched this column and proved the defect live; the
verifier is the one piece of that story left unresolved, and the fix is a known pattern this repo
has already applied once.

## What Changes

- **Format-agnostic recovery-code verification.** `MfaService.ValidateRecoveryCode` dispatches
  **per element** on the stored digest's shape, mirroring `PasswordService.VerifyPassword`
  (Platform/ADR-0013): a `$2`-prefixed element goes to BCrypt, anything else to the salted-SHA-256
  verifier. ADR-0013 §"Forward compatibility" pre-authorises exactly this extension — *"the
  prefix-discriminator pattern extends naturally. Add a new branch + new `IsXxxHash` predicate."*
- **The SHA-256 branch calls `IRecoveryCodeService.Verify`, not a copy of it.** That method already
  exists, is unit-tested, and uses `CryptographicOperations.FixedTimeEquals`. Wiring it removes the
  dead-code seam that caused this defect instead of duplicating logic beside it.
- **The salt is threaded through.** `RecoveryCodeService.Hash` salts with `user.UserId.Value`, so
  the verify signature gains the user id. `MfaService` becomes injectable-or-passed rather than
  purely static where required by the seam.
- **No stored-material exception ever escapes.** The BCrypt branch catches
  `BCrypt.Net.SaltParseException` and returns "no match", exactly as
  `PasswordService.VerifyPassword` has done since ADR-0013. A malformed or unrecognised stored
  digest is a failed verification, never a 500 — and never leaks the crypto library's message
  through `ProblemDetails.Detail`.
- **`/auth/mfa/verify` gains the failure bookkeeping it never had:** a failed attempt records a
  lockout attempt and emits an auth event, matching `Login`. This closes the audit-checklist
  Scope 3.4 gap the investigation surfaced.
- **BREAKING for nobody.** No stored value is rewritten, no code is invalidated, no format is
  migrated. Both families keep working; codes already in users' password managers keep working.
- **Regression tests that cross the seam.** The existing suites are green *because* each verifies
  the family it minted — `MfaServiceTests` hashes with BCrypt then verifies with BCrypt;
  `RecoveryCodeServiceTests` does the same for SHA-256; the one HTTP test that mints via the wizard
  asserts the returned codes' shape and never redeems them. New tests mint through each real
  endpoint and redeem through the real endpoint.

## Capabilities

### New Capabilities
- `mfa-recovery-code-redemption`: a recovery code minted by ANY mint path is redeemable at
  `POST /auth/mfa/verify`; verification dispatches per stored-digest format, never throws on stored
  material, and every failed attempt is audited and counted toward lockout.

### Modified Capabilities
<!-- None. No existing living spec covers MFA or recovery codes — `openspec/specs/` has 18
     capabilities and none is MFA-related (verified). This change creates the first one. -->

## Impact

- **`src/Verbara.Platform.Api/Services/MfaService.cs`** — `ValidateRecoveryCode` gains per-element
  format dispatch, a salt parameter, and the `SaltParseException` guard.
- **`src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs`** — `MfaVerify` passes the user id,
  resolves `IRecoveryCodeService`, and gains lockout + auth-event writes on the failure path.
- **`src/Verbara.Platform.Identity/Mfa/RecoveryCodeService.cs`** — no behaviour change; its `Verify`
  stops being dead code.
- **Tests** — new cross-seam coverage in `Verbara.Platform.Api.Tests`; the misleading
  "proves the verify→persist branch works end-to-end" comment in `MfaEnrollEndpointsTests` corrected
  (it means TOTP verification, not redemption).
- **Docs** — an addendum to `Platform/ADR-0013` recording that the prefix-discriminator pattern now
  also governs recovery-code digests; a threat-model A7 status update; `CHANGELOG.md`.
- **No schema change, no DTO change, no new endpoint.** `MfaVerifyRequest` is unchanged.
- **Cross-repo: none.** No `Verbara.Sdk` / `Verbara.Sdk.Pro` change and no pin movement.
  `Verbara.Platform.Web` needs no change to be *fixed* — but see Out of Scope on its error handling.
- **Interaction with `encrypt-mfa-secrets-at-rest` (PR #212, in flight):** none in substance. That
  change wraps the column and is byte-for-byte transparent, proven by
  `Get_ShouldPreserveBothHashFormats_WhenCodesMixBcryptAndSha256Hex` and by the identical live
  behaviour of both hosts. This change should be **rebased onto it** to avoid a textual conflict in
  the same tests, but neither depends on the other semantically.

### Out of Scope (explicit)

- **Migrating the two hash families to one.** The SHA-256 digest is a single round over a ~40-bit
  keyspace and deserves a stretched KDF, but changing it invalidates every outstanding code.
  Separate change; `encrypt-mfa-secrets-at-rest` already mitigates the at-rest exposure by wrapping
  the column.
- **The challenge-token consumption ordering.** `TakeAsync` burning the token before verification is
  a real usability wart, but once the 500 is gone a failed redemption is an ordinary 401 and the
  cost drops to "log in again", the same as a wrong TOTP code. Changing one-shot semantics is a
  security decision of its own.
- **The frontend's 500-renders-as-"invalid code" behaviour.** Correct to fix, but this change makes
  the endpoint stop returning 500 at all, so the misleading branch becomes unreachable in practice.
- **`PREPUB-2026-05-09-MFA-002`** (no step-up when re-enrolling MFA) — open, adjacent, unrelated.
- **Wiring the wizard into the frontend navigation.** Out of scope, and deliberately so: it MUST NOT
  happen before this fix ships, which is itself a reason to ship this now.
