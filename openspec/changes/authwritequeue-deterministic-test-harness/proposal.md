---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

PR #79 made the `AuthWriteQueue` start/drain barrier causal (`CompleteWriter` +
`ExecuteTask`) instead of a wall-clock `Task.Delay` guess, eliminating the shutdown-drain
flake under CI load. A **solution-wide audit** (all 33 test projects + a cross-repo scan),
run to pressure-test the original 5-site framing, re-scoped the flake class:

- The true **in-CI synchronization-fence surface is ~29 sites across 7 classes**, not the 5
  first catalogued in `Api.Tests`. The largest cluster lives in
  `tests/Verbara.Platform.Realtime.Tests/`: **18 fire-and-forget races** in
  `PushToHubRelayTests` (the SUT does `_ = SendConversationAsync(...)` at
  `PushToHubRelay.cs:156/186/213` *before* `RecordOutcome` writes the sink the test asserts
  on) **+ 6 spurious delays** in `RemoteEventDispatcherTests`. Decisively, **Realtime.Tests
  runs in CI** — `ci.yml:83/125` excludes only `Storage.Postgres.Tests` and
  `Identity.Redis.Tests` — so these 24 sites are live CI flake risk.
- **Root cause:** Platform forked a `GetUtcNow`-only `IClock` while `Verbara.Sdk` and
  `Verbara.Sdk.Pro` already converged on `System.TimeProvider` with `CreateTimer`-aware
  fakes (Sdk.Pro ships `Microsoft.Extensions.TimeProvider.Testing 10.0.0`; Platform does
  not). Several existing Platform test clocks override `GetUtcNow` only — never
  `CreateTimer` — so advancing them never fires a `PeriodicTimer` (a latent flake).

This change (**C1**) is the first of a 4-deliverable program (see *Program context*). It
eradicates the ~29 in-CI fences and adopts the ecosystem-standard fake time source —
**converging, not deepening the fork**.

## What Changes

Grouped **by seam class** (not by test project), because the fences are heterogeneous:

**Clock / TTL seams — adopt `FakeTimeProvider`; do NOT introduce a `MutableClock:IClock`:**

- Add `Microsoft.Extensions.TimeProvider.Testing` to the test dependency set (already
  de-risked in Sdk.Pro; test-only, reflection-free, never ships in a Native AOT host image).
- `ServiceHeartbeat` (`BackgroundServiceHealthCheckTests`, 2 sites): additive `internal
  RecordTickAt(serviceName, interval, at)` — force staleness deterministically, **no clock
  injection** (smallest seam; precedent `ResilienceStateObserver.SetForTest`).
- `InMemoryJtiRevocationCache` (1 site): seam with `TimeProvider clock = TimeProvider.System`
  (read `GetUtcNow()`); parameterless ctor chains to `TimeProvider.System` so the
  `Program.cs:647` DI registration is untouched. Test advances `FakeTimeProvider` past the TTL.
- `CachedTenantAuthConfigStore` (1 site, **test-only**): drive `MemoryCacheOptions.Clock`
  via a `FakeTimeProvider`-backed legacy `Microsoft.Extensions.Internal.ISystemClock`
  adapter; no production change (the test owns the `MemoryCache`).

**Completion seams — fire-and-forget → awaitable (benign-additive; precedent
`AuthWriteQueue.CompleteWriter` / `VoiceConversationBridge`):**

- `WebhookDispatcher` (3 sites): `async void OnEvent` → record an `internal Task`; expose
  `internal WaitForDispatchAsync`. Keep the whole-body `try/catch → LogDispatchError` so the
  recorded Task never faults unobserved. The negative `DidNotReceive` site is gated on
  completion, never on a channel drain.
- `RealtimeStateBridge` (1 helper × 11 tests): `async void OnEvent` → `void OnEvent => _ =
  HandleEventAsync(evt)` + `internal Task HandleEventAsync`. Keep one `Publish`/`OnNext`
  wiring test for Rx-path coverage.
- `PushToHubRelay` (**HOT PATH**, 18 `PushToHubRelayTests` sites): the 5 `Forward*` handlers
  (3 Pro-typed + 2 Core-typed) fire-and-forget `_ = SendXAsync(...)`. Add a **send-completion
  seam** — record the most-recently-started send in a single field and expose
  `internal WaitForDispatchAsync` — so tests await completion deterministically (single-field
  is sufficient: the bus `OnNext` is synchronous and one event triggers one send; tests await
  per emit). **Behaviour-preserving** — production still fires-and-forgets; only a field
  reference is kept (no list/lock/counter on the hot path).
- `RemoteEventDispatcher` (6 sites): the work already completes synchronously
  (`pending.AsTask().GetAwaiter().GetResult()` at `:163`, inside the synchronous `OnNext`)
  → **delete the 6 spurious delays outright** (no seam needed; the cheapest possible win).

**Keep-as-is (recorded non-goals + allow-listed by C2):** `WorkerResilienceTestHelpers.cs:29`
(guard — add a 1-line clarifying comment); `PostgresHealthCheckTests:40` (simulated-work);
`Identity.Redis.Tests` TTL waits (real-Redis, CI-excluded); Flows `SlowHttpMessageHandler`;
E2E `SettleDelay`; LoadTests loops; `RelayOutcomeSink` `Task.Yield`.

**Stress validation:** the in-CI suite (Api.Tests + Realtime.Tests) is run 10× under CPU
contention; zero flake across migrated tests before the change is complete.

## Capabilities

### New Capabilities

<!-- No new product/spec-level capabilities. Test determinism only; every shipping behaviour
     is byte-identical (production still uses real time and fire-and-forget dispatch). -->

### Modified Capabilities

<!-- No product/spec requirement changes. -->

## Impact

- **Affected (production — additive, behaviour-preserving seams):**
  `src/Verbara.Platform.Api/Services/WebhookDispatcher.cs`, `RealtimeStateBridge.cs`,
  `ServiceHeartbeat`; `src/Verbara.Platform.Identity/Auth/InMemoryJtiRevocationCache.cs`;
  `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs` (the one MEDIUM-risk hot-path
  seam — note Realtime is a non-AOT microservice per ADR-0023, so it does not touch the Api
  AOT gate).
- **Affected (tests):** `Api.Tests` (5 clusters), `Realtime.Tests`
  (`PushToHubRelayTests` ×18, `RemoteEventDispatcherTests` ×6).
- **Test dependency:** `+ Microsoft.Extensions.TimeProvider.Testing` (Api.Tests).
- **Scope-declaration correction:** this change is **NOT test-only** (the prior declaration
  was wrong). It adds production seams — all additive, visibility-elevating, behaviour-
  preserving, each with an in-tree precedent.
- **Cross-repo:** none in code (the `verbara-meta` TimeProvider ADR is deliverable **C4**).

## Program context (C1 of 4)

| # | Change | Tier | Scope |
|---|--------|------|-------|
| **C1** | `authwritequeue-deterministic-test-harness` (this) | MEDIANO | ~29 in-CI fences (clock + completion) + adopt `FakeTimeProvider` |
| **C2** | `sync-fence-regression-guard` | PEQUEÑO | CI grep-gate preventing new `Task.Delay`/`Thread.Sleep` sync-fences in tests; allow-list authored against the post-C1 census. **Depends on C1.** |
| **C3** | `worker-loop-timeprovider-determinism` | MEDIANO | per-worker `TimeProvider` triage to kill the 45–60s slow-test debt (slowness, not flakiness); **skip `VerbaraCapacitySyncService`** (`Task.Delay(Infinite)`). Depends on C1 (reuses `FakeTimeProvider`). |
| **C4** | `verbara-meta` ADR (docs) | — | codify `System.TimeProvider` as the ecosystem time-seam standard; document `IClock` retirement + the SDK/Pro clones. Dependency-free. |

**Explicitly OUT (YAGNI / structural / evidence-refuted):** a shared `TestKit` project
(136 `InternalsVisibleTo` grants in `src/` → a cross-project kit sees zero SUT internals, so
it cannot carry the per-SUT seams); a `PlatformEventBus` awaitable-dispatch rewrite (can't
span the SDK-owned `RxPushEventBus` leg; couples 35 publishers; zero production benefit); SDK
/ Pro / Web fence migration (no flake class there — Web has 1 benign timeout fallback); a new
`MutableClock:IClock` (would entrench the abandoned fork); ripping out the ~60 `IClock`
consumers (coexist; convert only at fence/timer sites).
