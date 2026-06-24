---
tier: PEQUEÑO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

PR #79 made the `AuthWriteQueue` start/drain barrier causal (`CompleteWriter` + `ExecuteTask`) instead of relying on a wall-clock `Task.Delay` guess, eliminating the shutdown-drain flake under CI load. The same `IHostedService`/`BackgroundService` lifecycle pattern — start → enqueue → drain → stop — recurs across worker tests in `tests/Verbara.Platform.Api.Tests/`, and several of those tests still use `Task.Delay` as a synchronization fence, leaving them susceptible to identical flakes under CPU contention.

## What Changes

- A reusable deterministic test helper (`BackgroundServiceTestHarness` or equivalent static helpers) that generalises the `CompleteWriter + ExecuteTask` pattern to any `BackgroundService` supporting a writer-complete/drain lifecycle.
- Migration of remaining wall-clock-dependent worker tests (`WebhookDispatcherTests`, `BackgroundServiceHealthCheckTests`, and any others identified during audit) onto the new causal helper.
- Stress validation: suite run repeatedly under synthetic CPU contention (e.g., parallel busy-work) to confirm zero flake across all migrated tests.

## Capabilities

### New Capabilities

<!-- No new product/spec-level capabilities are introduced. This change is implementation- and test-only. -->

### Modified Capabilities

<!-- No spec-level requirements change. -->

## Impact

- **Affected:** `tests/Verbara.Platform.Api.Tests/` — worker test helpers and individual test classes that use `Task.Delay` as a synchronization barrier.
- **Not affected:** all production source under `src/`; no API contracts, DTOs, endpoints, or database schemas change.
- **Cross-repo:** none — Platform only; no SDK or Platform.Web changes required.
