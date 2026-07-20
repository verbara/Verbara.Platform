# Tasks: dialer-license-audit-sink (Platform host — durable dialer license-enforcement audit)

The HOST (Platform) side — and the only side — of the `dialer-license-audit-sink` cross-repo change
(contract: `impact.yaml`, decision_ref Pro/ADR-0016). **No `Verbara.Sdk.Pro` / `Verbara.Sdk` change**:
the seam (`IDialerLicenseAuditSink`), the record (`DialerLicenseAuditRecord`), and the `DialerEngine`
emission already ship in `Verbara.Sdk.Pro` v2.11.0-pro and are already wired into Platform's dialer stack
(`AddProDialer` / `UsePostgresDialerStorage` in `Program.cs`). There is therefore **no cross-repo build
barrier and no pin advance** — this is a single-repo, additive Platform change. All work compiles against
the current Pro pin.

Follow Subagent-Driven Development with FCM batching: Phase A schema + AOT boundary (batch) → Phase B the
sink + registration (focused) → Phase C verification (batch).

## 1. Table migration (Phase A)

- [x] 1.1 `src/Verbara.Platform.Storage.Postgres/Migrations/017_DialerLicenseAudit.sql` (NEW) — `CREATE TABLE IF NOT EXISTS dialer_license_audit` with columns mapping 1:1 onto `DialerLicenseAuditRecord` (`fixtures/dialer-license-audit-record.v1.json`): `schema_version int`, `event text`, `occurred_at timestamptz`, `tick_sequence bigint`, `engine_instance_id uuid`, `reason text NULL`, `reason_sequence text NULL`, `consecutive_blocked_ticks int`, `campaigns jsonb`, `in_flight_at_quiesce int`, `license_id text NULL`, `licensee text NULL`, `tier text`, `campaigns_rebuilt int`, plus a surrogate PK and an `(occurred_at DESC)` index. Nullable columns are exactly `Reason`/`ReasonSequence`/`LicenseId`/`Licensee`. Additive, no existing-table change, no backfill (design D3)
- [x] 1.2 Confirm the migration is picked up by the `Migrations\*.sql` embedded-resource glob in `Verbara.Platform.Storage.Postgres.csproj` (no csproj edit needed — glob already covers it) and orders after `016_`

## 2. AOT JSON boundary for the Campaigns shape (Phase A)

- [x] 2.1 `src/Verbara.Platform.Storage.Postgres/PostgresJsonSerializer.cs` — add `[JsonSerializable(typeof(IReadOnlyList<QuiescedCampaignInfo>))]` and `[JsonSerializable(typeof(QuiescedCampaignInfo))]` to `PostgresJsonContext` so the `campaigns` jsonb column serializes through `PostgresJson.Ctx` (design D2, D6); `QuiescedCampaignInfo` is the Pro type `(long CampaignId, string TenantId, string Name)`. `JsonSerializerIsReflectionEnabledByDefault=false` — an unregistered shape fails the build

## 3. Postgres sink implementation (Phase B)

- [x] 3.1 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresDialerLicenseAuditSink.cs` (NEW) — internal `sealed class PostgresDialerLicenseAuditSink : IDialerLicenseAuditSink` taking `NpgsqlDataSource` (+ `ILogger`); `RecordAsync(DialerLicenseAuditRecord record, CancellationToken ct)` issues one `INSERT INTO dialer_license_audit (...) VALUES (@...)` via `NpgsqlDataSource.ExecuteAsync` (mirrors `PostgresAuditStore.SaveAsync`). Bind every field as an explicit `NpgsqlParameter`; the four nullable columns bind `(object?)value ?? DBNull.Value` with an explicit `NpgsqlDbType.Text` (avoids `42P08`); enum fields (`Event`/`Reason`/`Tier`) bind `.ToString()` text; `campaigns` binds `JsonSerializer.Serialize(record.Campaigns, PostgresJson.Ctx.<T>)` as a string param with the `@Campaigns::jsonb` SQL cast. No Dapper — `Verbara.Sdk.Data.Npgsql` only (design D1, D2)
- [x] 3.2 In `PostgresDialerLicenseAuditSink.RecordAsync` — wrap the `ExecuteAsync` in `try/catch` that logs the persistence fault and returns without rethrowing, honoring the sink contract "Must not throw into the dial path" (design D5); a transient DB fault degrades to a missing (logged) audit row, never a disrupted dial loop

## 4. DI registration (Phase B)

- [x] 4.1 `src/Verbara.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` — add `services.AddSingleton<IDialerLicenseAuditSink, PostgresDialerLicenseAuditSink>()` beside the existing `AddSingleton<IAuditStore, PostgresAuditStore>()`, so the Pro `DialerEngine`'s `GetService<IDialerLicenseAuditSink>()` (wired via `AddProDialer` / `UsePostgresDialerStorage` in `Program.cs`) resolves it — no `Program.cs` change needed (design D4)

## 5. Tests (Phase C)

- [x] 5.1 `tests/Verbara.Platform.Storage.Postgres.Tests/PostgresDialerLicenseAuditSinkTests.cs` (NEW) — `RecordAsync` on the golden fixture (`CampaignsQuiesced`, 2 campaigns, `Reason` `Revoked`, `ReasonSequence` `"NotLicensed,Revoked"`, `Tier` `SelfHostBusiness`) persists one `dialer_license_audit` row whose columns equal the record fields, including the `campaigns` jsonb round-trip (2 items, each with `CampaignId`/`TenantId`/`Name`) and the enum text values. Live-Postgres lane (`live-db-ci-lane`)
  - NOTE: the fixture's `"Tier": "SelfHostBusiness"` is a real member of `Verbara.Sdk.Pro.Licensing.LicenseTier` (`None`/`Developer`/`SelfHostStartup`/`SelfHostBusiness`/`SaaSBusiness`/`SaaSEnterprise`/`WhiteLabel`, v2.11.1-pro); the test constructs `LicenseTier.SelfHostBusiness` and asserts its `.ToString()` text form (the sink persists `tier` as `.ToString()` text).
- [x] 5.2 `PostgresDialerLicenseAuditSinkTests` — a `Recovered` event persists with `reason` NULL, `reason_sequence` NULL, `campaigns` `[]`, and `campaigns_rebuilt` > 0; a nullable-heavy record (`LicenseId`/`Licensee` null) persists with those columns NULL (no `42P08`)
- [x] 5.3 `PostgresDialerLicenseAuditSinkTests` — `RecordAsync` does NOT throw when the insert faults (simulated DB error / closed data source): assert it swallows + logs and returns, honoring the "must not throw into the dial path" contract (design D5)
- [x] 5.4 A DI resolution test (e.g. `tests/Verbara.Platform.Storage.Postgres.Tests/` or the existing wiring-test home) — after the Storage.Postgres registrations run, `GetService<IDialerLicenseAuditSink>()` resolves a non-null `PostgresDialerLicenseAuditSink` (the seam the Pro `DialerEngine` reads); before this change it resolved null

## 6. AOT + validation gate (Phase C)

- [x] 6.1 `dotnet build` 0-warning (TreatWarningsAsErrors, WarningLevel 9999) + AOT publish of `Verbara.Platform.Api` shows 0 trim/AOT warnings (`IL2026`/`IL3050`/`IL207x`) — the `Campaigns` source-gen registration is the only new JSON boundary
- [x] 6.2 `openspec validate --change dialer-license-audit-sink --strict` green; full `dotnet test` green (sink persist, nullable/enum/jsonb round-trips, fail-safe contract, DI resolution)
