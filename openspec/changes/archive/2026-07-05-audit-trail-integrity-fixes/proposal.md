---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Compliance / tenants relying on audit-trail integrity
decision_ref: Platform/ADR-0034
---

# Proposal: audit-trail-integrity-fixes

## Why

Four audit-trail integrity defects were identified at the typification-autonomous-disposition ship
(2026-07-01) and deferred; until now they were tracked only in session memory. This change is their
OpenSpec backlog home (per the standing "OpenSpec is the tracking home" instruction). All four are
grounded in current `main`:

1. **Correction endpoint 2-write window** — `POST /{id}/typification-correction`
   (`ConversationEndpoints.cs:60`, handler ~`:832`) persists the correction via
   `ITypificationSubmissionCorrectionStore` and records the audit event in **two separate,
   non-atomic writes**. A crash between them yields a correction with no audit trail (or vice
   versa) — unacceptable for an autonomous-disposition correction path whose whole justification
   is auditability.
2. **GDPR purge preview under-reports** — `GdprPurgeService.PreviewUserPurgeAsync`
   (`GdprPurgeService.cs:162`) hard-codes `AuditTrailCount: 0`, so the Art. 17 purge preview
   always claims zero audit rows will be affected regardless of reality.
3. **RecordAudit actor attribution is sub-only** — the `RecordAudit` helper
   (`TypificationEndpoints.cs:752`) resolves `actorId` as `FindFirst("sub") ?? "system"`. API-key
   callers (no `sub`) are recorded as `"system"` and impersonated sessions record the wrong
   principal — a recurrence of the claim-order bug class fixed for rate limiting in v2.14.1.
4. **RetainUntil is outside the integrity hash** — `DefaultAuditService.ComputeIntegrityHash`
   (`DefaultAuditService.cs:108`) covers `tenantId|actorType|actorId|action|targetType|targetId|occurredAt|metadata`
   but NOT `RetainUntil`; a retention-date mutation is undetectable by hash verification, which
   defeats the retention guarantee the hash is meant to protect.

## What Changes

- Make the typification-correction write + its audit record atomic (single transaction or
  transactional outbox — decided at design time).
- `PreviewUserPurgeAsync` returns the real audit-trail count for the user.
- `RecordAudit` resolves the actor through the same canonical resolution used by the
  rate-limiter/impersonation fix (API-key identity and impersonation-aware), across all its
  call-sites (~9 in `TypificationEndpoints.cs`, plus `ReasonHintEndpoints.cs` and
  `ConversationEndpoints.cs`).
- Include `RetainUntil` in the integrity-hash input for newly written entries, with a
  versioned-hash strategy so existing rows still verify (hash-scheme discriminator, decided at
  design time).

## Capabilities

### New Capabilities

- `audit-trail-integrity`: atomicity, actor attribution, preview accuracy, and tamper-evidence
  guarantees of the platform audit trail.

### Modified Capabilities

(none — the four fixes are additive guarantees; no existing living-spec requirement changes)

## Impact

`Verbara.Platform.Api` (ConversationEndpoints, TypificationEndpoints, ReasonHintEndpoints,
GdprPurgeService), `Verbara.Platform.Audit` (DefaultAuditService, AuditEntry),
`Verbara.Platform.Storage.Postgres` (PostgresAuditStore). Tests must follow the
`test-determinism` fences (no wall-clock races). Live-DB Postgres tests required — InMemory hid
the Art. 17 redaction inertness once already.

## Architectural Risk

**Level:** MEDIUM — touches the audit substrate that compliance relies on; the hash-versioning
must not invalidate existing rows. **Affected:** Api endpoints, Audit, Storage.Postgres.
**Mitigation:** versioned hash discriminator (old rows verify under the old scheme); atomicity via
the existing transaction seam in the Postgres store; characterize current preview/actor outputs
before changing them; live-DB tests per the ADR-0034 lesson.
