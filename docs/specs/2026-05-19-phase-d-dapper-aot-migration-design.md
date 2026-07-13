# Phase D — Dapper AOT-Shipping Path (Technical Design)

**Status:** Approach pivoted 2026-05-19 (same day) — see Pivot Notice below · **Companion plan:** [`docs/plans/completed/2026-05-19-phase-d-dapper-aot.md`](../plans/completed/2026-05-19-phase-d-dapper-aot.md) (authoritative, post-pivot) · **Day 1 evidence:** [`docs/operations/phase-d-validation/2026-05-19-day-1-findings.md`](../operations/phase-d-validation/2026-05-19-day-1-findings.md) · **Drives:** [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) Amendment §7 closure + new Amendment §8 (Phase D execution report, written post-ship)

---

## 🚨 PIVOT NOTICE — 2026-05-19 (same day as initial draft)

**The original design below (Sections 1-11) framed Phase D as a "Dapper.AOT source-generator migration" project. Day 1 empirical testing on canary A (`Verbara.Sdk.Sessions.Postgres`) invalidated that framing.**

### What Day 1 proved

1. **R10 confirmed (CommandDefinition not intercepted)** — Dapper.AOT 1.0.52 does NOT intercept `conn.QueryAsync(new CommandDefinition(sql, param, ct))`. Only the simple `(sql, param)` overload is intercepted. Upstream [PR #153](https://github.com/DapperLib/DapperAOT/issues/153) addresses this — unmerged 12 months.
2. **R11 confirmed (CT-in-params generator bug)** — The canonical [DAP045](https://github.com/dapperlib/dapperaot/blob/main/docs/rules/DAP045.md) pattern `new { id, cancellationToken = ct }` produces broken C# (CS0103 + CS0162) in Dapper.AOT 1.0.48 AND 1.0.52. Longstanding bug.
3. **R12 — THE meta-blocker** — Even if every consumer call site were Dapper.AOT-intercept-compatible, **ILC still scans `Dapper.dll` itself and emits the ~50 fatal diagnostics from base Dapper code**. This is upstream issue [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168), filed by the user (Harol-Reina) 2026-03-16, 0 comments since.

### What Day 1 also proved (empirical inventory that resized the problem)

- 92% of Dapper call sites (~411 of ~447) are ALREADY in the simple shape that Dapper.AOT can intercept
- Only ~14 files (of ~100) need special handling (R9 DynamicParameters ∪ R10 CommandDefinition ∪ R11 CT-in-params)
- The mainstream codebase pattern propagates CT via `OpenConnectionAsync(ct)` but NOT into the Dapper call itself — migrating to Dapper.AOT is a NO-OP for CT semantics on 411 sites

### Pivot to Option O

User decision 2026-05-19: build **`Verbara.Sdk.Dapper.Stubs`** — the solution the user's own issue #168 already proposed:

> A minimal `Dapper.Stubs` assembly that provides the same public API surface as `Dapper.dll` but with empty AOT-annotated method bodies + working implementations only for the runtime-touched types (`GridReader`, `DbString`, `IWrappedDataReader`, `ICustomQueryParameter`).

**Why this works:**
- Replaces `Dapper.dll` in the runtime closure with an AOT-clean assembly → ILC stops emitting diagnostics → resolves #168/R12
- `Dapper.AOT` source-generated interceptors replace the runtime calls → stub bodies are never invoked
- Bypasses R10 because the stub `CommandDefinition` extension methods just compile (never invoked at runtime when interceptors win)
- Bypasses R11 because the buggy CT-in-params pattern isn't needed (sites that need mid-query CT use NpgsqlCommand raw instead)
- Bounded scope: Dapper public API is stable (last public-breaking change pre-2020); stubs assembly is ~10 types

**The authoritative execution plan is the COMPANION PLAN file** ([`docs/plans/completed/2026-05-19-phase-d-dapper-aot.md`](../plans/completed/2026-05-19-phase-d-dapper-aot.md), amended same day). Sections 1-11 below are PRESERVED AS HISTORICAL ARTIFACT of the initial design and the reasoning that led to the pivot.

A NEW design spec for the `Verbara.Sdk.Dapper.Stubs` assembly itself will be authored as part of Phase D.1 kickoff (fresh-context brainstorming session) and committed alongside the implementation.

---

## SUPERSEDED SECTIONS BELOW (preserved as historical context for the pivot decision)

## 1. Problem statement

`Verbara.Platform.Api` cannot publish as Native AOT today because Dapper 2.1.72 (the SQL↔object mapper on every Postgres-storage hot path) uses `System.Reflection.Emit.DynamicMethod` + `System.Type.MakeGenericType` to build per-query parameter emitters and row deserialisers at runtime. In an AOT image there is no JIT, so the first DB query throws `PlatformNotSupportedException: Dynamic code generation is not supported on this platform.`

The empirical AOT publish run on 2026-05-19 (ADR-0022 Amendment §7) surfaced ~40 Dapper-attributable `IL3050` (dynamic code) + `IL2046`/`IL2060`/`IL2067`/`IL2070`/`IL2075`/`IL2080` (trim-unsafe reflection) diagnostics. Suppressing them would let `ilc` complete but the runtime would crash immediately.

**Consequence of inaction:** the public `ghcr.io/verbara/platform/api:*` image continues to ship `Verbara.Sdk.Pro.*.dll` as portable IL, recoverable via ILSpy. Per the maintainer directive *"esta imagen siempre debe ser AOT"*, Pro v2.5.0-pro public release stays BLOCKED.

## 2. Solution: Dapper.AOT source-generator adoption

[Dapper.AOT](https://aot.dapperlib.dev/) is a build-time tool (same maintainer as Dapper) that uses C# interceptors to replace vanilla `cnn.Query<T>()` / `cnn.Execute()` calls with ahead-of-time-compiled `RowFactory<T>` + `CommandFactory<TParam>` instances. The interceptors are emitted into `obj/Debug/generated/Dapper.AOT.Analyzers/.../<file>.generated.cs` per source file and per call site (mapped by `[InterceptsLocation(file, line, col)]`).

**Why it fits this codebase:**

1. **Zero-touch source migration for the common case.** Existing `cnn.Query<Row>(sql, param)` calls compile through the interceptor unchanged.
2. **House style is already optimal.** Cross-repo grep confirms ZERO `SplitOn` / `QueryMultiple` / `dynamic` usage — the codebase is overwhelmingly simple `Query<T>` / `Execute` / `QuerySingleOrDefault`. Combined with the existing convention of `SELECT id AS Id, tenant_id AS TenantId, ...` explicit column aliases, the generated `RowFactory<T>` uses hash-based column matching that maps cleanly with no extra work.
3. **Per-package opt-in via `[module: DapperAot]`.** Migration risk is bounded per package — if a package can't be cleanly migrated, scope it out without affecting the rest.

**Why NOT the alternatives** (evaluated in ADR-0022 Amendment §7):

- **Hand-roll `NpgsqlCommand` readers**: 5-10× code volume, loses Dapper parameter-emission optimisations.
- **`PublishReadyToRun=true` + `PublishTrimmed=false`**: partial native code, Pro DLLs still decompilable — solves perf, not IP leak.
- **Encrypted IL + custom AssemblyLoadContext**: security-by-obscurity, key management overhead, not chosen.

## 3. Integration model

### 3.1 Per-storage-package configuration

```xml
<!-- <Package>.csproj -->
<ItemGroup>
  <PackageReference Include="Dapper" />
  <PackageReference Include="Dapper.AOT" />
</ItemGroup>
<PropertyGroup>
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

```csharp
// <Package>/Properties/AssemblyInfo.cs (or any top-level file in the project)
[module: DapperAot]
```

### 3.2 Generated artifact shape

Given source like:

```csharp
return await cnn.QuerySingleOrDefaultAsync<AuditRow>(
    "SELECT id AS Id, tenant_id AS TenantId, occurred_at AS OccurredAt, metadata AS Metadata FROM audit_events WHERE id = @id",
    new { id });
```

The source generator emits an interceptor in `obj/Debug/generated/Dapper.AOT.Analyzers/Dapper.CodeAnalysis.DapperInterceptorGenerator/<File>.generated.cs` that:

1. Wires a `RowFactory<AuditRow>` with column-name → property hash table (`NormalizedHash("id")` → `Id` setter, etc.).
2. Wires a `CommandFactory<TParam>` that emits parameter binding without `DynamicMethod`.
3. Replaces the original call site via `[InterceptsLocation]`.

**Critical detail — `{ get; init; }` rows:** the Dapper.AOT canonical example uses `{ get; set; }` with property-by-property assignment (`result.Id = reader.GetInt32(0);`). Our house style uses init-only properties (per `feedback_dapper_npgsql9_rows.md`). The generator should emit object-initializer pattern (`new AuditRow { Id = ..., TenantId = ... }`) to be compatible — **canary B validates this empirically**. If the generator does NOT support init-only properties → fallback documented in Section 6.

### 3.3 What changes vs what stays

| Layer | Before | After |
|---|---|---|
| `using Dapper;` | unchanged | unchanged |
| `cnn.Query<T>(sql, param)` call sites | unchanged | unchanged (intercepted at compile-time) |
| Row types (`class { get; init; }`) | unchanged | unchanged (assuming canary B validates init-only) |
| SQL strings + explicit aliases | unchanged | unchanged |
| `.csproj` PackageReference list | `Dapper` only | `Dapper` + `Dapper.AOT` |
| AssemblyInfo | (none Dapper-specific) | `[module: DapperAot]` |
| Runtime behavior | `DynamicMethod`-based deserialisers | source-generated `RowFactory<T>` |
| AOT publish diagnostics | ~40 IL3050/IL207x errors | 0 (target) |

## 4. Spike scope (Phase D.0)

### 4.1 Canary A — `Verbara.Sdk.Sessions.Postgres`

| Attribute | Value |
|---|---|
| Repo | Verbara.Sdk |
| Files | ~5 .cs (1 store + supporting types) |
| Surface | INSERT + GET-by-id + DELETE-by-id |
| JSONB? | No |
| Init-only rows? | Yes (all SDK row types) |
| Why this | Simplest possible — isolates packaging round-trip (pack to local-feed → restore in Platform.Api → AOT publish smoke) from JSONB / init-only concerns |

### 4.2 Canary B — `Verbara.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs`

| Attribute | Value |
|---|---|
| Repo | Verbara.Platform |
| File | 1 (`PostgresAuditStore.cs`) inside `Storage.Postgres` package (other stores stay un-migrated this iteration) |
| Surface | INSERT + SELECT-by-filter + JSONB metadata column |
| JSONB? | Yes (`metadata JSONB` parameter) |
| Init-only rows? | Yes |
| Why this | Validates the two structural risks (init-only + JSONB) in a Platform-side package. Tests `[module: DapperAot]` partial migration (rest of `Storage.Postgres` still vanilla Dapper in the same .csproj). |

### 4.3 Spike exit criteria

See companion plan Section "Phase D.0 — Spike". Summary:
1. Both canaries build clean (`TreatWarningsAsErrors=true`, `WarningLevel=9999`).
2. Tests verdes (SDK Sessions + Platform Audit Postgres IT).
3. Empirical AOT publish diagnostic count drop ≥ N (N = canary file count).
4. Generated interceptor source files verified by manual inspection (init-only object initializer pattern).
5. Playbook written.
6. Risk register updated empirically.

## 5. Sweep architecture (Phase D.1)

### 5.1 Subagent batching pattern (FCM-aligned)

Per `feedback_subagent_execution.md`: always use Subagent-Driven Development with risk-weighted batching.

| Wave | Packages | Subagent count | Why grouped |
|---|---|---|---|
| **3.A Foundation (batch)** | All 9 packages | 1 batch agent | Mechanical: add Dapper.AOT to Directory.Packages.props × 3, csproj × 9, AssemblyInfo × 9. No code changes. |
| **3.B Wave 1 (parallel)** | Pro.Dialer / EventStore / Cluster / Realtime storage | 4 agents | Independent stores, distinct repos within Pro. Validate Wave-1 patterns before committing to more. |
| **3.B Wave 2 (parallel)** | Pro.Analytics / CallAnalytics / AgentAssist storage | 3 agents | Informed by Wave 1 outcomes. |
| **3.B Wave 3 (single)** | Platform.Storage.Postgres + Identity.DataProtection + Api direct sites | 1 agent | Single repo, single nupkg, single release tag — atomic migration. |
| **3.C Integration (sequential)** | Pack chain SDK → Pro → Platform, then AOT publish smoke | direct execution (no subagent) | Strict dep chain, no parallelism possible. |

### 5.2 dapper-aot-migration subagent invocation template

The codebase already has a specialized `dapper-aot-migration` subagent that "understands the Verbara Platform and Pro codebase patterns (class-based rows with `{ get; init; }`, Npgsql 9, PostgreSQL 18)". One invocation per package, fresh context.

```text
Context: Phase D Dapper.AOT migration of <package-name> per
docs/specs/2026-05-19-phase-d-dapper-aot-migration-design.md.

Playbook: docs/specs/2026-05-19-phase-d-dapper-aot-playbook.md
(written by canary A + B spike, includes init-only object initializer
pattern, JSONB binding rules, and analyzer warning whitelist).

Scope: migrate ALL .cs files in src/<package-name>/ to use Dapper.AOT-
generated interceptors. Expected ~N files.

Acceptance gate (this invocation):
  1. Package builds clean with TreatWarningsAsErrors=true
  2. Package-level tests verdes
  3. obj/Debug/generated/Dapper.AOT.Analyzers/ contains interceptors
     emitted per source file (verify with grep <file>.generated.cs)
  4. Manual diff review: SQL strings + column aliases unchanged
     (we are NOT rewriting queries, only adding [module: DapperAot]
     + PackageReference + InterceptorsPreviewNamespaces).

Out of scope: do NOT touch row types, do NOT touch SQL, do NOT add
new tests. Refactor only build configuration + AssemblyInfo.
```

### 5.3 Cross-repo packaging strategy

Per `feedback_nuget_two_feeds.md` (Docker context only sees the Platform-local copy):

- During sweep: experimental version suffix `2.5.0-dapperaot.N` (SDK) / `2.5.0-pro-dapperaot.N` (Pro). N = session-incremented.
- Each pack writes to BOTH `../local-nuget-feed/` AND `Verbara.Platform/local-nuget-feed/`.
- At sweep close (3.C green): single final pack with real semver (`2.5.0` SDK / `2.5.0-pro` Pro), single commit per repo.

## 6. Risk register (initial)

See companion plan "Risk register" table. Detail per row:

| # | Risk | Detection point | Mitigation/fallback |
|---|---|---|---|
| R1 | `{ get; init; }` row materialization pattern | Canary B manual inspection of generated `RowFactory<AuditRow>` | Object initializer (`new T { ... }`) expected; if generator emits property setters → file upstream issue + temporarily relax canary row type to `{ get; set; }` OR use primary constructor pattern (.NET 10) |
| R2 | `NpgsqlDbType.Jsonb` parameter binding through generated `CommandFactory` | Canary B Postgres IT (audit INSERT) | If generator emits `DbType.Object` instead of `NpgsqlDbType.Jsonb` → `[DapperAot(false)]` on affected methods + raw `NpgsqlCommand` for those sites (~5 sites estimated in Platform + Pro combined) |
| R3 | Dapper.AOT analyzer warnings (DAP001 / DAP005 / etc.) | Canary A build (any package with `[module: DapperAot]`) | Catalog expected warnings, address per-warning vs add to package-level `NoWarn`. Document policy in playbook. |
| R4 | Build time inflation > 30s from source-gen | Canary A + B with `dotnet build -bl:before.binlog` vs post-migration binlog | If > 30s additional → `EmitCompilerGeneratedFiles=false` in CI, keep `true` locally for debugging |
| R5 | `Identity.DataProtection.DapperXmlRepository` (Phase B) divergent pattern | Wave 3 Platform sweep | Migrate as part of `Platform.Storage.Postgres` sweep — same package, same release tag, no special handling |
| R6 | Pro signing/licensing interacts with interceptors | Pro test suite during Wave 1 / 2 | `[module: DapperAot]` applied only to `*.Storage.Postgres` packages — Pro.Licensing untouched. If signing chain fails → re-evaluate per-package attribute placement. |
| R7 | Dapper.AOT latest version has .NET 10 regression | Initial version pin selection | Pin to version with last-published-≥30d. If runtime bug → downgrade + report upstream. |
| R8 | Multi-mapping / `dynamic` / `QueryMultiple` surfaces not detected by initial grep | Per-package agent invocation (exhaustive grep before migrating) | Sweep playbook includes exhaustive per-package grep template. If detected → `[DapperAot(false)]` per method + leave vanilla Dapper for those sites (but then Platform.Api can't ship AOT — escalate to maintainer) |

## 7. Triple gate validation (Phase D.2)

### 7.1 Gate 1 — AOT publish clean

```sh
cd src/Verbara.Platform.Api
dotnet publish Verbara.Platform.Api.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:InvariantGlobalization=true \
  -p:TrimmerSingleWarn=false \
  -o /tmp/aot-publish-phase-d/ 2>&1 | tee /tmp/aot-publish-phase-d.log
```

**Pre-G1 csproj edit** (single commit):

```xml
<!-- Verbara.Platform.Api.csproj -->
<IsAotCompatible>true</IsAotCompatible>             <!-- was false -->
<PublishAot>true</PublishAot>                       <!-- new -->
<InvariantGlobalization>true</InvariantGlobalization>  <!-- new -->
<!-- REMOVE: EnableTrimAnalyzer/EnableSingleFileAnalyzer/EnableAotAnalyzer disables -->
```

**Pass criteria:**
- Exit 0
- `grep -cE "IL2026|IL3050|IL2046|IL2060|IL2067|IL2070|IL2075|IL2080"` → 0
- `file /tmp/aot-publish-phase-d/Verbara.Platform.Api` → `ELF 64-bit LSB pie executable`
- `ls /tmp/aot-publish-phase-d/Verbara.*.dll` → no matches

### 7.2 Gate 2 — Tests verdes

```sh
cd Verbara.Platform && dotnet test Verbara.Platform.slnx -c Release   # 943 + 22
cd ../Verbara.Sdk.Pro && dotnet test -c Release                       # 1,329 (1,165 unit + 164 IT)
cd ../Verbara.Sdk && dotnet test -c Release                           # ~3,079 unit
```

Pass: 0 new failures vs baseline snapshot taken at kickoff.

Postgres integration tests are the critical surface — they exercise the generated `RowFactory<T>` / `CommandFactory<T>` in runtime.

### 7.3 Gate 3 — AOT image E2E smoke

```sh
docker build -f docker/Dockerfile.api-aot -t verbara/platform-api:phase-d-smoke .
docker image inspect verbara/platform-api:phase-d-smoke --format '{{.Size}}'
# expected: ~75-100 MB

docker compose -f docker/docker-compose.reference-smb.yml \
  -f docker/docker-compose.override.phase-d.yml up -d
```

Manual E2E steps (per `docs/manuales/smb/03-setup-wizard.md`):

| # | Flow | Stores exercised |
|---|---|---|
| 3.1 | `POST /api/v1/setup/wizard/*` (5 steps) | TenantStore, UserStore, PlanStore, AuthEventStore, RefreshTokenStore |
| 3.2 | `POST /api/v1/auth/login` + `GET /api/v1/auth/me` | AuthEventStore, RefreshTokenStore (JWT + Argon2id path) |
| 3.3 | WebChat session create + send msg + agent reply | WebChatSessionStore, MessageStore (JSONB payloads), ConversationStore |
| 3.4 | Email inbound webhook + outbound reply | MessageStore, MediaStore (attachments), Mail microservice (non-AOT, IL) |
| 3.5 | SIP REGISTER + INVITE → bridge → CDR | Pro.Realtime.Storage (PJSIP Realtime tables) + Pro.EventStore (CDR) |

**Pass criteria:**
- All 2xx responses
- `docker logs platform-api` → 0 `PlatformNotSupportedException: Dynamic code generation`, 0 `MissingMethodException`
- `docker stats` → memory < 250 MB steady-state, CPU < 50% cold-start
- DB state byte-identical vs IL baseline (JSONB columns compared with `pg_dump --data-only`)

## 8. Failure cascade & rollback

| Gate | Failure mode | Action |
|---|---|---|
| G1 | Residual `IL3050` / `IL207x` in file X | Route to `dapper-aot-migration` agent with file/line; agent fixes; re-run G1 |
| G2 | Test integración Y falla en runtime | Likely init-only or JSONB binding edge → debug, fix per-file, re-run G2. No revert. |
| G3 | Runtime exception in flow Z during smoke | Revert sweep of the package owning Z; keep rest; re-spike via agent with stack trace |

Per-package atomic revert is supported because each Wave package is a separate nupkg with separate version suffix during sweep. Phase D.2 final commit is the only "all or nothing" point.

## 9. Acceptance criteria

Phase D is **done** when ALL of:
- [ ] Triple gate G1 + G2 + G3 all pass on Platform.Api published as AOT
- [ ] `<IsAotCompatible>true</IsAotCompatible>` committed on `main`
- [ ] All 9 storage packages on `main` with `[module: DapperAot]` + `Dapper.AOT` PackageReference
- [ ] Playbook (`docs/specs/2026-05-19-phase-d-dapper-aot-playbook.md`) committed
- [ ] Risk register empirically updated in this spec
- [ ] ADR-0022 Amendment §8 (Phase D execution report) appended
- [ ] Memory writeup `project_phase_d_dapper_aot.md` indexed in `MEMORY.md`

Phase E (image cutover) starts only after the above is true.

## 10. Out of scope

- **Phase F 24h AOT soak** — optional. Plan `docs/plans/active/2026-04-27-r5.5-execution-plan.md` covers it.
- **Renderer / Mail microservices** — already AOT-clean per ADR-0022 Amendment §7 (no Dapper consumers in those projects).
- **Verbara.Platform.Realtime** — non-AOT by design per ADR-0022 Phase A. Realtime owns SignalR Hub, which has un-resolved AOT issues upstream in ASP.NET Core SignalR.
- **K8s manifests update** — image tags propagate through existing Helm `values.yaml` templating. No structural changes.
- **Performance tuning of generated `RowFactory<T>` / `CommandFactory<T>`** — empirical comparison vs vanilla Dapper deferred to Phase F.

## 11. References

- [Companion plan](../plans/completed/2026-05-19-phase-d-dapper-aot.md)
- [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) (Amendment §7 → unmask Dapper; future Amendment §8 → Phase D report)
- [Dapper.AOT canonical docs](https://aot.dapperlib.dev/) (via Context7 lookup 2026-05-19, library ID `/dapperlib/dapperaot`)
- `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/reference_dapper_consumers_inventory.md` — cross-repo inventory (verified 2026-05-19)
- `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/feedback_dapper_npgsql9_rows.md` — class-based `{ get; init; }` row convention
- `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/feedback_aot_image_directive.md` — maintainer's "esta imagen siempre debe ser AOT" rule
