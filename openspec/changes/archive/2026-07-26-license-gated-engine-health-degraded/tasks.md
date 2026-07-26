## 1. Pin bump (consumer) — apply-stage cascade

- [x] 1.1 Bump the `Verbara.Sdk.Pro.*` package pins from `2.12.0-pro` to `2.13.0-pro` (the version
  carrying the Verbara.Sdk.Pro/ADR-0017 readiness-severity fix) in `Directory.Packages.props`
  (all `Verbara.Sdk.Pro.*` `PackageVersion` entries, e.g. `.Dialer`, `.EventStore`, `.Licensing`,
  `.Analytics`, `.CallAnalytics`, `.AgentAssist`, `.CsatRunner`, `.Routing`, `.Realtime`,
  `.Cluster`, `.MultiTenant`, `.Push`, and their `.Storage.Postgres`/`.SignalR` siblings).
- [x] 1.2 (apply-stage, NOT this propose-only child) Run the pin cascade at `/xr:apply` — producer
  Pro (buildOrder 1) is packed to the local NuGet feed by `cross-repo-pack.sh`, the NuGet cache is
  cleared (`rm -rf ~/.nuget/packages/verbara.sdk.pro*`), then Platform (buildOrder 2) restores.
  Do NOT run the cascade during propose.

## 2. Community-boot readiness contract test (integration)

- [x] 2.1 Add an integration test that boots Platform in an unlicensed / community configuration
  (Postgres configured, no valid Pro license) and issues `GET /health/ready`, asserting the HTTP
  status is **200** (NOT 503).
- [x] 2.2 In the same test, parse the JSON body emitted by
  `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs` and assert the top-level `status` is
  `Degraded`.
- [x] 2.3 Assert the `checks` object's `dialer-engine` entry has `status` == `Degraded`.
- [x] 2.4 Assert that entry's `description` STARTS WITH the prefix `dialer license blocked:`.
  Assert the PREFIX only — do NOT assert the reason suffix (`NotLicensed` / `Revoked` / `Expired`
  / `GraceExhausted`); cite the golden fixture
  `Verbara.Sdk.Pro/openspec/changes/license-gated-engine-health-degraded/fixtures/health-ready-community-boot.json`.
- [x] 2.5 Name the test per convention `Method_ShouldExpected_WhenCondition`
  (e.g. `HealthReady_ShouldReturn200WithDialerEngineDegraded_WhenUnlicensedCommunityBoot`).

## 3. Released-image smoke — sharpen + graduate

- [x] 3.1 Sharpen `docker/verbara-smoke-released.sh` so the community (unlicensed) boot leg asserts
  not merely that `/health/ready` is 200 but that its `checks.dialer-engine` entry has `status` ==
  `Degraded` and a `description` starting with `dialer license blocked:` (reuse the existing
  `python3` stdlib `json` parse; assert the prefix, never the suffix). Fail the smoke if the entry
  is missing, not `Degraded`, or the prefix is absent.
- [ ] 3.2 (FOLLOW-UP — NOT shipped in this change) After the sharpened community smoke leg has run
  green **twice consecutively** while report-only, graduate it in `.github/workflows/release.yml`
  from report-only (`continue-on-error: true`) to **gating** — drop `continue-on-error` from that
  leg and/or make a later step `needs:` it. (Follow-up after the first two green runs; not merged
  with 3.1. Left unticked on purpose: the leg is still `continue-on-error: true` in `release.yml`,
  gated on two green runs against images carrying the fix — design D5, tasks.md 6.4.)

## 4. CHANGELOG

- [x] 4.1 Add a `[Unreleased]` entry to `CHANGELOG.md` recording the consumed contract flip
  (`/health/ready` 503 → 200 on unlicensed community boots via `dialer-engine` `Degraded`), the
  `Verbara.Sdk.Pro.*` `2.12.0-pro` → `2.13.0-pro` pin bump, the new readiness contract test, and
  the sharpened/gating smoke leg. Cite `decision_ref: Verbara.Sdk.Pro/ADR-0017`. (PR citation
  `(#194)` present in all three `[Unreleased]` sections — ledger row 13.)

## 5. Out of scope (record, do not implement)

- [x] 5.1 Confirm NO change is made to any Platform health-check source
  (`src/Verbara.Platform.Api/Health/AsteriskAmiHealthCheck.cs`,
  `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs`, or the `/health/ready` mapping in
  `Program.cs`) — the aggregate flips 503 → 200 on the Pro pin bump alone.
- [x] 5.2 Confirm the licensed-profile smoke leg is NOT built here (separate follow-up change).

## 6. Verification

- [x] 6.1 `dotnet test` green with zero warnings (TreatWarningsAsErrors=true, WarningLevel=9999),
  including the new community-boot readiness integration test.
- [x] 6.2 `openspec validate --all --strict` green.
- [x] 6.3 CI green on the PR (Platform gate set: build, tests, OpenSpec validation, and any
  invariant scripts). (Feature PR #194 merged green.)
- [ ] 6.4 (FOLLOW-UP pre-condition for 3.2 — NOT satisfied yet) The released-image smoke community
  leg is green twice consecutively before the gating promotion in 3.2 lands.
