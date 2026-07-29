## ADDED Requirements

### Requirement: The anonymous second-factor endpoint carries its own rate-limit policy

`POST /api/v1/auth/mfa/verify` MUST carry a dedicated rate-limit policy. Today it carries none at the
group or endpoint level, so only the global bucket applies — and it is `AllowAnonymous`, sitting
between a valid password and a session.

The lockout bookkeeping added by `fix-recovery-code-redemption` is **not** a substitute. Lockout is
per-user and bounded by the tenant's policy; it constrains sustained guessing against one account,
not request volume against the endpoint. The two are complementary controls and this requirement is
about the second.

The policy MUST partition **per tenant**, consistent with Platform/ADR-0031 and the standing order
invariant that `TenantResolutionMiddleware` runs before `UseRateLimiter()` — otherwise every request
collapses into the shared `__global__` bucket, which is the v2.14.1 bug this repo already paid for.
Because the endpoint is anonymous, the partition key comes from tenant resolution, not from the
principal.

The limit MUST be sized against the **legitimate** worst case — a user fumbling a TOTP code across
clock skew, or working through several recovery codes — not against the attacker. A limit tuned only
to frustrate guessing turns into a self-inflicted denial of service on the unhappy path, which is the
exact path this endpoint exists to serve.

#### Scenario: Requests are partitioned per tenant

- **GIVEN** two tenants issuing MFA verification requests concurrently
- **WHEN** one tenant exhausts its limit
- **THEN** the other tenant's requests are unaffected
- **AND** neither is counted against the shared global bucket

#### Scenario: A legitimate retry sequence is not throttled

- **GIVEN** a user who mistypes a TOTP code twice and then submits a correct one
- **WHEN** the three requests are made in normal succession
- **THEN** none is rejected by the rate-limit policy

### Requirement: The challenge-token retry semantics is decided, not inherited

The lifecycle of the one-shot `mfaToken` MUST be an explicit decision recorded in this change.
`IMfaPendingCache.TakeAsync` is atomic and destructive and runs **before** the factor is verified, so
today every failed attempt — including an honest typo — burns the challenge and forces a full
re-login. That behaviour was never chosen; it is what the ordering happens to produce.

Either outcome satisfies this requirement, but one of them MUST be chosen and written down:

- **Keep strict one-shot**, and document why — that a challenge token which survives a failure widens
  the window in which a stolen token is useful.
- **Allow a bounded number of attempts against one challenge**, with a per-token attempt counter and
  the same lockout bookkeeping the redemption path already performs, and document the widened window
  as the accepted trade.

If the second is chosen, `IMfaPendingCache`'s contract changes: its doc comment currently fixes
`TakeAsync` as removing the entry atomically so "a token can only be consumed once", and **both**
implementations — `InMemoryMfaPendingCache` and `RedisMfaPendingCache` — must move together. A
per-token counter that is atomic in one implementation and racy in the other is worse than not
changing it at all.

#### Scenario: The chosen semantics is documented at the seam

- **GIVEN** the change has landed
- **WHEN** `IMfaPendingCache` and the `MfaVerify` handler are read
- **THEN** the retry semantics is stated explicitly, with the security trade that motivated it
- **AND** the statement matches what the implementation actually does

#### Scenario: A bounded-retry implementation is atomic in both caches

- **GIVEN** the bounded-retry option was chosen
- **WHEN** concurrent verification attempts race against one challenge token
- **THEN** the attempt counter is enforced atomically in both the in-memory and the Redis cache
- **AND** the bound cannot be exceeded by racing

### Requirement: The client reports what actually happened

`Verbara.Platform.Web`'s MFA verification view MUST distinguish a rejected factor from a server
error and from an exhausted or expired challenge. Today it special-cases 429 and 400-with-`expired`
and maps everything else — including any 5xx — to the generic "invalid code" message.

`fix-recovery-code-redemption` removed the 500 that made this actively misleading for recovery codes,
so the branch is no longer reachable for that case; the defect outlived its most visible symptom. A
genuine server error still tells the user they mistyped, while their challenge token has already been
destroyed server-side — so the user retries, burns another token, and is told the same wrong thing.

Hosted in this repo under the hub rule (verbara-meta/ADR-0005): the code lives in
`Verbara.Platform.Web` but the behaviour is part of this endpoint's contract. i18n parity is
CI-enforced, so EN-US, ES-419 and PT-BR MUST move together.

#### Scenario: A server error is not reported as an invalid code

- **GIVEN** the MFA verification request fails with a 5xx
- **WHEN** the client renders the failure
- **THEN** the user is told a server error occurred, not that their code was invalid
- **AND** the message distinguishes it from an expired or exhausted challenge

#### Scenario: All three locales carry the new strings

- **GIVEN** new client-facing strings were added
- **WHEN** the i18n parity check runs in CI
- **THEN** EN-US, ES-419 and PT-BR are all present and the check passes

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `POST /auth/mfa/verify` — anonymous, and the sole gate between a valid password and a session. A
  rate limit that is too tight locks out legitimate users at the moment they are already struggling;
  a challenge-token semantics that is too loose widens the value of a stolen `mfaToken`.
- `IMfaPendingCache` and both implementations, if bounded retry is chosen — a contract change on a
  security-relevant seam, where an in-memory/Redis behavioural split would be a real defect.
- The rate-limiter partition configuration in `Program.cs`, which carries the standing
  `TenantResolutionMiddleware` → `UseRateLimiter()` order invariant.
- **Cross-repo:** frontend only, hosted here per the hub rule. No SDK/Pro change, no pin movement.

**Mitigation:**
- The two security knobs are required to be *decided and documented*, not merely implemented, so
  neither trade is made silently.
- Sizing the limit against the legitimate worst case rather than the attacker is a stated
  requirement, which is the failure mode this kind of change usually ships with.
- If bounded retry is chosen, atomicity across **both** cache implementations is a requirement with
  its own scenario, so the split-behaviour hazard cannot pass review unnoticed.
- The client fix is additive and cannot affect the endpoint's security posture; i18n parity is
  already CI-enforced, so the usual omission is caught mechanically.
- `PREPUB-2026-05-09-MFA-007` (in-memory cache durability) touches the same type and is named in the
  proposal as adjacent-but-unresolved, so the two are read together rather than one being mistaken
  for the other.
