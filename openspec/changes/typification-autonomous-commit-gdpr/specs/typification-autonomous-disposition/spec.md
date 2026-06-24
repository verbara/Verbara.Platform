## ADDED Requirements

### Requirement: Tenant GDPR Art. 22 opt-in attestation before autonomous activation
A tenant SHALL NOT have the autonomous-commit worker activated for their conversations unless a
privileged administrator has explicitly attested acceptance of automated decision-making under
GDPR Art. 22. The attestation SHALL be persisted with the attesting user ID, tenant ID, and
UTC timestamp. Removing the attestation SHALL disable the worker for new wrap-ups immediately
(in-flight commits for the current leader cycle MAY complete; the next cycle SHALL skip
un-attested tenants).

#### Scenario: Worker skips tenant without attestation
- **GIVEN** a tenant has `typification:ai:autonomous = true` and `AutonomousThreshold` set
- **WHEN** the leader worker evaluates pending wrap-ups for that tenant
- **THEN** the worker SHALL skip all wrap-ups for that tenant and emit a `AutonomousSkipped` audit event with reason `GdprAttestationMissing`

#### Scenario: Admin records opt-in attestation
- **GIVEN** a platform or tenant admin calls `POST /tenants/{id}/typification-gdpr-attestation` with valid credentials
- **WHEN** the request is processed successfully
- **THEN** the system SHALL persist the attestation (user ID, tenant ID, UTC timestamp) and return HTTP 201

#### Scenario: Attestation removal disables worker for tenant
- **GIVEN** an existing attestation exists for a tenant and the autonomous worker is active
- **WHEN** an admin calls `DELETE /tenants/{id}/typification-gdpr-attestation`
- **THEN** the attestation record SHALL be soft-deleted and subsequent leader cycles SHALL skip that tenant's wrap-ups

### Requirement: Leader-gated autonomous-commit worker
A single leader node (elected via `IClusterLeaderElection` from Verbara.Sdk.Pro) SHALL run the
autonomous-commit worker. On each heartbeat cycle the worker SHALL:
1. Query pending wrap-ups whose idle time exceeds `AutonomousThreshold` for tenants with both
   `typification:ai:autonomous = true` AND a valid GDPR attestation.
2. For each candidate, run a verification pass (see Requirement: Autonomous verification pass).
3. Commit qualifying dispositions atomically, emitting an AI-actor audit event.
Non-leader nodes SHALL NOT commit autonomous dispositions. Leader loss SHALL cause the worker to
stop mid-cycle and hand off to the new leader on next election.

#### Scenario: Leader commits an abandoned wrap-up
- **GIVEN** a conversation wrap-up has been idle beyond `AutonomousThreshold`, the tenant has GDPR attestation, and the AI confidence is at or above the configured threshold
- **WHEN** the leader worker's heartbeat cycle processes the candidate
- **THEN** the system SHALL commit the disposition with `Source = SubmissionSource.AutoAi`, emit an `AutonomousCommit` audit event, and transition the wrap-up to `Closed`

#### Scenario: Non-leader node does not commit
- **GIVEN** a node does not hold the cluster leader lease
- **WHEN** a pending autonomous wrap-up exists
- **THEN** the node SHALL NOT commit the disposition; the leader node's next cycle SHALL handle it

#### Scenario: Leader cycle interrupted by leader loss
- **GIVEN** the current leader is mid-cycle processing autonomous commits
- **WHEN** the distributed lock lease expires or is revoked
- **THEN** the worker SHALL stop processing, any uncommitted candidates SHALL remain pending, and the new elected leader SHALL pick them up on the next cycle

### Requirement: Autonomous verification pass before commit
Before committing an autonomous disposition the worker SHALL re-verify:
- The typification schema referenced by the conversation is still active and the target node is
  still a valid leaf within it.
- The conversation is still in the wrap-up state (not manually closed or otherwise transitioned
  by a human agent in the interim).
- The AI confidence score on the pending suggestion still meets or exceeds the configured
  `AutonomousThreshold` for the tenant.
Any failed verification check SHALL cause the worker to skip that candidate and emit a
`AutonomousSkipped` audit event with the specific reason.

#### Scenario: Schema changes make the AI suggestion invalid between suggestion time and commit time
- **GIVEN** an AI suggestion was generated targeting node `X` at T0
- **WHEN** the schema is modified at T1 (removing node `X`) and the worker attempts to commit at T2 > T1
- **THEN** the worker SHALL detect that node `X` is no longer a valid leaf and SHALL emit `AutonomousSkipped` with reason `SchemaMutation`; the wrap-up SHALL remain open for manual disposition

#### Scenario: Human agent closes wrap-up before autonomous commit
- **GIVEN** an AI suggestion is pending autonomous commit
- **WHEN** a human agent manually typifies and closes the wrap-up before the worker's next cycle
- **THEN** the worker's verification pass SHALL detect the conversation is no longer in wrap-up state and SHALL skip it without error

### Requirement: AI-actor audit record for autonomous commits
Every autonomous disposition commit SHALL produce a structured, immutable audit record containing:
the AI actor identifier (service identity string, e.g. `verbara:ai:autonomous-worker`), the
tenant ID, the conversation ID, the selected node path (root→leaf array), the AI confidence
score, the UTC commit timestamp, and the worker node ID. The audit record SHALL be persisted in
the existing audit store and MUST NOT be deletable via normal API operations (append-only).

#### Scenario: Autonomous commit produces audit record
- **GIVEN** the leader worker commits an autonomous disposition for conversation `C` in tenant `T`
- **WHEN** the commit succeeds atomically
- **THEN** an `AutonomousCommit` audit event SHALL be written with actor `verbara:ai:autonomous-worker`, tenant ID, conversation ID, node path, confidence score, and UTC timestamp

#### Scenario: Audit record is not deletable
- **GIVEN** an `AutonomousCommit` audit event exists for conversation `C`
- **WHEN** any API caller attempts to delete or modify the event
- **THEN** the system SHALL return HTTP 405 or 403; the record SHALL remain unchanged

### Requirement: Data-subject right to contest an autonomous disposition
A data subject (or an agent acting on their behalf) SHALL be able to contest an autonomous
disposition by calling `PATCH /conversations/{id}/typification-dispute`. Upon receipt the system
SHALL:
1. Transition the disposition to `DisputeState.Pending` and set `DisputedAt` to UTC now.
2. Reopen the wrap-up to manual review.
3. Emit a `DisputeOpened` audit event recording the contesting identity, conversation ID, and
   UTC timestamp.
4. Return HTTP 202 with a `TypificationDisputeResponse` containing the dispute ID and the
   reopened wrap-up state.
Only autonomously committed dispositions (`Source = SubmissionSource.AutoAi`) MAY be disputed.
Manually submitted dispositions SHALL return HTTP 409 with a clear error code.

#### Scenario: Data subject contests an autonomous disposition
- **GIVEN** conversation `C` has an autonomous disposition (`Source = AutoAi`)
- **WHEN** `PATCH /conversations/{id}/typification-dispute` is called with a valid actor credential
- **THEN** the system SHALL set `DisputeState = Pending`, reopen the wrap-up, emit `DisputeOpened`, and return HTTP 202 with the dispute ID

#### Scenario: Contest request on a manually submitted disposition is rejected
- **GIVEN** conversation `C` has a manual disposition (`Source = Manual`)
- **WHEN** `PATCH /conversations/{id}/typification-dispute` is called
- **THEN** the system SHALL return HTTP 409 with error code `DisputeNotAllowed`

#### Scenario: Duplicate dispute request on an already-disputed conversation is rejected
- **GIVEN** conversation `C` is already in `DisputeState.Pending`
- **WHEN** `PATCH /conversations/{id}/typification-dispute` is called again
- **THEN** the system SHALL return HTTP 409 with error code `DisputeAlreadyPending`

### Requirement: Human reclassification SHALL resolve a disputed disposition
A disputed disposition in `DisputeState.Pending` SHALL be resolvable by a human supervisor or
agent holding the `typification:resolve-dispute` permission via manual reclassification. Upon
successful reclassification:
1. The disposition record SHALL be updated with the human-chosen node path and `Source = Manual`.
2. `DisputeState` SHALL transition to `Resolved` and `DisputeResolvedAt` SHALL be set.
3. A `DisputeResolved` audit event SHALL be emitted crediting the human actor's user ID,
   the original AI node path, and the final human node path.
4. If the human node path matches the original AI path, the event SHALL note `Confirmed = true`;
   if different, `Confirmed = false`.

#### Scenario: Supervisor resolves dispute with a different disposition
- **GIVEN** conversation `C` is in `DisputeState.Pending` with original AI path `[A, B, C_leaf]`
- **WHEN** a supervisor submits a manual reclassification with path `[A, B, D_leaf]`
- **THEN** the disposition SHALL be updated to `D_leaf`, `DisputeState = Resolved`, a `DisputeResolved` event emitted with `Confirmed = false`

#### Scenario: Supervisor confirms the original AI disposition
- **GIVEN** conversation `C` is in `DisputeState.Pending` with original AI path `[A, B, C_leaf]`
- **WHEN** a supervisor submits a manual reclassification with the same path `[A, B, C_leaf]`
- **THEN** the disposition SHALL remain `C_leaf`, `DisputeState = Resolved`, a `DisputeResolved` event emitted with `Confirmed = true`

### Requirement: Autonomous worker gated on TypificationAi license feature
The autonomous-commit worker SHALL be activated only when both `AdvancedTypification` AND
`TypificationAi` Pro license features are active for the tenant. The GDPR attestation
requirement is additional and non-waivable regardless of license state. An unlicensed tenant
with the autonomous config flags set SHALL have the worker silently skip their wrap-ups.

#### Scenario: Unlicensed tenant is skipped by the worker
- **GIVEN** a tenant does not hold the `TypificationAi` license feature
- **WHEN** the leader worker evaluates pending wrap-ups for that tenant
- **THEN** the worker SHALL skip all wrap-ups for that tenant without committing any disposition

#### Scenario: Licensed + attested tenant is processed
- **GIVEN** a tenant holds `AdvancedTypification` + `TypificationAi` features AND has a valid GDPR attestation AND `typification:ai:autonomous = true`
- **WHEN** a wrap-up has been idle beyond `AutonomousThreshold` with AI confidence above the configured threshold
- **THEN** the worker SHALL run the verification pass and, if passing, commit the disposition

## Architectural Risk

**Level:** HIGH

**Affected:**
- `Verbara.Platform.Typification` — new worker, policy, dispute service, state machine extension
- `Verbara.Platform.Api` — new endpoints, DTOs (must be in `ApiJsonContext`), AOT compatibility
- `Storage.Postgres` — schema migration on `typification_submissions` + tenant attestation table
- `Verbara.Sdk.Pro` — `IClusterLeaderElection` / `IDistributedLock` consumed (no new surface, but leader-election correctness is critical)
- GDPR / legal — regulatory surface; incorrect opt-in flow or missing dispute path is a compliance violation

**Mitigation:**
- Leader-election correctness: use the existing Pro `IDistributedLock` with a short-TTL renewable lease; worker acquires on startup and re-checks before each commit batch.
- Dispute state machine: enforce state transitions in the domain layer (not endpoint layer); unauthorized state transitions return 409 before any DB write.
- AOT: all new DTOs registered in `ApiJsonContext`; worker serialization uses source-gen contexts; zero `IL2026`/`IL3050` diagnostics in the publish step.
- GDPR: ship attestation + contest + audit before activating the worker in any production tenant; legal review required before any production activation.
- Cross-repo: no SDK changes required; Platform.Web dispute affordance is tracked separately.
