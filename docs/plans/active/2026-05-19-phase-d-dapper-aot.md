# Phase D — Dapper.AOT Migration (ADR-0022)

**Created:** 2026-05-19 · **Owner:** Maintainer · **Target tag:** Platform `2.4.0` (or `2.5.0` if major-bump for AOT cutover) · **Estimated runway:** ~3 weeks post-spike kickoff · **Canonical spec:** [`docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md`](../../specs/2026-05-19-phase-d-dapper-aot-migration-design.md) · **Approved via ExitPlanMode 2026-05-19** (origin: `~/.claude/plans/continua-mutable-token.md`)

## Context

Per [ADR-0022 Amendment §7](../../decisions/0022-platform-api-aot-shipping-path.md), Phases A (SignalR Hub extraction, commits `ce8a76dc`+`df9ad7f7`) and B (DataProtection EF Core → Dapper, commit `73b4db73`) eliminated the §3 baseline AOT blockers (3× SignalR `IL3050` + 5× EF Core `IL2026`/`IL3050`). Phase C empirical publish on 2026-05-19 (commit `95757307`) confirmed those are gone, **but unmasked Dapper 2.1.72 as the residual blocker** — Dapper uses `System.Reflection.Emit.DynamicMethod` + `System.Type.MakeGenericType` for IL emission at runtime, which is fundamentally not AOT-safe (~40 `IL3050`/`IL207x` diagnostics surface).

Until this phase ships, `Verbara.Platform.Api` continues to publish as portable IL DLLs. The public `ghcr.io/verbara/platform/api:*` images ship `Verbara.Sdk.Pro.*.dll` as decompilable IL — anyone pulling the image can recover Pro commercial source via ILSpy. Per the maintainer's directive *"esta imagen siempre debe ser AOT"* (memoria `feedback_aot_image_directive.md`), this is unacceptable, and Pro v2.5.0-pro public release is BLOCKED until Phase D + Phase E close.

**Intended outcome:** flip `<IsAotCompatible>true</IsAotCompatible>` on `Verbara.Platform.Api.csproj`, ship a Native-AOT single-binary image (~75-100 MB vs ~250 MB IL), and cut the Pro v2.5.0-pro public release. Raises the IP-extraction attack cost from "open in ILSpy" to "IDA Pro for weeks."

## Pre-conditions (verify at kickoff)

- [ ] **v2.2.0 (Pro v2.4.0-pro consumer migration) shipped + tagged + pushed** (workstream coordination decision 2026-05-19 — ~6h plan in `~/.claude/plans/si-refactored-pascal.md`, materialize first).
- [ ] D-LK soak writeup committed (closure of 24h soak run 2026-05-18 04:37 PASS) — does not block Phase D but should close the open ledger.
- [ ] Platform on `main` at `2.3.x` (post-v2.2.0).
- [ ] Cross-repo dev workflow validated: `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/` + `rm -rf ~/.nuget/packages/verbara.sdk*/` + `dotnet restore` round-trip clean.
- [ ] All tests green cross-repo at kickoff (snapshot for regression comparison): Platform.Api.Tests 943 + Realtime.Tests 22 + Pro 1,329 (1,165 unit + 164 IT) + SDK ~3,079 unit.

## Scope

**9 storage packages / ~169 .cs files across 3 repos** (verified 2026-05-19 via `reference_dapper_consumers_inventory.md`):

| Repo | Package | Approx files |
|---|---|---|
| Verbara.Sdk | `Sessions.Postgres` | ~5 |
| Verbara.Sdk.Pro | `Dialer.Storage.Postgres`, `EventStore.Postgres`, `Analytics.Storage.Postgres`, `CallAnalytics.Storage.Postgres`, `AgentAssist.Storage.Postgres`, `Realtime.Storage.Postgres`, `Cluster.Storage.Postgres` | ~100 |
| Verbara.Platform | `Storage.Postgres` + `Identity.DataProtection` (`DapperXmlRepository.cs` from Phase B) + direct Dapper sites in `Api` | ~64 |

**Cross-repo grep confirms ZERO usage of `SplitOn` / `QueryMultiple` / `dynamic`** — the codebase is overwhelmingly simple `Query<T>` / `Execute` / `QuerySingleOrDefault` surface, which is exactly what Dapper.AOT intercepts cleanly. House style ("class-based rows with `{ get; init; }` + explicit column aliases") aligns with what Dapper.AOT's hash-based column matcher expects.

## Approach: Spike → Sweep paralelo

### Phase D.0 — Spike (2 canaries, ~2 days)

Branch `feat/phase-d-dapper-aot-spike` per repo, isolated worktree.

**Canary A — `Verbara.Sdk.Sessions.Postgres`** (simplest possible, no JSONB)
- 1 store, ~5 .cs files
- Validates: PackageReference + `InterceptorsPreviewNamespaces` MSBuild flag + `[module: DapperAot]` AssemblyInfo + interceptor generation + cross-repo pack-and-restore round trip

**Canary B — `Verbara.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs`** (canary for JSONB + init-only rows)
- Validates: `NpgsqlDbType.Jsonb` parameter binding through generated `CommandFactory`, `{ get; init; }` row materialization via generated `RowFactory<T>` (object initializer pattern `new Row { ... }` not property setters), coexistence with un-migrated stores in the same package

**Spike exit criteria:**
1. Both canaries build clean (`TreatWarningsAsErrors=true`, `WarningLevel=9999`).
2. Tests verdes: SDK Sessions tests + Platform Audit-related Postgres IT.
3. `dotnet publish -p:PublishAot=true` shows diagnostic count drop ≥ N (N = files migrated). If less → un-intercepted call site → investigate.
4. Manual inspection of `obj/Debug/generated/Dapper.AOT.Analyzers/.../*.generated.cs` confirms object-initializer pattern for `{ get; init; }` rows.
5. **Playbook written** to `docs/specs/2026-05-19-phase-d-dapper-aot-playbook.md`: per-package checklist, JSONB pattern, init-only confirmation, analyzer warning whitelist.
6. Risk register updated (R1-R8 from spec) with empirical outcomes.

### Phase D.1 — Sweep (parallel subagents, ~5 days)

Apply playbook to remaining 7 paquetes via the `dapper-aot-migration` subagent (1 invocation per package, fresh context, given playbook + risk register + scoped prompt).

**Wave 1 (parallel, 4 stores — independent)**
- `Verbara.Sdk.Pro.Dialer.Storage.Postgres`
- `Verbara.Sdk.Pro.EventStore.Postgres`
- `Verbara.Sdk.Pro.Cluster.Storage.Postgres`
- `Verbara.Sdk.Pro.Realtime.Storage.Postgres`

**Gate Wave 1 → Wave 2:** all packages build clean + tests verdes + interceptors emitted.

**Wave 2 (parallel, 3 stores — informed by Wave 1)**
- `Verbara.Sdk.Pro.Analytics.Storage.Postgres`
- `Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres`
- `Verbara.Sdk.Pro.AgentAssist.Storage.Postgres`

**Wave 3 (Platform sweep)**
- `Verbara.Platform.Storage.Postgres` (remaining ~63 .cs files after canary B)
- `Verbara.Platform.Identity/DataProtection/DapperXmlRepository.cs` (Phase B output)
- `Verbara.Platform.Api` direct Dapper sites (~3-5 files)

**Cross-repo packaging during sweep**: experimental version suffix `2.5.0-dapperaot.N` (SDK) / `2.5.0-pro-dapperaot.N` (Pro), pack to both `/media/Data/Source/Verbara/local-nuget-feed/` AND `Verbara.Platform/local-nuget-feed/` (per `feedback_nuget_two_feeds.md`). Final pack at sweep close drops the suffix.

### Phase D.2 — Triple gate validation (~1 day)

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

### All 3 repos
- `Directory.Packages.props` — add `<PackageVersion Include="Dapper.AOT" Version="X.Y.Z" />` (pin version after spike validates)

### Per storage package (9 total)
- `<Package>.csproj` — add `<PackageReference Include="Dapper.AOT" />` + `<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>`
- `<Package>/Properties/AssemblyInfo.cs` (or any top-level file) — add `[module: DapperAot]`

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

## Risk register (initial — to be empirically updated post-spike)

| # | Risk | P | Mitigation |
|---|------|---|------------|
| R1 | `{ get; init; }` rows need object-initializer pattern (docs example uses `{ get; set; }`) | M | Canary B validates; fallback: relax to `{ get; set; }` only on row types, or use primary constructor pattern |
| R2 | JSONB parameter binding loses `NpgsqlDbType.Jsonb` annotation in generated `CommandFactory` | M | Canary B validates; fallback: `[DapperAot(false)]` per method + raw `NpgsqlCommand` for those sites |
| R3 | Dapper.AOT analyzer warnings break `TreatWarningsAsErrors=true` | A | Catalog expected warnings (DAP001 / DAP005 / etc.), address vs `NoWarn` per-package, document policy in playbook |
| R4 | Build time inflation > 30s from source-gen over ~169 files | L | Measure pre/post in spike; mitigation: `EmitCompilerGeneratedFiles=false` in CI |
| R5 | `Identity.DataProtection.DapperXmlRepository` (Phase B fresh code) has divergent pattern | L | Migrate as part of Platform.Storage.Postgres sweep (same package, same release tag) |
| R6 | Pro signing / license validation interacts with interceptors | L | `[module: DapperAot]` applies only to `*.Storage.Postgres` packages; Pro.Licensing untouched |
| R7 | Dapper.AOT latest version has .NET 10 regression | L | Pin to version with last-published-≥30d; downgrade + upstream issue if found |
| R8 | Multi-mapping / dynamic surfaces not detected by initial grep (false negatives) | VL | Sweep playbook includes exhaustive per-package grep before migration |

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

## Timeline

```
[D+0-2]   v2.2.0 Pro v2.4.0-pro consumer ship  (pre-condition)
[D+3]     Phase D spike kickoff (canary A + canary B)
[D+5]     Spike triple gate verify → playbook + risk register frozen
[D+6-10]  Phase D sweep (Wave 1, Wave 2, Platform sweep, integration)
[D+11]    Phase D triple gate validation (G1 + G2 + G3)
[D+12]    Phase E cutover (pack + tag + push + digest regen + manuales)
[D+13-14] Phase F 24h AOT soak (opcional pero recomendado)
[D+15]    R5.5 Phase F closure + Production Readiness Review
```

Total Phase D + E runway: ~12 días tras v2.2.0 ship, conservador con buffer.
