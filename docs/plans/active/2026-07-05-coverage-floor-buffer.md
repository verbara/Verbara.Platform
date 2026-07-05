# Plan — Coverage-floor buffer to unblock Dependabot #109

**Status:** active · **Opened:** 2026-07-05 · **Branch:** `test/coverage-floor-buffer`

## Problem

Dependabot PR #109 (test-stack: `coverlet.collector` + FluentAssertions + `Microsoft.NET.Test.Sdk`)
is blocked by the **Coverage Ratchet** required check: the new `coverlet.collector` measures line
coverage at **74.36%**, below the **75%** floor (`coverage-floor.json`). The bump itself compiles and
passes "Build + Unit Tests" — the ~0.64pp drop is a *measurement* change in the coverage tool, not a
real loss of tested behavior. `main` sits almost exactly on the floor, so any measurement shift trips it.

**Decision (user):** keep the floor at 75% and **add tests** to regain headroom (rejected: lowering the
floor; retaining the old coverlet).

## Approach — tests-only branch (Approach A)

A `test/coverage-floor-buffer` branch that adds **only tests** (no dependency/production changes). Raising
`main` to ~76% (old coverlet) means #109, on rebase, measures ~75.4% (new coverlet) and passes. Avoids
adopting the FluentAssertions bump (and any FA8 breaking-change risk) in this branch.

Need: **+180 lines** to reach 75.00%, **+321** for a 75.5% buffer. Target **~400+** covered lines.

## Targets (from coverage-report ROI analysis, ~545 est. lines)

| Class (assembly) | Test file | Type | Est. |
|------------------|-----------|------|------|
| `WebhookSubscriptionEndpoints` (Api) | new `WebhookSubscriptionEndpointsTests.cs` | integration (`UnifiedPlatformApiFactory`) | ~140 |
| `ScheduledReportEndpoints` (Api) | new `ScheduledReportEndpointsTests.cs` | integration | ~125 |
| `DncListEndpoints` (Api) | new `DncListEndpointsTests.cs` | integration | ~110 |
| `SurveyEndpoints` (Api) | new `SurveyEndpointsTests.cs` | integration | ~95 |
| `DefaultTypificationValidator` (Typification) | extend `DefaultTypificationValidatorTests.cs` | unit (pure) | ~75 |

Secondary (if short): `HolidayCalendarEndpoints`, `CallerIdPoolEndpoints`.

## Execution (subagent-driven, FCM)

- **Phase B (parallel write):** one subagent per target writes its test file following the harness idiom
  (`IClassFixture<UnifiedPlatformApiFactory>`, `CreateAuthenticatedClient()`, `JsonContent.Create`,
  status-code + `JsonNode` assertions), naming `Method_ShouldExpected_WhenCondition`, FA 7.1.0. No
  `dotnet` runs in subagents (concurrent builds corrupt each other); no production-code changes.
- **Phase C (central verify):** build `Api.Tests` + `Typification.Tests`, run new tests, fix failures,
  then run the CI coverage pipeline locally (`coverlet.runsettings` → ReportGenerator →
  `scripts/check-coverage-floor.py`) to confirm line-rate clears the floor with margin.

## Close-out

Commit → PR → merge (its own Coverage Ratchet passes comfortably) → `@dependabot rebase` #109 → its
armed auto-merge fires when green; Dependabot deletes the #109 branch on merge. `git mv` this plan to
`docs/plans/completed/` on ship.
