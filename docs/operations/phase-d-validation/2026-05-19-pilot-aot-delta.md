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

## Verdict

- **Platform Dapper removal: DONE + verified AOT-neutral** (0 Platform-attributable diagnostics; full solution builds 0 warnings; Storage.Postgres.Tests 34/34; full Platform suite — see Task 2.7).
- **AOT-delta gate: BLOCKED on Phase 3 (Pro sweep).** Not a failure of the approach — a sequencing correction. The proof of the entire approach is now gated on completing the Pro side, after which a single re-publish validates the drop to 0.
