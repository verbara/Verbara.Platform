# Coverage ratchet pilot (Verbara.Platform) — implementation plan

> **For agentic workers:** implement task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add a CI-blocking line-coverage ratchet to Verbara.Platform, mirroring Web's pattern, with the floor in a committed file raised manually.

**Architecture:** A new independent `coverage` job in `ci.yml` runs the same unit-test subset CI already runs, collects coverage via `coverlet.collector` + a `coverlet.runsettings`, merges per-project Cobertura with ReportGenerator, and fails if the merged line-rate is below `coverage-floor.json`. Branch coverage is advisory.

**Tech Stack:** .NET 10, `coverlet.collector` 6.0.4 (already present), `dotnet-reportgenerator-globaltool` (pinned local tool), python3 (stdlib, on ubuntu runners), GitHub Actions.

**Spec:** `docs/specs/2026-06-14-coverage-ratchet-pilot.md`.

**Branch:** `feat/coverage-ratchet-platform` (branch from `origin/main`, not local — P0 lesson).

---

### Task 1: Pin ReportGenerator as a local tool

**Files:**
- Create or modify: `Verbara.Platform/.config/dotnet-tools.json`

- [ ] **Step 1:** Check whether `.config/dotnet-tools.json` exists. If not: `dotnet new tool-manifest`. Then `dotnet tool install dotnet-reportgenerator-globaltool` (pins the current latest into the manifest). If it already lists reportgenerator, leave the version as-is.
- [ ] **Step 2:** Verify: `dotnet tool restore` then `dotnet reportgenerator --help | head -1` prints the version. Expected: a ReportGenerator banner, no error.
- [ ] **Step 3:** Commit. `git add .config/dotnet-tools.json && git commit -m "ci(platform): pin reportgenerator local tool for coverage"`

---

### Task 2: Coverage config files (runsettings, floor placeholder, compare script)

**Files:**
- Create: `Verbara.Platform/coverlet.runsettings`
- Create: `Verbara.Platform/coverage-floor.json`
- Create: `Verbara.Platform/scripts/check-coverage-floor.py`

- [ ] **Step 1:** Create `coverlet.runsettings` verbatim from spec §1 (cobertura format; `ExcludeByAttribute` GeneratedCode/CompilerGenerated/ExcludeFromCodeCoverage; `Exclude` `[*.Tests]*,[*.Tests.*]*`; `ExcludeByFile` `**/Migrations/*.cs`; `SkipAutoProps` true).
- [ ] **Step 2:** Create `coverage-floor.json` with `{ "line": 0.0 }` (placeholder; real value in Task 3).
- [ ] **Step 3:** Create `scripts/check-coverage-floor.py` verbatim from spec §4. `chmod +x scripts/check-coverage-floor.py`.
- [ ] **Step 4:** Smoke-test the script against a tiny hand-written cobertura stub with `line-rate="0.5"` and a floor of `40` → exits 0, prints "Coverage floor OK."; with floor `60` → exits 1. Delete the stub.
- [ ] **Step 5:** Commit. `git add coverlet.runsettings coverage-floor.json scripts/check-coverage-floor.py && git commit -m "ci(platform): add coverlet runsettings + coverage floor compare script"`

---

### Task 3: Measure baseline locally + set the floor

**Files:**
- Modify: `Verbara.Platform/coverage-floor.json`
- Create: `Verbara.Platform/docs/research/2026-06-14-platform-coverage-baseline.md`

- [ ] **Step 1:** Ensure the local Pro feed is restorable: `cd Verbara.Platform && dotnet restore`. If it fails on `Verbara.Sdk.Pro.*`, (re)pack per the cross-repo workflow (`dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/` in Sdk/Pro, clear cache, restore).
- [ ] **Step 2:** Build: `dotnet build Verbara.Platform.slnx -c Release`.
- [ ] **Step 3:** Collect coverage on the CI subset:
  ```bash
  rm -rf coverage && dotnet test Verbara.Platform.slnx --no-build -c Release \
    --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests" \
    --collect:"XPlat Code Coverage" --settings coverlet.runsettings \
    --results-directory ./coverage
  ```
- [ ] **Step 4:** Merge: `dotnet tool restore && dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:"coverage/report" -reporttypes:"Cobertura;TextSummary"`. Read `coverage/report/Cobertura.xml` root `line-rate` (and `branch-rate`).
- [ ] **Step 5:** Compute floor = `floor(line_pct) - 2`. Write `coverage-floor.json` → `{ "line": <floor> }`.
- [ ] **Step 6:** Sanity-run the gate: `python3 scripts/check-coverage-floor.py coverage/report/Cobertura.xml coverage-floor.json` → exits 0, prints line% (floor X%) and branch% advisory.
- [ ] **Step 7:** Record the measured baseline (line%, branch%, project count, date, the exclude set) in `docs/research/2026-06-14-platform-coverage-baseline.md`.
- [ ] **Step 8:** Commit. `git add coverage-floor.json docs/research/2026-06-14-platform-coverage-baseline.md && git commit -m "ci(platform): set coverage floor from measured baseline"`. (Do NOT commit the `coverage/` dir — ensure it is gitignored.)

---

### Task 4: Wire the `coverage` job into ci.yml

**Files:**
- Modify: `Verbara.Platform/.github/workflows/ci.yml`

- [ ] **Step 1:** Read the existing `ci.yml`. Note the exact pinned SHAs for `actions/checkout`, `actions/setup-dotnet`, `actions/upload-artifact`, and copy the **exact** private-feed auth step (the `dotnet nuget update source github … PACKAGES_PAT …` block) and dotnet-version used by the build/test jobs.
- [ ] **Step 2:** Add a new `coverage` job (spec §3) reusing those exact SHAs + feed-auth step + the verbatim test filter. Steps: checkout → setup-dotnet → feed auth → `dotnet build -c Release` → test with `--collect`/`--settings`/`--results-directory` → `dotnet tool restore` + reportgenerator merge → `python3 scripts/check-coverage-floor.py` → `upload-artifact` (with `if: always()`).
- [ ] **Step 3:** Lint the YAML locally if a linter is available; otherwise eyeball indentation against the existing jobs.
- [ ] **Step 4:** Commit. `git add .github/workflows/ci.yml && git commit -m "ci(platform): add blocking coverage ratchet job"`

---

### Task 5: Verify the gate bites (locally)

- [ ] **Step 1:** With the committed floor → `python3 scripts/check-coverage-floor.py coverage/report/Cobertura.xml coverage-floor.json` exits 0.
- [ ] **Step 2:** Temporarily edit `coverage-floor.json` to `baseline + 5` → re-run → exits 1 with the `::error::` message (proves the ratchet bites).
- [ ] **Step 3:** Restore `coverage-floor.json` to the committed floor. Confirm exits 0. (No commit — verification only.)
- [ ] **Step 4:** Confirm the merged report excludes the 2 container-backed projects and all `*.Tests`/generated assemblies (inspect `coverage/report/Cobertura.xml` package list).

---

### Task 6: PR + green CI

- [ ] **Step 1:** Push the branch; open a PR titled `ci(platform): add coverage ratchet (pilot)`. PR body: the measured baseline + floor, link to the spec, note that the `coverage` check must be added to branch protection (manual).
- [ ] **Step 2:** Wait for CI. The new `coverage` job must be green (line ≥ floor). If red on a tooling issue (reportgenerator path, python3 absent, artifact glob), fix forward.
- [ ] **Step 3:** On merge: tell the user to add `coverage` to branch-protection required checks. Move this plan to `docs/plans/completed/`. Mark the pilot done; Sdk + Pro replication is the next sub-task.

---

## Self-review notes

- **Filter parity:** the coverage job MUST use the identical filter to the unit-tests job, or the floor measures a different population than CI runs. Reused verbatim.
- **`--no-build` ordering:** build once, then `dotnet test --no-build` with collect — avoids a rebuild and matches the unit job.
- **Floor file not the runsettings:** coverlet.collector does not enforce thresholds; enforcement is the python compare step. Keep them separate.
- **gitignore:** ensure `coverage/` is ignored so reports never get committed.
