# Tasks — audit-trail-integrity-fixes

## 1. Grounding

- [x] 1.1 Confirm the correction 2-write window in `ConversationEndpoints.CorrectTypification`
      (order of store write vs audit write; transaction seam available?)
      — confirmed THREE separate writes (correction insert, submission UPSERT, audit insert), all
      via `Verbara.Sdk.Data.Npgsql`-backed stores sharing the same `NpgsqlDataSource`; a
      connection/transaction seam is available (mirrors `PostgresCreditLedgerStore`'s pattern).
- [x] 1.2 Determine which audit rows a user purge actually deletes (linkage for the preview count)
      — `PurgeUserDataAsync` does NOT currently delete/touch any `audit_entries` rows for the user
      at all (only auth events + the user record); the preview's correct linkage is "rows whose
      `actor_id` is the user" (mirrors `AuthEventCount`'s `ListAllByUserAsync` linkage), counted via
      a new `IAuditStore.CountByActorAsync`.
- [x] 1.3 Inventory every `RecordAudit`-style call-site (TypificationEndpoints ~9,
      ReasonHintEndpoints, ConversationEndpoints) and the canonical actor resolution from the
      v2.14.1 fix
      — canonical resolver is `ManagementImpersonationEndpoints.ResolveCallerUserId`
      (`user_id ?? NameIdentifier ?? sub`, PR #78). Confirmed the ONE true bug:
      `TypificationEndpoints.RecordAudit` (line 752) and `ReasonHintEndpoints.RecordAudit` used an
      inline `sub`-only lookup — every OTHER call-site in both files (`GetCallerUserId`,
      `ResolveCallerPermissions`, `ConversationEndpoints.GetCorrectingUserId`) already used the
      correct order inline. Extracted to `CallerIdentity` (`Endpoints/Shared/`) and routed ALL of
      them (plus `PlatformAdminAuthorizationHandler` and `ManagementImpersonationEndpoints`) through
      the one shared helper — no second order invented.
- [x] 1.4 Confirm `PostgresAuditStore` schema headroom for a hash-scheme discriminator
      — `integrity_hash` is a nullable `TEXT` column with no format constraint; chose an in-value
      scheme-discriminator PREFIX (`v2:`) over a new smallint column (zero migration risk, no schema
      change needed — the column already tolerates any string).

## 2. Implementation

- [x] 2.1 Atomic correction + audit write (single transaction; InMemory mirror)
- [x] 2.2 Real `AuditTrailCount` in `PreviewUserPurgeAsync`
- [x] 2.3 Canonical actor resolution helper + route all call-sites through it
- [x] 2.4 Versioned integrity hash including `RetainUntil` (old rows verify unchanged)

## 3. Verification

- [x] 3.1 Unit + live-DB Postgres tests per fix (deterministic per test-determinism fences)
- [x] 3.2 `dotnet test` green, zero warnings (TreatWarningsAsErrors) — verified locally for every
      affected project (Api.Tests 1511, Audit.Tests 55, Storage.InMemory.Tests 272,
      Storage.Postgres.Tests 186, Typification.Tests 126); CI-green left unchecked (not run here).
- [x] 3.3 Characterization: existing audit rows still hash-verify after 2.4
