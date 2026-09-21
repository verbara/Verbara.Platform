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
- 3.2 (FOLLOW-UP — NOT shipped in this change) Graduate the sharpened community smoke leg in
  `.github/workflows/release.yml` from report-only (`continue-on-error: true`) to **gating** — drop
  `continue-on-error` from that leg and/or make a later step `needs:` it (design D5, pre-condition
  6.4). — **LEFT THIS CHANGE; NOT DONE.** Still report-only as of 2026-09-20: `release.yml:476`
  `continue-on-error: true` on the `smoke` job, `:472` `name: Post-release functional smoke
  (report-only)`, and no `needs: smoke` edge anywhere. The 6.4 pre-condition is now **satisfied**
  (three consecutive greens — see 6.4), so this is unblocked work, not a wait. It is tracked in the
  open Platform change `openspec/changes/promote-community-smoke-to-gating` (proposed by PR #221,
  merged 2026-08-02), whose `tasks.md` 2.1–2.3 is this graduation verbatim and whose proposal cites
  this box by path and line.

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
- [x] 6.4 (FOLLOW-UP pre-condition for 3.2) The released-image smoke community leg is green twice
  consecutively before the gating promotion in 3.2 lands. — **SATISFIED 2026-07-28; re-verified
  2026-09-20.** Three consecutive green `smoke` jobs against images carrying the `2.13.0-pro` fix,
  each logging `Community-boot readiness contract OK (dialer-engine Degraded, 'dialer license
  blocked:' prefix present).` followed by `=== SMOKE PASSED: … ===`: run `30228537971` (`v2.21.2`,
  job `89863734271`, 2026-07-27), run `30324647348` (`v2.22.0`, job `90168452552`, 2026-07-28), run
  `32946067672` (`v2.23.0`, job `98108744545`, 2026-08-26). Non-vacuous: the sharpened assertion
  landed with commit `258dfbbc` (#194) and is absent from `v2.21.1`, whose smoke job is the
  counter-example red (run `30184375138`, job conclusion `failure` while the workflow concluded
  `success` — proof the job conclusion is reported faithfully under `continue-on-error: true`). The
  promotion this gates is tracked in the open change `promote-community-smoke-to-gating` (#221).
