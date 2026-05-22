# Phase D — Dapper AOT-Shipping Path (ADR-0022) [✅ CLOSED 2026-05-22]

> **✅ PHASE D CLOSED — gate passed 2026-05-22.** Final approach diverged from Option O (Stubs): instead of stub-shimming Dapper, Dapper was **removed entirely cross-repo** (SDK+Pro+Platform → `Verbara.Sdk.Data.Npgsql` facade; dead Stubs deleted in commit `ac01cb20`; permanent `BanDapperPackageReferences` guard added `09068bcd`). `Verbara.Platform.Api` now publishes **Native AOT** (45→0 IL diagnostics). Release v2.4.0 → v2.4.1 (4 images public+signed, ADR-0023). **Last gate — Phase 6/F 24h AOT soak — PASSED 2026-05-22 12:02Z** against `ghcr.io/verbara/platform/api:v2.4.1` (Docker, not Talos lab): 802,982,614 ok / **0 fail** · p99 **25.06 ms** · mem bounded 254–311 MiB (no leak) · pg_conns 11 flat · RestartCount 0. Soak artifacts: `tests/Verbara.Platform.LoadTests/soak-reports/soak-aot-v241-24h-20260521-1201.log` + `soak-24h-drift-20260521-1201.log`. See memory `project_phase_d_release_readiness.md`.

**Created:** 2026-05-19 · **Approach pivoted:** 2026-05-19 (same day) per [Day 1 empirical findings](../../operations/phase-d-validation/2026-05-19-day-1-findings.md) · **Owner:** Maintainer · **Target tag:** Platform `2.4.0` (or `2.5.0` if major-bump for AOT cutover) · **Revised runway:** ~2-3 weeks · **Canonical spec:** [`docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md`](../../specs/2026-05-19-phase-d-dapper-aot-migration-design.md)

> **Pivot summary (2026-05-19):** Original plan was "spike-then-sweep" using `Dapper.AOT` source generator alone. Day 1 empirical testing on canary A (`Verbara.Sdk.Sessions.Postgres`) revealed that the **real blocker is upstream issue [DapperLib/DapperAOT #168](https://github.com/DapperLib/DapperAOT/issues/168)** (filed by user 2026-03-16, 0 comments since) — ILC scans the base `Dapper.dll` and emits ~50 fatal diagnostics regardless of how many consumer call sites adopt the source generator. Pivoted to **Option O — build `Verbara.Sdk.Dapper.Stubs`** assembly per the user's own proposed solution in #168. R10 (CommandDefinition not intercepted) + R11 (CT-in-params generator bug) confirmed empirically but irrelevant once Stubs are in place.

## Context

Per [ADR-0022 Amendment §7](../../decisions/0022-platform-api-aot-shipping-path.md), Phases A (SignalR Hub extraction, commits `ce8a76dc`+`df9ad7f7`) and B (DataProtection EF Core → Dapper, commit `73b4db73`) eliminated the §3 baseline AOT blockers (3× SignalR `IL3050` + 5× EF Core `IL2026`/`IL3050`). Phase C empirical publish on 2026-05-19 (commit `95757307`) confirmed those are gone, **but unmasked Dapper 2.1.72 as the residual blocker** — Dapper uses `System.Reflection.Emit.DynamicMethod` + `System.Type.MakeGenericType` for IL emission at runtime, which is fundamentally not AOT-safe (~40 `IL3050`/`IL207x` diagnostics surface).

Until this phase ships, `Verbara.Platform.Api` continues to publish as portable IL DLLs. The public `ghcr.io/verbara/platform/api:*` images ship `Verbara.Sdk.Pro.*.dll` as decompilable IL — anyone pulling the image can recover Pro commercial source via ILSpy. Per the maintainer's directive *"esta imagen siempre debe ser AOT"* (memoria `feedback_aot_image_directive.md`), this is unacceptable, and Pro v2.5.0-pro public release is BLOCKED until Phase D + Phase E close.

**Intended outcome:** flip `<IsAotCompatible>true</IsAotCompatible>` on `Verbara.Platform.Api.csproj`, ship a Native-AOT single-binary image (~75-100 MB vs ~250 MB IL), and cut the Pro v2.5.0-pro public release. Raises the IP-extraction attack cost from "open in ILSpy" to "IDA Pro for weeks."

## Pre-conditions (verify at kickoff)

- [x] **v2.2.0 (Pro v2.4.0-pro consumer migration) shipped + tagged + pushed** — ✅ SHIPPED 2026-05-18 (commit `0de22761`, tag `v2.2.0`). Pre-condition already met when this plan was written.
- [ ] D-LK soak writeup committed (closure of 24h soak run 2026-05-18 04:37 PASS) — does not block Phase D but should close the open ledger.
- [ ] Platform on `main` at `2.4.0-rc` (current `Directory.Build.props`).
- [ ] Cross-repo dev workflow validated: `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/` + `rm -rf ~/.nuget/packages/verbara.sdk*/` + `dotnet restore` round-trip clean.
- [ ] All tests green cross-repo at kickoff (snapshot for regression comparison): Platform.Api.Tests 943 + Realtime.Tests 22 + Pro 1,329 (1,165 unit + 164 IT) + SDK ~3,079 unit.
- [ ] Baseline AOT publish log captured in `docs/operations/phase-d-validation/2026-05-19-baseline-aot-publish.log` (✅ done 2026-05-19) — 50 diagnostics, 100% Dapper-attributable.

## Scope (re-measured empirically 2026-05-19)

**9 storage packages / ~447 Dapper call sites across 3 repos.** Updated counts from Day 1 inventory:

| Surface | Count | Action under Option O |
|---|---|---|
| Total Dapper call sites cross-repo | ~447 | — |
| Sites in simple shape `conn.X<T>(sql, param)` (Dapper.AOT-compatible AS-IS) | ~411 (92%) | Adopt Dapper.AOT — auto-intercept, no code change |
| Sites with `new CommandDefinition(...)` (R10) | 16 in 5 files | Case-by-case: NpgsqlCommand raw OR refactor to simple shape |
| Sites passing `cancellationToken: ct` to a Dapper call (R11) | 18 in 5 files | Same — choose CT-preserving (NpgsqlCommand) or CT-dropping (simple shape) per site |
| Sites with `new DynamicParameters` (R9 — DAP015) | 14 in 11 files | Rewrite to typed/anonymous params OR NpgsqlCommand raw |
| **Files requiring special handling (R9 ∪ R10 ∪ R11)** | **~14 of ~100** | Targeted refactor (~3-5 days) |

**The 14 special-handling files** are concentrated and named: `PostgresSessionStore.cs` (SDK), `PostgresAuditStore.cs`, `RoleTemplateSeeder.cs`, `PostgresAuthEventStore.cs`, `PostgresConversationStore.cs`, `PostgresAgentStore.cs`, `PostgresPurgeLogStore.cs` (Platform), `PostgresLiveQueueSnapshotStore.cs`, `SnoopChannelManager.cs`, `PostgresClusterTransport.cs`, `PostgresIntervalSnapshotStore.cs`, `PostgresDncListStore.cs`, `PostgresCallAnalyticsStore.cs`, `PostgresCompletedSessionStore.cs` (Pro).

**Confirmed:** ZERO usage of `SplitOn` / `QueryMultiple` / `dynamic` across all 3 repos. House style ("class-based rows with `{ get; init; }` + explicit column aliases") aligns with Dapper.AOT's hash-based column matcher.

**CRITICAL META-FINDING ([#168](https://github.com/DapperLib/DapperAOT/issues/168), filed by user 2026-03-16):** Adopting `Dapper.AOT` source generator alone does NOT eliminate the 50 AOT publish diagnostics — ILC scans `Dapper.dll` itself and emits the diagnostics from there, regardless of consumer call site shape. **Dapper.Stubs is the only path that resolves this.**

## Approach: Option O — Build `Verbara.Sdk.Dapper.Stubs` + adopt Dapper.AOT cross-repo

Per [Day 1 findings](../../operations/phase-d-validation/2026-05-19-day-1-findings.md), the original Spike → Sweep approach is **ABANDONED** because empirical testing proved `Dapper.AOT` alone cannot resolve the AOT publish blocker. The real blocker is upstream issue #168 (ILC scans `Dapper.dll`). Solution: ship a parallel `Verbara.Sdk.Dapper.Stubs` assembly that satisfies ILC while interceptors handle the runtime calls.

### Phase D.1 — Build `Verbara.Sdk.Dapper.Stubs` (~5-7 days)

New project in Verbara.Sdk repo (MIT-licensed, candidate for upstream contribution to `dapperlib/dapperaot`):

- **Goal:** mirror the public API surface of `Dapper.dll` so consumer code compiles against either real Dapper OR these stubs; ship stubs with AOT-clean annotations + empty bodies that ILC can trim.
- **Mirrored types** (1:1 public API surface, with `[RequiresDynamicCode("…")] + [RequiresUnreferencedCode("…")]` annotations + `throw new NotSupportedException("…")` bodies):
  - `SqlMapper` static class — all `Query*` / `Execute*` / `ExecuteScalar*` extension methods + their async variants
  - `CommandDefinition` struct — passthrough constructor + properties (must compile; never invoked at runtime when interceptors win)
  - `DynamicParameters` class — same passthrough policy
  - `DefaultTypeMap`, `CustomPropertyTypeMap`, `SqlMapper.ITypeMap` — passthrough definitions
- **Working implementations** (interceptors call these at runtime — must NOT throw):
  - `GridReader` base class (abstract; `AotGridReader` extends it and calls `OnBeforeGrid`/`OnAfterGrid`)
  - `DbString` class (properties read at runtime by generated `DbStringHelpers`)
  - `IWrappedDataReader` interface (referenced by generated `AotWrappedDbDataReader`)
  - `SqlMapper.ICustomQueryParameter` interface (implemented by `PostgresSessionStore.JsonbParameter` — runtime calls `AddParameter`)
  - `SqlMapper.ITypeHandler` interface (referenced by tests that register custom type handlers — optional pending audit)
- **Spike validation:** after stubs are built, redo the canary on `Verbara.Sdk.Sessions.Postgres`:
  - Add `Dapper.AOT` PackageReference + `[module: DapperAot]`
  - Replace `Dapper` PackageReference with `Verbara.Sdk.Dapper.Stubs`
  - Build → expect 0 warnings (stubs satisfy compilation)
  - Pack SDK → pivot to Platform → AOT publish → **expect 0 IL3050/IL207x diagnostics from the Sessions.Postgres path**
- **Exit criteria for D.1:**
  1. Verbara.Sdk.Dapper.Stubs builds clean cross-target (net8.0 + net10.0).
  2. `Verbara.Sdk.Sessions.Postgres` builds clean against stubs (all 6 sites OK).
  3. AOT publish of Platform.Api with Sessions.Postgres using stubs → diagnostic count drops by the # attributable to Sessions surface (verifies stubs concept).
  4. Sessions.Postgres existing Testcontainers tests pass — runtime behavior identical.

### Phase D.2 — Sweep adoption cross-repo (~3-5 days, parallel subagents)

Apply pattern from D.1 to all 9 storage packages via the `dapper-aot-migration` subagent. Per-package mechanical change:

```diff
- <PackageReference Include="Dapper" />
+ <PackageReference Include="Dapper" ExcludeAssets="runtime" />
+ <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
+ <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
```

Plus new `AssemblyInfo.cs` with `[module: DapperAot]` + `<InterceptorsPreviewNamespaces>` MSBuild property.

**Wave 1 (parallel, 4 stores — independent + simplest patterns)**
- `Verbara.Sdk.Pro.Dialer.Storage.Postgres`
- `Verbara.Sdk.Pro.EventStore.Postgres`
- `Verbara.Sdk.Pro.Cluster.Storage.Postgres`
- `Verbara.Sdk.Pro.Realtime.Storage.Postgres`

**Wave 2 (parallel, 3 stores — touch the ~14 special-handling files)**
- `Verbara.Sdk.Pro.Analytics.Storage.Postgres` — includes `PostgresLiveQueueSnapshotStore` (4 CommandDefinition sites)
- `Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres` — includes `PostgresCallAnalyticsStore` (DynamicParameters)
- `Verbara.Sdk.Pro.AgentAssist.Storage.Postgres` — includes `SnoopChannelManager` (CommandDefinition)

**Wave 3 (Platform sweep)**
- `Verbara.Platform.Storage.Postgres` — includes RoleTemplateSeeder + PostgresAuditStore + 5 DynamicParameters stores
- `Verbara.Platform.Identity/DataProtection/DapperXmlRepository.cs` (Phase B output) — simple shape, no special handling expected
- `Verbara.Platform.Api` direct Dapper sites

**Cross-repo packaging during sweep:** pack `Verbara.Sdk.Dapper.Stubs` + updated SDK + updated Pro to `/media/Data/Source/Verbara/local-nuget-feed/` AND `Verbara.Platform/local-nuget-feed/` (per `feedback_nuget_two_feeds.md`). Experimental version suffixes: `2.1.3-aotstubs.N` (SDK) / `2.5.0-pro-aotstubs.N` (Pro). Final pack at sweep close drops the suffix.

### Phase D.3 — Special-handling site remediation (~3-5 days, can overlap with D.2)

For the ~32 sites in 14 files that use `CommandDefinition` / `DynamicParameters` / `cancellationToken: ct`, decide per-site:

| Pattern | Default action | Rationale |
|---|---|---|
| `CommandDefinition` with `cancellationToken: ct` where CT-mid-query is critical (hot path, long queries) | **Refactor to NpgsqlCommand raw** (preserves CT semantics, AOT-clean by construction) | NpgsqlCommand is AOT-compatible out of the box; ~10-20 lines per site |
| `CommandDefinition` with `cancellationToken: ct` where CT is cosmetic (short queries, batch operations) | **Refactor to simple shape** (drop mid-query CT; connection-level timeout from NpgsqlDataSource) | No regression — mainstream 411 sites already work this way |
| `DynamicParameters` for dynamic WHERE building | **Build SQL fully formed + use anonymous types** OR **NpgsqlCommand raw with parameter loop** | Per DAP015 doc + #157 (open upstream) |
| `DynamicParameters` for SQL output parameters | **NpgsqlCommand raw with explicit `ParameterDirection.Output`** | Dapper.AOT's `[DbValue]` workaround is unergonomic |

Per-file decision matrix lives in [day-1-findings.md](../../operations/phase-d-validation/2026-05-19-day-1-findings.md). Wave 3 (Platform sweep) does the Platform files. Pro files done in Wave 2.

### Phase D.1 — Verbara.Sdk.Dapper.Stubs ✅ SHIPPED 2026-05-19

Sub-deliverable D.1 shipped 2026-05-19 in 10 commits on Verbara.Sdk `feat/dapper-stubs` branch. See [`docs/plans/completed/2026-05-19-verbara-sdk-dapper-stubs.md`](../completed/2026-05-19-verbara-sdk-dapper-stubs.md). Outcomes:
- 30 Dapper 2.1.72 public types + 134 method stubs mirrored 1:1, all with `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`
- 16/16 stub tests PASS (PublicApiSurface + AotAnnotations + behavioral CommandDefinition/DynamicParameters/SqlMapperStub)
- Sessions.Postgres canary adopted (Phase E commit `08a36b8c` on Verbara.Sdk): stub `Dapper.dll` (50KB) confirmed in test `bin/` output replacing real Dapper.dll (240KB); 14/16 Sessions.Postgres.Tests fail with exact predicted `NotSupportedException — Dapper.AOT did not intercept this call site` (R10 confirmed empirically; PostgresSessionStore uses `CommandDefinition` overload in 6/6 sites)

**Phase F closure gap finding** ([`docs/operations/phase-d-validation/2026-05-19-stubs-smoke-findings.md`](../../operations/phase-d-validation/2026-05-19-stubs-smoke-findings.md)): the Platform.Api AOT publish diagnostic count stayed at 50 (zero delta from baseline) because **Sessions.Postgres is NOT in Platform.Api's closure** — CPM declares versions but doesn't pull packages; no Platform project `<PackageReference>`s Sessions.Postgres. The real AOT-diagnostic-delta validation requires migrating a storage package that IS in Platform.Api's closure — that's `Verbara.Platform.Storage.Postgres` (Phase D.2 first target).

**Re-scoped: Phase F (AOT publish triple gate validation) moves to Phase D.2 first-package close**, not Phase D.1.

### Phase D.4 — Triple gate validation (~1 day) — REVISED scope per Phase F closure gap finding

Runs AFTER the first Platform storage package migrates (likely `Verbara.Platform.Storage.Postgres`). Until then, the gate cannot validate at Platform scale.

| Gate | What it checks | Pass criteria |
|---|---|---|
| **G1 — AOT publish clean** | `dotnet publish src/Verbara.Platform.Api -c Release -r linux-x64 -p:PublishAot=true -p:InvariantGlobalization=true --self-contained true` | Exit 0; `grep -E "IL2026\|IL3050\|IL2046\|IL2060\|IL2067\|IL2070\|IL2075\|IL2080"` returns 0 matches; output is native ELF (`file` reports `ELF 64-bit LSB pie executable`); no `.dll` files in publish dir |
| **G2 — Tests verdes** | Full cross-repo test matrix | Platform.Api.Tests 943 + Realtime.Tests 22 + Pro 1,329 + SDK ~3,079 — zero new failures vs pre-Phase-D baseline |
| **G3 — AOT image E2E smoke** | Build AOT image (`docker/Dockerfile.api-aot`, `runtime-deps:10.0` base), bring up SMB reference stack with override, execute Setup Wizard + 3 V1 channels (WebChat / Email / Voz) | All steps return 2xx; logs show 0 `PlatformNotSupportedException: Dynamic code generation`, 0 `MissingMethodException`; memory steady < 250 MB; DB state coherente vs IL baseline (JSONB payloads byte-identical) |

**Pre-G1 csproj edit (single commit):**
```xml
<!-- Verbara.Platform.Api.csproj -->
<IsAotCompatible>true</IsAotCompatible>       <!-- was false -->
<!-- remove: EnableTrimAnalyzer / EnableSingleFileAnalyzer / EnableAotAnalyzer disables -->
<PublishAot>true</PublishAot>
<InvariantGlobalization>true</InvariantGlobalization>
```

**Failure cascade:**
- G1 fail → route to `dapper-aot-migration` agent with the failing file/line, fix, re-run G1.
- G2 fail → debug runtime regression (likely init-only or JSONB edge), fix per-file, re-run G2.
- G3 fail → revert the specific package sweep, re-spike via agent with the stack trace.

### Phase E — Image cutover (~1 day)

E.1 Pack final SDK + Pro (drop `-dapperaot.N` suffix): SDK `2.5.0`, Pro `2.5.0-pro`, Platform `2.4.0` (or `2.5.0`).
E.2 Tag + push (3 repos).
E.3 CI builds AOT images, pushes to ghcr.io: `platform/api`, `platform/realtime` (unchanged), `platform/web` (unchanged).
E.4 Regenerate `verbara-website/data/authorized-digests.json` — append new AOT image digest to every active license's `AuthorizedImageDigests` claim. Old IL digests stay temporarily for rollback window.
E.5 Update SMB manuales: `docs/manuales/smb/01-instalacion.md` + `02-arranque.md` with new image tags + license refresh instructions.
E.6 OCI annotation: `crane mutate ghcr.io/verbara/platform/api:2.3.x --annotation org.opencontainers.image.deprecated=true` for old IL tags.
E.7 `git mv docs/plans/active/2026-05-19-phase-d-dapper-aot.md docs/plans/completed/`.

### Phase F — 24h AOT soak (optional, ~2 days)

Re-run D-LK profile against `ghcr.io/verbara/platform/api:2.4.0` AOT image in Talos lab. Compare to IL baseline 2026-05-18 (p99 avg 60.66 ms, ~959M req, 0 fails, 12-13 Postgres conns sustained). Expected: equal or better.

## Critical files to modify

### NEW project: `Verbara.Sdk.Dapper.Stubs` (in Verbara.Sdk repo)
- `src/Verbara.Sdk.Dapper.Stubs/Verbara.Sdk.Dapper.Stubs.csproj` — multi-target net8.0;net10.0, MIT-licensed package; `<IsAotCompatible>true</IsAotCompatible>` + all AOT analyzers ON
- `src/Verbara.Sdk.Dapper.Stubs/SqlMapper.cs` — passthrough static class with all Query/Execute extension methods (`throw NotSupportedException` + AOT annotations)
- `src/Verbara.Sdk.Dapper.Stubs/CommandDefinition.cs` — passthrough struct
- `src/Verbara.Sdk.Dapper.Stubs/DynamicParameters.cs` — passthrough class
- `src/Verbara.Sdk.Dapper.Stubs/GridReader.cs` — abstract base class with WORKING `OnBeforeGrid` / `OnAfterGrid` virtual methods
- `src/Verbara.Sdk.Dapper.Stubs/DbString.cs` — class with WORKING property accessors
- `src/Verbara.Sdk.Dapper.Stubs/IWrappedDataReader.cs` — interface definition
- `src/Verbara.Sdk.Dapper.Stubs/ICustomQueryParameter.cs` — interface definition
- `Tests/Verbara.Sdk.Dapper.Stubs.Tests/` — unit tests that verify stubs throw correctly + working types behave correctly

### All 3 repos `Directory.Packages.props`
- add `<PackageVersion Include="Dapper.AOT" Version="1.0.52" />` (pinned)
- add `<PackageVersion Include="Verbara.Sdk.Dapper.Stubs" Version="..." />` (pinned to current Stubs build)

### Per storage package (9 total) — csproj diff
```diff
- <PackageReference Include="Dapper" />
+ <PackageReference Include="Dapper" ExcludeAssets="runtime" />
+ <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
+ <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
```
Plus `<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>` + new `AssemblyInfo.cs` with `[module: DapperAot]`.

### Special-handling files (14 across 3 repos)
- See [Day 1 findings — file inventory](../../operations/phase-d-validation/2026-05-19-day-1-findings.md). Per-site rewrite to NpgsqlCommand raw OR simple-shape refactor per the decision matrix in D.3.

### Platform.Api (final flip)
- `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` — flip `<IsAotCompatible>false→true</IsAotCompatible>`, remove analyzer disables, add `<PublishAot>true</PublishAot>` + `<InvariantGlobalization>true</InvariantGlobalization>`
- `src/Verbara.Platform.Api/Program.cs:1124` already AOT-clean post-Phase C
- New: `docker/Dockerfile.api-aot` (single-binary image based on `mcr.microsoft.com/dotnet/runtime-deps:10.0`)

### Documentation
- `docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md` — full spec (this file)
- `docs/specs/2026-05-19-phase-d-dapper-aot-playbook.md` — written post-spike, drives sweep subagents
- `docs/decisions/0022-platform-api-aot-shipping-path.md` — append Amendment §8 (Phase D execution report + Phase E completion)
- `docs/manuales/smb/01-instalacion.md` + `02-arranque.md` — image tags + license refresh
- `docs/plans/active/2026-05-19-phase-d-dapper-aot.md` (this plan) → `completed/` on ship

### Memory writeups (post-ship)
- `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_phase_d_dapper_aot.md`
- `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_phase_e_aot_cutover.md`
- Update `MEMORY.md` index

## Reuse / referenced patterns (existing code)

- **`feedback_dapper_npgsql9_rows.md`**: class-based rows with `{ get; init; }`, never positional records — keep this constraint; verify Dapper.AOT object-initializer pattern in canary B.
- **`feedback_nuget_two_feeds.md`**: pack to both `/media/Data/Source/Verbara/local-nuget-feed/` AND `Verbara.Platform/local-nuget-feed/` after every SDK/Pro pack. Sweep MUST follow this.
- **`Verbara.Sdk.Sessions.Postgres/PostgresSessionStore.cs`** (canary A): exemplifies simplest Dapper surface in the codebase, ideal spike target.
- **`Verbara.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs`** (canary B): exemplifies JSONB binding + init-only rows.
- **`dapper-aot-migration` subagent**: pre-existing specialized agent that "understands the Verbara Platform and Pro codebase patterns (class-based rows with {get; init;}, Npgsql 9, PostgreSQL 18)". One invocation per package during sweep, fresh context, prompted with playbook.

## Risk register (post-Day-1 empirical update)

| # | Risk | P | Status | Mitigation |
|---|------|---|---|---|
| R1 | `{ get; init; }` rows need object-initializer pattern | M | Untested under Option O | Stubs path: Dapper.AOT still emits the row factory; will surface during D.1 Sessions.Postgres canary |
| R2 | JSONB binding via `ICustomQueryParameter` | M | Mitigated by Stubs design | Stubs ship `ICustomQueryParameter` as a working interface (not a stub); `PostgresSessionStore.JsonbParameter` runtime call hits the real impl |
| R3 | Dapper.AOT analyzer warnings break `TreatWarningsAsErrors=true` | A | Validated 2026-05-19 — 0 warnings emitted in spike | None needed; build clean |
| R4 | Build time inflation from source-gen over ~169 files | L | Validated 2026-05-19 — 10s build, no concern | None |
| R5 | `Identity.DataProtection.DapperXmlRepository` (Phase B output) divergent pattern | L | Single file, simple shape | Migrate as part of Platform.Storage.Postgres sweep |
| R6 | Pro signing/licensing interacts with interceptors | L | Out of scope (Pro.Licensing has no Dapper) | None |
| R7 | Dapper.AOT version regression | L | Pin 1.0.52 (2026-05-16) — within 30d window relaxed | Trivial downgrade if needed |
| R8 | Multi-mapping / `dynamic` / `QueryMultiple` not detected by initial grep | VL | Confirmed ZERO cross-repo 2026-05-19 | None |
| **R9** | **`DynamicParameters` not interceptable by Dapper.AOT (DAP015)** | **CONFIRMED** | 14 sites in 11 files | Rewrite to typed/anonymous params OR NpgsqlCommand raw per D.3 decision matrix |
| **R10** | **`new CommandDefinition(...)` overload not intercepted by Dapper.AOT** | **CONFIRMED** | 16 sites in 5 files; upstream PR #153 unmerged 12 months | Bypass via Stubs (CommandDefinition compiles against stub; never invoked at runtime) + per-site decision per D.3 |
| **R11** | **DAP045 canonical CT-in-params pattern emits broken C# (CS0103 + CS0162)** | **CONFIRMED in 1.0.48 + 1.0.52** | Generator bug in `GetCancellationToken` override | Avoid the CT-in-params pattern entirely (use NpgsqlCommand raw for sites where CT-mid-query matters); file upstream issue + 10-line PR |
| **R12** | **ILC scans `Dapper.dll` and emits ~50 fatal diagnostics regardless of source-gen interception** | **CONFIRMED — issue #168 filed by user 2026-03-16; 0 comments since** | THE meta-blocker | **Option O — `Verbara.Sdk.Dapper.Stubs`** resolves by replacing Dapper.dll in the runtime closure with an AOT-clean stub assembly |
| R13 | Stubs assembly drifts from real Dapper API as Dapper evolves | L | Dapper API is stable (last public-breaking change pre-2020) | API mirroring test: contract-compare stubs.dll vs dapper.dll public API surface; CI gate |
| R14 | Some interceptor calls a stub method that should be working (not throwing) — runtime crash | M | Catch in D.1 canary | Expand stubs `working impl` set iteratively; runtime tests are the verifier |

## Verification (end-to-end)

```sh
# 1. Confirm AOT publish is clean (Gate 1)
cd /media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api
dotnet publish Verbara.Platform.Api.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true \
  -p:TrimmerSingleWarn=false \
  -o /tmp/aot-publish-phase-d/ 2>&1 | tee /tmp/aot-publish-phase-d.log
grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080" /tmp/aot-publish-phase-d.log
# expected: 0

file /tmp/aot-publish-phase-d/Verbara.Platform.Api
# expected: ELF 64-bit LSB pie executable, x86-64, ...

ls /tmp/aot-publish-phase-d/*.dll 2>/dev/null | wc -l
# expected: 0 (or only ICU/native deps, no Verbara.* DLLs)

# 2. Full test sweep (Gate 2)
cd /media/Data/Source/Verbara/Verbara.Platform && dotnet test Verbara.Platform.slnx -c Release
cd /media/Data/Source/Verbara/Verbara.Sdk.Pro && dotnet test -c Release
cd /media/Data/Source/Verbara/Verbara.Sdk && dotnet test -c Release
# expected: 0 new failures across all three

# 3. AOT image smoke (Gate 3)
cd /media/Data/Source/Verbara/Verbara.Platform
docker build -f docker/Dockerfile.api-aot -t verbara/platform-api:phase-d-smoke .
docker image inspect verbara/platform-api:phase-d-smoke --format '{{.Size}}'
# expected: ~75-100 MB (vs ~250 MB IL)

docker compose -f docker/docker-compose.reference-smb.yml \
  -f docker/docker-compose.override.phase-d.yml up -d
# manually execute setup wizard + WebChat session + Email inbound + SIP REGISTER smoke
# expected: 0 PlatformNotSupportedException in platform-api logs, all 2xx

# 4. ADR amendment + memory update + plan move
# (manual, post-validation)
```

## Out of scope (deferred)

- **Phase F 24h AOT soak** — optional, ~2 days. Recommended pero no bloquea cutover. Can ship Phase E + execute Phase F as immediate follow-up.
- **Renderer / Mail microservices** — already AOT-clean, no Dapper consumers. Out of Phase D scope.
- **Verbara.Platform.Realtime** — non-AOT by design (owns SignalR Hub per ADR-0022 Phase A). Out of Phase D scope.
- **K8s manifests update** — image tags propagate via existing Helm values templating. No structural changes required.

## Timeline (revised post-pivot)

```
[D+0]     Day 0 baseline + Day 1 empirical + pivot to Option O    ✅ 2026-05-19
[D+1-7]   D.1 — Build Verbara.Sdk.Dapper.Stubs + Sessions.Postgres canary
[D+8-12]  D.2 — Sweep adoption in 8 remaining storage packages (parallel subagents)
                Overlaps with D.3
[D+9-13]  D.3 — Special-handling site remediation (14 files, ~32 sites)
                Per-site decision matrix: NpgsqlCommand raw vs simple-shape refactor
[D+14]    D.4 — Triple gate validation (AOT publish → 0 diagnostics target)
[D+15]    Phase E cutover (pack + tag + push + digest regen + manuales)
[D+16-17] Phase F 24h AOT soak (optional)
[D+18]    R5.5 Phase F closure + Production Readiness Review
[parallel] Submit Verbara.Sdk.Dapper.Stubs as upstream PR to dapperlib/dapperaot#168
[parallel] Submit R11 fix (10-line generator change) upstream
```

Total Phase D + E runway: ~2.5 weeks (vs original "5-6 weeks" panic estimate; bounded scope confirmed by Day 1 empirical inventory).
