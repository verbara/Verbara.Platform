# Coverage ratchet — pilot (Verbara.Platform) — design

**Status:** Approved design — 2026-06-14
**Origin:** P2 sub-project 3 of the methodology audit (`verbara-meta`). Pilot on Platform; replicate to Sdk + Pro after green.

## Problem

The audit flagged a coverage asymmetry: **Web has a CI-blocking coverage ratchet** (`vitest.config.ts` thresholds `{lines:29, functions:31, branches:16, statements:27}` + a blocking `coverage` job that uploads an artifact, floor raised "progressively"), but the **3 .NET repos have none**. All three already carry `coverlet.collector` 6.0.4 in every unit-test project, yet **CI never collects coverage** — no threshold, no baseline, no upload. A refactor can silently delete tested behavior and nothing turns CI red.

This pilot closes that gap on **Verbara.Platform** (the most critical repo), establishing a .NET pattern that mirrors Web's, then gets replicated verbatim to Sdk + Pro.

## Goals / success criteria

- A new CI **`coverage` job** (blocking) collects line coverage over the **same unit-test subset CI already runs** (container-backed projects stay excluded) and **fails the build if line coverage drops below a committed floor**.
- The floor is a **committed file** raised manually (mirrors Web — no CI write-back to the repo).
- The merged HTML/Cobertura report is uploaded as a CI artifact (mirrors Web).
- Line coverage is the **only blocking metric**; branch/method are reported (TextSummary) but do not fail the build.
- Zero new flakiness: floor set at the measured baseline **rounded down with slack** so the first green is stable.

## Non-goals (deliberately excluded)

- **Auto-ratchet / CI write-back** — rejected by decision (2026-06-14): manual floor like Web; no bot commits, no write permission.
- **Branch/method as blocking gates** — reported only; can be promoted later (Web started low and raises).
- **coverlet v6→v10 bump** — out of scope; v6.0.4 collects fine. Tracked separately in Sdk's `docs/plans/active/2026-05-03-coverlet-v10-bump…`. Do NOT couple.
- **Codecov/Coveralls** — out of scope; artifact upload only (as Web does).
- **Coverage on container-backed tests** — excluded (CI doesn't run them; coverage measures only the unit subset).
- **Sdk + Pro implementation** — separate follow-up after this pilot is green (same pattern).

## Design

### 1. `coverlet.runsettings` (repo root: `Verbara.Platform/coverlet.runsettings`)

Measures only the production code the **CI unit job actually exercises**. Excludes test
assemblies, generated code (System.Text.Json source-gen carries `[GeneratedCode]`), SQL
migrations, **and any `src/` assembly whose only tests are container-backed** (so excluded
from the unit subset — counting them at 0% dilutes the number and masks regressions). On
Platform those are `Verbara.Platform.Storage.Postgres` + `Verbara.Platform.Identity.Redis`
(see `docs/research/2026-06-14-platform-coverage-baseline.md`). When replicating, each repo
substitutes its own such assemblies (Sdk/Pro have their own Postgres/Redis storage assemblies).

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <ExcludeByAttribute>GeneratedCodeAttribute,CompilerGeneratedAttribute,ExcludeFromCodeCoverageAttribute</ExcludeByAttribute>
          <Exclude>[*.Tests]*,[*.Tests.*]*,[Verbara.Platform.Storage.Postgres]*,[Verbara.Platform.Identity.Redis]*</Exclude>
          <ExcludeByFile>**/Migrations/*.cs</ExcludeByFile>
          <SkipAutoProps>true</SkipAutoProps>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

### 2. Floor file (repo root: `Verbara.Platform/coverage-floor.json`)

```json
{ "line": 0.0 }
```

A single committed value (percent, 0–100). Raised manually in a normal PR when coverage improves. `0.0` is a placeholder — the real value is set during implementation from the baseline run (see §5).

### 3. CI `coverage` job (`.github/workflows/ci.yml`)

A new job mirroring Web's: independent (own checkout / setup-dotnet / private-feed auth / build), runs the **same filter** as the unit-tests job plus coverage collection, merges with ReportGenerator, compares line-rate to the floor, uploads the report.

```yaml
  coverage:
    name: coverage
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@<pinned-sha>
      - uses: actions/setup-dotnet@<pinned-sha>
        with: { dotnet-version: '10.0.x' }
      # (reuse the SAME private Pro feed auth step the build/test jobs use:
      #  dotnet nuget update source github --username verbara
      #    --password "${{ secrets.PACKAGES_PAT }}" --store-password-in-clear-text)
      - name: Restore + build
        run: dotnet build Verbara.Platform.slnx -c Release
      - name: Test with coverage (same subset CI runs)
        run: >-
          dotnet test Verbara.Platform.slnx --no-build -c Release
          --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"
          --collect:"XPlat Code Coverage" --settings coverlet.runsettings
          --results-directory ${{ github.workspace }}/coverage
      - name: Merge report
        run: |
          dotnet tool restore
          dotnet reportgenerator \
            -reports:"coverage/**/coverage.cobertura.xml" \
            -targetdir:"coverage/report" \
            -reporttypes:"Cobertura;TextSummary;HtmlInline_AzurePipelines"
      - name: Enforce line-coverage floor (ratchet)
        run: python3 scripts/check-coverage-floor.py coverage/report/Cobertura.xml coverage-floor.json
      - name: Upload coverage report
        if: ${{ always() }}
        uses: actions/upload-artifact@<pinned-sha>
        with:
          name: coverage-${{ github.sha }}
          path: coverage/report
```

The job is added to branch-protection required checks (manual GitHub setting, noted to the user — same as the P0 gates).

### 4. Floor-compare script (`Verbara.Platform/scripts/check-coverage-floor.py`)

Pure-stdlib Python (ubuntu runners ship python3). Reads the **merged** Cobertura `line-rate` (0–1), compares to the floor (percent), prints both, exits non-zero if below. Branch-rate printed as advisory only.

```python
#!/usr/bin/env python3
import json, sys, xml.etree.ElementTree as ET

cobertura, floor_file = sys.argv[1], sys.argv[2]
root = ET.parse(cobertura).getroot()
line_pct = round(float(root.get("line-rate")) * 100, 2)
branch_pct = round(float(root.get("branch-rate")) * 100, 2)
floor = float(json.load(open(floor_file))["line"])

print(f"Line coverage:   {line_pct}%  (floor {floor}%)")
print(f"Branch coverage: {branch_pct}%  (advisory, non-blocking)")
if line_pct < floor:
    print(f"::error::Line coverage {line_pct}% is below the ratchet floor {floor}%.")
    sys.exit(1)
print("Coverage floor OK.")
```

### 5. Baseline + floor-setting (implementation step, not a code constant)

During implementation: run the coverage job's command locally once (private feed available via `local-nuget-feed`), read the real merged line-rate, and set `coverage-floor.json` to **floor = floor(baseline) − 2** (integer percent, 2-point slack so the first CI run is comfortably green and minor nondeterminism never flakes). Record the measured baseline in the PR description and in `docs/research/`.

### 6. ReportGenerator as a pinned local tool

Add `dotnet-reportgenerator-globaltool` to Platform's `.config/dotnet-tools.json` (create the manifest if absent), pinned. `dotnet tool restore` in the job. (Sdk already has it as a local tool — consistent.)

## Key files

- New: `Verbara.Platform/coverlet.runsettings`
- New: `Verbara.Platform/coverage-floor.json`
- New: `Verbara.Platform/scripts/check-coverage-floor.py`
- New/modified: `Verbara.Platform/.config/dotnet-tools.json` (add reportgenerator)
- Modified: `Verbara.Platform/.github/workflows/ci.yml` (add the `coverage` job)
- The unit-test filter is reused verbatim: `FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests`

## Verification

- `coverage` job runs green with line coverage ≥ floor; artifact uploaded.
- Locally lower the floor file by hand → no effect on pass (still above); raise the floor above the measured baseline → job goes RED (proves the gate bites). Restore.
- Confirm the two container-backed projects are absent from the report (excluded by the filter).
- Confirm test/generated assemblies are absent from the report (runsettings excludes).

## Replication (separate follow-up, after pilot green)

Same 4 files + job in **Sdk** (filter `Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`) and **Pro** (filter `FullyQualifiedName!~Postgres&FullyQualifiedName!~IntegrationTests&FullyQualifiedName!~FunctionalTests`; CI authenticates nuget.org only). Each gets its own measured baseline/floor. Once all three are green, promote the pattern to a short `verbara-meta` ADR/runbook.
