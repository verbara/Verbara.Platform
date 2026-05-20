# Phase D — Pilot AOT-delta gate result (Task 2.8)

**Date:** 2026-05-19 · **Branch:** `feat/phase-d-dapper-removal` · **Spec:** `docs/specs/2026-05-19-phase-d-dapper-removal-raw-npgsql-design.md`

## Measurement

| Stage | AOT diagnostic count | Attribution |
|---|---|---|
| **Baseline** (before Phase D, commit 821b855f) | **45** | 100% `Dapper.dll` internals (`SqlMapper`, `DefaultTypeMap`, `CommandDefinition`, `StructuredHelper`, `DapperRow.DapperRowTypeDescriptor`, `TypeExtensions`) |
| **After full Platform Dapper removal** (54 storage files + 4 Identity/Api/Mail consumers + 3 test files, all migrated; Dapper stripped from every Platform csproj + CPM) | **45** | **Still 100% `Dapper.dll` internals — ZERO attributable to any Platform assembly** |

Diagnostic command:
```sh
dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishAot=true -p:InvariantGlobalization=true -p:TrimmerSingleWarn=false -o /tmp/aot-phase2/
grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080" /tmp/aot-phase2.log   # → 45
```

## Root cause (decisive finding)

`Dapper.dll` is STILL in `Verbara.Platform.Api`'s publish closure — pulled **transitively by 7 Pro storage packages** that still declare `Dapper 2.1.72` as a dependency (verified in `src/Verbara.Platform.Api/obj/project.assets.json`):

```
Verbara.Sdk.Pro.AgentAssist.Storage.Postgres/2.5.0-pro   -> Dapper 2.1.72
Verbara.Sdk.Pro.Analytics.Storage.Postgres/2.5.0-pro     -> Dapper 2.1.72
Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres/2.5.0-pro -> Dapper 2.1.72
Verbara.Sdk.Pro.Cluster.Storage.Postgres/2.5.0-pro       -> Dapper 2.1.72
Verbara.Sdk.Pro.Dialer.Storage.Postgres/2.5.0-pro        -> Dapper 2.1.72
Verbara.Sdk.Pro.EventStore.Postgres/2.5.0-pro            -> Dapper 2.1.72
Verbara.Sdk.Pro.Realtime.Storage.Postgres/2.5.0-pro      -> Dapper 2.1.72
```

**The 45 diagnostics are intrinsic to `Dapper.dll` itself** — ILC scans the assembly's own reflective methods (`DynamicMethod`, `MakeGenericType`, `GetProperties`, `Activator.CreateInstance`) and emits them **regardless of how many consumer call sites exist or whether any consumer calls them**. This is the empirical confirmation of upstream issue [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168): the count is **binary on `Dapper.dll`'s presence in the closure**, not proportional to usage.

## Implications (re-scoping)

1. **The Phase 2 "pilot proof-of-concept" premise was false by construction.** Migrating only `Verbara.Platform.Storage.Postgres` (or even all of Platform) cannot drop the count, because `Dapper.dll` remains in the closure via Pro. This is the same structural reason the D.1 Stubs smoke saw a zero delta — just on the Pro side now. There is **no cheap intermediate validation**; the count drops to ~0 only when the **last** reference to `Dapper.dll` (Platform **and** all 7 Pro packages) is removed.

2. **The Platform migration is nonetheless fully successful and AOT-clean.** It introduced **zero** new AOT diagnostics — every one of the 45 is `Dapper.dll`-internal. This proves the `Verbara.Sdk.Data.Npgsql` facade + the 54-file migration are correct AOT-wise; the only residual blocker is `Dapper.dll`-via-Pro.

3. **The true gate requires Phase 3 (Pro sweep) first.** Once the 7 Pro storage packages are migrated off Dapper and repacked, `Dapper.dll` leaves Platform.Api's closure and the count is expected to drop to **0** (no non-Dapper residual was observed). Only then can `<IsAotCompatible>true>` + `<PublishAot>true>` (Task 2.4/Phase 4 flip) succeed.

## Verdict (interim — Platform only)

- **Platform Dapper removal: DONE + verified AOT-neutral** (0 Platform-attributable diagnostics; full solution builds 0 warnings; Storage.Postgres.Tests 34/34; full Platform suite — see Task 2.7).
- **AOT-delta gate: BLOCKED on Phase 3 (Pro sweep).** Not a failure of the approach — a sequencing correction. The proof of the entire approach is now gated on completing the Pro side, after which a single re-publish validates the drop to 0.

---

## FINAL GATE RESULT (2026-05-20, after Phase 3 Pro sweep) — ✅ PASSED

After migrating all 7 Pro storage packages off Dapper (repo `Verbara.Sdk.Pro`, branch `feat/dapper-removal`) and repacking `2.5.0-pro` Dapper-free, `Dapper.dll` left `Verbara.Platform.Api`'s closure entirely (verified: `project.assets.json` shows zero packages depending on Dapper). Re-running the AOT publish:

```
AOT diagnostic count:  0   (was 45)
Dapper mentions:       0
file /tmp/aot-final/Verbara.Platform.Api:
  ELF 64-bit LSB pie executable, x86-64, ... stripped   ← native AOT binary
```

**`Verbara.Platform.Api` now publishes as Native AOT with zero IL2026/IL3050/IL207x diagnostics.** The ADR-0022 goal is met: the public image can ship a native single binary instead of decompilable IL, closing the Pro-IP-leak exposure. Confirms empirically that the 45 baseline diagnostics were 100% intrinsic to `Dapper.dll`'s presence (issue #168) — removing the last reference dropped them to 0 with **no non-Dapper residual blocker**.

### Regression found + fixed during the Pro sweep (42P08)
The raw-Npgsql param-binding pattern `(object?)x ?? DBNull.Value` without an explicit `NpgsqlDbType` causes `42P08: could not determine data type of parameter` when the value is null in a type-ambiguous SQL position (`@X IS NULL OR col = @X`, `@X IS NOT NULL AND col = @X`, `COALESCE`, etc.). Caught by Pro `IntegrationTests` (DncCheckerTests + PostgresRealtimeStoreTests — 10 failures that PASS on `main`). A subagent's "pre-existing" claim was disproven by a `git checkout main` baseline run. Fixed repo-wide by the rule **every nullable `NpgsqlParameter` that can carry `DBNull.Value` gets an explicit `NpgsqlDbType`** (jsonb string params with an explicit `::jsonb` SQL cast excepted): ~103 params hardened in Pro (IntegrationTests → 276/276) + 159 params in Platform across 38 stores (Storage.Postgres.Tests → 34/34).

### Remaining (Phase 4/5/6, not yet done)
- **G1 AOT publish clean — ✅ DONE** (this result).
- **Csproj flip** — set `<IsAotCompatible>true>` + `<PublishAot>true>` + `<InvariantGlobalization>true>` on `Verbara.Platform.Api.csproj`; migrate `runtimeconfig.template.json` settings to `RuntimeHostConfigurationOption` (PublishAot warning).
- **G2 tests green** — full cross-repo suites (in progress).
- **G3 AOT image runtime smoke** — build the AOT Docker image, run the app, smoke the Setup Wizard + channels (proves runtime correctness, not just compile).
- **Phase 5 image cutover** + **Phase 6 24h AOT soak** (mandatory gate).
