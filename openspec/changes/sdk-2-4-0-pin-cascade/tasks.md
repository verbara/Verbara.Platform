> **Execution shape (Subagent-Driven Development, FCM batching):**
> **Phase A — foundation (batch):** §1 (cross-repo preconditions) — read-only verification, run as
> one batch. **Phase B — critical component (focused):** §2 (the single-commit pin edit) — one
> focused pass, never fanned out: the three families land in ONE commit and a parallel edit would
> race on the same file. **Phase C — integration (batch):** §3 (out-of-scope confirmations), §4
> (CHANGELOG) and §5 (verification) — batched.
>
> **Everything in §2-§5 is APPLY-STAGE work** (`/opsx:apply` + `/xr:apply`). This propose-only
> change writes no `Directory.Packages.props` edit and runs no restore.

## 1. Cross-repo preconditions (apply-stage, before any Platform edit)

- [ ] 1.1 Confirm `Verbara.Sdk` **`2.4.0`** is published (tag **`v2.4.0`**, 2026-07-26). This leg is
  `buildOrder: 1` and needs **no** code change and **no** pack hop — the packages are already on the
  public feed. Confirm the release is the pure dependency-bump one (no API change).
- [ ] 1.2 Confirm the `Verbara.Sdk.Pro` leg (`buildOrder: 2`) has landed its own cascade — its
  `Verbara.Sdk.*` pins at **`2.4.0`**, its `Microsoft.Extensions.*` pins at **`10.0.10`**, **and**
  its own published package version moved **`2.13.0-pro` → `2.13.1-pro` in the same commit**.
  Without that version move its release workflow produces a **silent no-op release**, `2.13.1-pro`
  never reaches the feed, and step 2.2 below cannot restore. **Halt here if it has not.**
- [ ] 1.3 Run the cross-repo sequence for the Pro hop: **edit Sdk/Pro → `dotnet pack` to the local
  NuGet feed (`scripts/cross-repo-pack.sh`, the `/xr:apply` stage barrier) → clear the NuGet cache
  (`rm -rf ~/.nuget/packages/verbara.sdk.pro*`) → `dotnet restore` the consumer (this repo)**. Do
  **NOT** run any of this during propose.

## 2. The pin edit — ONE commit, three families, 36 `PackageVersion` elements (apply-stage)

- [ ] 2.1 Bump the **7** direct `Verbara.Sdk.*` pins in `Directory.Packages.props` from **`2.3.2`**
  to **`2.4.0`**, across **two NON-ADJACENT `ItemGroup`s**: lines **24-26**
  (`Verbara.Sdk.Data.Npgsql`, `Verbara.Sdk.Cluster.Primitives`, `Verbara.Sdk.Cluster.Postgres`) and
  lines **43-46** (`Verbara.Sdk.Hosting`, `Verbara.Sdk.Push`, `Verbara.Sdk.Resilience`,
  `Verbara.Sdk.OpenTelemetry`). Editing only the group you opened first is the failure mode — check
  both.
- [ ] 2.2 Bump the **22** direct `Verbara.Sdk.Pro.*` pins in `Directory.Packages.props` lines
  **50-71** from **`2.13.0-pro`** to **`2.13.1-pro`**.
- [ ] 2.3 Bump the **7** direct `Microsoft.Extensions.*` pins in `Directory.Packages.props` lines
  **9-15** (one contiguous `ItemGroup`) from **`10.0.9`** to **`10.0.10`**. This is the transitive
  floor `Verbara.Sdk` `2.4.0` raises; it is the `NU1605` half of the change.
- [ ] 2.4 **Commit 2.1 + 2.2 + 2.3 together, in ONE commit** (`Verbara.Sdk/ADR-0040` D2). Every
  intermediate state is red: `Verbara.Sdk.*` at `2.4.0` with `Microsoft.Extensions.*` still at
  `10.0.9` is a fatal `NU1605` (`TreatWarningsAsErrors=true`, `NU1605` absent from `NoWarn`,
  `CentralPackageTransitivePinningEnabled=true`).
- [ ] 2.5 **Prefix-hazard check before committing:** confirm no `Verbara.Sdk.Pro.*` pin was set to
  `2.4.0` and no `Verbara.Sdk.*` (non-Pro) pin was set to `2.13.1-pro`. `Verbara.Sdk.` is a strict
  prefix of `Verbara.Sdk.Pro.`, and the two families move to **DIFFERENT** versions in this cascade.
  Anchor any rewrite on `Verbara.Sdk.` **not followed by** `Pro.`.
- [ ] 2.6 Confirm the edit touches **only** `Directory.Packages.props` — **no** file under `src/` or
  `tests/`, and **no** `Directory.Build.props` change in this repo (`2.4.0` carries no API change to
  consume; Platform's own package version is a release-train concern, not a cascade concern).

## 3. Out of scope — confirm, do not implement (`Verbara.Sdk/ADR-0040` D4)

- [ ] 3.1 Confirm `Microsoft.Extensions.TimeProvider.Testing` is **still `10.0.0`** at
  `Directory.Packages.props` line **140**. It is test-only and on a different servicing track; a
  blanket `Microsoft.Extensions.*` rewrite that sweeps it to `10.0.10` is a **defect**. This one
  does not necessarily fail the build — verify it by reading the line.
- [ ] 3.2 Confirm **no direct pin was added** for `OpenTelemetry` or `NATS.Client.Core`. Platform
  pins neither today; both arrive purely transitively, so there is nothing to re-align and adding a
  pin would invert the floor relationship.
- [ ] 3.3 Confirm **no version-asserting script, test or CI step** was added. This repo has none by
  design; verification is the existing required gate set (§5).
- [ ] 3.4 Confirm this cascade is **not** bundled with a release/version cut
  (`Verbara.Sdk/ADR-0040` D3) — no `CHANGELOG.md` version heading is cut, no tag is pushed.

## 4. CHANGELOG (apply-stage)

- [ ] 4.1 Add a `[Unreleased]` entry to `CHANGELOG.md` under this repo's existing `### Dependencies`
  section shape (precedent: the `[2.17.0]` entry for the `2.3.0` cascade) recording: `Verbara.Sdk`
  **`2.3.2` → `2.4.0`**; `Verbara.Sdk.Pro` **`2.13.0-pro` → `2.13.1-pro`**; and
  `Microsoft.Extensions.*` **`10.0.9` → `10.0.10`** with the reason — clearing the `NU1605`
  downgrade error the raised floor would otherwise cause. State that `2.4.0` is a **dependency-bump
  release with no API change**, so a reader can tell "baseline moved" from "feature adopted".
- [ ] 4.2 Confirm the entry states the resulting `Sdk × Pro × Platform` triple — this is what
  discharges `Verbara.Sdk/ADR-0040` D6's compatibility-matrix obligation for the versions touched.
- [ ] 4.3 Cite `decision_ref: Verbara.Sdk/ADR-0040` and the PR number in the entry (bind the PR
  number from the `gh pr create` output — never predict it).

## 5. Verification (apply-stage)

- [ ] 5.1 `dotnet restore` clean — **zero** `NU1605` / `NU1109` package-downgrade errors. This is
  the gate that proves the D2 same-commit rule was honoured.
- [ ] 5.2 `dotnet build` green with **zero warnings** (`TreatWarningsAsErrors=true`,
  `WarningLevel=9999`).
- [ ] 5.3 `dotnet test` green with **zero warnings**.
- [ ] 5.4 The required **`AOT Publish (Api)`** job (`aot-probe`,
  `.github/workflows/ci.yml:453-508`) green — it publishes `src/Verbara.Platform.Api` with
  `PublishAot=true` and fails on **any** `warning ILxxxx`. This is the gate that covers the raised
  transitive closure, including the `NATS.Client.Core` 3.x major (to which Platform has **ZERO**
  source-level exposure — no `NATS` reference anywhere under `src/`; the realtime transport is
  SignalR + StackExchange.Redis).
- [ ] 5.5 `openspec validate --all --strict` green.
- [ ] 5.6 **CI green on the PR** (full Platform required gate set, including `AOT Publish (Api)`).
- [ ] 5.7 Record the rollback shape in the PR description: reverting the single
  `Directory.Packages.props` commit restores the `2.3.2` / `2.13.0-pro` / `10.0.9` baseline exactly,
  because Platform owns no source change in this cascade.
