# Phase D (rev.) — Total Dapper Removal → Raw Npgsql + Owned Micro-Layer

**Created:** 2026-05-19 · **Status:** Approved (design) · **Supersedes:** Option O / `Verbara.Sdk.Dapper.Stubs` approach in [`docs/plans/active/2026-05-19-phase-d-dapper-aot.md`](../plans/active/2026-05-19-phase-d-dapper-aot.md) · **ADR:** [0022](../decisions/0022-platform-api-aot-shipping-path.md) (Amendment §8 pending) · **Owner:** Maintainer

> This design replaces the previously-chosen "Option O" (build a fake `Dapper.dll` stub assembly + adopt the `Dapper.AOT` source generator). After a deep option-space analysis (2026-05-19), Option O was judged a pragmatic *hack*, not the engineering destination: it ships a phantom `Dapper.dll` forever, depends on an under-maintained upstream source generator (issue [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168), filed by this project's maintainer, 0 comments since 2026-03-16; PR #153 unmerged 12 months), and leaves permanent runtime mine-fields (any un-intercepted call site throws `NotSupportedException` in production — R10 confirmed empirically on the Sessions.Postgres canary). Per the maintainer directive *"sin atajos y caminos fáciles, todo en pro del producto final,"* the committed destination is **total removal of Dapper** in favor of raw Npgsql ADO.NET plus a small, fully-owned, AOT-clean micro-layer.

## Goal

`Verbara.Platform.Api` must publish as **Native AOT** (`<PublishAot>true>`, native ELF, zero `IL2026`/`IL3050`/`IL207x` diagnostics) so the public `ghcr.io/verbara/platform/api:*` images stop shipping `Verbara.Sdk.Pro.*.dll` as decompilable IL. Dapper 2.1.72 is the last AOT blocker (it uses `System.Reflection.Emit.DynamicMethod` + `Type.MakeGenericType` for runtime IL emission — fundamentally not AOT-safe; ~50 ILC diagnostics, 100% Dapper-attributable per the [Phase D Day-0 baseline](../operations/phase-d-validation/2026-05-19-day-0-notes.md)).

**Intended outcome:** flip `<IsAotCompatible>false→true>` on `Verbara.Platform.Api.csproj`, ship a Native-AOT single-binary image (~75–100 MB vs ~250 MB IL), and unblock the Pro v2.5.0-pro public release. Raises the IP-extraction attack cost from "open in ILSpy" to "IDA Pro for weeks."

## Why not the alternatives (option-space analysis)

The decision space for the IP-protection goal reduces to exactly two paths; everything else fails the goal or is an inferior variant:

| Option | Verdict |
|---|---|
| **Option O — Dapper.Stubs + Dapper.AOT** (previously chosen) | Rejected as *final destination*. Phantom `Dapper.dll`, dependency on abandoned upstream tooling, permanent runtime mine-fields (R9/R10/R11). Functional but a hack. |
| IL obfuscation instead of AOT | Rejected — obfuscated IL is still decompilable IL; cosmetic, reversible with de4dot. Fails the goal. |
| Partial AOT / trimming without full AOT | Rejected — still managed IL, not native ELF. Fails the goal. |
| Different micro-ORM (FreeSql, etc.) | Rejected — heavy external dependency reintroduced; if rewriting anyway, target first-party Npgsql. |
| Adopt **Nanorm.Npgsql** (thin AOT helper, Damian Edwards) | Rejected — at v0.1.2, experimental/unmaintained; same external-tooling fragility we are escaping from Dapper.AOT. |
| Wait for upstream #168 fix | Rejected — 0 comments since 2026-03-16, indefinite timeline for a product blocker. |
| **Own micro-layer + own source generator for mappers** | Rejected — an owned generator still reintroduces "generated code you must trust" into the IP-critical path; smaller magic, but magic. |
| **Total Dapper removal — raw Npgsql + owned, hand-written micro-layer (this design)** | **Chosen.** Zero external tooling, fully owned, transparent (what you read is what ships), AOT-clean by construction, eliminates R9/R10/R11 as concepts. |

External corroboration: Microsoft's AOT guidance and the Npgsql docs both state that working directly with Npgsql (not Dapper) is the preferred AOT path; Npgsql ≥8 is AOT-clean out of the box and maps `DateOnly`↔`date` natively. The Humble-Object / Sessions pattern (Thinktecture / feO2x) is the established hand-written raw-ADO.NET approach for AOT.

## Scope

- **9 storage packages / ~447 Dapper call sites / ~72 distinct `*Row` classes across 3 repos.**
  - SDK: `Verbara.Sdk.Sessions.Postgres` (canary; 0 `*Row` classes — different shape)
  - Pro: 7 storage packages (Dialer, EventStore, Cluster, Realtime, Analytics, CallAnalytics, AgentAssist) — 17 `*Row` classes
  - Platform: `Verbara.Platform.Storage.Postgres` + `Verbara.Platform.Identity` DataProtection `DapperXmlRepository` + any direct `Verbara.Platform.Api` Dapper sites — 55 `*Row` classes
- **Confirmed cross-repo:** ZERO `SplitOn` / `QueryMultiple` / `dynamic`. House style (class-based `{ get; init; }` rows, snake_case columns, explicit column lists, JSON via source-gen `*Json.Ctx`) is uniform and migration-friendly.

**Out of scope:** Renderer / Mail microservices (already AOT-clean, no Dapper), `Verbara.Platform.Realtime` (non-AOT by design, owns SignalR Hub), K8s manifests (image tags propagate via Helm values).

## Architecture — four pieces, zero magic

### Piece 1 — `Verbara.Sdk.Data.Npgsql` (NEW shared package, SDK repo, MIT)

Lives in the SDK (base of the `Sdk → Pro → Platform` chain) so all 9 storage packages consume one copy, no duplication. `<IsAotCompatible>true>`, all AOT analyzers ON, `TreatWarningsAsErrors`.

**1a. Executor facade** over `NpgsqlDataSource` — centralizes command/connection/transaction/dispose plumbing **once** (today repeated across ~447 sites):

```csharp
public static class NpgsqlExecutor
{
    Task<T?>       QuerySingleOrDefaultAsync<T>(NpgsqlDataSource ds, string sql,
                       Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct);
    Task<List<T>>  QueryListAsync<T>(NpgsqlDataSource ds, string sql,
                       Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct);
    Task<int>      ExecuteAsync(NpgsqlDataSource ds, string sql,
                       Action<NpgsqlParameterCollection> bind, CancellationToken ct);
    Task<T>        ExecuteScalarAsync<T>(NpgsqlDataSource ds, string sql,
                       Action<NpgsqlParameterCollection> bind, CancellationToken ct);
    // Transaction-aware overloads accepting NpgsqlConnection/NpgsqlTransaction for multi-statement units.
}
```

Call sites stay one-liners, near-identical to today → minimal diff, low migration risk. The `bind` lambda is empty (`static _ => { }`) when there are no parameters.

**1b. Reflection-free reader helpers** — name-based extension methods on `NpgsqlDataReader` that remove null-handling boilerplate (the main bug source in hand-written mappers):

```csharp
string  GetString(this NpgsqlDataReader r, string col);
string? GetStringOrNull(this NpgsqlDataReader r, string col);
int     GetInt32(this NpgsqlDataReader r, string col);
int?    GetInt32OrNull(this NpgsqlDataReader r, string col);
bool    GetBoolean(this NpgsqlDataReader r, string col);
DateTime GetDateTime(this NpgsqlDataReader r, string col);
DateTime? GetDateTimeOrNull(this NpgsqlDataReader r, string col);
Guid    GetGuid(this NpgsqlDataReader r, string col);
DateOnly GetDateOnly(this NpgsqlDataReader r, string col);   // Npgsql-native date mapping
// + long/decimal/double/DateTimeOffset variants and their *OrNull forms, as the audit surfaces them.
```

Name-based lookup caches the ordinal per call (`GetOrdinal` once); robust to column reordering. No reflection, no `RequiresDynamicCode`.

### Piece 2 — Hand-written `Map(NpgsqlDataReader)` per `*Row` class (~72)

Each row class gains a `public static T Map(NpgsqlDataReader r)` written by hand using Piece 1b helpers, sitting next to the existing hand-written `ToX()` transform — same codebase style. Fully transparent, AOT-clean by construction, bug-localized, covered by each store's existing Testcontainers integration tests. **No source generator.** Example (current `PostgresQueueStore.QueueRow`):

```csharp
public static QueueRow Map(NpgsqlDataReader r) => new()
{
    queue_id       = r.GetString("queue_id"),
    tenant_id      = r.GetString("tenant_id"),
    name           = r.GetString("name"),
    is_active      = r.GetBoolean("is_active"),
    max_waiting    = r.GetInt32OrNull("max_waiting"),
    sla_targets    = r.GetStringOrNull("sla_targets"),
    // ... remaining columns
    created_at     = r.GetDateTime("created_at"),
    updated_at     = r.GetDateTimeOrNull("updated_at"),
};
```

The existing `ToQueue()` (JSON deserialize via `PostgresJson.Ctx.*`, value-object construction) is unchanged.

### Piece 3 — Explicit parameter binding (no anonymous objects)

Anonymous objects (`new { TenantId = ... }`) are Dapper's *other* hidden reflection. They are replaced by explicit binds:

```csharp
bind: p => {
    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
    p.Add(new NpgsqlParameter("QueueId", queueId.Value));
}
```

Dynamic-WHERE stores (`PostgresPurgeLogStore.ListAsync`, `PostgresCallAnalyticsStore`) build a conditions list and conditionally add parameters — more natural than `DynamicParameters`. JSONB params remain `string` values with `::jsonb` casts in SQL (unchanged).

### Piece 4 — Special cases are eliminated, not worked around

| Today (Dapper) | Final product (raw Npgsql) |
|---|---|
| JSONB via `JsonSerializer` + `*Json.Ctx` source-gen contexts | **Unchanged** — already AOT-clean on read and write |
| `TypeHandler` `DateOnly`↔`date` | **Gone** — `r.GetDateOnly(col)` + `NpgsqlParameter` with `DateOnly` (Npgsql ≥8 native) |
| `TypeHandler` `Metadata` `Dictionary`↔JSONB | **Gone** — folded into JSON source-gen like every other JSONB column |
| `DynamicParameters` (dynamic WHERE, R9) | conditions list + conditional `Parameters.Add` |
| `CommandDefinition` (transaction/CT, R10) | first-class `cmd.Transaction = tx; ExecuteReaderAsync(ct)` |
| `cancellationToken: ct` overload (R11) | CT flows through every facade method natively |

The R9/R10/R11 risk classes from the prior plan disappear as concepts rather than being mitigated.

## Behavioral-parity validation (the real risk across ~447 sites)

- **Primary gate — existing Testcontainers integration tests** per store assert round-trip behavior. Full suite green before *and* after each package migration ⇒ parity. **Discipline (no shortcuts):** before migrating a store, audit its IT coverage; where thin, add tests *first* (test-driven), then migrate.
- **Macro gate — AOT publish diagnostic count.** After each package *in `Verbara.Platform.Api`'s closure* migrates, re-run the AOT publish and confirm the count drops. The pilot (`Verbara.Platform.Storage.Postgres`) **must** show the drop from 50 — the empirical proof Option O's D.1 smoke could never produce (Sessions.Postgres was not in the closure).
- **JSONB byte-parity spot check** for JSON-heavy stores: a test asserting payloads written by the old path deserialize identically through the new path (and vice versa) during the migration window.

## Sequencing (cross-repo)

1. **Phase 1 — SDK foundation.** Build `Verbara.Sdk.Data.Npgsql` (facade + reader/param helpers) with unit tests. Migrate `Verbara.Sdk.Sessions.Postgres` as the SDK pilot (simplest surface, already the prior canary). Pack to both feeds (`/media/Data/Source/Verbara/local-nuget-feed/` + `Verbara.Platform/local-nuget-feed/` per `feedback_nuget_two_feeds.md`).
2. **Phase 2 — Platform pilot + proof-of-concept gate.** Migrate `Verbara.Platform.Storage.Postgres` (in Platform.Api closure → line 34 of its csproj references Dapper directly). Run the AOT publish and **confirm the diagnostic count drops from 50.** This validates the entire approach at Platform scale before the sweep.
3. **Phase 3 — Pro sweep.** Migrate the 7 Pro storage packages via the `dapper-aot-migration` subagent (one per package, fresh context), in waves: Wave 1 simplest (Dialer, EventStore, Cluster, Realtime), Wave 2 special-handling (Analytics, CallAnalytics, AgentAssist). Plus `Verbara.Platform.Identity` `DapperXmlRepository` and any direct `Verbara.Platform.Api` sites.
4. **Phase 4 — Flip + triple gate.** Edit `Verbara.Platform.Api.csproj` (`<IsAotCompatible>true>`, remove analyzer disables, add `<PublishAot>true>` + `<InvariantGlobalization>true>`). Triple gate: **G1** AOT publish clean (0 diagnostics, native ELF, no Verbara `.dll` in publish dir); **G2** full cross-repo test matrix green (Platform.Api 943 + Realtime 22 + Pro 1,329 + SDK ~3,079, zero new failures); **G3** AOT image E2E smoke (`docker/Dockerfile.api-aot`, `runtime-deps:10.0`; Setup Wizard + WebChat + Email + SIP smoke; 0 `PlatformNotSupportedException`, memory steady <250 MB, JSONB byte-identical vs IL baseline).
5. **Phase 5 — Image cutover (was Phase E).** Pack final SDK/Pro/Platform (drop experimental suffixes), tag + push 3 repos, CI builds AOT images to ghcr.io, regenerate `authorized-digests.json`, update SMB manuales (`01-instalacion.md` + `02-arranque.md`), OCI-deprecate old IL tags.
6. **Phase 6 — 24h AOT soak (mandatory gate before declaring Phase D complete).** Re-run the D-LK profile against the AOT image; compare to the IL baseline (p99 avg 60.66 ms, ~959M req, 0 fails, 12–13 Postgres conns sustained). **Pass criteria:** equal-or-better p99, 0 fails, no memory leak, Postgres connection count stable. A regression here blocks the production-readiness sign-off even though the image already ships.

## Files

**New (SDK repo):** `src/Verbara.Sdk.Data.Npgsql/` (executor facade + reader/param helpers + csproj) and `tests/Verbara.Sdk.Data.Npgsql.Tests/`.

**Per storage package (×9):** csproj diff — remove `<PackageReference Include="Dapper" />`; remove `Dapper.AOT` / `Verbara.Sdk.Dapper.Stubs` references if any were added during the D.1 canary; add `<PackageReference Include="Verbara.Sdk.Data.Npgsql" />`. Remove `using Dapper;`. Rewrite call sites to the facade; add `Map()` to each `*Row`; replace `TypeHandler` registrations with native/JSON handling.

**`Directory.Packages.props` (×3 repos):** drop `Dapper` and (if present) `Dapper.AOT` / `Verbara.Sdk.Dapper.Stubs` `PackageVersion` entries; add `Verbara.Sdk.Data.Npgsql`.

**Final flip:** `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` AOT properties; new `docker/Dockerfile.api-aot`.

**Docs:** this spec; `docs/decisions/0022-…md` Amendment §8 (removal execution report); `docs/plans/active/2026-05-19-phase-d-dapper-aot.md` → `archived/` with supersession note (D.1 Stubs work preserved as a documented empirical dead-end); SMB manuales image-tag updates.

## Risks

| # | Risk | P | Mitigation |
|---|------|---|------------|
| R1 | ~72 hand-written mappers introduce ordinal/null bugs | M | Name-based helpers (Piece 1b) remove the boilerplate; existing IT suite per store catches errors; coverage audited/extended before each migration |
| R2 | Volume (~447 sites) causes migration fatigue / inconsistency | M | Facade keeps sites one-liner-uniform; one subagent per package with a fixed playbook; `PostgresQueueStore`/`PostgresPurgeLogStore` as canonical templates |
| R3 | `DapperXmlRepository` (Phase B output) divergent shape | L | Single simple-shape file; migrate with the Platform package |
| R4 | A Pro store uses a Dapper feature not yet surfaced | L | Confirmed ZERO `SplitOn`/`QueryMultiple`/`dynamic`; per-package audit step before rewrite |
| R5 | Pilot AOT publish does **not** drop from 50 | L | Would indicate a non-Dapper residual blocker; Phase 2 is explicitly the cheap proof-of-concept gate that surfaces this before the sweep |
| R6 | JSONB round-trip regression during rewrite | M | JSON path is unchanged (same source-gen contexts); byte-parity spot check + IT round-trip assertions |
| R7 | New shared `Verbara.Sdk.Data.Npgsql` becomes its own maintenance burden | L | Tiny, stable surface (ADO.NET wrappers); fully owned; no external dependency; unit-tested |

## Verification (end-to-end)

```sh
# Pilot proof-of-concept (Phase 2) — the gate Option O could never pass
cd /media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api
dotnet publish Verbara.Platform.Api.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true -p:TrimmerSingleWarn=false \
  -o /tmp/aot-publish-phase-d/ 2>&1 | tee /tmp/aot-publish-phase-d.log
grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080" /tmp/aot-publish-phase-d.log   # target: trending to 0
file /tmp/aot-publish-phase-d/Verbara.Platform.Api                                                 # ELF 64-bit LSB pie executable
ls /tmp/aot-publish-phase-d/*.dll 2>/dev/null | wc -l                                              # 0 (no managed Verbara DLLs)

# Full test matrix (Phase 4 G2)
cd /media/Data/Source/Verbara/Verbara.Platform && dotnet test Verbara.Platform.slnx -c Release
cd /media/Data/Source/Verbara/Verbara.Sdk.Pro && dotnet test -c Release
cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet test -c Release
```
