## ADDED Requirements

### Requirement: Worker-loop tests are fast and deterministic without wall-clock races
Background-worker tests SHALL reach sub-second determinism without importing a timing race. A
test that must exercise a contract living in the worker's `ExecuteAsync` loop SHALL drive the
real loop at an options-overridable small *real* interval and complete on a causal signal (the
`ExecuteTask` fault), bounded by a guard-timeout backstop; a test whose contract is reachable
outside the loop SHALL call a direct single-cycle method instead. Worker-loop tests MUST NOT
drive a `BackgroundService` loop via `FakeTimeProvider.Advance` (it re-imports the
Advance-before-armed, cross-thread fault-observation, and dual-clock `CancellationTokenSource`
races the determinism program exists to remove) and MUST NOT gate on a fixed wall-clock
`Task.Delay`/`Thread.Sleep`.

#### Scenario: Outer-loop fault observed via a small real interval
- **GIVEN** `ConversationTimeoutWorker` with its startup delay and sweep interval set to a few milliseconds via options, and `IServiceHeartbeat.RecordTick` configured to throw
- **WHEN** the worker is started and the first real tick fires
- **THEN** the injected fault SHALL surface on `ExecuteTask` and be observed causally via the guard-timeout-bounded helper, with no `FakeTimeProvider.Advance` and no multi-second wall-clock wait

#### Scenario: Loop-reachable-only contract still drives the real loop
- **GIVEN** a resilience test asserting the worker's outer try/catch (a contract unreachable from the public single-cycle method)
- **WHEN** the test runs under the millisecond interval
- **THEN** it SHALL still execute the real `ExecuteAsync` (not a bypass), preserving the contract while running sub-second

### Requirement: Production worker intervals are options-overridable, defaulting to production values
A worker timing interval that a test needs to shrink SHALL be exposed as an option that defaults
to the current production value, so the seam is additive and production cadence is byte-identical.
No `TimeProvider` parameter or `IClock`-to-`TimeProvider` conversion SHALL be introduced for the
sole purpose of unit-test loop determinism; that abstraction change belongs to the ecosystem
`TimeProvider` ADR (deliverable C4) and only where a hosted/integration test genuinely needs a
fake clock.

#### Scenario: Default interval preserves production behaviour
- **GIVEN** `QueueDistributionWorker`'s hardcoded startup delay made options-overridable
- **WHEN** no override is supplied (production)
- **THEN** the effective delay SHALL equal the prior hardcoded value, asserted by a test, so production behaviour is unchanged

### Requirement: A slow test of a non-loop contract is rewritten, and an unreachable contract is recorded
A slow test that asserts a contract reachable without the loop SHALL be rewritten to a direct
single-cycle call rather than sped up in place. Where a worker's outer-loop contract is
structurally unreachable from unit scope, that gap MUST be recorded explicitly rather than
silently dropped.

#### Scenario: Mislabeled resilience test rewritten to a direct call
- **GIVEN** the `WebhookDeliveryService` tests that wait ~30s on the poll loop only to assert `fault == null` (an inner-catch-recoverable contract)
- **WHEN** an internal single-cycle method (`ProcessPendingRetriesOnceAsync`) is extracted and the tests call it directly
- **THEN** they SHALL assert the same inner-catch-swallow + loop-continue contract in sub-second time, and the structurally-unreachable outer-rethrow gap SHALL be recorded in the change
