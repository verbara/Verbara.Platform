---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Tenant security owners; any user who mistypes a second factor
decision_ref: Platform/ADR-0031
---

## Why

`POST /api/v1/auth/mfa/verify` is **anonymous** — it authenticates on a one-shot `mfaToken`
challenge, not a bearer token — and it is the sole gate between a valid password and a session.
Three things about it are still unresolved after `fix-recovery-code-redemption` (#215), each
recorded there as an Open Question or an explicit Out of Scope rather than silently dropped.

**1 — It has no rate-limit policy of its own.** No `RequireRateLimiting` at the group or endpoint
level; only the global bucket applies. #215 added lockout bookkeeping, which substantially covers
sustained guessing against a *single* account, but lockout is per-user and per-tenant-policy — it
does not bound request volume against the endpoint itself. Platform/ADR-0031 already established
that per-tenant partitioning is load-bearing here, and this endpoint sits outside it.

**2 — The challenge token is consumed before verification.** `mfaCache.TakeAsync` is atomic,
destructive and one-shot, and runs 18 lines before the factor is checked. Every failed attempt —
including an honest typo — costs the user a full re-login to mint a fresh challenge. Before #215
this was severe, because the failure was a 500; now it is an ordinary 401, so the cost dropped to
"log in again". It is still a papercut on the unhappy path, and one-shot replay semantics is a
security decision that deserves deciding rather than inheriting.

**3 — The web client reports any unexpected status as "invalid code".** `mfa-verify.tsx`
special-cases 429 and 400-with-`expired`; everything else falls to the generic
`auth.mfa_invalid_code` message. #215 removed the 500 that made this actively misleading for
recovery codes, so the branch is no longer reachable *for that case* — but a genuine server error
still tells the user they mistyped, while their challenge token is already destroyed. The bug
outlived its most visible symptom.

## What Changes

- **A dedicated rate-limit policy on `/auth/mfa/verify`**, partitioned per tenant consistently with
  Platform/ADR-0031 and the `TenantResolutionMiddleware`-before-`UseRateLimiter()` order invariant.
  Size it against the legitimate worst case — a user fumbling a TOTP code across clock skew — not
  against the attacker, so the limit does not become a self-inflicted denial of service.
- **Decide the challenge-token semantics deliberately.** Either keep strict one-shot and document
  why, or allow a bounded number of attempts against one challenge with a per-token counter and the
  same lockout bookkeeping #215 added. The second is the common shape and is friendlier, but it
  widens the window in which a stolen `mfaToken` is useful — which is exactly the trade to make
  explicitly rather than by default.
- **Fix the client's error mapping** so a 5xx is reported as a server error rather than as an
  invalid code, and so an exhausted or expired challenge is distinguishable from a wrong factor.
  Hosted here under the hub rule (verbara-meta/ADR-0005): the change is in `Verbara.Platform.Web`
  but its behaviour is part of this endpoint's contract.

## Capabilities

### New Capabilities
- `mfa-verify-endpoint-hardening`: the anonymous second-factor endpoint carries its own per-tenant
  rate-limit policy, an explicitly decided challenge-token retry semantics, and a client that
  reports what actually happened.

### Modified Capabilities
<!-- None. `mfa-recovery-code-redemption` owns redemption correctness and its lockout/audit
     bookkeeping; none of its requirements change here. This change is about the endpoint's
     throttling, its challenge lifecycle, and its client — all additive. -->

## Impact

- **Source:** `src/Verbara.Platform.Api/Endpoints/AuthEndpoints.cs` (the endpoint's rate-limit
  attribute and, if the semantics change, `MfaVerify`'s use of `IMfaPendingCache`);
  `src/Verbara.Platform.Identity/Mfa/IMfaPendingCache.cs` and both implementations
  (`InMemoryMfaPendingCache`, `RedisMfaPendingCache`) if a per-token attempt counter is adopted —
  note the interface's doc comment currently fixes `TakeAsync` as destructive, so that contract
  changes with it.
- **Rate limiting:** the policy registration in `Program.cs`. The
  `TenantResolutionMiddleware` → `UseRateLimiter()` → `UseAuthentication()` order invariant must
  hold; this endpoint is anonymous, so the partition key comes from tenant resolution, not the
  principal.
- **Frontend (`Verbara.Platform.Web`):** `src/core/auth/mfa-verify.tsx` error mapping, plus i18n
  strings in EN-US / ES-419 / PT-BR — parity is CI-enforced, so all three move together.
- **Cross-repo:** frontend only, hosted here per the hub rule. No `Verbara.Sdk` / `Verbara.Sdk.Pro`
  change, no pin movement.

### Out of Scope (explicit)

- **The recovery-code digest strength** — tracked as `harden-recovery-code-digest`.
- **`PREPUB-2026-05-09-MFA-007`** (the in-memory `IMfaPendingCache` in single-process deployments) —
  a pre-existing open finding about the cache's *durability*, not its retry semantics. It touches
  the same type and should be read alongside this change, but it is not resolved by it.
