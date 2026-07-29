# sdk-pin-cascade Specification

## Purpose
Platform's consumer-side contract for advancing the `Verbara.Sdk` baseline. Platform is the **leaf**
of `Sdk` → `Sdk.Pro` → `Platform` and pins **both prongs directly** (Platform/ADR-0001), so a
cascade here is purely a consumer act with three obligations that must land together: take the new
`Verbara.Sdk.*` pins, take the matching `Verbara.Sdk.Pro.*` build compiled against them, and
re-align every direct pin whose transitive floor the new Sdk raises — **in the SAME commit**,
because `Directory.Packages.props` sets `CentralPackageTransitivePinningEnabled=true` and
`Directory.Build.props` leaves `NU1605` out of `NoWarn` under `TreatWarningsAsErrors=true`, so a
direct pin below the declared floor is a build error, not a warning. Every intermediate state is
red; the same-commit rule is enforced by configuration whether or not anyone remembers it.

The requirements below also fence what this shape invites: a pin family split across
**non-adjacent** `ItemGroup`s (edit the group you opened, leave the rest behind), a prefix-anchored
rewrite that sweeps a same-prefix family onto the wrong version band (`Verbara.Sdk.` is a strict
prefix of `Verbara.Sdk.Pro.`, and the two move to **different** targets; `Microsoft.Extensions.`
also matches a test-only package on another servicing track), and the temptation to bolt on a
version-asserting gate. They also fix the verification posture — the existing required gates carry
the proof, notably `AOT Publish (Api)`, since a raised transitive closure is exactly what can
regress AOT-safety — and the disclosure posture: the `[Unreleased]` `### Dependencies` entry
records the resulting `Sdk × Pro × Platform` triple.

**Reading the version literals.** These requirements are written against the cascade that
introduced them (`Verbara.Sdk` `2.3.2` → `2.4.0`, `Verbara.Sdk.Pro` `2.13.0-pro` → `2.13.1-pro`,
`Microsoft.Extensions.*` `10.0.9` → `10.0.10`; counts **7**, **22**, **7** and the line ranges of
that `Directory.Packages.props`) because that cascade's contract froze those values verbatim. The
durable content is the **shape** — same-commit prongs, declared-not-inferred scope, gate-based
verification, the CHANGELOG triple — which a later cascade re-instantiates with its own versions,
counts and line ranges. The governing decision is `Verbara.Sdk/ADR-0040`; the sibling middle-link
statement of the same mechanism (with the extra producer obligation Platform does not have) is
`Verbara.Sdk.Pro`'s own `sdk-pin-cascade` capability.

## Requirements
### Requirement: The `Verbara.Sdk.*` prong advances `2.3.2` → `2.4.0` across all 7 direct pins

Platform MUST advance every direct `Verbara.Sdk.*` pin in `Directory.Packages.props` from
**`2.3.2`** to **`2.4.0`** — **7** `PackageVersion` elements, spread over **two NON-ADJACENT**
`ItemGroup`s at lines **24-26** (`Verbara.Sdk.Data.Npgsql`, `Verbara.Sdk.Cluster.Primitives`,
`Verbara.Sdk.Cluster.Postgres`) and lines **43-46** (`Verbara.Sdk.Hosting`, `Verbara.Sdk.Push`,
`Verbara.Sdk.Resilience`, `Verbara.Sdk.OpenTelemetry`). The target version is **`2.4.0`**, published
**2026-07-26** as tag **`v2.4.0`**; it carries **no API change** (a pure dependency-bump release), so
Platform SHALL NOT make any `src/` or `tests/` change to consume it.

A cascade that advances only the `ItemGroup` an editor happened to open leaves the other family
below the baseline; the split across two non-adjacent groups is the failure mode this requirement
pins.

#### Scenario: All 7 `Verbara.Sdk.*` pins reach `2.4.0`, both `ItemGroup`s

- **GIVEN** `Directory.Packages.props` with 7 direct `Verbara.Sdk.*` `PackageVersion` elements at
  `2.3.2`, three at lines 24-26 and four at lines 43-46
- **WHEN** the cascade is applied
- **THEN** all **7** carry `Version="2.4.0"`
- **AND** no `Verbara.Sdk.*` pin is left at `2.3.2`

#### Scenario: No Platform source change accompanies the Sdk bump

- **GIVEN** `Verbara.Sdk` `2.4.0` ships zero API change relative to `2.3.2`
- **WHEN** the cascade is applied
- **THEN** no file under `src/` or `tests/` is modified by this change

### Requirement: Raised transitive floors move in the SAME commit as the `Verbara.Sdk.*` pins

Platform MUST re-align, **in the same commit** as the `Verbara.Sdk.*` bump, every direct pin that
`Verbara.Sdk` `2.4.0` raises as a transitive floor — for this cascade that is the
`Microsoft.Extensions.*` family: **7** `PackageVersion` elements at `Directory.Packages.props` lines
**9-15** (one contiguous `ItemGroup`), from **`10.0.9`** to **`10.0.10`**. This realizes
`Verbara.Sdk/ADR-0040` D2.

Splitting the two halves across commits is not a style preference: `Directory.Build.props` sets
`TreatWarningsAsErrors=true` and its `NoWarn` list covers `NU1902` and `NU1603` but deliberately
**not** `NU1605`, while `Directory.Packages.props` sets
`CentralPackageTransitivePinningEnabled=true` — so a direct pin below the declared floor errors
rather than resolving upward. Either half alone leaves `main` red. `Microsoft.Extensions.*` is the
**only** family that collides: Platform pins neither `NATS.Client.Core` nor `OpenTelemetry`
directly, and both arrive purely transitively.

#### Scenario: Both halves land together and restore is clean

- **GIVEN** the 7 `Verbara.Sdk.*` pins are advanced to `2.4.0`
- **WHEN** the 7 `Microsoft.Extensions.*` pins at lines 9-15 are advanced from `10.0.9` to
  `10.0.10` in the same commit
- **THEN** `dotnet restore` completes with no `NU1605` package-downgrade error

#### Scenario: The Sdk half alone is red

- **GIVEN** a commit that advances the `Verbara.Sdk.*` pins to `2.4.0` but leaves
  `Microsoft.Extensions.*` at `10.0.9`
- **WHEN** restore runs under `CentralPackageTransitivePinningEnabled=true` and
  `TreatWarningsAsErrors=true`
- **THEN** `NU1605` is raised as an **error** and the build fails — the change MUST NOT be merged in
  that shape

### Requirement: The `Verbara.Sdk.Pro.*` prong advances `2.13.0-pro` → `2.13.1-pro` across all 22 pins

Platform MUST advance every direct `Verbara.Sdk.Pro.*` pin in `Directory.Packages.props` from
**`2.13.0-pro`** to **`2.13.1-pro`** — **22** `PackageVersion` elements at lines **50-71** — because
`2.13.1-pro` is the `Verbara.Sdk.Pro` build compiled against `Verbara.Sdk` **`2.4.0`**. Platform
consumes the chain through **both prongs** (Platform/ADR-0001), so leaving the Pro prong on
`2.13.0-pro` while the Sdk prong moves to `2.4.0` re-creates the same floor split inside the
transitive graph.

This half has a hard cross-repo precondition the Platform leg cannot satisfy on its own: the
`Verbara.Sdk.Pro` leg of this cascade MUST move its own published package version
**`2.13.0-pro` → `2.13.1-pro`** in the same commit as its pins. If it does not, its release workflow
produces a **silent no-op release**, `2.13.1-pro` never exists on the feed, and Platform can never
restore a Pro build compiled against Sdk `2.4.0`.

#### Scenario: All 22 `Verbara.Sdk.Pro.*` pins reach `2.13.1-pro`

- **GIVEN** `Directory.Packages.props` lines 50-71 hold 22 `Verbara.Sdk.Pro.*` pins at
  `2.13.0-pro`
- **WHEN** the cascade is applied
- **THEN** all **22** carry `Version="2.13.1-pro"`
- **AND** no `Verbara.Sdk.Pro.*` pin is left at `2.13.0-pro`

#### Scenario: Platform cannot restore if the Pro leg published a no-op

- **GIVEN** the `Verbara.Sdk.Pro` leg merged its pin bump without moving its own published package
  version from `2.13.0-pro` to `2.13.1-pro`
- **WHEN** Platform restores against `2.13.1-pro`
- **THEN** restore fails because no `2.13.1-pro` package exists on the feed
- **AND** the Platform leg is blocked until the Pro leg republishes correctly

### Requirement: Cascade scope is DECLARED, never inferred by prefix

Platform MUST bound this cascade to the three families named above and MUST NOT sweep in any pin
that merely shares a prefix. This realizes `Verbara.Sdk/ADR-0040` D4: a blanket prefix rewrite is a
defect, not a shortcut. Two prefix hazards are live in this repo's pin file:

- **`Verbara.Sdk.` also matches `Verbara.Sdk.Pro.`**, and in this cascade the two families move to
  **DIFFERENT** target versions — **`2.4.0`** vs **`2.13.1-pro`**. Any rewrite MUST anchor on
  `Verbara.Sdk.` **not followed by** `Pro.`.
- **`Microsoft.Extensions.*` also matches `Microsoft.Extensions.TimeProvider.Testing`**, pinned at
  **`10.0.0`** on line **140**. It is test-only and on a different servicing track; it SHALL remain
  at **`10.0.0`**.

`OpenTelemetry` and `NATS.Client.Core` are out of scope for a different reason: Platform pins
neither directly, so there is no direct pin to re-align.

#### Scenario: The `Verbara.Sdk.Pro.*` family is not swept to `2.4.0`

- **GIVEN** a rewrite anchored on the bare prefix `Verbara.Sdk.`
- **WHEN** it is applied to `Directory.Packages.props`
- **THEN** it would set the 22 `Verbara.Sdk.Pro.*` pins to `2.4.0` instead of `2.13.1-pro`
- **AND** that outcome is a defect — the applied cascade MUST leave the Pro family at
  `2.13.1-pro`

#### Scenario: `Microsoft.Extensions.TimeProvider.Testing` stays at `10.0.0`

- **GIVEN** `Microsoft.Extensions.TimeProvider.Testing` pinned at `10.0.0` on line 140
- **WHEN** the 7 `Microsoft.Extensions.*` pins at lines 9-15 are advanced to `10.0.10`
- **THEN** line 140 still reads `Version="10.0.0"`

### Requirement: Correctness is proven by the existing required gates, not by a version-asserting check

Platform MUST verify this cascade through its existing required CI gate set and SHALL NOT introduce
any script, test or CI check that asserts a package version string. No such check exists in this
repo today, and that is deliberate: a version-asserting gate would have to be edited by every future
cascade and would assert a fact `Directory.Packages.props` already states.

The gates that DO carry the proof are: `dotnet restore` (catches `NU1605`/`NU1109` resolution
failures), `dotnet build` + `dotnet test` under `TreatWarningsAsErrors=true` / `WarningLevel=9999`
(zero warnings), and the **required** `AOT Publish (Api)` job (`aot-probe`,
`.github/workflows/ci.yml:453-508`), which fails on **any** `warning ILxxxx` from the Native AOT
publish of the Api closure. That job exists precisely because a dependency bump can regress
AOT-safety and would otherwise only surface at tag time.

#### Scenario: A regression in the raised graph fails an existing gate

- **GIVEN** the cascade advances the Sdk, Pro and `Microsoft.Extensions.*` pins
- **WHEN** the new transitive closure regresses AOT-safety
- **THEN** the required `AOT Publish (Api)` job emits `warning ILxxxx` and fails the PR
- **AND** no new version-asserting check was needed to detect it

#### Scenario: No version-assertion artifact is added

- **GIVEN** the cascade is applied
- **WHEN** the diff is reviewed
- **THEN** it contains no script, test or workflow step that asserts `2.4.0`, `2.13.1-pro` or
  `10.0.10` as a literal

### Requirement: The CHANGELOG records the resulting `Sdk × Pro × Platform` triple

Platform MUST record, in `CHANGELOG.md` under `[Unreleased]` using this repo's existing
`### Dependencies` section shape, the version triple this cascade establishes — `Verbara.Sdk`
**`2.4.0`** × `Verbara.Sdk.Pro` **`2.13.1-pro`** × Platform — together with the raised
`Microsoft.Extensions.*` floor **`10.0.10`** and the reason it moved (`NU1605` is fatal here). This
discharges `Verbara.Sdk/ADR-0040` D6's compatibility-matrix obligation for the versions this cascade
touches: the incremental CHANGELOG callout **is** the matrix.

The entry SHALL state that `2.4.0` carries no API change, so a reader can tell "we moved the
baseline" apart from "we adopted a feature".

#### Scenario: The `[Unreleased]` entry carries the triple and the reason

- **GIVEN** the cascade is applied
- **WHEN** `CHANGELOG.md` `[Unreleased]` is read
- **THEN** it names `Verbara.Sdk` `2.3.2` → `2.4.0`, `Verbara.Sdk.Pro` `2.13.0-pro` →
  `2.13.1-pro`, and `Microsoft.Extensions.*` `10.0.9` → `10.0.10`
- **AND** it states that the `Microsoft.Extensions.*` bump clears the `NU1605` downgrade error
- **AND** it states that `2.4.0` is a dependency-bump release with no API change

