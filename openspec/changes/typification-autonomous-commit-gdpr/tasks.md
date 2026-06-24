## 1. Spec the GDPR Art. 22 compliance surface

- [ ] 1.1 Identify all data-subject rights touch points: opt-in attestation, contest, audit retention; document constraints in a research note
- [ ] 1.2 Define `tenant_gdpr_attestations` table schema (tenant_id, attested_by_user_id, attested_at, revoked_at, revoked_by_user_id)
- [ ] 1.3 Define `TypificationDisputeState` enum: `None`, `Pending`, `Resolved`
- [ ] 1.4 Specify `PATCH /conversations/{id}/typification-dispute` request/response contract and error codes (`DisputeNotAllowed`, `DisputeAlreadyPending`)
- [ ] 1.5 Specify `POST /tenants/{id}/typification-gdpr-attestation` and `DELETE /tenants/{id}/typification-gdpr-attestation` contracts
- [ ] 1.6 Obtain legal sign-off on the opt-in attestation flow, dispute path, and audit retention policy before shipping to production

## 2. Storage migration (Phase A foundation — batch with 3.x)

- [ ] 2.1 Write Postgres migration: add `autonomous_actor_id TEXT`, `dispute_state SMALLINT NOT NULL DEFAULT 0`, `disputed_at TIMESTAMPTZ`, `dispute_resolved_at TIMESTAMPTZ` to `typification_submissions`
- [ ] 2.2 Write Postgres migration: create `tenant_gdpr_attestations` table (columns per 1.2)
- [ ] 2.3 Extend `TypificationSubmission` row type with the new columns and update `Map(NpgsqlDataReader)` accordingly (no Dapper; NpgsqlDataReader getters only)
- [ ] 2.4 Add `TenantGdprAttestation` row type + `ITenantGdprAttestationRepository` interface + Postgres implementation
- [ ] 2.5 Run `dotnet test` — storage-layer tests green; zero warnings

## 3. Domain layer: autonomous policy + dispute service (Phase B — focused subagents)

- [ ] 3.1 Implement `IAutonomousCommitPolicy` with `ShouldCommitAsync(candidate)`: checks tenant GDPR attestation, license features (`AdvancedTypification + TypificationAi`), AI confidence ≥ `AutonomousThreshold`, wrap-up still in pending state
- [ ] 3.2 Implement autonomous verification pass: re-validate schema node is still a valid leaf, conversation is still in wrap-up state, confidence still ≥ threshold; return `VerificationResult` (pass | fail with reason)
- [ ] 3.3 Implement `TypificationDisputeService.OpenDisputeAsync(conversationId, actorId)`: guards (Source == AutoAi, DisputeState == None), transitions to `Pending`, emits `DisputeOpened` audit event
- [ ] 3.4 Implement `TypificationDisputeService.ResolveDisputeAsync(conversationId, actorId, newNodePath)`: guards (DisputeState == Pending), updates submission, transitions to `Resolved`, emits `DisputeResolved` (with `Confirmed` flag)
- [ ] 3.5 Unit tests for `IAutonomousCommitPolicy` (all gating paths: no attestation, no license, below threshold, happy path)
- [ ] 3.6 Unit tests for `TypificationDisputeService` (all state-machine transitions + error paths)
- [ ] 3.7 Run `dotnet test` — domain tests green; zero warnings

## 4. Leader-gated autonomous-commit worker (Phase B — focused)

- [ ] 4.1 Implement `AutonomousCommitWorker` as `IHostedService`; acquire leader lease via Pro `IDistributedLock` on startup; renew on each heartbeat
- [ ] 4.2 Heartbeat loop: query candidates (idle > `AutonomousThreshold`, tenant attested + licensed + autonomous flag), run `IAutonomousCommitPolicy`, run verification pass, commit or emit `AutonomousSkipped`
- [ ] 4.3 On commit: write disposition with `Source = SubmissionSource.AutoAi`, `AutonomousActorId = "verbara:ai:autonomous-worker"`, emit `AutonomousCommit` audit event (actor, tenant, conversation, node path, confidence, UTC, node ID)
- [ ] 4.4 Register `AutonomousCommitWorker` in DI only when `typification:ai:autonomous` is enabled (feature-flag guard in `Program.cs`)
- [ ] 4.5 Integration test: leader commits, non-leader skips, leader-loss mid-cycle leaves candidates pending
- [ ] 4.6 Run `dotnet test` — worker integration tests green; zero warnings

## 5. API endpoints + DTOs (Phase B — focused)

- [ ] 5.1 Add `PATCH /conversations/{id}/typification-dispute` to `ConversationEndpoints.cs`; authorize `Authenticated` + optional `typification:contest` permission; delegate to `TypificationDisputeService.OpenDisputeAsync`; return `TypificationDisputeResponse`
- [ ] 5.2 Add `POST /tenants/{id}/typification-gdpr-attestation` and `DELETE /tenants/{id}/typification-gdpr-attestation` to `TenantEndpoints.cs`; authorize `AdminOnly`
- [ ] 5.3 Add `PUT /conversations/{id}/typification-dispute/resolve` for supervisor reclassification; authorize `typification:resolve-dispute` permission; delegate to `TypificationDisputeService.ResolveDisputeAsync`
- [ ] 5.4 Define all new DTOs as typed sealed records: `TypificationDisputeResponse`, `GdprAttestationRequest`, `GdprAttestationResponse`; register in `ApiJsonContext`
- [ ] 5.5 Add `typification:contest` and `typification:resolve-dispute` to RBAC permission registry; assign to appropriate role templates (Agent = contest; Supervisor/Admin = both)
- [ ] 5.6 Api-layer tests for all new endpoints (happy path + error codes + unauthorized)
- [ ] 5.7 Run `dotnet test` — Api tests green; zero warnings

## 6. Audit record append-only enforcement

- [ ] 6.1 Verify that existing audit storage implementation rejects DELETE/UPDATE on `AutonomousCommit` and `DisputeOpened`/`DisputeResolved` event types; add guard if absent
- [ ] 6.2 Test that `DELETE /audit/{id}` returns 405 or 403 for autonomous-commit event records

## 7. Integration + AOT validation (Phase C — batch)

- [ ] 7.1 `dotnet build -warnaserror` — zero warnings across Platform solution
- [ ] 7.2 Native AOT publish: `dotnet publish -c Release -r linux-x64` — zero `IL2026`/`IL3050`/`IL207x` diagnostics; confirm new DTOs are in `ApiJsonContext` source-gen context
- [ ] 7.3 Full `dotnet test Verbara.Platform.slnx` — all tests green
- [ ] 7.4 Manual E2E smoke: configure autonomous + attestation → idle conversation past threshold → worker commits → audit record present; contest → wrap-up reopened; supervisor resolves → `DisputeResolved` audit record present
- [ ] 7.5 Verify unlicensed tenant: no autonomous commit occurs, worker emits `AutonomousSkipped`
- [ ] 7.6 Verify non-attested tenant: no autonomous commit occurs, worker emits `AutonomousSkipped` with reason `GdprAttestationMissing`
- [ ] 7.7 CI green (all checks pass, coverage ratchet not regressed)
