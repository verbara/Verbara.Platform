---
tier: GRANDE
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

The autonomous-commit E5 worker (auto-closes abandoned wrap-ups without human review) was deliberately
deferred after P2b: the configuration substrate (`AutonomousThreshold`, `typification:ai:autonomous`,
write-gate) is built and tested, but neither the leader-gated consumer nor the GDPR Art. 22 compliance
surface exists. Shipping a fully-automated disposition decision against a data subject without a
tenant opt-in attestation, a right-to-contest mechanism, and a dispute/reopen path would constitute
a non-compliant automated decision under GDPR Art. 22 — a legal blocker that supersedes the
worker-mechanics work.

## What Changes

- **Leader-gated autonomous-commit worker:** a singleton leader (distributed lock via Pro Cluster)
  consumes the existing `AutonomousThreshold` + `typification:ai:autonomous` config to close
  abandoned wrap-ups; runs a verification pass before each commit (schema still valid, conversation
  still in the expected state, AI confidence still above threshold).
- **AI-actor audit record:** every autonomous disposition MUST emit a structured audit event that
  records the AI actor identity, the confidence score, the chosen node path, the timestamp, and the
  tenant ID.
- **GDPR Art. 22 opt-in attestation:** tenants MUST explicitly attest acceptance of automated
  decision-making before the autonomous worker activates for their conversations; a new
  `typification:ai:gdpr-autonomous-optin` flag + attestation timestamp persisted per tenant.
- **Right-to-contest surface:** a `PATCH /conversations/{id}/typification-dispute` endpoint allows
  the data subject (or a human agent acting on their behalf) to contest an autonomous disposition;
  the system SHALL reopen the wrap-up to manual review and record the dispute event.
- **Dispute/reopen path:** contested dispositions enter a `DisputedAutonomous` sub-state; a human
  supervisor or agent completes manual reclassification; resolution closes the dispute and emits a
  second audit event crediting the human actor.

**Compliance is the gating concern.** The worker mechanics depend on the GDPR surface being fully
shipped and tested first.

## Capabilities

### New Capabilities

- `typification-autonomous-disposition`: Leader-gated autonomous-commit worker consuming the existing
  autonomous config substrate, with GDPR Art. 22 opt-in attestation, right-to-contest, dispute/reopen
  path, and AI-actor audit events.

### Modified Capabilities

<!-- No existing openspec/specs/ entries are modified — the autonomous-commit path was not previously
     specified (only stubbed). -->

## Impact

- **Verbara.Platform.Typification** — new `AutonomousCommitWorker`, `IAutonomousCommitPolicy`,
  `TypificationDisputeService`; extends `TypificationSubmission` with `AutonomousActorId`,
  `DisputeState`, `DisputedAt`.
- **Verbara.Platform.Api** — new endpoint `PATCH /conversations/{id}/typification-dispute`;
  `TypificationEndpoints.cs`; new DTOs in `ApiJsonContext`.
- **Storage.Postgres** — migration: `typification_submissions` adds `autonomous_actor_id`,
  `dispute_state`, `disputed_at`; new `tenant_gdpr_attestations` (or extend `tenant_settings`).
- **Verbara.Sdk.Pro (Cluster)** — distributed-lock lease API used by the leader-gated worker
  (no new Pro surface needed; existing `IClusterLeaderElection` / `IDistributedLock` consumed).
- **GDPR / legal review** — opt-in attestation flow, data-subject rights documentation, data
  retention for audit events; requires legal sign-off before shipping to production.
- **Cross-repo:** Platform only; no SDK changes. Platform.Web will surface the opt-in toggle (admin)
  and dispute affordance (agent/contact), tracked in that repo's own backlog.
