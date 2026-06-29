# test-determinism Specification

## Purpose
TBD - created by archiving change authwritequeue-deterministic-test-harness. Update Purpose after archive.
## Requirements
### Requirement: Deterministic time source for clock/TTL test fences
Time-dependent test fences SHALL be driven from an advanceable fake time source
(`Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`), never from wall-clock
`Task.Delay`/`Thread.Sleep`. Production classes whose tests force staleness or TTL expiry
MUST expose either a `TimeProvider` seam (defaulting to `TimeProvider.System`) or an additive
deterministic setter; no migration SHALL introduce a `MutableClock:IClock` (the abandoned
Platform fork). The fake time source MUST be test-only and MUST NOT enter any shipped Native
AOT host image.

#### Scenario: TTL eviction advanced without wall-clock
- **GIVEN** `InMemoryJtiRevocationCache` seamed with `TimeProvider` and a revoked jti with a finite TTL
- **WHEN** the test advances `FakeTimeProvider` past the TTL and queries the cache
- **THEN** the entry SHALL be reported expired with no elapsed wall-clock time, and production behaviour with `TimeProvider.System` SHALL be byte-identical

#### Scenario: Heartbeat staleness forced deterministically
- **GIVEN** `ServiceHeartbeat` with an additive `RecordTickAt(serviceName, interval, at)` seam
- **WHEN** the test records a tick stamped far enough in the past
- **THEN** the health check SHALL report the service stale with no `Task.Delay`, and the production hot path (`RecordTick` using real time) SHALL be unchanged

#### Scenario: MemoryCache absolute expiry advanced via fake clock
- **GIVEN** `CachedTenantAuthConfigStoreTests` constructing its own `MemoryCache` with `MemoryCacheOptions.Clock` set to a `FakeTimeProvider`-backed `ISystemClock` adapter
- **WHEN** the test advances the fake clock past the entry TTL and reads again
- **THEN** the cache SHALL miss and fall through to the inner store deterministically, with no production change to `CachedTenantAuthConfigStore`

### Requirement: Causal completion signal for fire-and-forget dispatch tests
Tests asserting on a fire-and-forget async dispatch SHALL await a causal completion signal
exposed by the SUT, never a wall-clock delay. The dispatch may be an `async void` handler or
a `_ = SendXAsync(...)` continuation. The SUT seam MUST be additive and behaviour-preserving
— production continues to fire-and-forget — and MUST route handler faults so the recorded
completion Task never faults unobserved.

#### Scenario: Positive outcome observed before assertion
- **GIVEN** a dispatcher/relay whose handler performs async work then records an outcome
- **WHEN** the test awaits the SUT completion seam after publishing one event
- **THEN** the recorded outcome SHALL be observable before the assertion runs, with no reliance on elapsed time

#### Scenario: Negative assertion gated on completion, not drain
- **GIVEN** `WebhookDispatcherTests` asserting `SaveAsync.DidNotReceive()` on the no-match branch
- **WHEN** the test awaits the dispatch-completion seam (which completes after the early return)
- **THEN** the negative assertion SHALL be deterministic — a channel-drain gate is structurally invalid here because nothing is written

#### Scenario: Hot-path send completion
- **GIVEN** `PushToHubRelay` whose `Forward*` handlers fire-and-forget `_ = SendXAsync(...)` (recorded as the most-recently-started send)
- **WHEN** the test awaits the relay send-completion seam after each emit (the bus `OnNext` is synchronous and each event triggers exactly one send, so "most recent" equals "the one just started")
- **THEN** that send SHALL have completed (and `RecordOutcome` written) before the assertion, with production fire-and-forget semantics preserved

### Requirement: Spurious delays removed outright
Test delays that guard work which already completes synchronously SHALL be deleted rather
than migrated. (`RemoteEventDispatcher` re-publishes synchronously via
`pending.AsTask().GetAwaiter().GetResult()` inside the synchronous `OnNext`, so its 6
`Task.Delay` sites are no-ops.)

#### Scenario: Synchronous-completion delay deleted
- **GIVEN** a `RemoteEventDispatcherTests` site that delays after `bus.Emit(...)`
- **WHEN** the delay is removed
- **THEN** the test SHALL still pass deterministically, because the dispatch work completed synchronously during `Emit`

### Requirement: Stress stability of migrated tests
Migrated tests SHALL pass without flake under CPU contention. All in-CI tests that previously
used `Task.Delay`/`Thread.Sleep` as a synchronization fence MUST stay green when the in-CI
suite (Api.Tests + Realtime.Tests) is run 10 consecutive times under parallel load; this
SHALL be verified before the change is considered complete.

#### Scenario: Repeated suite run under contention
- **GIVEN** the in-CI suite with all fences replaced by causal signals (or deleted)
- **WHEN** `dotnet test` over the migrated projects is executed 10 times under maximum parallelism
- **THEN** zero failures or timeouts SHALL occur across all runs for the migrated tests

### Requirement: Non-goal delays recorded explicitly
Non-fence delays SHALL be left in place and recorded as deliberate non-goals. These include
guards, simulated-work, real-Redis TTL waits, settle delays, and cooperative yields; the C2
regression guard MUST allow-list them so a future audit does not mistakenly try to fake-clock
them.

#### Scenario: Real-Redis TTL wait preserved
- **GIVEN** `Identity.Redis.Tests` waiting on real Redis server-side TTL eviction (a clock the SUT does not own, CI-excluded)
- **WHEN** the migration runs
- **THEN** these sites SHALL be left unchanged and documented as non-goals, not converted to a fake clock

### Requirement: Source-level regression guard against new wall-clock test fences
The build SHALL fail when test source under `tests/` introduces a wall-clock synchronization
barrier without an explicit inline allow-marker. The banned set MUST be `Task.Delay`,
`Thread.Sleep`, `Thread.SpinWait`, `SpinWait.SpinUntil`, and a best-effort `Stopwatch`-elapsed
spin loop. The guard MUST match call-sites syntactically (parsing C# so that a comment, XML-doc,
or string-literal mention of a banned API is never a match), MUST scan every `tests/**/*.cs` by
directory walk from a `[CallerFilePath]` anchor — covering projects excluded from or absent from
the default CI test run (`Identity.Redis.Tests`, `LoadTests`, `E2E.Harness`) — excluding `obj/`,
`bin/`, `*.g.cs`, `*.Designer.cs`, `*.generated.cs`, and `<auto-generated>` files, and MUST
itself be a deterministic in-process test with no wall-clock delay and no subprocess.

#### Scenario: New unmarked fence fails the guard
- **GIVEN** a test file with an `await Task.Delay(...)` call carrying no `// fence-allow:` marker
- **WHEN** the guard runs
- **THEN** it SHALL report that file and line as a violation and the test SHALL fail

#### Scenario: Prose or string mention never trips
- **GIVEN** a comment, XML-doc (`<c>Task.Delay</c>`), or string literal that mentions a banned API
- **WHEN** the guard parses the file
- **THEN** no violation SHALL be reported, because the mention is not an `InvocationExpression`

#### Scenario: Coverage independent of CI execution
- **GIVEN** a banned call added to `LoadTests` or `E2E.Harness` (projects never built in the default CI suite)
- **WHEN** the guard runs as part of the standard `dotnet test`
- **THEN** the guard SHALL still detect it, because it walks source files rather than loaded assemblies

### Requirement: Inline allow-marker is the sole, structured exception mechanism
A legitimate wall-clock delay in test code SHALL be permitted only by an inline
`// fence-allow: CATEGORY — reason` marker on the call's line or the immediately-preceding
non-blank line, where `CATEGORY` is one of the closed enum `{SIMULATED-WORK, GUARD-TIMEOUT,
SETTLE, LOOP-DRIVER}` and `reason` is non-empty. A marker with an unknown category or an empty
reason MUST be rejected so that the site still fails. There MUST be no external allow-list file —
the marker is co-located with the code it excuses, so it is line-shift-proof and appears in the
PR diff for reviewer approval. The eleven census-identified legitimate sites SHALL each carry a
marker.

#### Scenario: Correctly marked site passes
- **GIVEN** `await Task.Delay(750); // fence-allow: SETTLE — wait for Redis TTL expiry`
- **WHEN** the guard runs
- **THEN** no violation SHALL be reported for that site

#### Scenario: Malformed marker is rejected
- **GIVEN** a banned call annotated `// fence-allow:` with no category/reason, or `// fence-allow: WHATEVER — x` with an out-of-enum category
- **WHEN** the guard runs
- **THEN** the site SHALL be reported as a violation despite the marker

### Requirement: Guard liveness and documented limitations
The guard SHALL self-verify its own liveness and record its accepted limits so it is never
silently inert and never oversold. It MUST assert that it scanned more than a sanity floor of
files (defeating the "found zero files → green" failure), that a planted unmarked-fence input
trips detection, that a prose mention does not, and that a well-formed marker passes. It MUST
also flag introduction of a threading alias or `using static System.Threading.Thread` in test
source as a reviewable proxy for the one unresolvable false-negative (aliased calls). The guard
MUST be documented as a syntactic backstop whose accepted false-negatives are aliased usings and
a call placed inside a string-interpolation hole, and whose primary durable defenses remain the
causal-completion seam, `FakeTimeProvider` adoption, reviewer enforcement, and the ecosystem
`TimeProvider` ADR (deliverable C4).

#### Scenario: Guard refuses to pass when it scanned nothing
- **GIVEN** a path-resolution regression that yields zero source files
- **WHEN** the guard runs
- **THEN** the liveness assertion SHALL fail rather than report a false green

#### Scenario: Self-test proves detection on a planted positive
- **GIVEN** an in-memory source string containing an unmarked `Thread.Sleep(100)`
- **WHEN** the detector parses it
- **THEN** exactly one violation SHALL be reported, demonstrating the detector is live

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

