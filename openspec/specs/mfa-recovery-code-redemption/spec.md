# mfa-recovery-code-redemption Specification

## Purpose

A recovery code exists for exactly one situation: the user has lost their authenticator. This
capability holds the contract that makes it work — a code minted by **any** mint path is redeemable
at `POST /auth/mfa/verify`, verification classifies **each stored element by its own shape**, no
content of `users.mfa_recovery_codes` can raise out of the verifier, and every failed attempt is
audited and counted toward lockout.

**How to read the durable content.** The column carries more than one digest family, and it will
keep doing so — `MfaService.HashRecoveryCodes` writes BCrypt, `IRecoveryCodeService.Hash` writes a
salted SHA-256 hex digest, and a future stretched-KDF family is anticipated. The durable rule is the
**per-element prefix dispatch** borrowed from `PasswordService.VerifyPassword` (Platform/ADR-0013),
whose forward-compatibility clause exists precisely so a family can be added as one more branch.
Per element, not per array: the API cannot produce a mixed row today, but a partially-applied
migration or a manual edit can, and a whole-array decision fails such a row on its first element.

**The fail-closed property is the point, not a detail.** Every branch returns "no match" unless it
positively verifies, and a parse failure of stored material is converted into "no match" — never
into an exception, never into success. That is enforced in one place, `BcryptVerifyGuard.SafeVerify`,
because catching `SaltParseException` alone was measured to be insufficient: a digest corrupt
*inside* the `$2` family raises `ArgumentOutOfRangeException`, `FormatException` or
`IndexOutOfRangeException`, two of which reach the error middleware's fallthrough arm as HTTP 500.
Any future credential verifier routes through that helper rather than writing its own `try`/`catch`.

**Why the last requirement exists.** This capability was written because the feature shipped broken
while the suite was green: each test suite verified the family it minted, in a different project,
and none crossed the seam. "The regression suite crosses the mint-to-redeem seam" is therefore a
first-class requirement, not test hygiene — it is the specific control whose absence let a full
second-factor lockout reach production.

Deliberately outside this capability: the **strength** of any digest family (tracked as
`harden-recovery-code-digest`), and the endpoint's throttling and challenge-token lifecycle
(tracked as `harden-mfa-verify-endpoint`).

## Requirements
### Requirement: A recovery code minted by any mint path is redeemable at login

A recovery code returned to a user by ANY mint path MUST be redeemable at
`POST /api/v1/auth/mfa/verify`. Four mint paths write `users.mfa_recovery_codes` today and they
persist two different digest families: `POST /auth/mfa/setup` and
`POST /auth/mfa/recovery-codes/regenerate` store BCrypt cost-10 digests via
`MfaService.HashRecoveryCodes`; `POST /profile/security/mfa/enroll/verify` and
`POST /profile/security/recovery-codes/regenerate` store salted SHA-256 hex digests via
`IRecoveryCodeService.Hash(code, salt)` where the salt is `user.UserId.Value`. Redemption MUST
succeed for a code from any of the four, without the caller knowing which minted it.

Redemption MUST remain one-time: on a successful match the redeemed element is removed from
`User.MfaRecoveryCodes` and the user is persisted, exactly as today. A code MUST NOT be redeemable
twice.

No stored value may be rewritten, re-hashed, or invalidated to satisfy this requirement. Codes
already held by users MUST keep working across the deploy in both families.

#### Scenario: A wizard-minted code redeems successfully

- **GIVEN** a user who enrolled through `POST /profile/security/mfa/enroll/verify`, so every element
  of `mfa_recovery_codes` is a 64-character salted SHA-256 hex digest
- **AND** the user holds the plaintext codes that endpoint returned once
- **WHEN** the user logs in, receives an `mfaToken` challenge, and posts one of those codes to
  `POST /auth/mfa/verify` as `recoveryCode`
- **THEN** the response is **200** with an access token
- **AND** the redeemed code is removed from the stored array, so replaying it fails

#### Scenario: A legacy-minted code still redeems successfully

- **GIVEN** a user whose `mfa_recovery_codes` elements are BCrypt cost-10 digests written by
  `POST /auth/mfa/setup`
- **WHEN** the user redeems one of those codes at `POST /auth/mfa/verify`
- **THEN** the response is 200 with an access token, unchanged from today's behaviour

#### Scenario: Regenerating through the wizard does not break redemption

- **GIVEN** a user whose codes were minted by the legacy path and currently redeem correctly
- **WHEN** the user calls `POST /profile/security/recovery-codes/regenerate`, replacing the array
  with salted SHA-256 digests
- **THEN** the newly returned codes redeem successfully at `POST /auth/mfa/verify`

### Requirement: Verification dispatches per stored-digest format, per element

`MfaService.ValidateRecoveryCode` MUST decide how to verify **each stored element individually**, by
that element's own shape, mirroring the prefix-discriminator pattern `PasswordService.VerifyPassword`
already uses for password hashes (Platform/ADR-0013, whose "Forward compatibility" section
pre-authorises adding a branch). An element beginning with `$2` MUST be verified with BCrypt; any
other element MUST be verified as a salted SHA-256 digest through `IRecoveryCodeService.Verify`,
which uses `CryptographicOperations.FixedTimeEquals`.

The salted-SHA-256 branch MUST delegate to `IRecoveryCodeService.Verify` rather than reimplement the
digest. That method is already implemented and unit-tested but has **zero callers in `src/`** — the
unwired seam is the root cause of this defect, and wiring it is the fix rather than duplicating the
computation next to it.

Because `IRecoveryCodeService.Hash` salts with the user's id, the verifying seam MUST receive that
salt. The redemption handler MUST supply `user.UserId.Value`; it MUST NOT be reconstructed from any
other value.

Per-element dispatch — rather than deciding once for the whole array — is required so an array
holding a mix of families still verifies correctly. The API cannot produce a mixed array today (every
mint path replaces the array wholesale), but a partially-applied migration or a manual database edit
can, and a whole-array decision would fail such a row on its first element.

#### Scenario: A mixed-family array verifies each element on its own terms

- **GIVEN** a stored array holding one BCrypt digest and one salted SHA-256 digest
- **WHEN** the plaintext matching the SHA-256 element is redeemed
- **THEN** verification succeeds and that element is the one removed
- **AND** the same array also redeems the plaintext matching the BCrypt element

#### Scenario: A wrong code is rejected cleanly in either family

- **GIVEN** a stored array in either digest family
- **WHEN** a code that matches no element is submitted
- **THEN** the response is **401**, not 200 and not 500
- **AND** no element is removed from the stored array

### Requirement: Stored material never raises out of the verification path

Verification MUST NOT allow an exception originating in stored material to escape to the middleware.
The BCrypt branch MUST catch `BCrypt.Net.SaltParseException` and treat it as "no match", exactly as
`PasswordService.VerifyPassword` has done since Platform/ADR-0013 — that ADR records the rationale
as avoiding leaking hash shape through the exception type. An unrecognised, truncated, or corrupt
stored digest MUST therefore produce a failed verification, never an unhandled exception.

This is the specific regression being closed: `BCrypt.Verify` on a 64-character hex digest throws
`SaltParseException`, which derives directly from `Exception` and so falls through
`ErrorHandlingMiddleware`'s `_` arm to **HTTP 500**, with `ProblemDetails.Detail` carrying the raw
library message `Invalid salt version`. After this change `POST /auth/mfa/verify` MUST NOT return
500 for any content of `users.mfa_recovery_codes`, and MUST NOT surface a cryptography library's
message to the caller.

#### Scenario: A corrupt stored digest yields 401, not 500

- **GIVEN** a stored array whose elements are neither valid BCrypt digests nor valid salted SHA-256
  digests (for example a value written by a manual database edit)
- **WHEN** any recovery code is submitted at `POST /auth/mfa/verify`
- **THEN** the response is **401**
- **AND** the response body contains no cryptography-library message and no
  `Invalid salt version` text
- **AND** no unhandled exception is logged

#### Scenario: The verifier does not throw on a non-BCrypt digest

- **GIVEN** `MfaService.ValidateRecoveryCode` is called directly with a stored array of 64-character
  hex digests and a plaintext that does not match any of them
- **WHEN** the call completes
- **THEN** it returns "no match" and does not throw

### Requirement: Every failed redemption attempt is audited and counted toward lockout

`POST /auth/mfa/verify` MUST record a failed attempt against the tenant's lockout policy and MUST
emit an authentication event when verification fails, matching what `AuthEndpoints.Login` already
does on a bad password. Today the handler does neither on any failure path — not for a wrong code,
not for the crash this change removes — so second-factor guessing against the endpoint is
unaudited and unthrottled beyond the global rate-limit bucket.

This closes a gap against `docs/security/audit-checklist.md` Scope 3.4, which requires MFA
verification to be audited, and against threat-model asset **A7**, whose sensitivity rests on
recovery being observable.

A successful redemption MUST continue to reset the attempt counter and emit its success event
through the existing token-issuing path.

#### Scenario: A wrong recovery code is audited and counted

- **GIVEN** an authenticated MFA challenge in progress
- **WHEN** a recovery code that matches no stored element is submitted
- **THEN** the response is 401
- **AND** an authentication event recording the failed MFA verification is written
- **AND** the failure counts toward the tenant's lockout policy

#### Scenario: Repeated failures eventually lock the account

- **GIVEN** a tenant lockout threshold of N failed attempts
- **WHEN** N failed MFA verification attempts are made for the same user
- **THEN** the account is locked according to that tenant's policy, as it would be for N failed
  password attempts

### Requirement: The regression suite crosses the mint-to-redeem seam

The test suite MUST contain at least one test per mint path that mints a code through the real
endpoint and then redeems it through the real redemption endpoint. The defect shipped precisely
because no such test existed: `MfaServiceTests` hashes with BCrypt and verifies with BCrypt;
`RecoveryCodeServiceTests` hashes with salted SHA-256 and verifies with salted SHA-256; each suite is
a closed loop inside one family, in a different test project, and neither crosses the boundary. The
one HTTP test that mints through the wizard asserts only the shape of the returned plaintext and
discards the codes — its comment claiming it "proves the verify→persist branch works end-to-end"
refers to TOTP verification, not redemption, and MUST be corrected so it cannot mislead again.

Endpoint-test fixtures that seed `MfaRecoveryCodes` with placeholder strings such as `"code1"` MUST
NOT be taken as redemption coverage; those values are neither digest family and would themselves have
thrown had any test ever redeemed them.

#### Scenario: Each mint path has an end-to-end redemption test

- **GIVEN** the four mint paths
- **WHEN** the test suite runs
- **THEN** for each path there is a test that mints via that endpoint, then redeems one of the
  returned codes via `POST /auth/mfa/verify`, asserting 200
- **AND** at least one test asserts that redeeming the same code a second time fails

#### Scenario: A regression reintroducing single-family verification fails the suite

- **GIVEN** the cross-seam tests are present
- **WHEN** a change makes `ValidateRecoveryCode` verify only BCrypt digests again
- **THEN** the wizard-path redemption test fails, rather than the suite staying green

