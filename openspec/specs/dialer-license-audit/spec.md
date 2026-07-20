# dialer-license-audit Specification

## Purpose
Durable persistence of the dialer's license-enforcement episodes for compliance and forensics.
Pro's `DialerEngine` emits a tick-scoped `DialerLicenseAuditRecord` (quiesce / flap-absorb / recover)
through the optional seam `IDialerLicenseAuditSink` (`Verbara.Sdk.Pro.Dialer`); with no implementation
registered it resolved `null` and the record was silently dropped. This capability is Platform's
Postgres implementation of that seam — turning each episode into a durable `dialer_license_audit` row —
so that "which campaigns were torn down, when, under what license block reason, and how many calls were
in-flight" is answerable after the fact. Scope is the tick-scoped episode grain (decision_ref
Pro/ADR-0016); per-originate per-call denial attribution is a deferred cross-repo epic (verbara-meta
roadmap R-011b) requiring tenant/campaign identity plumbed through the Sdk's `OriginateAction`.
## Requirements
### Requirement: Durable dialer license-enforcement audit sink

Platform MUST implement the optional Pro seam `IDialerLicenseAuditSink` as a Postgres-backed
`PostgresDialerLicenseAuditSink` in `Verbara.Platform.Storage.Postgres`, so that a `DialerLicenseAuditRecord`
delivered by the Pro `DialerEngine` becomes a durable row instead of being silently dropped. The
implementation SHALL persist the record via `Verbara.Sdk.Data.Npgsql` (`NpgsqlExecutor` / `ExecuteAsync`,
explicit `NpgsqlParameter` binding, NO Dapper — Platform/ADR-0022), mirroring the canonical
`PostgresAuditStore` pattern. Every persisted column MUST map 1:1 onto a `DialerLicenseAuditRecord` field
as frozen by `fixtures/dialer-license-audit-record.v1.json`: `SchemaVersion`, `Event`, `OccurredAt`,
`TickSequence`, `EngineInstanceId`, `Reason`, `ReasonSequence`, `ConsecutiveBlockedTicks`, `Campaigns`,
`InFlightAtQuiesce`, `LicenseId`, `Licensee`, `Tier`, and `CampaignsRebuilt`. The `Event`, `Reason`, and
`Tier` enum fields SHALL persist as text; `Campaigns` SHALL persist as a `jsonb` column.

#### Scenario: A campaigns-quiesced episode is persisted

- **GIVEN** the Pro `DialerEngine` delivers a `DialerLicenseAuditRecord` with `Event` `CampaignsQuiesced`, `Reason` `Revoked`, `ReasonSequence` `"NotLicensed,Revoked"`, `Tier` `SelfHostBusiness`, and a non-empty `Campaigns` list
- **WHEN** `PostgresDialerLicenseAuditSink.RecordAsync` is invoked with it
- **THEN** exactly one `dialer_license_audit` row is inserted whose columns equal the record's `SchemaVersion`, `Event`, `OccurredAt`, `TickSequence`, `EngineInstanceId`, `Reason`, `ReasonSequence`, `ConsecutiveBlockedTicks`, `InFlightAtQuiesce`, `LicenseId`, `Licensee`, `Tier`, and `CampaignsRebuilt`

#### Scenario: The Campaigns list round-trips as jsonb

- **GIVEN** a record whose `Campaigns` list carries two `QuiescedCampaignInfo` items, each with a `CampaignId`, `TenantId`, and `Name`
- **WHEN** the record is persisted
- **THEN** the `campaigns` jsonb column holds both items with their `CampaignId`, `TenantId`, and `Name` preserved, serialized through the `PostgresJson.Ctx` source-gen context (no reflection)

### Requirement: Nullable and empty-collection record fields persist faithfully

The sink MUST persist the record's nullable fields — `Reason`, `ReasonSequence`, `LicenseId`, and
`Licensee` — as SQL `NULL` when the field is null, binding each nullable parameter as
`(object?)value ?? DBNull.Value` with an explicit `NpgsqlDbType` (else Postgres throws `42P08`). For a
non-quiesce event whose `Campaigns` list is empty, the sink MUST persist an empty jsonb array. The
`Recovered` event carries a null `Reason` and a populated `CampaignsRebuilt`, and MUST persist as such.

#### Scenario: A Recovered event persists with null reason and empty campaigns

- **GIVEN** a record with `Event` `Recovered`, a null `Reason`, an empty `Campaigns` list, and `CampaignsRebuilt` greater than zero
- **WHEN** `RecordAsync` persists it
- **THEN** the row's `reason` column is `NULL`, its `campaigns` column is an empty jsonb array, and its `campaigns_rebuilt` column equals the record's `CampaignsRebuilt`

#### Scenario: Null license identity fields persist as NULL without a bind error

- **GIVEN** a record whose `LicenseId` and `Licensee` are both null
- **WHEN** `RecordAsync` persists it
- **THEN** the `license_id` and `licensee` columns are `NULL` and no `42P08` (indeterminate parameter type) error is raised

### Requirement: The sink must not throw into the dial path

Per the seam contract ("Must not throw into the dial path", `DialerLicenseAudit.cs`),
`PostgresDialerLicenseAuditSink.RecordAsync` MUST NOT propagate an exception to its caller. A persistence
fault (transient DB outage, closed data source) SHALL be caught and logged, and `RecordAsync` SHALL
return normally — degrading a durability failure to a missing (logged) audit row, never to a disrupted
dial loop. This is required at the sink even though the Pro `DialerEngine` also invokes the sink
try/caught (defense in depth).

#### Scenario: A persistence fault is swallowed and logged

- **GIVEN** the underlying Postgres insert faults (e.g. the data source is unavailable)
- **WHEN** `RecordAsync` is invoked
- **THEN** it catches and logs the fault and returns without rethrowing, so the caller's dial path is unaffected

### Requirement: The sink is registered so the dialer resolves it

Platform MUST register `IDialerLicenseAuditSink → PostgresDialerLicenseAuditSink` (as a singleton) in
`Verbara.Platform.Storage.Postgres` `ServiceCollectionExtensions`, beside the existing
`IAuditStore → PostgresAuditStore` registration, so that the Pro `DialerEngine`'s
`GetService<IDialerLicenseAuditSink>()` resolves a non-null sink. The dialer stack is already wired in
`Program.cs` via `AddProDialer` / `UsePostgresDialerStorage`; no additional `Program.cs` change is
required. Before this change the seam resolved `null` and the record was dropped; after it, the seam
resolves the Postgres sink.

#### Scenario: The seam resolves the Postgres sink after registration

- **GIVEN** the `Verbara.Platform.Storage.Postgres` service registrations have run
- **WHEN** the container resolves `IDialerLicenseAuditSink` (the seam the Pro `DialerEngine` reads via `GetService`)
- **THEN** it resolves a non-null `PostgresDialerLicenseAuditSink` (not `null`, as it did before this change)

### Requirement: Additive migration for the audit table

Platform MUST add migration `017_DialerLicenseAudit.sql` (the next number after the highest existing
`016_SurveyCsatExtensions.sql`) creating the `dialer_license_audit` table with `CREATE TABLE IF NOT EXISTS`.
The migration MUST be additive — no change to any existing table and no backfill — and its columns MUST
map 1:1 onto the `DialerLicenseAuditRecord` fields, with exactly `reason`, `reason_sequence`, `license_id`,
and `licensee` nullable, `campaigns` typed `jsonb`, and the `event`/`reason`/`tier` enums typed `text`.

#### Scenario: The migration is picked up and additive

- **GIVEN** the `Migrations\*.sql` embedded-resource glob and the migration runner
- **WHEN** Platform applies pending migrations
- **THEN** `017_DialerLicenseAudit.sql` runs after `016_`, creates `dialer_license_audit` if absent, and alters no existing table

