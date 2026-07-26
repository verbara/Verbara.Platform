## Context

`GET /health/ready` on Platform aggregates every health check tagged `ready`
(`src/Verbara.Platform.Api/Program.cs`, `Predicate = r => r.Tags.Contains("ready")`) and renders
the report through the custom `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs`, which
emits, per check, `{status, durationMs, description}` under a `checks` object plus a top-level
`status`. Pro's `AddProDialer` registers a `ready`-tagged `dialer-engine` health check on any
Postgres-configured deploy. Today a license-blocked engine maps to `Unhealthy`, which drags the
aggregate to `Unhealthy` → HTTP **503**. Because that firing condition is true for **every**
unlicensed community / self-host deployment, those pods are permanently un-ready and never join the
load balancer — a readiness-probe re-litigation of the "the binary always runs; Pro features are
gated" model (Pro/ADR-0012).

The fix is producer-side, in Verbara.Sdk.Pro (Verbara.Sdk.Pro/ADR-0017): a license-blocked engine
now settles `Degraded` (with the `dialer license blocked:` reason preserved) instead of
`Unhealthy`. This document is the **consumer child** design: how Platform consumes that fix,
pins the resulting readiness contract, and hardens the released-image smoke around it — WITHOUT
touching any Platform health-check source.

Platform already has the exact precedent this generalizes:
`src/Verbara.Platform.Api/Health/AsteriskAmiHealthCheck.cs` follows "not-configured → Healthy;
configured-but-unprobeable → Degraded; only a long-open circuit → Unhealthy" — a subsystem you did
not enable (or that is blocked for a non-operational reason) must not fail readiness. The Pro fix
brings the `dialer-engine` check into line with that same posture.

## Goals / Non-Goals

**Goals:**
- Consume the Pro producer fix by bumping the `Verbara.Sdk.Pro.*` pins `2.12.0-pro` → `2.13.0-pro`
  in `Directory.Packages.props`, so `/health/ready` on an unlicensed boot returns 200.
- Pin the consumer readiness contract with an integration test that asserts the exact wire shape:
  200 + top-level `status` `Degraded` + `checks.dialer-engine.status` == `Degraded` +
  `description` starting with `dialer license blocked:` — asserting the prefix, never the suffix.
- Sharpen `docker/verbara-smoke-released.sh` to assert the `dialer-engine` Degraded-with-reason
  shape (not merely a 200), then graduate the community smoke leg from report-only to gating after
  two consecutive green runs.
- Record a `[Unreleased]` CHANGELOG entry.

**Non-Goals:**
- Any change to Platform health-check source (`AsteriskAmiHealthCheck`, `HealthReportJsonWriter`,
  the `/health/ready` mapping in `Program.cs`). The aggregate flips 503 → 200 on the pin bump
  alone; Platform owns no severity logic for the `dialer-engine` check.
- Asserting the licensed-profile readiness shape — a separate follow-up change.
- Re-specifying the producer's license semantics; those stay private in Pro (Pro/ADR-0017). No
  Pro-only IP crosses the boundary.

## Decisions

**D1 — Pin bump only; no Platform source change.**
The severity logic lives entirely in Pro. Once `Verbara.Sdk.Pro.*` is at `2.13.0-pro`, the
`dialer-engine` check returns `Degraded` and the ASP.NET Core aggregate maps that to HTTP 200 on
its own. *Alternative considered:* add a Platform-side wrapper/override that reclassifies the
`dialer-engine` result. *Rejected:* it would duplicate the license-severity decision on the
consumer side, drift from the private Pro source of truth, and re-own IP the open-core boundary
deliberately keeps in Pro. The consumer's job is to pin the contract, not re-derive it.

**D2 — Assert the `dialer license blocked:` PREFIX, never the reason suffix.**
The golden fixture
(`Verbara.Sdk.Pro/openspec/changes/license-gated-engine-health-degraded/fixtures/health-ready-community-boot.json`)
documents the `description` as `dialer license blocked: <reason>` where `<reason>` ∈
{`NotLicensed`, `Revoked`, `Expired`, `GraceExhausted`}. The stable half is the prefix; the suffix
depends on the boot's license state. Both the integration test and the smoke assert a prefix match
(`StartsWith` / `case "$desc" in "dialer license blocked:"*`). *Alternative:* assert the full
`dialer license blocked: NotLicensed` string. *Rejected:* it would make the test brittle to a
license-state change that is not a contract violation and contradicts the fixture's own
instruction.

**D3 — Where the readiness assertion runs: integration test parses the real JSON body.**
The integration test drives `/health/ready` through the Platform test host and parses the JSON
`HealthReportJsonWriter` emits, reading `checks.dialer-engine.status` and `.description`. This is a
propose-only child — the concrete test project/fixture wiring is authored at `/opsx:apply`, not
here. The contract it must pin is fixed by the spec delta and the golden fixture. AOT constraint:
the test asserts over parsed JSON (`System.Text.Json` document reads), introducing no reflection
and no new `[JsonSerializable]` DTO on the product surface (Platform/ADR-0022 respected).

**D4 — Smoke sharpening reuses the existing python3 stdlib parse.**
`docker/verbara-smoke-released.sh` already uses `python3` (stdlib `json` only) to parse the
login response. The sharpened readiness assertion fetches the `/health/ready` body once, parses
`checks["dialer-engine"]["status"]` and `["description"]`, and fails unless `status == "Degraded"`
and `description.startswith("dialer license blocked:")`. The current readiness loop uses
`curl -fsS` (which only checks the 2xx code); the sharpened body assertion runs after readiness is
reached. No new tool dependency.

**D5 — Gate the smoke leg only after two consecutive green runs.**
`.github/workflows/release.yml` runs the smoke job report-only (`continue-on-error: true`) as a
walking-skeleton stage still earning trust (released-image-smoke). Promotion to gating (drop
`continue-on-error`, and/or make a later step `needs:` it) waits for two consecutive green runs of
the sharpened community leg, bounding the blast radius of a flaky new assertion. *Alternative:*
gate immediately on merge. *Rejected:* it would turn a still-earning-trust assertion into
release-red noise before it has a track record.

**D6 — Pin cascade is an apply-stage step, not part of this propose-only child.**
The `Directory.Packages.props` edit is authored here as a task, but the mechanical cascade
(`cross-repo-pack.sh` pack of the new Pro version → NuGet cache clear → `dotnet restore`) runs at
`/xr:apply` between build stages (buildOrder: producer Pro = 1 → consumer Platform = 2). This child
authors docs/specs only; it does not run the cascade or touch `src/`/`tests/`.

## Risks / Trade-offs

- **[The Pro `2.13.0-pro` package is not yet packed to the local feed when Platform builds]** →
  The cross-repo apply sequences producer-before-consumer (buildOrder 1 → 2); `cross-repo-pack.sh`
  packs Pro and clears the NuGet cache before Platform restores. This child does not build against
  the new pin; that is an apply-stage concern.
- **[A stalled LICENSED engine could be wrongly de-escalated to Degraded]** → Out of scope for the
  producer fix by construction (Pro/ADR-0017 de-escalates the license-blocked branch only; the
  stall path stays `Unhealthy`). The Platform contract test asserts the license-blocked path; a
  real operational fault still fails readiness.
- **[Consumer test drifts from the producer wire shape]** → The spec delta and both assertions cite
  the golden fixture's field names verbatim (`status`, `checks`, `dialer-engine`, `description`,
  `dialer license blocked:` prefix). A rename on the producer side surfaces as a Platform test/smoke
  failure, which is the intended coupling.
- **[Smoke flakiness on the new assertion gates releases prematurely]** → Mitigated by D5: gate only
  after two consecutive green runs.

## Migration Plan

1. (propose — this child) Author proposal, specs delta, design, tasks. No `src/`/`tests/` edits.
2. (apply — `/opsx:apply` + `/xr:apply`) Bump `Verbara.Sdk.Pro.*` pins to `2.13.0-pro`; the
   producer-first pin cascade packs Pro, clears the NuGet cache, restores Platform.
3. (apply) Add the community-boot readiness integration test; sharpen
   `docker/verbara-smoke-released.sh`; add the CHANGELOG `[Unreleased]` entry.
4. (post-merge) After two consecutive green community smoke runs, promote the leg to gating in
   `.github/workflows/release.yml`.

**Rollback:** revert the `Directory.Packages.props` pins to `2.12.0-pro`. Because Platform owns no
severity source here, reverting the pin restores the prior (503) behavior with no other Platform
change to undo. The added test/smoke assertions are inert without the new pin (they would fail,
signalling the revert), so a rollback also reverts them together.

## Open Questions

- None blocking. The exact test-project placement and fixture-loading mechanism for the integration
  test are apply-stage implementation details, constrained by the spec delta and the golden fixture.
