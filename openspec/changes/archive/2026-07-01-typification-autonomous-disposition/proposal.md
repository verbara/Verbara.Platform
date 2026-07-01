---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
decision_ref: Platform/ADR-0034
---

## Why

The deferred E5 autonomous-commit capability was originally framed (`typification-autonomous-commit-gdpr`,
0/40) as a **GDPR Art. 22** regime: a new leader-gated worker auto-closing abandoned wrap-ups, gated by a
tenant opt-in *attestation* (as Art. 22 consent), a data-subject *right-to-contest* endpoint, a
*dispute/reopen* path, and *append-only* audit. A grounding + framing pressure-test
([Platform/ADR-0034](../../../docs/decisions/0034-autonomous-typification-disposition.md)) invalidated that
framing on two counts:

1. **The abandoned-wrap-up auto-close already exists.** `ConversationTimeoutWorker.ProcessWrapUpTimeoutsAsync`
   already transitions idle `WrapUp` conversations to `Closed` after `DefaultWrapUpTimeoutSeconds` — but
   writes **no disposition**. So the only marginal value left is making that *existing* close optionally
   **stamp a disposition** (neutral, or an AI high-confidence suggestion). This is **AI analytics
   enrichment of an existing path**, not new abandonment machinery — a separate worker would duplicate it.
2. **GDPR Art. 22 does not apply.** A typification is internal interaction call-coding for the controller's
   analytics/routing; it produces no legal or similarly-significant effect on the data subject (the end
   contact). The tenant attestation cannot be an Art. 22 *consent* (consent is the data subject's, not a
   third-party tenant admin's); "append-only forever" is itself unlawful under Art. 5(1)(e)/Art. 17; and
   the data-subject dispute endpoint is unreachable by a login-less contact. Verbara is the **processor**;
   the tenant is the **controller** who owns any genuine Art. 22 duty.

This change therefore ships **autonomous AI disposition enrichment** of the existing close path — governed
by Art. 5 (accuracy, purpose limitation) + Art. 13–14 transparency — with the corrections ADR-0034 mandates.

## What Changes

- **Disposition stamping on the existing auto-close:** extend `ConversationTimeoutWorker.ProcessWrapUpTimeoutsAsync`
  so that, when it auto-closes an abandoned wrap-up, it MAY record a `TypificationSubmission`:
  for tenants who opt in (gate + license + a pending high-confidence AI suggestion), the AI suggestion is
  stamped `Source = SubmissionSource.AutoAi` by an AI actor. Default behaviour (no opt-in) is unchanged
  (close without a disposition). **No new worker.**
- **Per-tenant activation gate (controller instruction, NOT consent):** a `tenant_autonomous_disposition`
  record `(tenant_id, attested_by_user_id, attested_at, revoked_at, revoked_by_user_id)` that a privileged
  admin sets to enable autonomous stamping; it is a documented controller instruction + config gate,
  re-checked inside the commit. Default OFF.
- **Verification pass + conditional (CAS) commit:** before stamping, re-verify (node still a valid leaf,
  conversation still in `WrapUp`, confidence still ≥ `AutonomousThreshold`); commit via a conditional
  `UPDATE … WHERE state = 'WrapUp' AND version = @observed` so a concurrent human typify wins.
- **AI-actor audit + time-bounded retention:** emit an AI-actor audit event (actor type `ai`, confidence,
  node path); records are tamper-evident (existing integrity hash) but carry a **retention floor** the
  `AuditRetentionService` purge honours (fix the blanket-purge conflict) and are redacted on Art. 17 erasure.
- **Append-only operator correction (no reopen, no data-subject dispute):** a supervisor MAY correct an
  auto-stamped disposition within a bounded window via a **new corrective submission** referencing the
  immutable original; the conversation is **not** transitioned back to `WrapUp`.
- **Dark rollout:** per-tenant flag OFF by default, a global circuit breaker (config, no redeploy), a
  per-tenant rate cap, poison-candidate back-off to a terminal skip, and observability metrics.

**Out of scope (deferred, per ADR-0034):** bulk human-confirmed AI disposition ("assisted ceiling", α);
per-category autonomous whitelist (β); the dispute-rate → calibration auto-pause feedback loop (metrics
emitted in v1, action deferred); Platform.Web surfaces.

## Capabilities

### New Capabilities

- `typification-autonomous-disposition`: AI disposition enrichment of the existing abandoned-wrap-up
  auto-close, with a per-tenant controller-instruction gate, verification + CAS commit, AI-actor audit with
  time-bounded retention, and append-only operator correction. Dark by default.

### Modified Capabilities

<!-- No existing openspec/specs/ entries are modified. The audit-retention purge floor is an implementation
     adjustment to AuditRetentionService (no living spec governs it yet). -->

## Impact

- **Verbara.Platform.Api** — extend `ConversationTimeoutWorker` (disposition stamping on wrap-up close);
  `DistributionOptions` (+ autonomous-disposition options, code-overridable per the C3 test-determinism
  convention); circuit-breaker + rate-cap; `AuditRetentionService` retention-floor honouring; new admin
  endpoints for the activation gate + supervisor correction; new DTOs in `ApiJsonContext`.
- **Verbara.Platform.Typification** — extend `TypificationSubmission` with autonomous/correction fields;
  a `CorrectionState` enum; an `IAutonomousDispositionPolicy` (gate + license + confidence + verification).
- **Storage.Postgres** — migration **014**: `typification_submissions` correction/autonomous columns;
  new `tenant_autonomous_disposition` table; `audit_entries` retention-floor column.
- **Verbara.Sdk.Pro** — `IClusterLeader.IsLeader` already consumed by `ConversationTimeoutWorker`
  (it runs on the existing leader-gated path); no new Pro surface.
- **GDPR / legal** — superseded: no "legal sign-off" blocker. ADR-0034 + a DPIA-lite document the Art. 22
  non-applicability stance; the controller discharges any genuine duty.
- **Cross-repo:** Platform only. Platform.Web correction affordance is a deferred follow-on.

## Architectural Risk

**Level:** MEDIUM (down from HIGH — no new worker, no Art. 22 legal surface, dark by default).

**Affected:** `ConversationTimeoutWorker` (behaviour change on the close path — must stay byte-identical
when the gate is OFF); `TypificationSubmission` + storage (additive migration 014); `AuditRetentionService`
(retention-floor logic — a regression here could destroy or over-retain records); AOT (new DTOs in
`ApiJsonContext`).

**Mitigation:** gate OFF by default with a byte-identical no-op path (locked by a defaults test); CAS
conditional commit so a human typify always wins the race; retention-floor regression test (CI has no
live-DB test on the purge path today); verification pass before every stamp; per-tenant rate cap + global
circuit breaker; all new DTOs registered in `ApiJsonContext`, zero `IL2026`/`IL3050` in publish.
