---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Tenant security owners; anyone whose recovery codes sit in a leaked table extract
decision_ref: Platform/ADR-0013
---

## Why

`users.mfa_recovery_codes` still carries a digest family that is **cheap to brute-force offline**.
`RecoveryCodeService.Hash` computes a **single-round** `SHA-256` over `"{salt}:{CODE}"`, where the
salt is `user.UserId.Value` — a value stored in the same row. Codes are 8 characters over a 32-glyph
alphabet, so the keyspace is roughly **2^40**. Salt plus digest plus an unstretched hash means an
attacker holding the column recovers the plaintext codes; per-user salting only forbids a shared
rainbow table, it does not make the search expensive.

A recovery code is a **full second-factor bypass**. Recovering one is equivalent to recovering the
TOTP secret.

**Why it is not urgent, and why that is not the same as "fine".** Two changes already reduced the
exposure and neither addressed the digest:

- `encrypt-mfa-secrets-at-rest` (#212) wraps the column with `IDataProtection`, so a **partial**
  leak — a table-scoped dump, a report extract, a read-replica — now yields ciphertext. That is
  peppering, and it is real mitigation.
- `fix-recovery-code-redemption` (#215) made both digest families verifiable, deliberately treating
  the elements as opaque strings so it could not change the format.

What neither covers is the case ADR-0003's own addendum calls out: **a complete database dump
includes `data_protection_keys`**, which holds key XML unencrypted in the same database. Against
that adversary the wrap buys nothing, and the 2^40 single-round digest is exactly what is left.

**Why now is the right moment.** #215 established the seam that makes this migratable without
invalidating anything: verification already dispatches per element on the digest's shape, so a third
family can be added as one more branch — the pattern Platform/ADR-0013's forward-compatibility
clause explicitly anticipates. And unlike the password case, a **redeemed recovery code arrives in
plaintext**, so rehash-on-successful-redeem is available.

## What Changes

- **Mint new codes with a stretched KDF.** Adopt the same algorithm the password path already
  uses — Argon2id, already a dependency via `PasswordService` — so recovery codes and passwords
  share one hardness story rather than two. All four mint paths switch to it.
- **Verify three families, not two.** `MfaService.ValidateRecoveryCode` gains a third branch, keyed
  on the Argon2id prefix, alongside the existing `$2` → BCrypt and fallback → salted SHA-256. The
  per-element dispatch #215 built is the extension point; no new mechanism is introduced.
- **Rehash on successful redemption.** When a code verifies against a legacy digest, the *remaining*
  codes cannot be upgraded — only the redeemed one arrived in plaintext, and it is consumed. So the
  realistic migration is: **on the next regenerate, everything becomes Argon2id**, and legacy
  digests stay verifiable indefinitely until then. State that plainly rather than implying
  convergence the password migration has and this one does not.
- **Prompt the upgrade.** Because there is no per-login convergence, users on the legacy family stay
  there until they regenerate. Surface a one-time prompt — or an admin-visible signal — so operators
  can drive it, rather than waiting for it to happen by accident.
- **NOT invalidating anything.** No outstanding code is revoked by this change. A user's printed
  codes keep working.

## Capabilities

### New Capabilities
- `recovery-code-digest-hardening`: recovery codes are minted with a stretched KDF; all three digest
  families remain verifiable per element; no outstanding code is invalidated, and the absence of
  per-login convergence is stated rather than assumed.

### Modified Capabilities
- `mfa-recovery-code-redemption`: the requirement "Verification dispatches per stored-digest format,
  per element" gains a third family. Its scenarios currently enumerate two; adding a branch changes
  what that requirement asserts, so it is a genuine delta rather than an additive capability.

## Impact

- **Source:** `src/Verbara.Platform.Identity/Mfa/RecoveryCodeService.cs` (mint + a verify branch),
  `src/Verbara.Platform.Api/Services/MfaService.cs` (third dispatch branch),
  and the four mint call sites if the service's surface changes.
- **Tests:** extend the cross-seam suites `fix-recovery-code-redemption` shipped —
  `Mfa/MfaRecoveryCodeRedemptionTests` and `Services/MfaServiceTests` — with the third family and a
  mixed three-family array.
- **Docs:** ADR-0013 addendum (a third row in the dispatch table); `IRecoveryCodeService`'s XML doc,
  which currently fixes SHA-256 as the contract.
- **Performance:** Argon2id per code × 10 codes per mint is materially slower than SHA-256 ×10.
  Measure it — a regenerate that takes seconds is a UX regression, and a per-code cost that is fine
  for one password check may not be fine for ten.
- **No schema change. No cross-repo impact.**

### Out of Scope (explicit)

- **Lengthening the codes.** 8 characters over 32 glyphs is ~40 bits, which is defensible *once the
  digest is expensive*. Changing the code format is a separate UX decision.
- **Wrapping the DataProtection keyring** — the other half of the full-dump story, named in the
  ADR-0003 addendum and tracked there.
