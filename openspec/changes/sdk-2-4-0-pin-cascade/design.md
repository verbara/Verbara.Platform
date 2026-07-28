## Context

Platform sits at the leaf of the `Verbara.Sdk → Verbara.Sdk.Pro → Verbara.Platform` NuGet chain and
consumes it through **two prongs simultaneously** (Platform/ADR-0001): direct `Verbara.Sdk.*`
references *and* direct `Verbara.Sdk.Pro.*` references. Under central package management every
version lives in `Directory.Packages.props`; today that file holds **7** `Verbara.Sdk.*` pins at
**`2.3.2`**, **22** `Verbara.Sdk.Pro.*` pins at **`2.13.0-pro`**, and **7** `Microsoft.Extensions.*`
pins at **`10.0.9`**.

`Verbara.Sdk` published **`2.4.0`** on **2026-07-26** (tag **`v2.4.0`**) as a pure dependency-bump
release: **no API change**. What it *did* change is the set of transitive floors its packages
declare — `Microsoft.Extensions.*` to **`10.0.10`**, `OpenTelemetry` to `1.17.0`, and
`NATS.Client.Core` to `3.0.0` (a **major**). Of those three, only `Microsoft.Extensions.*` collides
with a Platform pin, because Platform pins that family **directly**, below the new floor. The other
two arrive purely transitively — Platform pins neither.

The collision is fatal by configuration, not advisory:

- `Directory.Build.props` — `TreatWarningsAsErrors=true`, `WarningLevel=9999`, and a `NoWarn` list
  that suppresses `NU1902` and `NU1603` but **deliberately not `NU1605`**.
- `Directory.Packages.props` — `CentralPackageTransitivePinningEnabled=true`, which is exactly why a
  direct pin below a declared transitive floor errors instead of resolving upward in silence.

So the naive "bump the `Verbara.Sdk.*` lines" edit does not degrade — it turns `main` red. This repo
has already paid for that once: `CHANGELOG.md` `[2.17.0]` records the same shape for the `2.3.0`
cascade ("Sdk 2.3.0 raises transitive floors to `Microsoft.Extensions.*` ≥ 10.0.9 and Npgsql ≥
10.0.3; Platform's central pins for both are bumped alongside … to clear the resulting
NU1605/NU1109 downgrade errors").

`Verbara.Sdk/ADR-0040` is the governing decision. It makes an SDK pin cascade a first-class
cross-repo change of its own (D1/D3), requires the raised floors to move in the same commit as the
pins (D2), requires the scope to be **declared, not inferred** (D4), reduces a major in the
transitive closure to a **review item scoped by real source exposure** (D5), and makes the cascade
discharge the compatibility-matrix obligation through the consumer CHANGELOGs (D6). This document
is the **host leg** design: how Platform, the downstream-most affected consumer, executes that.

Orchestration context: this is `buildOrder: 3` of the `sdk-2-4-0-pin-cascade` cross-repo change
(verbara-meta/ADR-0006 staging). `Verbara.Sdk` is `buildOrder: 1` (producer, already published, no
code change) and `Verbara.Sdk.Pro` is `buildOrder: 2` (sibling consumer leg).

## Goals / Non-Goals

**Goals:**

- Advance the **7** direct `Verbara.Sdk.*` pins from **`2.3.2`** to **`2.4.0`** across the **two
  NON-ADJACENT** `ItemGroup`s at `Directory.Packages.props` lines **24-26** and **43-46**.
- Advance the **22** direct `Verbara.Sdk.Pro.*` pins from **`2.13.0-pro`** to **`2.13.1-pro`**
  (lines **50-71**) — the Pro build compiled against Sdk `2.4.0`.
- Advance the **7** direct `Microsoft.Extensions.*` pins from **`10.0.9`** to **`10.0.10`**
  (lines **9-15**) **in the same commit**, clearing `NU1605` per `Verbara.Sdk/ADR-0040` D2.
- Keep the scope **declared**: `Microsoft.Extensions.TimeProvider.Testing` stays at **`10.0.0`**
  (line **140**), and neither `OpenTelemetry` nor `NATS.Client.Core` gains a direct pin.
- Record the resulting `Sdk × Pro × Platform` triple in `CHANGELOG.md` `[Unreleased]`
  (`Verbara.Sdk/ADR-0040` D6).

**Non-Goals:**

- **Any Platform source change.** `2.4.0` ships zero API change; nothing under `src/` or `tests/`
  is touched, and no new capability is adopted.
- **Running the cascade mechanics.** `scripts/cross-repo-pack.sh` → NuGet cache clear →
  `dotnet restore` is apply-stage work, not propose-stage.
- **Bundling with a release.** `Verbara.Sdk/ADR-0040` D3 — no version cut rides along.
- **Adding a version-asserting gate.** This repo has none, deliberately (see D5 below).
- **Re-specifying anything the SDK owns.** The floors are facts published by `v2.4.0`; Platform
  consumes them, it does not re-decide them.

## Decisions

**D1 — One commit, three families, 36 `PackageVersion` elements.**
The `Verbara.Sdk.*` bump (7), the `Verbara.Sdk.Pro.*` bump (22) and the `Microsoft.Extensions.*`
floor re-alignment (7) land together. *Alternative considered:* three commits, one per family, for
a reviewable diff. *Rejected:* every intermediate state is red — `Verbara.Sdk.*` at `2.4.0` with
`Microsoft.Extensions.*` at `10.0.9` is `NU1605` (fatal), and `Verbara.Sdk.*` at `2.4.0` with
`Verbara.Sdk.Pro.*` still at `2.13.0-pro` re-creates the same split one level down in the transitive
graph. `Verbara.Sdk/ADR-0040` D2 states this as a rule; the configuration enforces it whether or not
anyone remembers it.

**D2 — Anchor every rewrite on `Verbara.Sdk.` NOT followed by `Pro.`.**
The two Verbara families move to **DIFFERENT** versions in this cascade — **`2.4.0`** vs
**`2.13.1-pro`** — and the shorter prefix is a strict prefix of the longer one. The same hazard
exists for `Microsoft.Extensions.*`, which also matches `Microsoft.Extensions.TimeProvider.Testing`
(out of scope, `10.0.0`, line **140**, a different servicing track). *Alternative considered:* a
single `sed` over `Verbara.Sdk.` and one over `Microsoft.Extensions.`. *Rejected:* that is exactly
the defect `Verbara.Sdk/ADR-0040` D4 names — it would drag the 22 Pro pins onto `2.4.0` and the
test-only `TimeProvider.Testing` pin onto `10.0.10`. Scope is declared: the target line ranges
(9-15, 24-26, 43-46, 50-71) and the excluded line (140) are stated in the spec and in the tasks so
the edit is checkable by reading, not by trusting a regex.

**D3 — Propose-only; the cascade mechanics belong to apply.**
This change authors proposal, spec delta, design and tasks. It writes **no**
`Directory.Packages.props` and **no** `Directory.Build.props` edit, and it does not run
`scripts/cross-repo-pack.sh`, does not clear the NuGet cache, and does not `dotnet restore`. That
sequence runs at `/opsx:apply` + `/xr:apply`, staged by `buildOrder` with the pack barrier between
stages. This mirrors the archived
`openspec/changes/archive/2026-07-26-license-gated-engine-health-degraded` consumer child, whose
"What Changes" was likewise a pin bump that explicitly deferred "the pin cascade proper
(`cross-repo-pack.sh` + NuGet cache clear + `dotnet restore`)" to the apply stage. *Alternative
considered:* fold the pin edit into this change so the branch builds green immediately. *Rejected:*
Platform cannot restore `2.13.1-pro` before the `Verbara.Sdk.Pro` leg (`buildOrder: 2`) publishes
it; a propose-stage edit would put the branch in a state that cannot restore, which reads as a
broken change rather than a correctly-ordered one.

**D4 — `decision_ref` points at the PRODUCER repo's ADR; no new Platform ADR is created.**
The durable architectural decision — *an SDK pin cascade is its own train* — is already recorded, in
the repo whose publishing behaviour originates it: `Verbara.Sdk/ADR-0040` (which explicitly rejected
recording it in verbara-meta, Option E). Platform adds nothing durable of its own here; it executes.
*Alternative considered:* mint a Platform ADR restating the cascade rule. *Rejected:* it would be a
copy that drifts, and it contradicts this repo's own precedent — the archived
`2026-07-26-license-gated-engine-health-degraded` change likewise set `decision_ref` to the producer
repo's ADR (`Verbara.Sdk.Pro/ADR-0017`) rather than minting a local one. Platform-specific facts
that *are* durable (the dual-prong pin shape) already live in Platform/ADR-0001. Cross-repo ADR
citations here are repo-qualified throughout, per this repo's citation convention.

**D5 — Verification is the existing required gate set; no version-asserting check is added.**
There is **no** script, test or CI check in this repo that asserts a package version, and none is
added. Correctness is enforced indirectly and sufficiently: `dotnet restore` catches
`NU1605`/`NU1109`; `dotnet build` + `dotnet test` run under `TreatWarningsAsErrors=true` /
`WarningLevel=9999`; and the **required** `AOT Publish (Api)` job
(`aot-probe`, `.github/workflows/ci.yml:453-508`) publishes `src/Verbara.Platform.Api` with
`PublishAot=true` and fails on **any** `warning ILxxxx`. That job was added precisely because a
dependency bump can regress AOT-safety and would otherwise only surface at tag time — this cascade
is its designed use case. *Alternative considered:* add a guard script asserting the pins match the
cascade's fixture. *Rejected:* it would need editing by every future cascade, and it would assert a
fact `Directory.Packages.props` already states in one place. AOT constraint (Platform/ADR-0022) is
honoured trivially here: no source changes, so no new reflection, no new `[JsonSerializable]` DTO,
no Dapper — the raised closure's AOT behaviour is what `aot-probe` measures.

**D6 — The `NATS.Client.Core` 3.x major is a review item, and the review comes out empty.**
`Verbara.Sdk/ADR-0040` D5 requires each consumer to state its **actual source-level exposure** to a
major raised in the transitive closure. Platform's exposure to NATS is **ZERO**: there is no `NATS`
reference anywhere under `src/`; the realtime transport is SignalR + StackExchange.Redis; the
remaining mentions are documentation/history (and one false positive in a coturn compose file where
"NATs" means NAT traversal). NATS reaches the graph only through a Verbara SDK push package Platform
does not reference. Residual risk is therefore **not** "a major library changed under us" but the
two mechanical ones: restore resolution and AOT publish behaviour of the new assembly inside the Api
publish closure. Both are covered by D5's gates. *Alternative considered:* pin `NATS.Client.Core`
directly at the old version to hold it back. *Rejected:* it would add a direct pin Platform does not
need, invert the floor relationship, and re-introduce `NU1605` from the other side.

**D7 — CHANGELOG shape: this repo's `### Dependencies` section, not Pro's blockquote callout.**
`Verbara.Sdk/ADR-0040` D6 asks for the triple in "the consumer CHANGELOGs' existing
`Dependency floors:` callout". That callout shape is `Verbara.Sdk.Pro`'s; Platform's own established
shape for exactly this content is a `### Dependencies` section — see `CHANGELOG.md` `[2.17.0]`,
which recorded the `2.3.0` cascade's version move *and* its raised floors in that form. The
obligation is the content (the `Sdk × Pro × Platform` triple plus the raised floor and its reason),
not the punctuation; this leg satisfies D6 in this repo's existing idiom rather than importing a
sibling repo's. *Alternative considered:* introduce the blockquote callout here for cross-repo
symmetry. *Rejected:* a one-off formatting import in a 22-version changelog is drift, not symmetry.

## Risks / Trade-offs

- **[A prefix-anchored rewrite drags the 22 `Verbara.Sdk.Pro.*` pins onto `2.4.0`]** → D2: anchor on
  `Verbara.Sdk.` **not followed by** `Pro.`; the tasks state the target line ranges (24-26, 43-46 for
  `2.4.0`; 50-71 for `2.13.1-pro`) so the diff is checkable by reading. A wrong version here fails
  restore immediately — the package does not exist — so it cannot reach `main`.
- **[A blanket `Microsoft.Extensions.*` rewrite sweeps `TimeProvider.Testing` from `10.0.0` to
  `10.0.10`]** → D2 + `Verbara.Sdk/ADR-0040` D4: the excluded line (**140**) is named in the
  proposal, the spec and the tasks. This one would **not** necessarily fail the build, which is why
  it is called out explicitly rather than left to CI.
- **[The `Verbara.Sdk.Pro` leg merges its pins without moving its own published package version
  `2.13.0-pro` → `2.13.1-pro`]** → its release workflow then produces a **silent no-op release**,
  `2.13.1-pro` never lands on the feed, and Platform's restore fails with a missing package. Not a
  Platform-fixable failure: the ordering (`buildOrder: 2` before `3`) plus the pack barrier in the
  apply staging is the mitigation, and the spec pins it as an explicit scenario so the dependency is
  visible from this repo.
- **[The raised transitive closure regresses Native AOT]** → the required `AOT Publish (Api)` job
  (`.github/workflows/ci.yml:453-508`) fails the PR on any `warning ILxxxx`. This is the risk the
  job was created for; no new gate is warranted (D5).
- **[Only one of the two halves is committed]** → structurally impossible to merge silently:
  `NU1605` is fatal (`TreatWarningsAsErrors`, not in `NoWarn`,
  `CentralPackageTransitivePinningEnabled=true`), so restore fails before review.
- **[The cascade is folded into a release train under schedule pressure]** → `Verbara.Sdk/ADR-0040`
  D3 forbids it, and the reason is diagnostic, not ceremonial: bundled, a failed restore is
  indistinguishable from a failed release and blocks a train that had nothing to do with it.
- **[Trade-off accepted: Platform lags the SDK baseline by design]** → `2.3.2` → `2.4.0` sat
  unconsumed from 2026-07-26. `Verbara.Sdk/ADR-0040` D1 makes that lag an accepted cost rather than
  a drift signal; the cost of removing it is exactly this ceremony.

## Migration Plan

1. **(propose — this change)** Author proposal, spec delta, design, tasks. No
   `Directory.Packages.props` edit, no `Directory.Build.props` edit, no `src/`/`tests/` edit, no
   restore.
2. **(apply — `/xr:apply` stage 2)** The `Verbara.Sdk.Pro` leg lands its own cascade **including**
   its published package version `2.13.0-pro` → `2.13.1-pro` in the same commit, and publishes.
   `scripts/cross-repo-pack.sh` runs as the stage barrier; the NuGet cache is cleared.
3. **(apply — `/xr:apply` stage 3, this repo)** In one commit to `Directory.Packages.props`: 7
   `Verbara.Sdk.*` pins `2.3.2` → `2.4.0` (lines 24-26, 43-46); 22 `Verbara.Sdk.Pro.*` pins
   `2.13.0-pro` → `2.13.1-pro` (lines 50-71); 7 `Microsoft.Extensions.*` pins `10.0.9` → `10.0.10`
   (lines 9-15). Leave line 140 (`Microsoft.Extensions.TimeProvider.Testing`) at `10.0.0`.
4. **(apply)** `dotnet restore` → `dotnet build` → `dotnet test`, all green with zero warnings; add
   the `CHANGELOG.md` `[Unreleased]` `### Dependencies` entry carrying the triple.
5. **(PR)** CI green including the required `AOT Publish (Api)` job. `openspec validate --all
   --strict` green.

**Rollback:** revert the single `Directory.Packages.props` commit. Because Platform owns no source
change in this cascade, that restores the `2.3.2` / `2.13.0-pro` / `10.0.9` baseline exactly, with
nothing else to undo. The `Verbara.Sdk.Pro` `2.13.1-pro` package remains published and harmless —
reverting the consumer pin simply stops resolving it.

## Open Questions

- **None blocking.** The three target versions, the three pin counts, the four in-scope line ranges
  and the one excluded line are all fixed by `fixtures/pin-targets.v1.json` and verified against the
  current `Directory.Packages.props`. The only external dependency is the `Verbara.Sdk.Pro` leg
  publishing `2.13.1-pro`, which the apply staging sequences (`buildOrder: 2` before `3`).
- **Deferred, not open:** whether Platform later adopts anything from the `2.4.0` baseline is a
  separate per-consumer call and is explicitly out of scope here (`2.4.0` carries no API change to
  adopt).
