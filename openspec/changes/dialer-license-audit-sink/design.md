# Design: dialer-license-audit-sink (Platform host — durable dialer license-enforcement audit)

## Context

`Verbara.Platform` is the **consumer / host** (buildOrder 1, the only repo) in the
`dialer-license-audit-sink` cross-repo change (contract: `impact.yaml`, decision_ref Pro/ADR-0016). The
optional durability seam, the record type, and the `DialerEngine` emission all already ship in
`Verbara.Sdk.Pro` (v2.11.0-pro) and are already wired into Platform's dialer stack; this change supplies
the Postgres implementation Pro's doc-comment defers to Platform ("Platform implements this over
`NpgsqlExecutor` as a follow-up").

The seam (read-only grounding, `Verbara.Sdk.Pro.Dialer/Diagnostics/DialerLicenseAudit.cs:83`):

```csharp
public interface IDialerLicenseAuditSink
{
    ValueTask RecordAsync(DialerLicenseAuditRecord record, CancellationToken ct);
}
```

Optional; **none registered by default**. `DialerEngine` resolves it via `GetService<IDialerLicenseAuditSink>()`
→ `null` today and silently drops the record (the live blind-spot). The engine invokes the sink
**try/caught**, and the contract states the sink **must not throw into the dial path**.

The record it delivers — tick-scoped, schema v1, never per-originate (that is Scope B, deferred) — with
its fields VERBATIM (the golden shape is `fixtures/dialer-license-audit-record.v1.json`; Pro never
serializes this record, so the fixture keys are the .NET property names):

| Field | Type | Notes |
|-------|------|-------|
| `SchemaVersion` | `int` | currently `1` |
| `Event` | `DialerLicenseAuditEvent` (enum: `QuiescencePending`\|`CampaignsQuiesced`\|`FlapAbsorbed`\|`Recovered`) | stored as text |
| `OccurredAt` | `DateTimeOffset` | from the engine's `TimeProvider` clock |
| `TickSequence` | `long` | reconciliation tick the event was produced on |
| `EngineInstanceId` | `Guid` | correlates events from one process lifetime |
| `Reason` | `LicenseBlockReason?` | **nullable** (null for `Recovered`); stored as text |
| `ReasonSequence` | `string?` | **nullable**, adjacent-deduped, bounded 8, e.g. `"NotLicensed,Revoked"` |
| `ConsecutiveBlockedTicks` | `int` | |
| `Campaigns` | `IReadOnlyList<QuiescedCampaignInfo>` | `QuiescedCampaignInfo(long CampaignId, string TenantId, string Name)`; empty `[]` for non-quiesce events; a `jsonb` column |
| `InFlightAtQuiesce` | `int` | in-flight originations at quiescence (they complete and bill) |
| `LicenseId` | `string?` | **nullable** |
| `Licensee` | `string?` | **nullable** |
| `Tier` | `LicenseTier` (enum) | stored as text |
| `CampaignsRebuilt` | `int` | populated only for `Recovered` |

The mirror pattern this design follows (the exemplar):
`Verbara.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs` — `Verbara.Sdk.Data.Npgsql`
`NpgsqlExecutor` (`ExecuteAsync`), a private `sealed` row class with a hand-written
`static Map(NpgsqlDataReader)`, explicit `NpgsqlParameter` with `NpgsqlDbType` (+ `DBNull.Value` for
nullables, `::jsonb` cast for JSON columns), source-gen `PostgresJson.Ctx` for the AOT JSON boundary.
Second precedent: `Stores/PostgresTypificationCorrectionAuditWriter.cs`.

## Goals / Non-Goals

**Goals:**

- Turn the silently-dropped `DialerLicenseAuditRecord` into a durable Postgres row by implementing the
  already-published `IDialerLicenseAuditSink` over `NpgsqlExecutor`.
- Register the sink so the Pro `DialerEngine`'s `GetService<IDialerLicenseAuditSink>()` resolves it,
  beside the existing `IAuditStore` registration.
- Add the `017` table migration whose columns map 1:1 onto the record fields (verbatim-fixture-citation).
- Honor the sink contract: `RecordAsync` never throws into the dial path.
- Stay Native AOT clean (Platform/ADR-0022): the `Campaigns` shape source-gen registered, no reflection,
  no Dapper — `Verbara.Sdk.Data.Npgsql` for the write.

**Non-Goals:**

- Any change to `Verbara.Sdk.Pro` or `Verbara.Sdk` — the seam, record, and emission already ship; this
  is a pure consumer implementation with no cascade.
- A read/query/report HTTP surface over the audit table — write-only durability here.
- Scope B / R-011b: per-originate spend-point grant/denial rows (deferred — see proposal + `impact.yaml`).
- Any change to the frozen fixture or `impact.yaml`.

## Decisions

### D1 — `PostgresDialerLicenseAuditSink` over `NpgsqlExecutor`, mirroring `PostgresAuditStore`

Implement `IDialerLicenseAuditSink` as an internal `sealed class PostgresDialerLicenseAuditSink` in
`Verbara.Platform.Storage.Postgres/Stores/`, taking `NpgsqlDataSource` (the canonical DI singleton).
`RecordAsync` issues a single `INSERT INTO dialer_license_audit (...) VALUES (@...)` via
`NpgsqlDataSource.ExecuteAsync(sql, bind, ct)` — the exact shape `PostgresAuditStore.SaveAsync` uses.
Every parameter is an explicit `NpgsqlParameter`; nullable columns (`Reason`, `ReasonSequence`,
`LicenseId`, `Licensee`) bind `(object?)value ?? DBNull.Value` with an explicit `NpgsqlDbType.Text`
(the "every nullable param that can be `DBNull.Value` MUST set an explicit `NpgsqlDbType`" rule — else
Postgres throws `42P08`); the `Campaigns` jsonb param uses the `@Campaigns::jsonb` SQL cast on a string
param (the excepted case). Enum columns (`Event`, `Reason`, `Tier`) bind `.ToString()` text.
**Alternative rejected:** a generic reusable audit-writer abstraction — the record shape is specific and
Pro-owned; a bespoke sink mirroring the established `PostgresAuditStore` is the least-surprise path and
keeps the column/param binding legible for the verbatim-fixture-citation audit.

### D2 — `Campaigns` list as a `jsonb` column through the `PostgresJson.Ctx` AOT boundary

`Campaigns` (`IReadOnlyList<QuiescedCampaignInfo>`) is the one non-scalar field, so it is a natural
`jsonb` column. `RecordAsync` serializes it with `JsonSerializer.Serialize(record.Campaigns, PostgresJson.Ctx.<T>)`
where `<T>` is a NEW `[JsonSerializable(typeof(IReadOnlyList<QuiescedCampaignInfo>))]` (and
`typeof(QuiescedCampaignInfo)`) entry added to `PostgresJsonContext` (`PostgresJsonSerializer.cs`), then
binds it as a string param with the `::jsonb` cast — exactly how `PostgresAuditStore` serializes
`Metadata`/`Before`/`After`. `JsonSerializerIsReflectionEnabledByDefault=false` means an unregistered
shape fails the build; registering it in the source-gen context keeps the write AOT-safe (0 `IL2026`/`IL3050`).
`QuiescedCampaignInfo` is a Pro type (`public sealed record QuiescedCampaignInfo(long CampaignId, string TenantId, string Name)`),
serializable by JSON source-gen with no reflection. **Alternative rejected:** a normalized child table
(`dialer_license_audit_campaigns` FK'd to the parent) — the campaigns list is a point-in-time snapshot
(captured BEFORE teardown, immutable, never queried relationally), so a jsonb column is the correct
grain and matches the audit-metadata precedent; a child table adds a join and a second insert for no
query benefit.

### D3 — Migration `017_*.sql`: one additive `dialer_license_audit` table, columns cite the record

The highest existing migration is `016_SurveyCsatExtensions.sql`; the new file is `017_DialerLicenseAudit.sql`
(embedded via the `Migrations\*.sql` glob in the `.csproj`). It is `CREATE TABLE IF NOT EXISTS
dialer_license_audit (...)` — additive, no existing-table change, no backfill. Columns map 1:1 onto the
record fields (verbatim-fixture-citation rule):

| Column | Type | Nullability | Record field |
|--------|------|-------------|--------------|
| `id` | `bigint GENERATED ... AS IDENTITY` / surrogate PK | not null | (surrogate) |
| `schema_version` | `int` | not null | `SchemaVersion` |
| `event` | `text` | not null | `Event` |
| `occurred_at` | `timestamptz` | not null | `OccurredAt` |
| `tick_sequence` | `bigint` | not null | `TickSequence` |
| `engine_instance_id` | `uuid` | not null | `EngineInstanceId` |
| `reason` | `text` | **null** | `Reason` |
| `reason_sequence` | `text` | **null** | `ReasonSequence` |
| `consecutive_blocked_ticks` | `int` | not null | `ConsecutiveBlockedTicks` |
| `campaigns` | `jsonb` | not null (`'[]'` for non-quiesce) | `Campaigns` |
| `in_flight_at_quiesce` | `int` | not null | `InFlightAtQuiesce` |
| `license_id` | `text` | **null** | `LicenseId` |
| `licensee` | `text` | **null** | `Licensee` |
| `tier` | `text` | not null | `Tier` |
| `campaigns_rebuilt` | `int` | not null | `CampaignsRebuilt` |

The four nullable columns are exactly the record's nullable fields (`Reason`, `ReasonSequence`,
`LicenseId`, `Licensee`). An index on `(occurred_at DESC)` (and/or `engine_instance_id`) supports the
future report read without committing to it here. **Alternative rejected:** reusing the generic
`audit_entries` table — its columns (action/entity/actor/tenant) do not match the episode grain (an
enforcement episode is engine-tick-scoped, spans multiple campaigns/tenants), so a purpose-built table
is cleaner than shoehorning the record into the general audit shape.

### D4 — DI registration beside `IAuditStore`; resolved by the Pro `DialerEngine`'s `GetService`

Register `services.AddSingleton<IDialerLicenseAuditSink, PostgresDialerLicenseAuditSink>()` in
`Storage.Postgres/ServiceCollectionExtensions.cs`, beside the existing
`AddSingleton<IAuditStore, PostgresAuditStore>()`. The Pro `DialerEngine` resolves the sink via
`GetService<IDialerLicenseAuditSink>()` (optional resolution → null when absent, non-null once
registered), and the dialer stack is wired in `Program.cs` via `AddProDialer` / `UsePostgresDialerStorage`.
Because this Postgres registration ships in the same `Storage.Postgres` package the dialer storage wiring
already pulls in, no `Program.cs` change is required — resolving the now-registered sink is automatic.
**Alternative rejected:** registering in `Program.cs` directly — it would split the sink from its sibling
Postgres store registrations; the `ServiceCollectionExtensions` home keeps all Postgres store bindings in
one place (single source of truth for the Postgres composition).

### D5 — `RecordAsync` MUST NOT throw into the dial path (contract-honoring, defense in depth)

The engine already invokes the sink try/caught, but the sink contract (`DialerLicenseAudit.cs`: "Must not
throw into the dial path") makes the *sink* responsible too. `RecordAsync` therefore wraps the
`ExecuteAsync` in a `try/catch` that logs the persistence fault (via `ILogger`) and returns without
rethrowing — a transient DB outage degrades to a missing audit row (logged), never to a disrupted dial
loop. This is durability-best-effort by design at the boundary; the engine's own try/caught is the outer
belt. **Alternative rejected:** letting exceptions propagate and relying solely on the engine's
try/caught — correct in practice but violates the explicit sink contract and couples the sink's safety to
the caller's discipline; honoring the contract at the sink is defense in depth.

### D6 — Native AOT registration (Platform/ADR-0022)

`JsonSerializerIsReflectionEnabledByDefault=false`: the `Campaigns` jsonb shape
(`IReadOnlyList<QuiescedCampaignInfo>` + `QuiescedCampaignInfo`) is added to `PostgresJsonContext`
source-gen (D2). No reflection, no `Activator.CreateInstance`, no anonymous `new {}` — the row read-back
(for the sink's own tests, if any) uses a hand-written `static Map(NpgsqlDataReader)` with name-based
getters, `{ get; init; }` on a class-based row type, mirroring `PostgresAuditStore`. `TreatWarningsAsErrors`;
AOT publish of `Verbara.Platform.Api` must show 0 `IL2026`/`IL3050`/`IL207x`.

## Architectural Risk

- **Level:** LOW.
- **Affected:** `Verbara.Platform.Storage.Postgres` (new sink + migration + JSON source-gen entry) and,
  transitively, the dialer stack wired in `Verbara.Platform.Api` (which now resolves a non-null sink).
  No `Verbara.Sdk.Pro` / `Verbara.Sdk` change — no cross-repo build cascade.
- **Mitigation:** the change is purely additive (new table, new registration, no existing-behavior change);
  the sink is fail-safe (D5) so a persistence fault cannot disrupt the dial path; the migration is
  `CREATE TABLE IF NOT EXISTS` with no backfill; column/param binding mirrors the proven `PostgresAuditStore`;
  AOT cleanliness is enforced by the build (`JsonSerializerIsReflectionEnabledByDefault=false` fails on any
  unregistered shape) and the release AOT-publish gate.

## Migration Plan

1. Add migration `017_DialerLicenseAudit.sql` (`CREATE TABLE IF NOT EXISTS dialer_license_audit`) — runs on
   the next Platform deploy's migration step; additive, no existing table touched.
2. Add the `Campaigns` shape to `PostgresJsonContext` source-gen and the `PostgresDialerLicenseAuditSink`
   implementation.
3. Register `IDialerLicenseAuditSink → PostgresDialerLicenseAuditSink` in `ServiceCollectionExtensions.cs`.
4. Deploy Platform: the Pro `DialerEngine` (already wired) now resolves the non-null sink and license
   episodes become durable from the first tick after deploy.
5. **Rollback:** the registration is a drop; with the sink absent, `DialerEngine` reverts to the prior
   silently-dropping behavior (no dial-path impact). The table can be left in place (harmless empty table)
   or dropped; there is no data migration to reverse.

## Open Questions

- **Retention / purge policy for `dialer_license_audit`.** The generic `audit_entries` table carries a
  `retain_until` column; this compliance table has no explicit retention decision. Proposed default: no
  automatic purge (compliance artifacts are kept), revisited if volume warrants. A reviewer should confirm
  whether a retention column/policy is wanted at table-creation time (cheaper than adding it later).
- **Tenant scoping of the row.** The record is engine-tick-scoped and can span multiple tenants (the
  `Campaigns[]` items each carry their own `TenantId`); the top-level row has no single tenant. Proposed:
  no top-level `tenant_id` column (tenant lives inside the `campaigns` jsonb, matching the record's grain).
  A reviewer should confirm this is acceptable for whatever tenant-filtered read the future report needs
  (vs. denormalizing a tenant array/column now).
- **Report/read surface (deferred, not blocking).** This change is write-only. A future
  `GET /api/v1/.../dialer-license-audit` read API is out of scope; the table shape (D3) is designed to
  support it (`occurred_at DESC` index) without a schema change.
