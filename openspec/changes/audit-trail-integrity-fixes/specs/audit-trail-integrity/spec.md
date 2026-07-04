# audit-trail-integrity — Delta

## ADDED Requirements

### Requirement: Correction and its audit record are atomic
The typification-correction endpoint (`POST /{id}/typification-correction`) SHALL persist the
correction and its audit record atomically: either both are durable or neither is. A fault between
the two writes SHALL NOT be able to produce a correction without an audit record.

#### Scenario: Crash between writes leaves no orphan correction
- **GIVEN** a valid correction request for an autonomous submission
- **WHEN** the process faults after the correction write but before the audit write would have completed
- **THEN** after recovery the store contains either both the correction and its audit record, or neither

### Requirement: GDPR purge preview reports the real audit-trail count
`PreviewUserPurgeAsync` SHALL return the actual number of audit-trail rows attributable to the user
that a purge would affect, not a constant.

#### Scenario: Preview counts existing audit rows
- **GIVEN** a user with 12 audit-trail rows in the tenant
- **WHEN** the purge preview is requested
- **THEN** the preview reports `AuditTrailCount = 12`

### Requirement: Audit actor attribution is canonical for all caller types
Audit records written via the endpoint `RecordAudit` helpers SHALL attribute the actor using the
platform's canonical actor resolution: an impersonated session SHALL record the impersonating
operator identity (not the impersonated subject alone), and an API-key caller SHALL record the
API-key identity (never a generic `"system"` fallback caused by a missing `sub` claim).

#### Scenario: API-key caller is attributed to the key identity
- **GIVEN** a management/API-key caller (no `sub` claim) mutates a typification schema
- **WHEN** the audit record is written
- **THEN** `actorId` identifies the API key (and `actorType` reflects it), not `"system"`

#### Scenario: Impersonated session is attributed to the operator
- **GIVEN** a platform operator impersonating a tenant user performs an audited action
- **WHEN** the audit record is written
- **THEN** the record attributes the impersonating operator per the canonical resolution (matching the v2.14.1 claim-order semantics)

### Requirement: RetainUntil is covered by the integrity hash
The audit-entry integrity hash SHALL cover `RetainUntil` for newly written entries, under a
versioned hash scheme: entries written before the scheme change SHALL still verify under their
original scheme, and a mutation of `RetainUntil` on a new entry SHALL be detectable.

#### Scenario: Retention-date tampering is detectable
- **GIVEN** an audit entry written under the new hash scheme
- **WHEN** its `retain_until` column is mutated directly in storage
- **THEN** hash verification for that entry fails

#### Scenario: Pre-existing entries still verify
- **GIVEN** an audit entry written before the hash-scheme change
- **WHEN** hash verification runs
- **THEN** the entry verifies under its original scheme (no mass invalidation)
