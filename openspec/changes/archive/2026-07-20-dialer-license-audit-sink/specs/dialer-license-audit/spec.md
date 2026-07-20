# dialer-license-audit — Delta

Introduces the `dialer-license-audit` capability: the durable persistence of dialer license-enforcement
episodes. Pro's `DialerEngine` already detects each episode (quiesce / flap-absorb / recover) and
delivers a tick-scoped `DialerLicenseAuditRecord` through the OPTIONAL seam
`IDialerLicenseAuditSink.RecordAsync(DialerLicenseAuditRecord, CancellationToken)`
(`Verbara.Sdk.Pro.Dialer/Diagnostics/DialerLicenseAudit.cs:83`). None is registered by default, so the
engine resolves `GetService<IDialerLicenseAuditSink>()` → `null` and silently drops the record (the live
compliance blind-spot). This delta ADDS the Platform-side Postgres implementation of that seam, its DI
registration, and the backing `dialer_license_audit` table (migration `017`).

**Cross-repo note (chain Sdk → Sdk.Pro → Platform ← Platform.Web):** the seam, the record type, and the
`DialerEngine` emission are all Pro-side and already shipped (`Verbara.Sdk.Pro` v2.11.0-pro), already
wired into Platform's dialer stack (`AddProDialer` / `UsePostgresDialerStorage`). This change touches
**Platform only** — no Pro/Sdk edit, no pin advance, no build cascade.

The record shape is frozen by `fixtures/dialer-license-audit-record.v1.json` (SchemaVersion 1). Pro never
serializes this record — the fixture keys are the .NET property names of `DialerLicenseAuditRecord`, and
every requirement below cites them verbatim (the verbatim-fixture-citation rule): `SchemaVersion`,
`Event`, `OccurredAt`, `TickSequence`, `EngineInstanceId`, `Reason`, `ReasonSequence`,
`ConsecutiveBlockedTicks`, `Campaigns` (a list of `QuiescedCampaignInfo(CampaignId, TenantId, Name)`),
`InFlightAtQuiesce`, `LicenseId`, `Licensee`, `Tier`, `CampaignsRebuilt`.

## ADDED Requirements

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

## Architectural Risk

- **Level:** LOW.
- **Affected:** `Verbara.Platform.Storage.Postgres` (new `PostgresDialerLicenseAuditSink`, the `017` table
  migration, and one new `[JsonSerializable]` entry for the `Campaigns` shape) and, transitively, the
  dialer stack wired in `Verbara.Platform.Api`, which now resolves a non-null sink. No `Verbara.Sdk.Pro`
  or `Verbara.Sdk` change — no cross-repo build cascade along the chain.
- **Mitigation:** the change is purely additive (new table, new registration; existing dialer behavior is
  unchanged — a resolved sink turns a silently-dropped record into a durable row). The sink is fail-safe
  (it must not throw into the dial path), so a persistence fault cannot disrupt originations. The migration
  is `CREATE TABLE IF NOT EXISTS` with no backfill. Column/parameter binding mirrors the proven
  `PostgresAuditStore`. AOT cleanliness is enforced by the build (`JsonSerializerIsReflectionEnabledByDefault=false`
  fails on any unregistered JSON shape) and the AOT-publish gate. Rollback is a registration drop: the seam
  reverts to `null` and the engine to its prior silently-dropping behavior, with no data migration to reverse.
