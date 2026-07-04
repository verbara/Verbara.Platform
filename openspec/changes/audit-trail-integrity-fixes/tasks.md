# Tasks — audit-trail-integrity-fixes

## 1. Grounding

- [ ] 1.1 Confirm the correction 2-write window in `ConversationEndpoints.CorrectTypification`
      (order of store write vs audit write; transaction seam available?)
- [ ] 1.2 Determine which audit rows a user purge actually deletes (linkage for the preview count)
- [ ] 1.3 Inventory every `RecordAudit`-style call-site (TypificationEndpoints ~9,
      ReasonHintEndpoints, ConversationEndpoints) and the canonical actor resolution from the
      v2.14.1 fix
- [ ] 1.4 Confirm `PostgresAuditStore` schema headroom for a hash-scheme discriminator

## 2. Implementation

- [ ] 2.1 Atomic correction + audit write (single transaction; InMemory mirror)
- [ ] 2.2 Real `AuditTrailCount` in `PreviewUserPurgeAsync`
- [ ] 2.3 Canonical actor resolution helper + route all call-sites through it
- [ ] 2.4 Versioned integrity hash including `RetainUntil` (old rows verify unchanged)

## 3. Verification

- [ ] 3.1 Unit + live-DB Postgres tests per fix (deterministic per test-determinism fences)
- [ ] 3.2 `dotnet test` green, CI green, zero warnings (TreatWarningsAsErrors)
- [ ] 3.3 Characterization: existing audit rows still hash-verify after 2.4
