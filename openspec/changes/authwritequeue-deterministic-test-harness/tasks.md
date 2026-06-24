## 1. Audit and Design

- [ ] 1.1 Audit all `Task.Delay`-as-synchronization usages in `tests/Verbara.Platform.Api.Tests/` — catalogue each site (file, line, worker under test, why the delay exists)
- [ ] 1.2 For each site, determine the causal signal that replaces the delay (e.g. `ExecuteTask`, a `TaskCompletionSource`, a counting semaphore, or `Channel.Reader.Completion`)
- [ ] 1.3 Design the `BackgroundServiceTestHarness` API surface: static helpers vs. a small wrapper type; decide which patterns are general enough to centralise vs. kept inline per test

## 2. Build Deterministic Drain Helper

- [ ] 2.1 Implement the shared helper (generalising the `CompleteWriter + ExecuteTask.WaitAsync` pattern from `AuthWriteQueueTests.StartAndDrain`) in `tests/Verbara.Platform.Api.Tests/Workers/` or a new `Helpers/` folder
- [ ] 2.2 Ensure the helper is causal-only — no `Task.Delay`, no `Thread.Sleep`; a hard `TimeSpan` timeout via `WaitAsync` is the only wall-clock contact (guards against infinite hang, not a synchronization fence)
- [ ] 2.3 Verify the helper compiles with `TreatWarningsAsErrors=true, WarningLevel=9999` — zero warnings

## 3. Migrate Wall-Clock Worker Tests

- [ ] 3.1 Migrate `WebhookDispatcherTests` (3 `await Task.Delay(200)` sites) — replace with a causal completion signal on the dispatcher's internal async subscription
- [ ] 3.2 Migrate `BackgroundServiceHealthCheckTests` (2 `await Task.Delay(50)` sites for heartbeat-staleness forcing) — replace with an injectable `IClock` or a direct staleness setter so no real time passes
- [ ] 3.3 Migrate `RealtimeStateBridgeTests` (`await Task.Delay(delayMs)` for async-void `OnEvent`) — replace with an observable/task signal that fires after the handler completes
- [ ] 3.4 Review `Auth/InMemoryJtiRevocationCacheTests` (`await Task.Delay(250)` for TTL expiry) — replace with `IClock` injection or a fake time source so the TTL can be advanced programmatically
- [ ] 3.5 Confirm `WorkerResilienceTestHelpers.AwaitExecuteFaultAsync` timeout usage is a guard (not a fence) — document with a comment if already correct; tighten if it acts as a fence

## 4. Verification

- [ ] 4.1 Run `dotnet test tests/Verbara.Platform.Api.Tests/ -v q` — all tests green, zero warnings
- [ ] 4.2 Run the full suite 10× under synthetic CPU contention (`dotnet test` with `--parallel` across projects or a parallel stress wrapper) — confirm zero flake across migrated tests
- [ ] 4.3 Confirm CI green (PR gate: build + test + AOT diagnostics pass)
