---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Compliance officers, contact-center operators, and tenants running the predictive dialer under license enforcement
decision_ref: Pro/ADR-0016
---

# Proposal: dialer-license-audit-sink (Platform host — durable dialer license-enforcement audit)

## Why

Pro's `DialerEngine` already detects and acts on license-enforcement episodes: on a sustained license
block it quiesces live campaigns, absorbs transient flaps, and rebuilds campaigns on recovery. Each
such episode produces a tick-scoped `DialerLicenseAuditRecord` — the "provable compliance artifact for
a license-enforcement episode" (Pro/ADR-0016) — which the engine delivers through the **optional** seam
`Verbara.Sdk.Pro.Dialer/Diagnostics/DialerLicenseAudit.cs:83`
(`IDialerLicenseAuditSink.RecordAsync(DialerLicenseAuditRecord, CancellationToken)`).

That seam is unimplemented in Platform today. **None is registered by default**, so `DialerEngine`
resolves `GetService<IDialerLicenseAuditSink>()` → `null` and silently drops the record — a live
compliance blind-spot: when the dialer tears down campaigns because a license was revoked, there is no
durable record of *when*, *which campaigns*, *how many in-flight calls billed through the quiesce*, or
*under what block reason*. The record's own doc-comment states the intended fix verbatim: "Platform
implements this over `NpgsqlExecutor` as a follow-up." This change is that follow-up.

## What Changes

This is the **HOST (Platform)** side — and the *only* side — of the `dialer-license-audit-sink`
cross-repo change (contract: `impact.yaml`, decision_ref Pro/ADR-0016). The seam, the record type, and
the `DialerEngine` emission all **already ship** in `Verbara.Sdk.Pro` (v2.11.0-pro) and are already
wired into Platform's dialer stack via `AddProDialer` / `UsePostgresDialerStorage` (`Program.cs`). Only
the Postgres implementation of the sink, its DI registration, and the backing table remain — **Pro is
NOT touched** (no seam to add), so there is no build cascade. All changes are additive and back-compat:
existing dialer behavior is unchanged; a resolved sink turns a silently-dropped record into a durable
audit row, and the sink contract forbids throwing into the dial path.

- **Postgres sink implementation** — a new internal `PostgresDialerLicenseAuditSink : IDialerLicenseAuditSink`
  in `Verbara.Platform.Storage.Postgres`, mirroring the canonical Postgres audit pattern in
  `Stores/PostgresAuditStore.cs` (second precedent: `Stores/PostgresTypificationCorrectionAuditWriter.cs`):
  `Verbara.Sdk.Data.Npgsql` `NpgsqlExecutor` (`ExecuteAsync`), explicit `NpgsqlParameter` binding with
  `NpgsqlDbType` (`DBNull.Value` for the nullable columns, `::jsonb` cast for the campaigns column), and
  the source-gen `PostgresJson.Ctx` AOT boundary for the `Campaigns` JSON. `RecordAsync` MUST NOT throw
  into the dial path (the sink contract) — a persistence fault is swallowed/logged, never propagated.
- **DI registration** — register `IDialerLicenseAuditSink → PostgresDialerLicenseAuditSink` beside the
  existing `AddSingleton<IAuditStore, PostgresAuditStore>()` in `Storage.Postgres/ServiceCollectionExtensions.cs`,
  wired so the Pro `DialerEngine`'s `GetService<IDialerLicenseAuditSink>()` resolves it (the dialer is
  wired in `Program.cs`).
- **Table migration `017_*.sql`** — a new `dialer_license_audit` table (highest existing migration is
  `016_SurveyCsatExtensions.sql`), columns mapping 1:1 onto the `DialerLicenseAuditRecord` fields frozen
  by `fixtures/dialer-license-audit-record.v1.json`. The `Campaigns` list is a `jsonb` column;
  `Event`/`Reason`/`Tier` enums store as `text`; the nullable columns are `Reason`, `ReasonSequence`,
  `LicenseId`, `Licensee`.

## Capabilities

### New Capabilities

- `dialer-license-audit`: the durable persistence of dialer license-enforcement episodes. There is no
  pre-existing `dialer` capability in `openspec/specs/`; this change introduces the living spec for the
  audit-sink boundary (the tick-scoped enforcement-episode grain).

### Modified Capabilities

(none — the seam, record, and emission are Pro-side and already shipped; Platform adds a new capability.)

## Impact

- **Code:** `Verbara.Platform.Storage.Postgres` — new `Stores/PostgresDialerLicenseAuditSink.cs`, a new
  `[JsonSerializable]` registration for the `Campaigns` shape on the AOT JSON boundary (`PostgresJson.Ctx`),
  and the sink registration in `ServiceCollectionExtensions.cs`. No `Verbara.Platform.Api` change beyond
  the fact that the dialer stack it already wires now resolves a non-null sink. **No Pro / Sdk change.**
- **APIs:** none — this is an internal durability sink, not a public HTTP surface. (A future read/report
  API over the table is out of scope; see the deferred follow-up below.)
- **Wire contract:** 1 fixture, frozen at `/xr:change` time and unmodified here:
  `fixtures/dialer-license-audit-record.v1.json` — the golden shape of `DialerLicenseAuditRecord`
  (SchemaVersion 1). The 017 migration columns and the row `Map(NpgsqlDataReader)` cite its field names
  verbatim (verbatim-fixture-citation rule). Pro never serializes this record; the fixture keys are the
  .NET property names.
- **Dependencies:** cross-repo *reference* only — Platform consumes the already-published Pro seam via
  the local NuGet feed pin (`Verbara.Sdk.Pro` v2.11.0-pro). No pin advance, no pack, no cascade: `impact.yaml`
  declares a single repo (`../Verbara.Platform`, role `consumer`, `childChange: HOST`).
- **Data:** one new additive table (`dialer_license_audit`, migration 017). No change to existing tables;
  no backfill.

## Deferred follow-up (explicitly OUT of scope)

**Scope B / roadmap R-011b — durably recording per-originate spend-point grants/denials — is deferred.**
Pro's `OriginateExecutorBase` spend-point path today only logs (EventId 13104) and increments a metric,
then drops the event; routing it to a sink would require a NEW Pro-side emission on the deliberately
allocation-free hot path — a two-repo cascade, and a per-call grain rather than the coherent
business-level episode grain this change persists. The tick-scoped engine record above (never emitted
per originate attempt — the hot path stays allocation-free) is the right audit grain for compliance
today. Scope B is deferred until a concrete compliance need requires per-call rows (recorded in
`impact.yaml` and Pro/ADR-0016).
