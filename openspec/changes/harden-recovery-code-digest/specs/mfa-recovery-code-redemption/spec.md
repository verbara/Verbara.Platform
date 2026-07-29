## MODIFIED Requirements

### Requirement: Verification dispatches per stored-digest format, per element

`MfaService.ValidateRecoveryCode` MUST decide how to verify **each stored element individually**, by
that element's own shape, mirroring the prefix-discriminator pattern `PasswordService.VerifyPassword`
already uses for password hashes (Platform/ADR-0013, whose "Forward compatibility" section
pre-authorises adding a branch). **Three** families MUST be verifiable: an element beginning with
the Argon2id prefix MUST be verified with Argon2id; an element beginning with `$2` MUST be verified
with BCrypt; any other element MUST be verified as a salted SHA-256 digest through
`IRecoveryCodeService.Verify`, which uses `CryptographicOperations.FixedTimeEquals`.

Argon2id is the family every **new** mint produces. BCrypt and salted SHA-256 are legacy families
kept verifiable **indefinitely** — see the convergence note below.

The salted-SHA-256 branch MUST delegate to `IRecoveryCodeService.Verify` rather than reimplement the
digest. That method uses a constant-time comparison; duplicating it beside the dispatch would put a
second comparison on an authentication path.

Because `IRecoveryCodeService.Hash` salts with the user's id, the verifying seam MUST receive that
salt. The redemption handler MUST supply `user.UserId.Value`; it MUST NOT be reconstructed from any
other value. The Argon2id family MUST NOT reintroduce a separate salt parameter — the KDF carries its
salt inside the encoded digest, as it does on the password path.

Per-element dispatch — rather than deciding once for the whole array — is required so an array
holding a mix of families still verifies correctly. With three families in circulation a mixed array
stops being a theoretical case: any user who regenerates after this change lands on Argon2id while
older rows stay legacy, and a partially-applied migration or a manual database edit can mix them
within one row.

**Convergence, stated because it differs from the password migration.** Platform/ADR-0013's password
path converges: every successful login rehashes one user, so the legacy family drains. **Recovery
codes do not converge that way.** Only the redeemed code arrives in plaintext, and it is consumed on
success — the other nine digests in the array cannot be upgraded from a redemption. The only path to
Argon2id for an existing user is a **regenerate**, which mints a fresh set. Legacy families therefore
remain verifiable indefinitely, and no requirement here may be read as implying they drain on their
own.

#### Scenario: A three-family array verifies each element on its own terms

- **GIVEN** a stored array holding one Argon2id digest, one BCrypt digest and one salted SHA-256
  digest
- **WHEN** the plaintext matching any one of them is redeemed
- **THEN** verification succeeds and that element is the one removed
- **AND** the other two elements remain redeemable by their own plaintexts

#### Scenario: A newly minted set is Argon2id

- **GIVEN** a user who regenerates their recovery codes after this change
- **WHEN** the new array is inspected in storage
- **THEN** every element is an Argon2id digest
- **AND** each of the returned plaintext codes redeems successfully

#### Scenario: A legacy code keeps working with no regenerate

- **GIVEN** a user whose stored array predates this change, in either legacy family
- **WHEN** they redeem one of the codes they already hold
- **THEN** verification succeeds
- **AND** no stored element was rewritten or invalidated by the deploy

#### Scenario: A wrong code is rejected cleanly in every family

- **GIVEN** a stored array in any of the three digest families
- **WHEN** a code that matches no element is submitted
- **THEN** the response is **401**, not 200 and not 500
- **AND** no element is removed from the stored array

## ADDED Requirements

### Requirement: New recovery codes are minted with a stretched KDF

Every mint path MUST produce Argon2id digests. The salted SHA-256 form currently written by
`IRecoveryCodeService.Hash` is a **single round** over `"{salt}:{CODE}"` where the salt —
`user.UserId.Value` — lives in the same row, and the codes are 8 characters over a 32-glyph alphabet:
roughly a 2^40 keyspace against an unstretched digest. An attacker holding the column recovers the
plaintext codes, and a recovery code is a full second-factor bypass.

Argon2id MUST be used rather than a third algorithm, because `PasswordService` already depends on it:
recovery codes and passwords then share one hardness story and one place to tune cost, instead of two.

The per-code cost MUST be measured, not assumed. A mint produces **ten** codes, so a per-code cost
that is unremarkable for a single password verification is paid tenfold in one request. If the
measured regenerate latency is user-hostile, the cost parameters MUST be tuned deliberately and the
chosen values recorded — not left at whatever the password path happens to use.

#### Scenario: Mint cost is measured before the parameters are fixed

- **GIVEN** the Argon2id parameters proposed for recovery codes
- **WHEN** a regenerate producing ten codes is timed
- **THEN** the measured latency is recorded alongside the chosen parameters
- **AND** the parameters were chosen against that measurement rather than inherited unexamined

### Requirement: Users on a legacy family are prompted to upgrade

Because redemption cannot upgrade an array, this change MUST surface the upgrade rather than wait for
it. A user whose stored digests are in a legacy family SHOULD be prompted — once, non-blocking — to
regenerate, and operators MUST have a way to see how many users remain on a legacy family.

Without this, "new codes use a stretched KDF" is true and almost meaningless: every existing user
stays exactly as exposed as before, indefinitely, and nobody can tell how many that is.

#### Scenario: An operator can see the remaining legacy population

- **GIVEN** a deployment with users on both legacy and Argon2id families
- **WHEN** an operator inspects the relevant admin surface or metric
- **THEN** the count of users still holding legacy-family recovery codes is visible

#### Scenario: Regenerating clears the prompt

- **GIVEN** a user prompted to regenerate because their codes are legacy-family
- **WHEN** they regenerate
- **THEN** their stored array is Argon2id and the prompt does not reappear

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `MfaService.ValidateRecoveryCode` — the sole redemption path, on the anonymous
  `POST /auth/mfa/verify`. A defect is a second-factor bypass, worse than the weak digest being
  fixed.
- `IRecoveryCodeService` and its four mint call sites.
- Regenerate latency, which becomes ~10× a single Argon2id verification in one request.
- **Cross-repo: none.**

**Mitigation:**
- The dispatch extension point already exists and is tested: `fix-recovery-code-redemption` built
  per-element prefix dispatch precisely so a family could be added as one more branch, and its
  cross-seam suites (`MfaRecoveryCodeRedemptionTests`, `MfaServiceTests`) extend to a third family
  rather than needing new machinery.
- Nothing stored is rewritten or invalidated, so the change is reversible by reverting the binary —
  the only consequence is that newly minted Argon2id digests stop verifying, which is why the
  legacy branches are kept rather than removed.
- Argon2id arrives via an existing dependency with existing parameters, so no new crypto primitive
  enters the codebase.
- The mint-cost requirement forces the tenfold-per-request cost to be measured before it ships,
  rather than discovered by a user waiting on a regenerate.
