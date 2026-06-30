## ADDED Requirements

### Requirement: Autonomous AI disposition enrichment of the existing abandoned-wrap-up close
The existing `ConversationTimeoutWorker` wrap-up-timeout path SHALL, when it auto-closes an abandoned
wrap-up for a tenant that has opted in, additionally record a `TypificationSubmission` derived from the
conversation's pending high-confidence AI suggestion, stamped `Source = SubmissionSource.AutoAi`. The
default behaviour (no opt-in) SHALL be byte-identical to today: the wrap-up is closed with no disposition.
No new background worker SHALL be introduced; the enrichment reuses the existing leader-gated close path.

#### Scenario: Opted-in tenant with a high-confidence suggestion gets an AI disposition stamped
- **GIVEN** a tenant has the autonomous-disposition gate enabled, holds the required license features, and a wrap-up has been idle beyond the wrap-up timeout with a pending AI suggestion whose confidence is at or above `AutonomousThreshold`
- **WHEN** the timeout path auto-closes the wrap-up
- **THEN** the system SHALL persist a `TypificationSubmission` with `Source = SubmissionSource.AutoAi`, the suggestion's node path and confidence, close the conversation, and emit an AI-actor audit event

#### Scenario: Non-opted-in tenant close path is unchanged
- **GIVEN** a tenant has NOT enabled the autonomous-disposition gate
- **WHEN** a wrap-up is auto-closed by the timeout path
- **THEN** the conversation SHALL be closed with no `TypificationSubmission` written, exactly as before this change

#### Scenario: No qualifying suggestion means no stamp
- **GIVEN** an opted-in, licensed tenant whose abandoned wrap-up has no pending AI suggestion, or a suggestion below `AutonomousThreshold`
- **WHEN** the timeout path auto-closes the wrap-up
- **THEN** the conversation SHALL be closed with no disposition and the system SHALL emit an `AutonomousSkipped` audit event with the reason (`NoSuggestion` or `BelowThreshold`)

### Requirement: Per-tenant activation gate is a controller instruction, not consent
A tenant SHALL NOT have autonomous disposition stamping activated unless a privileged administrator has
recorded an activation instruction, persisted as `(tenant_id, attested_by_user_id, attested_at, revoked_at,
revoked_by_user_id)`. This record is a documented controller instruction and configuration gate under the
data-processing agreement — it is NOT, and SHALL NOT be represented as, the data subject's consent. Removing
it SHALL disable stamping for subsequent close cycles immediately.

#### Scenario: Admin records the activation instruction
- **GIVEN** a tenant or platform admin calls the activation endpoint with valid credentials
- **WHEN** the request succeeds
- **THEN** the system SHALL persist the instruction (attesting user ID, tenant ID, UTC timestamp) and return HTTP 201

#### Scenario: Revocation disables stamping for the next cycle
- **GIVEN** an active activation instruction exists and stamping is enabled
- **WHEN** an admin revokes it
- **THEN** the record SHALL be soft-deleted (`revoked_at`, `revoked_by_user_id` set) and subsequent close cycles SHALL NOT stamp dispositions for that tenant

### Requirement: Verification pass and conditional commit
Before stamping an autonomous disposition the system SHALL re-verify, at commit time, that the target node
is still a valid leaf in the active schema, the conversation is still in the `WrapUp` state, and the
suggestion confidence still meets or exceeds `AutonomousThreshold`. The commit SHALL be a single conditional
write predicated on the observed conversation state and version (compare-and-set), so that a concurrent
human typification that lands first causes the autonomous write to affect zero rows and abort without error.
The activation gate SHALL be re-checked within the same atomic write.

#### Scenario: A concurrent human typification wins the race
- **GIVEN** the close path has selected a candidate and passed verification
- **WHEN** a human agent commits a manual typification before the conditional write executes
- **THEN** the autonomous write SHALL match zero rows, the worker SHALL skip the candidate without error, and the human disposition SHALL stand

#### Scenario: Schema mutation between suggestion and commit is detected
- **GIVEN** a suggestion targeting node `X` whose schema is modified to remove `X` before commit
- **WHEN** the verification pass runs
- **THEN** the system SHALL detect `X` is no longer a valid leaf, skip the candidate, and emit `AutonomousSkipped` with reason `SchemaMutation`

#### Scenario: Activation revoked microseconds before commit is honoured
- **GIVEN** the activation gate is re-checked inside the conditional write
- **WHEN** the gate is revoked just before the write executes
- **THEN** the conditional write SHALL fail its gate predicate and no disposition SHALL be stamped

### Requirement: AI-actor audit record with time-bounded retention
Every autonomous disposition commit SHALL produce a structured audit record using actor type `ai` and an AI
actor identifier, recording the tenant ID, conversation ID, selected node path, confidence score, and UTC
timestamp. The record SHALL be tamper-evident via the existing integrity hash. It SHALL carry a retention
floor that the `AuditRetentionService` purge honours, so the blanket time-based purge cannot delete a record
still within its retention window; once past the floor the record is purged normally. The record SHALL NOT
be retained indefinitely.

#### Scenario: Autonomous commit produces an AI-actor audit record
- **GIVEN** the close path commits an autonomous disposition for conversation `C` in tenant `T`
- **WHEN** the commit succeeds
- **THEN** an `AutonomousCommit` audit record SHALL be written with actor type `ai`, the AI actor identifier, tenant ID, conversation ID, node path, confidence, and UTC timestamp

#### Scenario: Retention purge preserves a record still within its floor
- **GIVEN** an `AutonomousCommit` audit record whose retention floor has not elapsed
- **WHEN** the `AuditRetentionService` blanket purge runs against an older cutoff
- **THEN** the record SHALL be preserved; a record past its floor SHALL be purged

#### Scenario: Right-to-erasure redacts the contact linkage but retains the decision fact
- **GIVEN** a valid Art. 17 erasure for the contact linked to conversation `C`
- **WHEN** the purge runs
- **THEN** the contact-identifying linkage SHALL be redacted while the decision-fact record (node path, confidence, timestamp) is retained under the legal-defence exemption

### Requirement: Append-only operator correction without conversation reopen
A supervisor holding the correction permission SHALL be able to correct an autonomously stamped disposition
within a bounded correction window by submitting a new corrective `TypificationSubmission` that references
the original; the original autonomous submission SHALL remain immutable. The conversation SHALL NOT be
transitioned from `Closed` back to `WrapUp`. There SHALL be no data-subject-facing dispute endpoint. A
correction request outside the window SHALL be rejected.

#### Scenario: Supervisor corrects an auto-stamped disposition
- **GIVEN** conversation `C` has an autonomous disposition within the correction window
- **WHEN** a supervisor submits a corrective reclassification with a different node path
- **THEN** the system SHALL persist a new corrective submission (`Source = Manual`) referencing the original, leave the original immutable, keep the conversation `Closed`, and emit a `DispositionCorrected` audit event recording the original and corrected node paths

#### Scenario: Correction after the window is rejected
- **GIVEN** conversation `C`'s autonomous disposition is older than the correction window
- **WHEN** a supervisor attempts to correct it
- **THEN** the system SHALL return HTTP 409 with error code `CorrectionWindowExpired`

### Requirement: License and entitlement gating
Autonomous disposition stamping SHALL occur only when the tenant holds both the `AdvancedTypification` and
`TypificationAi` `LicenseFeature` entitlements, in addition to the activation gate. An unlicensed tenant
with the gate enabled SHALL have the close path skip stamping and emit `AutonomousSkipped` with reason
`LicenseMissing`.

#### Scenario: Unlicensed tenant is not stamped
- **GIVEN** a tenant lacks `TypificationAi` but has the activation gate enabled
- **WHEN** an abandoned wrap-up is auto-closed
- **THEN** no disposition SHALL be stamped and the system SHALL emit `AutonomousSkipped` with reason `LicenseMissing`

### Requirement: Dark rollout with circuit breaker, rate cap, and poison-candidate handling
Autonomous disposition stamping SHALL be controllable independently of code deployment: a per-tenant flag
defaulting OFF, a global circuit breaker the operator can flip via configuration without redeploy, and a
per-tenant cap on autonomous commits per cycle. A candidate that repeatedly fails verification SHALL be
moved to a terminal skipped state and SHALL emit `AutonomousSkipped` once rather than every cycle.
Autonomous commits SHALL be idempotent on `(conversation_id, version)` so a retry cannot double-stamp.

#### Scenario: Global circuit breaker halts stamping without redeploy
- **GIVEN** the global autonomous circuit breaker is tripped via configuration
- **WHEN** the close path runs
- **THEN** no autonomous disposition SHALL be stamped for any tenant until the breaker is reset

#### Scenario: Per-tenant rate cap bounds a cycle
- **GIVEN** a tenant whose per-cycle autonomous-commit cap is `N`
- **WHEN** more than `N` candidates qualify in one cycle
- **THEN** at most `N` SHALL be stamped in that cycle and the remainder SHALL be deferred to the next

#### Scenario: Chronically failing candidate is skipped once
- **GIVEN** a candidate that fails verification on every cycle
- **WHEN** it has failed a bounded number of times
- **THEN** it SHALL be moved to a terminal skipped state and `AutonomousSkipped` SHALL be emitted once, not repeatedly

### Requirement: Observability metrics for autonomous disposition
The system SHALL emit metrics for autonomous disposition activity: total autonomous commits, total skips by
reason, and total operator corrections (and the overturn ratio derivable from them). Acting on these
metrics (auto-pausing a tenant on a high overturn ratio) is out of scope for this change; only emission is
required.

#### Scenario: Commit and correction metrics are emitted
- **GIVEN** the close path commits an autonomous disposition and a supervisor later corrects one
- **WHEN** each event occurs
- **THEN** the corresponding counters (`autonomous_commits_total`, `autonomous_corrections_total`) SHALL be incremented with tenant dimension

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `Verbara.Platform.Api` — `ConversationTimeoutWorker` close-path behaviour change (must be byte-identical when the gate is OFF); `AuditRetentionService` retention-floor logic; new admin endpoints + DTOs; AOT.
- `Verbara.Platform.Typification` — `TypificationSubmission` extension, `CorrectionState` enum, `IAutonomousDispositionPolicy`.
- `Storage.Postgres` — additive migration 014 (`typification_submissions` columns, `tenant_autonomous_disposition` table, `audit_entries` retention floor).
- `Verbara.Sdk.Pro` — `IClusterLeader.IsLeader` already consumed on the close path; no new surface.
- GDPR / legal — Art. 22 non-applicability is documented (ADR-0034 + DPIA-lite); the controller owns any genuine duty.

**Mitigation:**
- Gate OFF by default with a byte-identical no-op close path, locked by a defaults test.
- CAS conditional commit so a concurrent human typification always wins; verification pass before every stamp.
- Retention-floor regression test (CI has no live-DB purge test today); records tamper-evident but time-bounded (Art. 5(1)(e)).
- Per-tenant rate cap + global circuit breaker for blast-radius control; poison-candidate back-off.
- All new DTOs registered in `ApiJsonContext`; zero `IL2026`/`IL3050` diagnostics in the AOT publish.
