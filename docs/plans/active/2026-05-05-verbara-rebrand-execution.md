# Plan: AHH Housekeeping + Verbara Rebrand Execution

## Context

ADR-0016 (accepted 2026-05-03) decided to rebrand from "Asterisk.*" to "Verbara.*" before public launch — Sangoma/Digium holds the "Asterisk" trademark and has enforcement history (FreePBX forced rename). The product is **pre-launch with zero customers**, making this the optimal moment: minimum blast radius, no backward compat obligations.

Additionally, the Auth Hotpath Hardening plan sits in `docs/plans/active/` with 0/56 checkboxes marked despite the work shipping as v1.14.0-v1.14.6 (8 commits, 5 ADRs). Housekeeping needed.

**Decisions confirmed by user:**
- Version strategy: **2.0.0 major bump** (semver-correct for breaking namespace change)
- Scope: **Single unified plan** with pre-alignment step (bump Pro's SDK pin 1.15.1 → 1.15.3 before rename)
- Config section: See deep analysis below

---

## Deep Analysis: Product Identity Layers

The original 3 options (keep/rename-config/rename-both) were incomplete. The product identity has **5 distinct layers**, each with different semantics:

### Layer 1: Code Identity (namespaces, packages, assemblies)
**Decision: ALL rename to `Verbara.*`**
- 3,078 .cs files, 81 packages, 4 repos

### Layer 2: Configuration Section (`"Asterisk:Ami"`, `"Asterisk:Ari"`)
**Decision: KEEP as `"Asterisk"`**

Rationale (Redis analogy): The config section names the TARGET TECHNOLOGY, not our product. StackExchange.Redis uses `"Redis"` config section. Npgsql connects to config key `"PostgreSQL"`. Our SDK connects to Asterisk PBX → the section `"Asterisk"` is semantically correct.

Impact: ZERO changes to docker-compose env vars (`Asterisk__Ami__Hostname`, etc.) or K8s manifests. Operators deploying Verbara see `Asterisk__Ami__*` env vars and immediately understand: "this configures the Asterisk PBX connection." A comment in helm values.yaml clarifies for anyone confused.

The extension method `AddAsterisk(configuration)` → `AddVerbara(configuration)` but internally still reads `GetSection("Asterisk:Ami")`.

### Layer 3: Database Name
**Decision: Standardize to `verbara`**

Current state (inconsistent):
- Docker compose: already uses `Database=platform` (neutral!) ✓
- K8s CloudNativePG: `asterisk_platform` → change to `verbara`
- Loadtest: `asterisk_loadtest` → change to `verbara_loadtest`
- Docs/operations: reference `asterisk_platform` → update

Since NO production database exists with real data, this is a config string change — no migration script, no dump/restore, no downtime.

### Layer 4: User-Facing Identity (customers see this)
**Decision: ALL rename to `Verbara`**

| Item | Current | New | Impact |
|------|---------|-----|--------|
| MFA TOTP issuer (shows in Google Authenticator!) | `"AsteriskPlatform"` | `"Verbara"` | `MfaService.cs:12` |
| OpenAPI/Scalar title | defaults to assembly name | explicit `"Verbara Platform API"` | Program.cs AddOpenApi |
| Setup wizard default name | `"Asterisk Platform"` | `"Verbara"` | `SetupEndpoints.cs:44` |
| Favicon/title in browser tab | frontend controlled | `"Verbara"` | Platform.Web |

### Layer 5: Operational Identity (ops teams see this)
**Decision: ALL rename to `Verbara`/`verbara`**

| Item | Current | New | Where |
|------|---------|-----|-------|
| ARI Stasis app name | `asterisk_platform` / `asterisk-platform` (inconsistent!) | `verbara` | extensions.conf + docker-compose + K8s |
| Postgres `application_name` | `"Asterisk.Platform"` | `"Verbara.Platform"` | Program.cs:502,512 |
| Telemetry meter names | `"Asterisk.Platform.Auth.WriteQueue"` | `"Verbara.Platform.Auth.WriteQueue"` | AuthWriteQueue.cs:41 |
| Logger category names | `"Asterisk.Platform.Api.Endpoints.Sse"` | auto-rename with namespace | SseEndpoints.cs:26 |
| Docker image names | `asterisk-platform-api` | `verbara-platform-api` | compose + K8s |
| PrometheusRules meter matchers | any rule matching `Asterisk.*` meters | update to `Verbara.*` | K8s CRDs |

### What STAYS as "Asterisk" (refers to the PBX software itself)

- `docker/asterisk-config/*` (pjsip.conf, extensions.conf, etc.)
- `docker/Dockerfile.asterisk` (builds Asterisk 22 PBX container)
- `infra/k8s/helm/asterisk/` chart (deploys actual Asterisk PBX)
- Config section `"Asterisk:Ami"` / `"Asterisk:Ari"` (Layer 2 above)
- Env vars `Asterisk__Ami__*` / `Asterisk__Ari__*` (derived from config section)
- Doc references to "Asterisk PBX" as a technology (e.g., "connects to Asterisk 22")
- K8s labels/annotations referring to the Asterisk PBX workload (not our product)

---

## Task 1: AHH Plan Housekeeping (5 min)

### Steps
1. `git mv docs/plans/active/2026-04-27-auth-hotpath-hardening.md docs/plans/completed/`
2. Prepend completion note:
   ```
   > **COMPLETED 2026-05-05.** Shipped as v1.14.0 → v1.14.6 (8 commits, ADRs 0010-0014).
   > Checkboxes not individually ticked during execution but all objectives delivered:
   > Argon2id migration, cache decorators, write deferral, JWT rotation pool, multi-replica baseline.
   > Superseded by v1.14.5-1.14.6 (ADR-0015 Phases 1+2).
   ```
3. Commit: `docs(plans): move auth-hotpath-hardening to completed/ (shipped v1.14.0-v1.14.6)`

---

## Task 2: Verbara Rebrand Execution

### Phase 0: Preparation (~30 min)

| Step | Action |
|------|--------|
| 0.1 | Create branch `rebrand/verbara-2.0` in all 4 repos |
| 0.2 | Tag current HEADs: `pre-rebrand` (safety rollback point) |
| 0.3 | Verify all repos build + tests pass on current state |
| 0.4 | **Pre-alignment:** In Asterisk.Sdk.Pro, bump SDK pin from 1.15.1 → 1.15.3 in `Directory.Packages.props`, run full test suite, fix any breaks |
| 0.5 | **Pre-alignment:** In Asterisk.Platform, bump SDK pin from 1.15.1 → 1.15.3, verify build + tests |
| 0.6 | Wipe `local-nuget-feed/` (old .nupkg files are dead weight) |
| 0.7 | Commit pre-alignment: `chore: align SDK pin to 1.15.3 before rebrand` |

### Phase 1: Asterisk.Sdk → Verbara.Sdk 2.0.0 (~3-4 hours)

**Repo:** `/media/Data/Source/IPcom/Asterisk.Sdk/`
**Scope:** 26 src + 31 test packages, 1,855 .cs files (1,192 with namespace)

#### 1A. Structural rename (folders, projects, solution)
- Rename solution: `Asterisk.Sdk.slnx` → `Verbara.Sdk.slnx`
- Rename all `src/Asterisk.Sdk.*` folders → `src/Verbara.Sdk.*` (26 folders)
- Rename all `Tests/Asterisk.Sdk.*` folders → `Tests/Verbara.Sdk.*` (~31 folders)
- Rename `.csproj` files inside to match folder names
- Update `.slnx` project paths
- Update all `<ProjectReference>` paths in .csproj files
- **Gate:** `dotnet build` should fail on namespaces (expected) but ProjectReferences resolve

#### 1B. Namespace + using statements
```bash
find src Tests -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" \
  -exec sed -i 's/namespace Asterisk\./namespace Verbara./g; s/using Asterisk\./using Verbara./g' {} \;
```
- Also handle `[assembly: InternalsVisibleTo("Asterisk.` → `"Verbara.`

#### 1C. Public API type renames
| Current | New |
|---------|-----|
| `AsteriskOptions` | `VerbaraOptions` |
| `AsteriskServer` | `VerbaraServer` |
| `AsteriskServerPool` | `VerbaraServerPool` |
| `IAsteriskServer` | `IVerbaraServer` |
| `AsteriskServerHostedService` | `VerbaraServerHostedService` |
| `AsteriskTelemetry` | `VerbaraTelemetry` |
| `AddAsterisk(IConfiguration)` | `AddVerbara(IConfiguration)` |
| `AddAsteriskSessions*` | `AddVerbaraSessions*` |
| `AddAsteriskMultiServer` | `AddVerbaraMultiServer` |
| `AddAsteriskPush*` | `AddVerbaraPush*` |
| All `AddAsterisk*` extension methods | `AddVerbara*` |

**CRITICAL PRESERVE:** `configuration.GetSection("Asterisk:Ami")` and `configuration.GetSection("Asterisk:Ari")` — these stay as "Asterisk" (Layer 2).

#### 1D. Telemetry constants
- `ActivitySource` names: `"Asterisk.Sdk.Ami"` → `"Verbara.Sdk.Ami"`, etc.
- `Meter` names: same pattern

#### 1E. Build metadata + API validation
- `Directory.Build.props`: Product, Company, PackageTags, `<PackageVersion>2.0.0</PackageVersion>`, RepositoryUrl → github.com/verbara/verbara-sdk
- Remove/reset `PackageValidationBaselineVersion` (new package IDs have no baseline)
- `PublicAPI.Shipped.txt`: replace `Asterisk.` → `Verbara.` in all namespace refs
- CI workflows: update solution name

#### 1F. Gate
- `dotnet build Verbara.Sdk.slnx -c Release` → 0 errors, 0 warnings
- `dotnet test Verbara.Sdk.slnx` → all pass
- `dotnet pack -c Release -o /media/Data/Source/IPcom/local-nuget-feed/` → 26 `Verbara.Sdk.*.2.0.0.nupkg`
- Commit + tag `v2.0.0`

---

### Phase 2: Asterisk.Sdk.Pro → Verbara.Sdk.Pro 2.0.0-pro (~2-3 hours)

**Repo:** `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/`
**Scope:** 24 src + ~20 test packages, 1,092 .cs files (813 with namespace)
**Depends on:** Phase 1 complete (Verbara.Sdk 2.0.0 in local-nuget-feed)

#### 2A. Structural rename
- Same pattern as Phase 1A for Pro folders/projects

#### 2B. Namespace + using rename
- Same sed pattern as Phase 1B

#### 2C. Type renames
- All `AddAsteriskPro*` → `AddVerbaraPro*` extension methods
- All `AsteriskPro*` types → `VerbaraPro*`

#### 2D. Package references update
- `Directory.Packages.props`: `Asterisk.Sdk.*` → `Verbara.Sdk.*` @ `2.0.0`
- `Directory.Build.props`: version `2.0.0-pro`, metadata
- Individual .csproj `<PackageReference>` IDs

#### 2E. Gate
- `dotnet restore` → resolves Verbara.Sdk 2.0.0 from local feed
- `dotnet build Verbara.Sdk.Pro.slnx -c Release` → 0 errors
- `dotnet test` → all pass
- `dotnet pack -c Release -o /media/Data/Source/IPcom/local-nuget-feed/` → 24 `Verbara.Sdk.Pro.*.2.0.0-pro.nupkg`
- Commit + tag `v2.0.0-pro`

---

### Phase 3: Asterisk.Platform → Verbara.Platform 2.0.0 (~4-5 hours)

**Repo:** `/media/Data/Source/IPcom/Asterisk.Platform/`
**Scope:** 31 src + tests, 1,508 .cs files (1,073 with namespace) + infra
**Depends on:** Phase 2 complete (both SDK + Pro in local-nuget-feed)

#### 3A. Structural rename
- 31 `src/Asterisk.Platform.*` → `src/Verbara.Platform.*`
- All test project folders similarly
- Solution: `Asterisk.Platform.slnx` → `Verbara.Platform.slnx`
- All .csproj renames + ProjectReference updates

#### 3B. Namespace + using rename
- Same sed pattern, ~1,508 files

#### 3C. Package references
- `Directory.Packages.props`: both `Asterisk.Sdk.*` → `Verbara.Sdk.*` @ 2.0.0 AND `Asterisk.Sdk.Pro.*` → `Verbara.Sdk.Pro.*` @ 2.0.0-pro
- `Directory.Build.props`: version 2.0.0, metadata

#### 3D. Extension method call sites (Program.cs)
| Current | New |
|---------|-----|
| `AddAsteriskMultiServer()` | `AddVerbaraMultiServer()` |
| `AddAsteriskSessionsMultiServer()` | `AddVerbaraSessionsMultiServer()` |
| `AddAsteriskPush()` | `AddVerbaraPush()` |
| `AddAsteriskProPushSignalR(...)` | `AddVerbaraProPushSignalR(...)` |
| `AddAsteriskOpenTelemetry(...)` | `AddVerbaraOpenTelemetry(...)` |
| `AddAsteriskProOpenTelemetry()` | `AddVerbaraProOpenTelemetry()` |
| `AddAsteriskResilience()` | `AddVerbaraResilience()` |
| `AddAsteriskPlatformIdentityRedis(...)` | `AddVerbaraPlatformIdentityRedis(...)` |
| `AddAsteriskCluster(...)` | `AddVerbaraCluster(...)` |
| `AddAsteriskMultiTenant()` | `AddVerbaraMultiTenant()` |
| `AddAsteriskRealtime(...)` | `AddVerbaraRealtime(...)` |
| `AddAsteriskEventStore()` | `AddVerbaraEventStore()` |
| `AddAsteriskAnalytics()` | `AddVerbaraAnalytics()` |
| `AddAsteriskProAnalyticsLive()` | `AddVerbaraProAnalyticsLive()` |

#### 3E. User-facing identity (Layer 4)
- `MfaService.cs:12`: `issuer = "AsteriskPlatform"` → `"Verbara"`
- `SetupEndpoints.cs:44`: `"Asterisk Platform"` → `"Verbara"`
- `Program.cs` OpenAPI: add explicit `.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) => { doc.Info.Title = "Verbara Platform API"; ... }))`

#### 3F. Operational identity (Layer 5)
- `Program.cs:502,512`: `ApplicationName = "Asterisk.Platform*"` → `"Verbara.Platform*"`
- `AuthWriteQueue.cs:41`: `MeterName = "Asterisk.Platform.Auth.WriteQueue"` → `"Verbara.Platform.Auth.WriteQueue"`
- `SseEndpoints.cs:26`: `"Asterisk.Platform.Api.Endpoints.Sse"` → auto-fixed by namespace rename
- ARI app name: standardize to `verbara` across:
  - `docker/asterisk-config/extensions.conf`: `Stasis(asterisk_platform,...)` → `Stasis(verbara,...)`
  - `docker/docker-compose.full.yml`: `Asterisk__Ari__Application: asterisk-platform` → `verbara`
  - `docker/demo/docker-compose.demo.yml`: same
  - `infra/k8s/helm/platform/` values: same
  - `appsettings.json` / `appsettings.Development.json`: ARI Application value

#### 3G. Database name (Layer 3)
- Docker compose: already uses `Database=platform` → **keep as-is** (already neutral)
- K8s CloudNativePG: `database: asterisk_platform` → `database: verbara`
- Loadtest: `asterisk_loadtest` → `verbara_loadtest`
- Docs/operations references: update

#### 3H. Docker image names
- `docker-compose.loadtest.yml`: `asterisk-platform-api:loadtest` → `verbara-platform-api:loadtest`
- Dockerfile ENTRYPOINT: `Asterisk.Platform.Api.dll` → `Verbara.Platform.Api.dll`
- K8s helm values: image repo `asterisk-platform/api` → `verbara-platform/api`

#### 3I. What stays "Asterisk" (explicit DO NOT TOUCH list)
- `docker/asterisk-config/*` (PBX config files)
- `docker/Dockerfile.asterisk` (builds Asterisk PBX)
- `infra/k8s/helm/asterisk/` chart (deploys Asterisk PBX)
- All `Asterisk__Ami__*` / `Asterisk__Ari__*` env vars in compose/K8s
- `configuration.GetSection("Asterisk:Ami/Ari")` in SDK (Layer 2)
- `appsettings.json` top-level `"Asterisk": { }` section KEY (values like Application change, key stays)
- `AsteriskServerPool` type usage in `RealtimeStateBridge.cs` XML doc (describes PBX interaction)

#### 3J. Gate
- `dotnet build Verbara.Platform.slnx -c Release` → 0 errors
- `dotnet test tests/Verbara.Platform.Api.Tests/` → 882/882 pass
- `docker build . -t verbara-platform-api:2.0.0` → builds
- `docker compose -f docker/docker-compose.full.yml up` → boots, connects to Asterisk PBX, health check green
- Commit + tag `v2.0.0`

---

### Phase 4: Asterisk.Platform.Web → verbara-web 2.0.0 (~1-2 hours)

**Repo:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/`
**Depends on:** Phase 3 (API running for E2E verification)

#### 4A. Package + config
- `package.json`: `"name": "asterisk-platform-web"` → `"verbara-web"`
- Version → `2.0.0`

#### 4B. Brand copy (i18n)
- All locale files: "Asterisk Platform" → "Verbara" (product branding only)
- **KEEP** references to "Asterisk" that describe PBX nodes/version columns

#### 4C. Source references
- `welcome-step.tsx` fallback string
- Any hardcoded "Asterisk Platform" in .tsx/.ts
- localStorage key: `'asterisk.lang'` → `'verbara.lang'`

#### 4D. Gate
- `pnpm build` → passes
- `pnpm test` → all pass
- Visual: login page, sidebar, setup wizard say "Verbara"
- Commit + tag `v2.0.0-web`

---

### Phase 5: Cross-cutting Finalization (~1 hour)

| Step | Action |
|------|--------|
| 5.1 | Purge local-nuget-feed of all old `Asterisk.*.nupkg` (only `Verbara.*` remains) |
| 5.2 | Full stack E2E: docker compose up (all services), verify boot + basic flow |
| 5.3 | Update Platform `CLAUDE.md` to reflect new names |
| 5.4 | Update all memory files that reference old package/namespace names |
| 5.5 | Write ADR-0017: "Rebrand Execution — Versioning and Scope Decisions" (documents decisions A-I from this plan) |
| 5.6 | Final commit: `docs: update project documentation for Verbara rebrand` |

### Phase 6: GitHub Org Transfer (SEPARATE, after 1-2 days validation)

Not part of this execution — separate operational step after the code rename is proven stable:
- Create `github.com/verbara` org
- Transfer repos with new names
- Update `RepositoryUrl` in all `Directory.Build.props`
- Update cross-repo ADR links

---

## Verification

After all phases complete:
1. `dotnet build` passes in all 3 C# repos (0 errors, 0 warnings)
2. Full test suites pass (SDK ~2,998 + Pro 1,329 + Platform 882 = ~5,209 tests)
3. `docker compose -f docker/docker-compose.full.yml up` → Platform boots, connects to Asterisk PBX, health OK
4. `curl localhost:5000/health` → returns healthy
5. MFA setup shows "Verbara" in authenticator app
6. Scalar UI at `/scalar/v1` shows "Verbara Platform API"
7. `pg_stat_activity` shows `application_name = 'Verbara.Platform'`
8. Prometheus metrics use `Verbara.Platform.*` meter names

## Critical Files

**SDK:**
- `/media/Data/Source/IPcom/Asterisk.Sdk/Directory.Build.props`
- `/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs` (config section preservation)

**Pro:**
- `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/Directory.Packages.props` (SDK pin)
- `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/Directory.Build.props`

**Platform:**
- `/media/Data/Source/IPcom/Asterisk.Platform/Directory.Packages.props` (SDK + Pro pins)
- `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Program.cs` (extension methods + config reads)
- `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Services/MfaService.cs` (user-facing issuer)
- `/media/Data/Source/IPcom/Asterisk.Platform/src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs` (default name)
- `/media/Data/Source/IPcom/Asterisk.Platform/docker/asterisk-config/extensions.conf` (ARI Stasis app)
- `/media/Data/Source/IPcom/Asterisk.Platform/infra/k8s/manifests/cloudnativepg/cluster.yaml` (DB name)
- `/media/Data/Source/IPcom/Asterisk.Platform/infra/k8s/helm/platform/values.yaml` (image + ARI app)

**Web:**
- `/media/Data/Source/IPcom/Asterisk.Platform.Web/package.json`
- `/media/Data/Source/IPcom/Asterisk.Platform.Web/public/locales/*/common.json`

## Estimated Total Effort

| Phase | Hours |
|-------|-------|
| Task 1 (AHH housekeeping) | 0.1 |
| Phase 0 (prep + pre-alignment) | 0.5 |
| Phase 1 (SDK) | 3-4 |
| Phase 2 (Pro) | 2-3 |
| Phase 3 (Platform) | 4-5 |
| Phase 4 (Web) | 1-2 |
| Phase 5 (finalization) | 1 |
| **Total** | **~12-16 hours (2 working days)** |

## Risk Mitigations

1. **`pre-rebrand` tags** in all repos → instant rollback via `git reset --hard pre-rebrand`
2. **Build after every sub-phase** → catch breaks immediately, not at the end
3. **Explicit DO NOT TOUCH list** → prevents accidental rename of PBX references
4. **sed exclusions** (`-not -path "*/obj/*" -not -path "*/bin/*"`) → don't corrupt build artifacts
5. **Phase gate pattern** → no Phase N+1 starts until Phase N builds + tests pass
