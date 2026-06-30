# Tasks — typification-autonomous-disposition

> Governed by **Platform/ADR-0034**. Execution: Subagent-Driven Development + FCM risk-weighted batching
> (Phase A foundation = batch; Phase B critical = focused subagents; Phase C integration = batch).
> .NET 10 Native AOT, TreatWarningsAsErrors, test naming `Method_ShouldExpected_WhenCondition`.

## 1. Design finalization (done in ADR + spec)

- [x] 1.1 ADR-0034 accepted (Art. 22 non-applicability; reuse existing close path; corrections)
- [x] 1.2 Proposal + delta spec re-scoped to ADR-0034 Decisions 2–7
- [ ] 1.3 Author a DPIA-lite note in `docs/research/` recording the processing, the data subject (end contact), and the Art. 22 non-applicability argument; link it from ADR-0034

## 2. Phase A — Storage foundation (batch)

- [x] 2.1 Write migration `014_autonomous_disposition.sql`: add to `typification_submissions` — `autonomous_actor_id TEXT NULL`, `correction_state SMALLINT NOT NULL DEFAULT 0`, `corrected_at TIMESTAMPTZ NULL`, `corrects_conversation_id TEXT NULL` (the original conversation this submission corrects); all idempotent (`ADD COLUMN IF NOT EXISTS`). No `row_version` column — the CAS commit is **state-based** (`WHERE state='WrapUp'`), which is also naturally idempotent (a re-attempt after success finds the conversation no longer `WrapUp`)
- [x] 2.2 In the same migration: create `tenant_autonomous_disposition` (`tenant_id TEXT NOT NULL`, `attested_by_user_id TEXT NOT NULL`, `attested_at TIMESTAMPTZ NOT NULL`, `revoked_at TIMESTAMPTZ NULL`, `revoked_by_user_id TEXT NULL`, `PRIMARY KEY (tenant_id)`); and add `retain_until TIMESTAMPTZ NULL` to `audit_entries`
- [x] 2.3 Add `CorrectionState` enum to `Verbara.Platform.Typification` (`None=0`, `Corrected=1`); extend `TypificationSubmission` record with `AutonomousActorId` (string?), `CorrectionState`, `CorrectedAt` (DateTimeOffset?), `CorrectsConversationId` (EntityId?)
- [x] 2.4 Update `PostgresTypificationSubmissionStore`: `SelectColumns`, `SubmissionRow` + static `Map(NpgsqlDataReader)`, INSERT params (explicit `NpgsqlDbType` on every nullable; no Dapper) for the new columns; update `InMemoryTypificationSubmissionStore` to mirror
- [x] 2.5 Add `TenantAutonomousDisposition` row type + `ITenantAutonomousDispositionStore` (Get/Upsert/Revoke) + Postgres + InMemory implementations
- [x] 2.6 Add `retain_until` to `AuditEntry` + audit store Map/INSERT (Postgres + InMemory); default NULL
- [x] 2.7 Run `dotnet test Verbara.Platform.slnx` — storage-layer tests green; zero warnings

## 3. Phase B — Domain policy (focused)

- [x] 3.1 Implement `IAutonomousDispositionPolicy.EvaluateAsync(conversation, suggestion, ct)`: checks activation gate (`ITenantAutonomousDispositionStore`), license (`AdvancedTypification`+`TypificationAi`), confidence ≥ `AutonomousThreshold`, conversation still `WrapUp`, node still a valid leaf in the active schema; returns a result discriminating `Commit` vs `Skip(reason)` where reason ∈ {`NoSuggestion`,`BelowThreshold`,`LicenseMissing`,`GateDisabled`,`SchemaMutation`,`NotWrapUp`}
- [x] 3.2 Unit tests for `IAutonomousDispositionPolicy` — one per gating path (gate off, unlicensed, below threshold, no suggestion, schema mutation, not-wrapup, happy path)
- [x] 3.3 Run `dotnet test` — policy tests green; zero warnings

## 4. Phase B — Close-path enrichment in ConversationTimeoutWorker (focused)

- [ ] 4.1 Add options to `DistributionOptions` (code-overridable, NOT config-bound, per C3): `AutonomousDispositionEnabled` (global breaker, default false), `AutonomousDispositionPerCycleCap` (default e.g. 50), `AutonomousCorrectionWindowDays` (default e.g. 30); lock defaults in `DistributionOptionsDefaultsTests`
- [ ] 4.2 Extend `ProcessWrapUpTimeoutsAsync`: when global breaker on AND tenant gate on, for each timed-out wrap-up resolve the pending suggestion, run `IAutonomousDispositionPolicy`; on `Commit` perform a **state-based conditional close** (a store method that closes the conversation only `WHERE state='WrapUp'`) that persists the `AutoAi` submission AND closes the conversation atomically — affecting 0 rows (clean skip) if a human already moved it out of `WrapUp`; on `Skip` close with no disposition (unchanged) and emit `AutonomousSkipped(reason)`. When the breaker/gate is OFF the method MUST be byte-identical to today
- [ ] 4.3 Emit `AutonomousCommit` AI-actor audit (actor type `ai`, actor id `verbara:ai:autonomous-worker`, node path, confidence, UTC) with `retain_until` set to `now + AutonomousCorrectionWindowDays` (floor); enforce per-cycle cap; idempotency is inherent in the state-based conditional close (a re-attempt finds the conversation no longer `WrapUp`); poison back-off → terminal skip emitting `AutonomousSkipped` once
- [ ] 4.4 Emit metrics: `autonomous_commits_total`, `autonomous_skipped_total{reason}`, `autonomous_corrections_total` (tenant-dimensioned)
- [ ] 4.5 Integration tests (no `FakeTimeProvider.Advance` on the loop — options-overridable real-ms interval + causal signal per C3): gate-off path byte-identical; opted-in+licensed+high-confidence stamps `AutoAi`; concurrent human typify wins the CAS race; schema mutation → `SchemaMutation` skip; unlicensed → `LicenseMissing` skip; per-cycle cap honoured
- [ ] 4.6 Run `dotnet test` — worker tests green; zero warnings

## 5. Phase B — AuditRetentionService retention floor (focused)

- [ ] 5.1 Update `IAuditStore.DeleteOlderThanAsync` (or its query) so the purge predicate respects `retain_until` (delete only where `retain_until IS NULL OR retain_until < now`); Postgres + InMemory
- [ ] 5.2 Add Art. 17 redaction path: on contact erasure, NULL/scrub contact-identifying columns on autonomous audit records while retaining the decision-fact (coordinate with `IGdprPurgeService`)
- [ ] 5.3 Regression test: a record within its `retain_until` survives a blanket purge against an older cutoff; a record past its floor is purged (InMemory store — CI has no live-DB purge test)
- [ ] 5.4 Run `dotnet test` — retention tests green; zero warnings

## 6. Phase B — API endpoints + DTOs + RBAC (focused)

- [ ] 6.1 Add activation-gate endpoints `POST /admin/typification/autonomous-disposition` and `DELETE /admin/typification/autonomous-disposition` (authorize `AdminOnly`); delegate to `ITenantAutonomousDispositionStore`; return 201 / 204
- [ ] 6.2 Add supervisor correction endpoint `POST /conversations/{id}/typification-correction` (authorize a new `typification:correct-autonomous` permission); guards (submission is `Source=AutoAi`, within `AutonomousCorrectionWindowDays`); persists a corrective submission referencing the original (original immutable; conversation stays `Closed`); 409 `CorrectionWindowExpired` / `NotAutonomous`
- [ ] 6.3 Define DTOs as typed sealed records — `AutonomousDispositionGateRequest`, `AutonomousDispositionGateResponse`, `TypificationCorrectionRequest`, `TypificationCorrectionResponse` — and register them in `ApiJsonContext`
- [ ] 6.4 Add `typification:correct-autonomous` permission to `PermissionSeeder.cs`; assign to Supervisor + Admin role templates in `RoleTemplateSeeder.cs`
- [ ] 6.5 Api-layer tests: gate set/revoke (happy + unauthorized); correction (happy + `CorrectionWindowExpired` + `NotAutonomous` + unauthorized)
- [ ] 6.6 Run `dotnet test` — Api tests green; zero warnings

## 7. Phase C — Integration + AOT validation (batch)

- [ ] 7.1 `dotnet build Verbara.Platform.slnx` — zero warnings (TreatWarningsAsErrors)
- [ ] 7.2 Native AOT publish `dotnet publish src/Verbara.Platform.Api -c Release -r linux-x64` — zero `IL2026`/`IL3050`/`IL207x`; confirm new DTOs are in `ApiJsonContext`
- [ ] 7.3 Full `dotnet test Verbara.Platform.slnx` — all green
- [ ] 7.4 Manual/E2E smoke: gate OFF → close unchanged (no submission); gate ON + licensed + high-confidence → `AutoAi` stamped + `AutonomousCommit` audit with `retain_until`; supervisor correction within window → corrective submission + conversation stays Closed; correction after window → 409
- [ ] 7.5 Verify retention purge preserves an in-window autonomous audit record (regression test green in CI)
- [ ] 7.6 CI green (Build+Unit Tests, Analyze, Dependency Review, Coverage Ratchet, CodeQL); coverage not regressed
