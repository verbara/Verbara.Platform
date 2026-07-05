# ADR-0034: Autonomous typification disposition — AI quality enrichment of an existing close path, not a GDPR Art. 22 regime

- **Status:** Accepted
- **Date:** 2026-06-30
- **Supersedes/extends:** restructures the `typification-autonomous-commit-gdpr` OpenSpec change (GRANDE, 0/40 tasks), which this ADR re-frames and re-scopes.
- **Related:** ADR-0022 (Native AOT, no Dapper), ADR-0029 (typification cascading/conditional/AI module), ADR-0032 (PlatformLlm entitlement cutoff), the GDPR Art. 17/20 surface (Plan 30C — `IGdprExportService`/`IGdprPurgeService`/`AuditRetentionService`).

## Context

The `typification-autonomous-commit-gdpr` change proposes shipping the deferred **E5 autonomous-commit
worker**: a leader-gated singleton that auto-closes abandoned conversation wrap-ups by committing an
AI-suggested **typification** (an internal disposition/outcome code) *without human review*, wrapped in a
full **GDPR Art. 22** compliance regime — a per-tenant **opt-in attestation**, a **right-to-contest**
endpoint, a **dispute/reopen** path, and **append-only** AI-actor audit records. It was written right
after milestone P2b; it is at 0/40 tasks.

Per the team's standing rule (pressure-test the framing before load-bearing decisions —
`feedback_pressure_test_option_framing`), the change was grounded against the current code (7 read-only
agents) and its framing pressure-tested (legal / distributed-correctness / product-scope lenses + a
completeness critic). Two findings invalidate the change's spine.

### Finding 1 — the abandoned-wrap-up auto-close **already exists**

`ConversationTimeoutWorker.ProcessWrapUpTimeoutsAsync` (src/Verbara.Platform.Api/Services/) already lists
conversations in `WrapUp`, and after `DefaultWrapUpTimeoutSeconds` of inactivity **transitions them to
`Closed`** and publishes `ConversationStateChangedEvent` — but writes **no typification**. So abandoned
wrap-ups do *not* rot; they are auto-closed today, just **without a disposition label**.

Therefore the entire marginal value of the proposed change collapses to **one thing**: making that
existing auto-close, instead of closing *blank*, optionally **stamp a disposition** — neutral (non-AI) or,
for tenants who opt in, the AI's high-confidence suggestion. This is **AI analytics enrichment of an
existing close path**, not new abandonment machinery. A separate `AutonomousCommitWorker` would *duplicate*
the existing worker's loop, leader-gating, and candidate query.

### Finding 2 — GDPR Art. 22 does not apply; the change is mis-framed

Art. 22 bites only on a decision "based **solely** on automated processing … which produces **legal
effects** … or **similarly significantly affects**" the **data subject** (here, the end *contact* — not the
tenant, not the agent). A typification is internal **interaction call-coding** for the controller's
analytics/routing/reporting; per WP29/EDPB guidance a "significant effect" approaches the legal (denial of
credit, automated contract termination, e-recruitment). Tagging a closed, abandoned conversation
`Sales > Upgrade > Completed` for internal reporting does none of this, and the contact typically never
sees it. **Best judgment: Art. 22 does not apply.** The change builds a heavyweight Art. 22 regime around
processing that falls outside Art. 22 — simultaneously **over-built** (attestation/contest/reopen
machinery) and, were it to apply, **under-built** (no transparency/notification, no data-subject-initiated
review).

Three corollaries:

1. **The tenant attestation cannot be an Art. 22 legal basis.** Art. 22(2)(c) consent is the **data
   subject's** to give; a tenant admin is a third party. Verbara is the **processor** (Art. 28); the tenant
   is the **controller** (Art. 4(7)). The attestation is properly a **documented controller instruction**
   (DPA / Art. 28(3)(a)) + a **configuration gate** — never "consent."
2. **"Append-only / never deletable" is itself unlawful.** An audit record carrying actor, confidence,
   node path and conversation linkage is personal data; Art. 5(1)(e) storage limitation + Art. 17 forbid
   indefinite retention. The existing `AuditRetentionService` (~12-month purge) is correct *in principle*;
   the spec's "never delete" carve-out is the bug — though decision records must out-live the contest
   window (see Decision 4).
3. **The data-subject "dispute" endpoint is decorative.** The contact is a WhatsApp/SMS user with **no
   Verbara login** and cannot reach an authenticated API. "An agent disputes on their behalf" conflates
   processor staff with the data subject.

### Verified ground-truth (drift the implementer must not re-derive)

- `SubmissionSource.AutoAi`, `TypificationAiConfig.{Autonomous, AutonomousThreshold=0.95}` **exist**;
  `Autonomous` is **per-schema** (schema JSONB), not a global toggle, not per-tenant.
- `LicenseFeature.{AdvancedTypification, TypificationAi}` **exist** (Pro Licensing `[Flags]`, consumed at
  `ConversationEndpoints.cs:48`). There is **no license blocker** — the earlier "missing" reading confused
  `PlanFeature` (Core billing) with `LicenseFeature` (Pro).
- Leader-gating: the house pattern is `IClusterLeader.IsLeader` keyed by resource (5 workers already
  leader-gated); **not** an acquire/renew lease API. Next migration is **014** (not "010").
- Audit append-only is **structural** — there is **no** `DELETE /audit/{id}` endpoint; `IAuditStore` is
  insert/select + an internal `DeleteOlderThanAsync(tenantId, cutoff)` used only by `AuditRetentionService`
  (**no actor/action filter** — this is the conflict in Decision 4).
- `ConversationState.Abandoned = 51` already exists; GDPR Art. 17/20 already exist; Art. 22 does not.

## Decision

1. **Re-frame the work as "autonomous AI disposition enrichment," governed by Art. 5 (accuracy, purpose
   limitation) + Art. 13–14 transparency — NOT a GDPR Art. 22 consent regime.** The autonomous mode is the
   far end of the existing opt-in automation gradient (`Manual → SuggestOnly → AutoFill → Autonomous`); it
   enriches an interaction-coding taxonomy, it does not make a significant decision about a person.

2. **Reuse the existing close path; do not build a new worker.** Extend
   `ConversationTimeoutWorker.ProcessWrapUpTimeoutsAsync` so that, when it auto-closes an abandoned wrap-up,
   it MAY record a disposition: a configurable neutral leaf (non-AI), or — for tenants with `Autonomous` +
   `LicenseFeature.AdvancedTypification|TypificationAi` + a high-confidence pending suggestion — the AI
   suggestion stamped `Source = SubmissionSource.AutoAi` with an AI-actor audit event. No 6th leader-gated
   worker; the existing worker already runs the loop.

3. **Keep the per-tenant attestation as a controller-instruction config gate, never as "consent."** It
   records that the tenant (controller) has instructed Verbara (processor) to enable autonomous stamping
   and warrants its own lawful basis/DPIA. Persist `(tenant_id, attested_by_user_id, attested_at,
   revoked_at, revoked_by_user_id)`. It gates activation; it is not a data-subject legal basis. Re-check it
   inside the same atomic write as the commit (no revocation race).

4. **Retention is time-bounded and tamper-evident, never infinite.** AI-actor disposition/audit records
   are integrity-hashed (already are) but carry a **retention floor tied to the correction window**
   (Decision 5) and are then purged. Fix the `AuditRetentionService` conflict by honouring a per-record
   retention floor (e.g. a `retain_until` the blanket purge respects) so the sweep cannot destroy a record
   still inside its window — with a regression test (CI has no live-DB test on this path today). On a valid
   Art. 17 erasure, **redact the contact linkage but retain the decision-fact** (Art. 17(3) defence/legal
   exemption).

5. **Correction is append-only; there is no data-subject dispute endpoint and no `Closed→WrapUp` reopen.**
   The original autonomous `TypificationSubmission` stays **immutable**. A correction by an operator
   supervisor (the realistic actor) creates a **new corrective submission** referencing it; the
   conversation is **not** transitioned back to `WrapUp` (which would strand SLA timers, already-emitted
   analytics, already-charged AI-credit metering, queue routing, and fired webhooks). A bounded correction
   window applies; outside it, correction is closed. Operator correction reuses the normal reclassification
   surface where possible.

6. **Commit safely: verification pass + conditional (CAS) write + dark rollout.** Before stamping, re-verify
   (schema node still a valid leaf, conversation still in `WrapUp`, confidence still ≥ threshold). The
   commit is a single conditional `UPDATE … WHERE state = 'WrapUp' AND version = @observed` so a concurrent
   human typify wins (worker affects 0 rows and aborts) — leader-gating serialises workers, not the human
   API path. Ship behind a **per-tenant flag defaulting OFF**, a **global circuit breaker** (config, no
   redeploy), and a **per-tenant rate cap**. Chronically-failing candidates move to a terminal
   `AutonomousSkipped` (emit once, not every cycle); idempotency keyed on `(conversation_id, version)`.

7. **Kill the "obtain legal sign-off" task.** It is intractable for a solo operator and would either block
   forever or be rubber-stamped (the shortcut the team forbids). Replace it with **this ADR + a DPIA-lite**
   ([`docs/research/2026-07-01-dpia-lite-autonomous-typification-disposition.md`](../research/2026-07-01-dpia-lite-autonomous-typification-disposition.md))
   recording the processing, the data subject, and the Art. 22 non-applicability argument, plus a kill-switch.
   The Art. 22 exposure, if any, is the **controller's**; Verbara provides tooling and documents it.

8. **EU AI Act note (not GDPR Art. 22).** An AI system that classifies contact-center interactions is
   plausibly **limited/minimal-risk** under the EU AI Act, not a prohibited or high-risk system (it is not
   biometric categorisation, not credit/employment scoring). Transparency (tenants/agents know AI stamped
   the disposition — satisfied by the AI-actor audit + provenance already shipped) is the main obligation.
   Recorded here so it is considered, not silently assumed; revisit if scope changes.

## Rejected

- **The GDPR Art. 22 regime as the design's spine** — mis-applied to internal call-coding (Context,
  Finding 2). Kept only as a documented *non-applicability* argument.
- **A new `AutonomousCommitWorker`** — duplicates `ConversationTimeoutWorker`, which already owns the
  abandoned-wrap-up close (Finding 1, Decision 2).
- **The data-subject `PATCH /conversations/{id}/typification-dispute` endpoint** — unreachable by the
  actual data subject; the realistic need is operator-side correction (Decision 5).
- **`Closed → WrapUp` reopen on contest** — destructive mutation of a terminal state with stranded
  downstream effects (Decision 5).
- **Append-only / never-deletable audit** — violates Art. 5(1)(e)/Art. 17 (Decision 4).
- **Tenant attestation as Art. 22 "consent"** — invalid legal basis; re-purposed as controller instruction
  (Decision 3).

## Deferred (not rejected)

- **Bulk human-confirmed AI disposition ("assisted ceiling," option α)** — a supervisor review queue that
  pre-fills AI suggestions for idle wrap-ups and lets a human bulk-confirm. Keeps a human in the loop (never
  "solely automated") and is likely the ceiling most tenants want; a strong *separate* product feature, not
  blocking this change.
- **Per-category autonomous whitelist (option β)** — allow fully-automatic stamping only on tenant-marked
  low-stakes leaves (`Wrong number`, `Spam`, `Abandoned`); a refinement of Decision 2.
- **Dispute-rate feedback loop** — auto-pause a tenant's autonomous mode when the corrective-overturn ratio
  breaches a threshold, closing the loop to the existing `AutonomousReady` calibration gate. Emit the
  metrics in v1; act on them later.
- **Platform.Web surfaces** — agent/supervisor correction affordance + a "stamped by AI" indicator; tracked
  in that repo.

## Consequences

- **Positive:** the work shrinks from a GRANDE Art. 22 regime to a focused, correct enrichment of an
  existing path; no duplicated worker; the legal frame is documented and defensible; retention stays lawful;
  the no-reopen correction model avoids a class of downstream-corruption bugs; rollout is dark and
  reversible.
- **Negative:** tenants/contacts who *expected* a formal Art. 22 contest portal do not get one — accepted,
  because that portal was unreachable by the data subject and legally mis-aimed; the controller discharges
  any genuine Art. 22 duty.
- **Neutral:** no code ships from this ADR. It restructures the OpenSpec change into a single refocused
  change (Capa A enrichment, Decisions 2–7) with α/β/feedback/UI enumerated as deferred follow-ons, and is
  the durable home of the Art. 22 non-applicability stance the DPIA-lite expands.

## OpenSpec restructure (effect of this ADR)

The `typification-autonomous-commit-gdpr` change is rewritten to **`typification-autonomous-disposition`**:
proposal/spec/tasks re-scoped to Decisions 2–7 (extend the existing worker; attestation-as-gate; CAS +
verification; time-bounded retention + purge fix + regression test; append-only operator correction; dark
rollout). The Art. 22 contest/reopen requirements are removed; the deferred items above are listed as
out-of-scope follow-ons. This ADR is referenced from the change's `proposal.md`.

## Addendum (2026-07-05): audit-trail-integrity-fixes — 4 defects spawned at ship, now closed

Grounding the `typification-autonomous-disposition` ship (2026-07-01) against the shipped audit
substrate surfaced four integrity defects, tracked as their own OpenSpec change
(`audit-trail-integrity-fixes`, decision_ref Platform/ADR-0034) and shipped `e8d4a7b9` (PR#124,
archived). All four are closed:

1. **Correction-endpoint write atomicity** — `POST /{id}/typification-correction` persisted the
   correction, the submission upsert, and the audit record as **three** separate non-atomic writes
   (grounding found three, not the two originally assumed) — a crash mid-sequence could leave a
   correction with no audit trail. Fixed: all three now commit inside one Npgsql transaction.
2. **GDPR purge preview under-reporting** — `GdprPurgeService.PreviewUserPurgeAsync` hard-coded
   `AuditTrailCount: 0`, so the Art. 17 purge preview always claimed zero audit rows would be
   affected. Fixed: a real count via the new `IAuditStore.CountByActorAsync`.
3. **Actor attribution recurrence of the claim-order bug class** — `RecordAudit` in
   `TypificationEndpoints.cs` and `ReasonHintEndpoints.cs` resolved `actorId` via a `sub`-only
   lookup, mis-attributing API-key callers and impersonated sessions to `"system"` (the same bug
   class as the v2.14.1 rate-limiter fix, `reference_typification_discovered_bugs`). Fixed: both
   call-sites now route through the shared `CallerIdentity` resolver (`Endpoints/Shared/`)
   alongside every other call-site that already used the correct precedence.
4. **`RetainUntil` outside the integrity hash** — `DefaultAuditService.ComputeIntegrityHash` did not
   cover `RetainUntil`, so a retention-date mutation was undetectable by hash verification. Fixed: a
   `v2:`-prefixed versioned hash scheme covers `RetainUntil` for newly written entries; existing rows
   continue to verify under the original (`v1`) scheme — no backfill, no invalidation.

Verified live-DB (Postgres Testcontainers, not just InMemory — the ADR-0034 lesson that InMemory hid
the Art. 17 redaction inertness applies here too): 186/186 Storage.Postgres tests green, plus
1511+55+272+126 unit tests across Api/Audit/Storage.InMemory/Typification, zero warnings.
