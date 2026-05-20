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

### Phase 4 results (2026-05-20)

- **G1 AOT publish clean — ✅ DONE.** 0 diagnostics, native ELF (66 MB), 0 managed Verbara DLLs in publish output.
- **Csproj flip — ✅ DONE.** `Verbara.Platform.Api.csproj`: `<IsAotCompatible>true>` (analyzers on permanently) + `<EnableConfigurationBindingGenerator>true>` (so regular `dotnet build` matches the AOT publish — without it, analyzers flag 8 config-binding IL2026/IL3050 that the binding source generator resolves) + `<RuntimeHostConfigurationOption Include="Npgsql.EnableLegacyTimestampBehavior" Value="true" Trim="false" />` (replaces `runtimeconfig.template.json`, deleted). `PublishAot=true` is NOT asserted unconditionally (the AOT Dockerfile passes it) so `dotnet build/test/run` keep using the portable JIT path. Regular build: 0 warnings. csproj-driven AOT publish: 0 diagnostics, native ELF, no runtimeconfig warning. Root `Dockerfile` flipped to the AOT pathway (sdk + clang/zlib build stage → `runtime-deps:10.0` final stage, native ENTRYPOINT).
- **G2 tests green — ✅ DONE.** 0 real regressions (full-suite failures = pre-existing InMemory/ONNX + Testcontainers parallelism flakiness, all pass in isolation).
- **G3 AOT runtime smoke — PARTIAL: data layer ✅, HTTP/JSON layer ⚠️.** Ran the native ELF directly against a real `postgres:18-alpine`:
  - ✅ **Boots fully under AOT** (`Now listening`, `Application started`, `Hosting environment: Production`) — the entire DI composition (70 endpoint groups + all Pro packages) + config-validation pipeline execute with ZERO AOT runtime errors (no `PlatformNotSupportedException`/`MissingMethodException`).
  - ✅ **The migrated data-access code runs correctly under AOT against real Postgres:** `DatabaseMigrationService` applied migrations; the RBAC seeder ran; `OidcClientSecretEncryptionMigrator` completed; `NpgsqlXmlRepository` created DataProtection keys. **This is the core proof that the Dapper→Npgsql migration is AOT-correct at runtime.**
  - ⚠️ **NEW finding (out of Phase D scope, pre-existing): HTTP/JSON-layer AOT-readiness gaps.** `/health`, `/api/v1/branding`, `/openapi/v1.json` all return 500 under AOT. Confirmed cause for endpoints: `System.NotSupportedException: JsonTypeInfo metadata for type 'Verbara.Platform.Api.Endpoints.LoginRequest' was not provided` — some endpoint request/response DTOs are not registered in `ApiJsonContext` (it has 348 `[JsonSerializable]` entries but is incomplete); `/openapi` 500 is the well-known OpenAPI-doc-generation-uses-reflection issue under AOT. **ILC does not catch these — they are runtime-only System.Text.Json failures.** This is a separate AOT-readiness workstream (audit every endpoint DTO for `[JsonSerializable]` registration; decide OpenAPI strategy under AOT) that must close before the native image is shippable. It is NOT a Dapper-removal regression.

### Conclusion
**ADR-0022 Phase D (total Dapper removal) is COMPLETE and its goal met:** Dapper was the last *compile-time* AOT blocker; it is gone cross-repo, Platform.Api compiles to a clean native binary (0 diagnostics), and the migrated data layer runs correctly under AOT. **A shippable AOT image additionally requires closing the HTTP/JSON-layer AOT-runtime gaps (JSON source-gen completeness + OpenAPI)** — a distinct workstream surfaced by the G3 smoke, tracked separately before Phase 5 cutover.

### Remaining (separate workstreams)
- **HTTP/JSON AOT-readiness** (new) — register all endpoint DTOs in `ApiJsonContext`; resolve OpenAPI-under-AOT. Prerequisite for a functional AOT image.
- **Phase 5 image cutover** — requires publishing the Dapper-free Pro `2.5.0-pro` packages to GitHub Packages (the Docker build restores Pro from `github`, not the local feed) + digest regen + ghcr.io push.
- **Phase 6 24h AOT soak** (mandatory gate before production-readiness sign-off).
