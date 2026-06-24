## ADDED Requirements

### Requirement: Causal drain helper for BackgroundService tests
The test suite SHALL provide a reusable helper that drives any `BackgroundService` with a channel-writer drain lifecycle through start → enqueue → drain → stop deterministically. The helper MUST NOT use `Task.Delay` or `Thread.Sleep` as a synchronization fence; its only permitted wall-clock contact is a hard `WaitAsync(TimeSpan)` guard that protects against infinite hang, not against data races.

#### Scenario: All enqueued items processed before drain returns
- **GIVEN** a `BackgroundService` that reads from a `Channel<T>` and all items are enqueued before the drain call
- **WHEN** the test helper signals writer completion and awaits `ExecuteTask`
- **THEN** every enqueued item SHALL be processed before control returns to the test, with no reliance on elapsed wall-clock time

#### Scenario: Drain does not swallow service faults
- **GIVEN** a `BackgroundService` whose `ExecuteAsync` faults with a non-cancellation exception
- **WHEN** the drain helper awaits `ExecuteTask`
- **THEN** the exception SHALL propagate to the test so xUnit marks the test as failed, not as hung

#### Scenario: Wall-clock timing tests migrated to causal signals
- **GIVEN** an existing test that uses `await Task.Delay(N)` as a synchronization barrier for a worker or dispatcher
- **WHEN** the test is migrated to the deterministic helper
- **THEN** the test SHALL pass reliably under CPU contention without any sleep or delay, and the test wall-clock time SHALL not be bounded from below by the original delay value

### Requirement: Stress stability of migrated tests
After migration, all worker tests that previously used `Task.Delay` as a fence SHALL pass without flake when the full test suite is run 10 consecutive times under parallel CPU load. This SHALL be verified before the change is considered complete.

#### Scenario: Repeated suite run under contention
- **GIVEN** the test suite with all wall-clock fences replaced by causal drain signals
- **WHEN** `dotnet test` is executed 10 times consecutively with maximum parallelism
- **THEN** zero test failures or timeouts SHALL occur across all runs for the migrated tests

### Architectural Risk
- **Level:** LOW
- **Affected:** `tests/Verbara.Platform.Api.Tests/` only — no production code
- **Mitigation:** The helper is test-only; no production path changes. If `ExecuteTask` is `null` (service not started), the helper returns immediately with no assertion, which is a safe no-op. A hard `WaitAsync` timeout on the drain prevents infinite hangs from masked bugs.
