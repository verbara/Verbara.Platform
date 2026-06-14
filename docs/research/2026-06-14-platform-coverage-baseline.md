# Platform coverage baseline (coverage-ratchet pilot)

**Date:** 2026-06-14
**Context:** P2 SP3 — coverage ratchet pilot. First measured baseline that sets `coverage-floor.json`.
**Spec:** `docs/specs/2026-06-14-coverage-ratchet-pilot.md`.

## Result

| Metric | Value | Note |
|--------|-------|------|
| **Line coverage** | **77.37%** | the gated metric (8111 / 10484 coverable lines) |
| Branch coverage | 63.92% | advisory only (2342 / 3664) |
| Method coverage | 79.8% | advisory (1369 / 1715) |
| Assemblies | 29 | production `src/` assemblies the unit job exercises |
| **Floor set** | **75** | `⌊77.37⌋ − 2` → 2-point slack so the first CI run is comfortably green |

All tests green (Failed: 0 across every project). Subset = CI's unit filter
`FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests`.

## Refinement vs the spec (discovered during this baseline)

The spec's first cut measured all of `src/` and produced **64.81%** — but two src
assemblies came in at **0%** because their *only* tests are container-backed and thus
excluded from the CI unit subset:

- `Verbara.Platform.Storage.Postgres` (large — ~70 stores; tested by `Storage.Postgres.Tests` / Testcontainers)
- `Verbara.Platform.Identity.Redis` (tested by `Identity.Redis.Tests` / Testcontainers)

Counting them at 0% diluted the aggregate (64.81% vs 77.37%) and, worse, a static 0%
block of that size could **mask a real regression** in unit-tested code. Decision
(approved 2026-06-14): exclude those two assemblies in `coverlet.runsettings` so the
ratchet measures the code the unit job actually targets. Covered-line count is
unchanged (8111) — only the dead-weight denominator (12516 → 10484) was removed. Their
coverage will be tracked only if their container tests ever run in CI.

## How to reproduce locally

```bash
cd Verbara.Platform
dotnet build Verbara.Platform.slnx -c Release
rm -rf coverage
dotnet test Verbara.Platform.slnx --no-build -c Release \
  --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests" \
  --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory ./coverage
dotnet tool restore
dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" \
  -targetdir:"coverage/report" -reporttypes:"Cobertura;TextSummary"
python3 scripts/check-coverage-floor.py coverage/report/Cobertura.xml coverage-floor.json
```

## Raising the floor

When coverage improves, raise `coverage-floor.json` in a normal PR (manual ratchet,
mirrors Web). Keep ~2 points of slack below the then-current measured value.
