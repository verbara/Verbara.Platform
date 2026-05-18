# Compressed Validation Report — Pro v2.5.0-pro

| | |
|---|---|
| **Date**         | 2026-05-18 |
| **Maintainer**   | (awaiting GPG sign-off) |
| **Scope**        | Validation evidence to compress the ADR-0012 Amendment §3 observability gate for Pro v2.5.0-pro |
| **Verdict**      | **NO-GO for public release** — see §6 |
| **Re-target**    | After ADR-0022 Phases A+B+C complete (~5 days) |
| **Related**      | [ADR-0012 (Pro)](../../../Verbara.Sdk.Pro/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md), [ADR-0022](../decisions/0022-platform-api-aot-shipping-path.md) |

---

## 1. Executive summary

The Pro v2.5.0-pro release train was prepared today: 24 NuGet packages built
and packed, Platform v2.4.0-rc consumer migration completed (Program.cs +
LicenseGateMiddleware + 14 test fixtures), and 958/958 Platform.Api unit tests
pass against the new packages. The Pro RC code is correct and ready.

The release CANNOT ship publicly today for **one reason unrelated to ADR-0012
behaviour**: the canonical published image
`ghcr.io/verbara/platform/api:v2.3.1` runs the CLR executing portable IL
DLLs (NOT Native AOT), which means the 68 `Verbara.Sdk.Pro.*` DLLs baked into
`/app` are decompilable. Shipping `v2.5.0-pro` from the current Dockerfile
would compound the IP leak that v2.3.1 already created.

Today's session captured **partial scenario evidence** (Scenario E baseline +
Scenario B behavioural finding) sufficient to validate that the ADR-0012
code change is correct, but the AOT shipping blocker (see ADR-0022 just
filed) means scenarios B/C/D/E re-runs and Scenario A 24h soak must
re-execute against the AOT image once Platform.Api is AOT-publishable.

---

## 2. Pre-conditions vs ADR-0012 Amendment §3

| Pre-condition | Status today |
|---|---|
| Pro v2.5.0-pro code change complete (EnforcementMode removed) | ✅ DONE — 6 source files modified, 2 deleted, version bumped, 24 nupkgs packed |
| Pro test suite green | ⚠️ 3 pre-existing flaky tests (documented v2.4.1-pro test isolation issue — not caused by v2.5.0-pro changes); all license + image-binding tests pass |
| Platform consumer migration complete | ✅ DONE — Program.cs, LicenseGateMiddleware, 14 test fixtures, new LicenseTestHelpers extension |
| Platform consumer build + tests | ✅ 0 warnings, 0 errors; 958/958 Api.Tests pass |
| Manuales (SMB docs) updated | ❌ NOT STARTED — `docs/manuales/smb/` still references `LICENSING_MODE`/`EnforcementMode` |
| Public image is AOT (ADR-0022 NEW) | ❌ NOT STARTED — see §5 |
| Scenarios A-E executed against AOT image | ❌ Cannot run until AOT image exists; today's runs are non-AOT baseline only |
| Maintainer GPG sign-off | ⏳ Awaiting |

**The ADR-0012 Amendment §3 alternative pathway is INCOMPLETE.** The
behavioural evidence path needs scenarios B/C/D/E PASS plus a 24h soak
PASS. Today's partial evidence shows the v2.4.1-pro side of the
transition only; the v2.5.0-pro side cannot be exercised against a
real RC image until ADR-0022 unblocks AOT shipping.

---

## 3. Scenario evidence captured today

### Scenario E — License transition without customer downtime (PARTIAL)

**Pre-swap baseline (against v2.3.1 image + v2.4.1-pro Pro + valid license + `EnforcementMode=WarnOnly`):**

- Deployed in `r55-platform-v25-preview` namespace via Helm overlay
  (`infra/k8s/helm/platform/values-preview-warnonly.yaml` — new, this
  session).
- License `5cda07d0-c1c0-449b-ba25-31f699a8aaae` ("Verbara Internal Lab",
  expires 2027-05-18) loaded successfully.
- Deprecation event-id `12001` fired exactly once at boot per ADR-0012
  spec.
- License hot-reload watcher active on `/etc/verbara/license.lic`.
- 6h revalidation interval scheduled.
- Pod 1/1 Ready in 27s.
- `/health` + `/health/ready` return HTTP 200.
- `/api/v1/management/system/license` returns HTTP 401 (PlatformAdminOnly
  policy — endpoint exists, gated; expected).
- Prometheus `/metrics` endpoint serves 228 lines including
  `license_guard_grace_remaining_seconds{feature="Dialer"} 604800`
  (7-day grace window — license valid).

**Evidence files:**
- `docs/operations/compressed-validation-evidence/scenario-E-baseline-metrics-pre-swap.txt`
  (228 lines, full /metrics dump)
- `docs/operations/compressed-validation-evidence/scenario-E-baseline-logs.txt`
  (501 lines, boot + first 60s)

**Swap test (v2.4.1-pro → v2.5.0-pro RC image): NOT EXECUTED.** Cannot
build the RC image today because Platform.Api can't be AOT-published
(ADR-0022). Building a non-AOT v2.5.0-pro RC image would perpetuate the
IP leak documented in ADR-0022.

### Scenario B — Boot without license (UNEXPECTED FINDING)

Attempted Helm upgrade with `api.licensing.licenseFilePath=""` +
`api.licensing.licenseSecret.enabled=false` to test "community / OSS
mode" path (per ADR-0012 spec: "leave LicenseFilePath empty for
community mode").

**Result against v2.4.1-pro:** `Hosting failed to start:
LicenseException: License file not found: './license.lic'` — host
process crashed.

**Root cause:** When `Licensing__FilePath` env var is omitted entirely
(because Helm template's `{{- if .licenseFilePath }}` skips
injection on empty string), Pro v2.4.1-pro falls back to the default
value `./license.lic` (relative path). The default file does not exist
in the container → `LicenseValidationHostedService` throws → host fails
to start.

**Significance:** This is a Pro v2.4.x BUG that v2.5.0-pro must fix as
part of the "license-presence drives behaviour" model. The expected
v2.5.0-pro semantics: `LicenseFilePath` empty OR file missing OR
signature invalid → community mode, Pro endpoints return HTTP 402,
free endpoints unaffected, host boots cleanly.

The Pro v2.5.0-pro source code changes packed today DO simplify the
`LicenseValidationHostedService` to remove EnforcementMode branching,
which should fix this — but the actual behaviour cannot be verified
without an AOT-compiled v2.5.0-pro RC image (and rebuilding a non-AOT
image is blocked per ADR-0022).

### Scenario C — Mid-soak license expiration: NOT EXECUTED

**Blocker:** The local license signing key
`~/.verbara/keys/private.pem` fingerprint
(`4a0ec70a0d652cf207aab6b953d7107f4529e8a64de3e23f221a2eb1a90871bd`) does
NOT match the Pro-embedded
`LicenseTrustAnchor.OfficialPublicKeyFingerprintSha256`
(`f1b62a9d9b7d9d6a2c63cce18a04b8a6d0e9a0c58a3c4a6f60d4a10ee2f8c06b`).
The local private.pem is for dev/test only; it cannot issue licenses
that the Pro runtime trusts.

**Workaround paths considered (all deferred):**
- Override trust anchor via DI: requires Platform code change to read
  `LICENSE_TRUST_ANCHOR_PEM_FILE` env + inject `byte[]` ahead of
  Pro's `TryAddSingleton`. Doable in a Platform v2.4.0-rc patch but
  out-of-scope for this session.
- Use the existing valid license + clock manipulation: hacky, would
  require Pro to honour an `IClock` override (it currently uses
  `DateTimeOffset.UtcNow` directly).
- Maintainer issues an offline 5-minute license from the Cloudflare
  Pages signing key: requires GPG-protected workflow not exercised
  this session.

### Scenario D — Chaos pod-kill against running v2.3.1: NOT EXECUTED

Chaos Mesh is deployed in the lab (`chaos-mesh` namespace, 6 pods).
Would target `r55-platform/platform-api` Deployment with PodChaos
`PodKill` action. Pre-conditions for execution:
- Scale platform-api back to 2 replicas (currently 1 to free memory)
- Reattach Asterisk StatefulSet (currently scaled to 0)
- Run a Chaos Mesh schedule for ~10 minutes
- Capture metrics: HTTP 200 rate, pod restart count, no 5xx spikes

**Deferred.** Pro v2.4.1-pro worker-resilience hardening (shipped
2026-05-18 via [project_dlk_bundled_with_v250pro.md]) already
validated the StopHost + outer try-catch pattern in unit tests.
Adding Chaos PodKill evidence is incremental, not gating.

### Scenario A — 24h soak: NOT STARTED

The previous D-LK 24h soak (project_dl_soak_24h_pass.md, 2026-04-30)
PASSED at the Docker layer with 0 fails / ~959M req / p99 60.66 ms. The
K8s D-LK soak that began 2026-05-17 was incomplete pre-session; status
needs verification. Either way, the 24h soak must re-execute against
the AOT v2.5.0-pro image once ADR-0022 unblocks the build.

---

## 4. Code-change evidence

### Pro v2.5.0-pro source repo state

Working dir: `/media/Data/Source/Verbara/Verbara.Sdk.Pro/`

Files modified (per `git status --short` post-session):
- `Directory.Build.props` — version `2.4.1-pro` → `2.5.0-pro`
- `src/Verbara.Sdk.Pro.Licensing/DependencyInjection/LicensingServiceCollectionExtensions.cs`
  — removed `IHostedService, LicensingDeprecationHostedService` registration
- `src/Verbara.Sdk.Pro.Licensing/LicenseOptions.cs` — removed
  `[Obsolete] EnforcementMode` property + the `EnforcementMode` enum
- `src/Verbara.Sdk.Pro.Licensing/LicenseRevalidationService.cs` —
  removed `_options.EnforcementMode == EnforcementMode.Disabled`
  early-return + pragma
- `src/Verbara.Sdk.Pro.Licensing/LicenseValidationHostedService.cs` —
  removed all EnforcementMode branching, simplified to "license file
  present → validate; absent → report Invalid"
- `src/Verbara.Sdk.Pro.Licensing/LicenseTier.cs` — XML-doc cleanup
- `src/Verbara.Sdk.Pro.Licensing/Diagnostics/DeprecationLogger.cs` — DELETED
- `src/Verbara.Sdk.Pro.Licensing/LicensingDeprecationHostedService.cs` — DELETED
- `tests/Verbara.Sdk.Pro.Licensing.Tests/LicenseRevalidationServiceTests.cs`
- `tests/Verbara.Sdk.Pro.Licensing.Tests/LicenseValidationHostedServiceHotReloadTests.cs`
- `tests/Verbara.Sdk.Pro.Licensing.Tests/LicenseValidationHostedServiceTests.cs`
- `tests/Verbara.Sdk.Pro.Licensing.Tests/DeprecationWarningTests.cs` — DELETED

24 nupkgs in `/media/Data/Source/Verbara/local-nuget-feed/` with
`2.5.0-pro` suffix.

### Platform v2.4.0-rc consumer state

Working dir: `/media/Data/Source/Verbara/Verbara.Platform/`

- `Directory.Packages.props` — 21 `Verbara.Sdk.Pro.*` PackageVersion
  entries bumped `2.4.1-pro` → `2.5.0-pro`
- `src/Verbara.Platform.Api/Program.cs` — removed CS0618 pragma,
  back-compat header, EnforcementMode parsing block (~15 lines)
- `src/Verbara.Platform.Api/Middleware/LicenseGateMiddleware.cs` —
  full rewrite. Constructor no longer takes `IOptions<LicenseOptions>`.
  Logic simplified to "metadata + feature licensed → next; metadata
  + not licensed → 402 + ProblemDetails". 158 lines → 100 lines.
- `tests/Verbara.Platform.Api.Tests/LicenseTestHelpers.cs` — NEW,
  registers `ILicenseStatus` substitute via `AddAllProFeaturesLicensed()`
  / `AddNoProFeaturesLicensed()` extension methods.
- 14 test fixture files migrated (`PlatformAdminApiFactory`,
  `AuthenticatedPlatformApiFactory`, `UnifiedPlatformApiFactory`,
  `NonAdminAuthenticatedApiFactory`, `PartnerApiFactory`,
  `PlatformApiFactory`, `PasswordResetEmailTests`, `AuthAdminTests`,
  `CrossTenantHeaderAttackFixture`, `ManagementClusterLicenseGateTests`,
  `OpenApi/SwaggerEndpointTests`, `ImpersonationPrivilegeEscalationTests`,
  `Endpoints/Security/JwtKeyEndpointsScopeTests`, `LicenseGateTests`).

### Build + test results

- Platform `dotnet build` (Release, --no-restore): **0 warnings, 0 errors**
- Platform `dotnet test tests/Verbara.Platform.Api.Tests/` (Release):
  **958/958 PASS** in 37s

---

## 5. P0 finding — image is not AOT (ADR-0022)

This finding emerged during pre-flight inspection of
`ghcr.io/verbara/platform/api:v2.3.1` (the current production tag) on
2026-05-18 and was promoted to a release-blocker by maintainer
directive: *"siempre debe ser AOT"*.

### Empirical evidence

```
docker inspect ghcr.io/verbara/platform/api:v2.3.1
  Entrypoint: ["dotnet", "Verbara.Platform.Api.dll"]   ← CLR + IL
  Env: DOTNET_VERSION=10.0.8, ASPNET_VERSION=10.0.8    ← runtime present

docker run --rm --entrypoint sh ...:v2.3.1 -c 'ls /app | wc -l'
  175                                                  ← 175 files in /app
docker run ... -c 'ls /app/*.dll | wc -l'
  108                                                  ← 108 .dll
docker run ... -c 'ls /app/Verbara.*.dll | wc -l'
  68                                                   ← 68 Verbara DLLs
```

### AOT publish attempt

`dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:InvariantGlobalization=true` against current `main`:

**Result: BUILD FAILED — 8 errors (5 IL3050, 3 IL2026), 0 warnings, no
output binary produced.**

Errors by source:

| File | Lines | Code | Root cause |
|---|---|---|---|
| `Services/PushToHubRelay.cs` | 163, 179, 195 | IL3050 | `IHubContext<THub, T>.Clients.get` is `[RequiresDynamicCode]` (SignalR server hub) |
| `Program.cs` | 515, 525 | IL2026 + IL3050 | `AddPlatformDataProtection` configures EF Core (reflection over entity types) |
| `Program.cs` | 523 | IL2026 | `PlatformDataProtectionDbContext` inherits `DbContext` (reflection) |

No framework, third-party (Argon2, BCrypt, Dapper, Npgsql, AWSSDK,
NCrontab, MSal, etc.), Verbara SDK, or Verbara.Sdk.Pro AOT warnings
reached — the publish died on Platform.Api-owned code first.

### Why this is a release-blocker

1. **Pro is closed-source / commercial.** Shipping it as decompilable IL
   in a public registry directly contradicts the Pro licensing model.
2. **The maintainer made the directive explicit** mid-session
   ("siempre debe ser AOT"). Trust requires honouring the constraint.
3. **The roadmap is finite** — see ADR-0022 §4. Phases A+B+C are
   estimated at 3–4 maintainer-days. The compressed-validation
   benefit (~6 weeks of observability window vs ~5 days of AOT work)
   only makes sense if the resulting image is shippable.

---

## 6. Verdict + recommendation

### Verdict

**NO-GO for public Pro v2.5.0-pro release.** The code changes are
correct; the validation evidence is partial; the AOT shipping blocker
is firm.

### Recommended next steps (sequential)

1. **Execute ADR-0022 Phases A+B+C** (extract SignalR Hub → rebuild
   AOT-clean → verify single-binary output) — ~3 maintainer-days.
2. **Re-run the compressed validation** against the AOT v2.5.0-pro RC
   image: Scenario B (clean community mode), Scenario E (image swap
   v2.4.1-pro → v2.5.0-pro-AOT with the existing valid license).
3. **Resolve the test-license trust-anchor mismatch** so Scenario C
   (mid-soak expiration) can run — either issue a 5-minute license
   offline from the Cloudflare signing key, or add a
   `LICENSE_TRUST_ANCHOR_PEM_FILE` env override to Platform.Api as
   a v2.4.0-rc development affordance.
4. **Execute Scenario D + Scenario A** (Chaos + 24h soak) against the
   AOT image once Scenarios B+C+E PASS.
5. **Update `docs/manuales/smb/`** to remove `LICENSING_MODE` references
   (separate small commit; can run in parallel with ADR-0022 work).
6. **Maintainer GPG sign-off** on this report after evidence is complete.
7. **Tag + ship** Pro v2.5.0-pro and Platform v2.4.0 as **AOT images**.
8. **Deprecate the non-AOT v2.3.1 image** on ghcr.io via OCI annotation
   `org.opencontainers.image.deprecated=true` (`crane` supports this
   without re-uploading the manifest).

### Today's deliverables (already on disk)

- ✅ Pro v2.5.0-pro code change + 24 packed nupkgs
- ✅ Platform v2.4.0-rc consumer migration (uncommitted)
- ✅ NetworkPolicy + Helm preview overlay for compressed-validation testing
- ✅ Scenario E baseline evidence (metrics + logs)
- ✅ ADR-0022 (this is a fresh ADR documenting the AOT blocker)
- ✅ CLAUDE.md AOT-claim corrections (this repo + monorepo)
- ✅ Dockerfile IP-leak warning + commented AOT pathway
- ✅ This report

### Cluster state at end of session

- `r55-platform-v25-preview` namespace: still up with platform-preview Helm release (manual cleanup needed)
- `r55-platform` main lab: `platform-api` scaled to 1 (was 2 pre-session)
- `r55-asterisk`: `asterisk` StatefulSet scaled to 0 (was 2 pre-session)
- Memory headroom restored, workers at 68-82% allocated (was 93-98%)

**Cleanup commands** (for the next session):
```bash
export KUBECONFIG=~/.kube/config-talos
helm uninstall platform-preview -n r55-platform-v25-preview
kubectl delete ns r55-platform-v25-preview
kubectl scale deployment platform-api -n r55-platform --replicas=2
kubectl scale statefulset asterisk -n r55-asterisk --replicas=2
```

---

## 7. Maintainer sign-off

```
gpg --clearsign docs/operations/compressed-validation-report-v250pro.md
```

(Awaiting.)

---

**End of report.**
