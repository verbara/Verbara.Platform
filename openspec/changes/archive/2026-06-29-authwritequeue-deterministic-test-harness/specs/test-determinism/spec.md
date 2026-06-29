## ADDED Requirements

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
