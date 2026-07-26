---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform operators (community / self-host deployments)
decision_ref: Verbara.Sdk.Pro/ADR-0017
---

## Why

Every unlicensed community / self-host Platform deployment reports `GET /health/ready` as
**503** today and is therefore permanently un-ready — its pod never joins the load balancer. The
root cause is producer-side: `AddProDialer` registers the `dialer-engine` health check (tagged
`ready`) on **any** Postgres-configured deploy, and a license-blocked engine currently maps to
`Unhealthy`, which drags the `/health/ready` aggregate to 503. That re-litigates the "the binary
always runs; Pro features are gated" model (Pro/ADR-0012) through the readiness probe. The fix
lives in Pro (Verbara.Sdk.Pro/ADR-0017): a license-blocked engine now settles **Degraded** (HTTP
200) instead of **Unhealthy** (503). This is the **consumer child** — Platform consumes that
producer fix and pins the resulting readiness contract so it cannot silently regress.

## What Changes

- **Pin bump (consumer):** advance the `Verbara.Sdk.Pro.*` package pins from `2.12.0-pro` to
  `2.13.0-pro` (the version carrying the Pro readiness-severity fix) in
  `Directory.Packages.props`. The pin cascade proper (`cross-repo-pack.sh` + NuGet cache clear +
  `dotnet restore`) is an **apply-stage** step (`/xr:apply`), NOT part of this propose-only child.
- **Readiness contract test (new):** an integration test pins the community-boot contract — an
  unlicensed / community boot returns `GET /health/ready` **200**, and in the JSON body emitted by
  `src/Verbara.Platform.Api/Health/HealthReportJsonWriter.cs` the `checks` object's `dialer-engine`
  entry has `status` == `Degraded` and a `description` that STARTS WITH the stable prefix
  `dialer license blocked:`. The test asserts the **prefix**, never the exact reason suffix.
- **Smoke assertion (sharpen + graduate):** sharpen `docker/verbara-smoke-released.sh` to assert
  not merely that `/health/ready` is 200 but that its `dialer-engine` entry is `Degraded` with the
  `dialer license blocked:` prefix. Once the community smoke leg is green **twice consecutively**,
  graduate it in `.github/workflows/release.yml` from report-only (`continue-on-error: true`) to
  **gating**.
- **CHANGELOG:** a `[Unreleased]` entry recording the consumed contract flip (503 → 200) and the
  pin bump.
- **No Platform health-check source change.** The `/health/ready` aggregate flips 503 → 200 on its
  own once the Pro pin is bumped — Platform owns no severity logic here.

## Capabilities

### New Capabilities
- `community-boot-readiness`: the consumer-side readiness contract Platform pins over the Pro
  producer fix — an unlicensed / community boot's `/health/ready` returns 200 with the
  `dialer-engine` check `Degraded` and its `description` preserving the `dialer license blocked:`
  prefix, and the released-image smoke asserts that shape (not merely a 200) as a gating leg.

### Modified Capabilities
<!-- None. No existing Platform capability's requirements change: released-image-smoke's existing
     requirements still hold; this change ADDS a new dialer-engine-degraded assertion to the smoke
     leg, specified under the new community-boot-readiness capability rather than by rewriting the
     released-image-smoke spec's requirements. dialer-license-audit is a separate persistence
     capability and is untouched. -->

## Impact

- **Config:** `Directory.Packages.props` — all `Verbara.Sdk.Pro.*` pins `2.12.0-pro` → `2.13.0-pro`.
- **Tests:** one new integration test asserting the community-boot readiness contract against the
  `HealthReportJsonWriter` JSON body.
- **CI / release:** `docker/verbara-smoke-released.sh` (sharpened body assertion) and
  `.github/workflows/release.yml` (community smoke leg promoted report-only → gating after two
  consecutive green runs).
- **Docs:** `CHANGELOG.md` `[Unreleased]`.
- **No production source touched.** No change to any file under `src/Verbara.Platform.Api/Health/`
  or elsewhere in `src/` — the aggregate flips on the pin bump alone.
- **Cross-repo:** consumer child of Verbara.Sdk.Pro's `license-gated-engine-health-degraded` host
  change (producer = Pro; Pro/ADR-0017). No Pro-only IP crosses the boundary; the license
  semantics stay in private Pro.

### Out of Scope (explicit)

- **Any change to Platform health-check source** (`AsteriskAmiHealthCheck`,
  `HealthReportJsonWriter`, or the `/health/ready` mapping in `Program.cs`) — the aggregate flips
  503 → 200 on its own once the Pro pin is bumped.
- **The licensed-profile smoke leg** — asserting a *licensed* boot's readiness shape is a separate
  follow-up change, not built here.
