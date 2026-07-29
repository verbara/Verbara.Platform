---
tier: GRANDE
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Platform maintainers + release engineering (the cross-repo pin cascade train)
decision_ref: Verbara.Sdk/ADR-0040
---

## Why

**The pain:** Platform is the downstream-most consumer of the `Sdk → Sdk.Pro → Platform` NuGet
chain, and it pins **both prongs directly** (Platform/ADR-0001): `Verbara.Sdk.*` at `2.3.2` and
`Verbara.Sdk.Pro.*` at `2.13.0-pro`. `Verbara.Sdk` published **`2.4.0`** on **2026-07-26** — a pure
dependency-bump release with **zero API change** — but that release raises the
`Microsoft.Extensions.*` transitive floor to **`10.0.10`**. Platform *also* pins
`Microsoft.Extensions.*` **directly**, today at **`10.0.9`**. Bumping only the `Verbara.Sdk.*` pins
therefore leaves a direct pin *below* the floor Platform's own dependency now declares, which is
`NU1605` (package downgrade).

**Why that is fatal, not advisory:** `Directory.Build.props` sets `TreatWarningsAsErrors=true` /
`WarningLevel=9999` and its `NoWarn` list suppresses `NU1902` and `NU1603` but deliberately **not**
`NU1605`; `Directory.Packages.props` sets `CentralPackageTransitivePinningEnabled=true`, which is
exactly why the collision surfaces as an error instead of resolving upward in silence. A naive
one-family pin swap does not degrade — it turns `main` red. The exact same failure mode has already
been paid for once in this repo (`CHANGELOG.md` `[2.17.0]`: "Sdk 2.3.0 raises transitive floors to
`Microsoft.Extensions.*` ≥ 10.0.9 and Npgsql ≥ 10.0.3; Platform's central pins for both are bumped
alongside … to clear the resulting NU1605/NU1109 downgrade errors").

**Who it hurts:** Platform maintainers (a red `main` on what looks like a one-line bump) and, more
slowly, every Platform deployment — the baseline lag is drift that compounds until a security patch
forces the cascade under time pressure.

**Why now:** `Verbara.Sdk/ADR-0040` makes an SDK pin cascade a **first-class cross-repo change of
its own**, never folded into a release train (D3), and requires that the raised floors move **in the
same commit** as the pins (D2). This change is the Platform (host) leg of that train, registered as
verbara-meta roadmap **R-013**.

## What Changes

- **`Verbara.Sdk.*` pins (7):** advance from **`2.3.2`** to **`2.4.0`** in `Directory.Packages.props`
  — lines **24-26** and **43-46**, **two NON-ADJACENT `ItemGroup`s** (`Verbara.Sdk.Data.Npgsql`,
  `Verbara.Sdk.Cluster.Primitives`, `Verbara.Sdk.Cluster.Postgres`; `Verbara.Sdk.Hosting`,
  `Verbara.Sdk.Push`, `Verbara.Sdk.Resilience`, `Verbara.Sdk.OpenTelemetry`).
- **`Verbara.Sdk.Pro.*` pins (22):** advance from **`2.13.0-pro`** to **`2.13.1-pro`** in
  `Directory.Packages.props` lines **50-71** — the Pro build that itself consumes Sdk **`2.4.0`**.
- **`Microsoft.Extensions.*` pins (7):** advance from **`10.0.9`** to **`10.0.10`** in
  `Directory.Packages.props` lines **9-15** (one contiguous `ItemGroup`), **in the same commit** as
  the `Verbara.Sdk.*` bump — this is the floor re-alignment `Verbara.Sdk/ADR-0040` D2 mandates.
  Splitting it into a second commit leaves `main` red under `TreatWarningsAsErrors`.
- **`CHANGELOG.md` `[Unreleased]`:** an entry under the repo's existing `### Dependencies` shape
  recording the resulting `Sdk × Pro × Platform` triple (`2.4.0` × `2.13.1-pro` × Platform) and the
  raised `Microsoft.Extensions.*` floor `10.0.10`. This discharges `Verbara.Sdk/ADR-0040` D6's
  compatibility-matrix obligation for the versions this cascade touches.
- **No Platform source change.** Nothing under `src/` or `tests/` is touched. There is no API
  change to consume: `2.4.0` is a pure dependency-bump release.
- **Propose-only.** The pin edits themselves and the cascade mechanics
  (`scripts/cross-repo-pack.sh` → NuGet cache clear → `dotnet restore`) are **apply-stage** work
  (`/opsx:apply` + `/xr:apply`), exactly as the archived
  `2026-07-26-license-gated-engine-health-degraded` consumer child scoped it. This change writes no
  `Directory.Packages.props` and no `Directory.Build.props` edit.

### Out of Scope (explicit — `Verbara.Sdk/ADR-0040` D4: scope is declared, never inferred)

- **`Microsoft.Extensions.TimeProvider.Testing` stays at `10.0.0`** (`Directory.Packages.props`
  line **140**). It is test-only and rides a **different servicing track**. A blanket
  `Microsoft.Extensions.*` rewrite that sweeps it onto `10.0.10` is a **defect**, not a shortcut.
- **`OpenTelemetry` and `NATS.Client.Core`** — Platform pins **neither** directly; both arrive
  purely transitively, so there is nothing here to re-align for them.
- **Adopting any Sdk `2.4.0` capability.** `2.4.0` ships zero API change; whether to adopt anything
  later stays a separate per-consumer call.
- **Any release train.** `Verbara.Sdk/ADR-0040` D3 — this cascade is not bundled with a version cut.

### Prefix hazards (both are real in this repo's pin file)

- **`Verbara.Sdk.` also matches `Verbara.Sdk.Pro.`** in `Directory.Packages.props`, and in this
  cascade the two families move to **DIFFERENT** versions — `2.4.0` vs `2.13.1-pro`. Any rewrite
  MUST anchor on `Verbara.Sdk.` **not followed by** `Pro.`.
- **`Microsoft.Extensions.*` also matches `Microsoft.Extensions.TimeProvider.Testing`**, which is
  out of scope (above).

## Capabilities

### New Capabilities

- `sdk-pin-cascade`: Platform's consumer-side contract for advancing the `Verbara.Sdk` baseline —
  the pin families and their target versions are **declared, not inferred**; a `Verbara.Sdk.*` bump
  and the transitive floors that release raises move in **one commit**; out-of-scope pins on other
  servicing tracks are named and left alone; and correctness is proven by restore + build + test +
  the required `AOT Publish (Api)` gate rather than by any version-asserting script.

### Modified Capabilities

<!-- None. No existing Platform capability's requirements change. This cascade consumes a
     zero-API-change SDK release, so no behavioural spec (released-image-smoke,
     community-boot-readiness, typed-response-schemas, …) is re-specified. The new capability
     covers the pin-state contract itself, which no existing spec owned. -->

## Impact

- **Config:** `Directory.Packages.props` only — 7 `Verbara.Sdk.*` pins (`2.3.2` → `2.4.0`),
  22 `Verbara.Sdk.Pro.*` pins (`2.13.0-pro` → `2.13.1-pro`), 7 `Microsoft.Extensions.*` pins
  (`10.0.9` → `10.0.10`). **36 `PackageVersion` elements, one commit.**
- **Source:** none. No file under `src/` or `tests/` changes.
- **CI:** no workflow edit. The cascade is validated by the existing required gates — restore +
  build + `dotnet test` under `TreatWarningsAsErrors`, and the required **`AOT Publish (Api)`**
  (`aot-probe`) job at `.github/workflows/ci.yml:453-508`, which exists precisely because "a
  dependency bump … could regress AOT-safety and only surface at tag time". This repo has **no**
  script, test or CI check that asserts a package version; correctness is enforced indirectly and
  that is by design.
- **Docs:** `CHANGELOG.md` `[Unreleased]`.
- **Cross-repo:** this is the **host** leg (`buildOrder: 3`, chain leaf) of the
  `sdk-2-4-0-pin-cascade` train. `Verbara.Sdk` (`buildOrder: 1`) is the already-published producer
  and needs no code change. `Verbara.Sdk.Pro` (`buildOrder: 2`) is a sibling consumer leg whose own
  cascade must **also** move its published package version **`2.13.0-pro` → `2.13.1-pro`** in the
  same commit as its pins — otherwise its release workflow produces a **silent no-op release** and
  Platform can never restore a Pro build compiled against Sdk **`2.4.0`**.
  `Verbara.Platform.Web` and `verbara-website` are **absent by design**: neither pins any
  `Verbara.Sdk.*` package.
- **Risk posture:** the only major in the raised transitive closure is **`NATS.Client.Core` 3.x**,
  and Platform's source-level exposure to it is **ZERO** (no `NATS` reference anywhere under
  `src/`; the realtime transport is SignalR + StackExchange.Redis). Residual risk reduces to
  restore resolution and AOT publish behaviour — both already covered by the required gates above.
