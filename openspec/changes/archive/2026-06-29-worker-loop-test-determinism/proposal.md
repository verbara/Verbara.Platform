---
tier: MEDIANO
owner: maintainer
approver: maintainer
stakeholder: Platform team
roadmap_ref: verbara-meta/docs/roadmap.md#typification-p2c
---

## Why

Program slot **C3** (after C1 #102 + C2 #104, both shipped). The slot was planned as
`worker-loop-timeprovider-determinism` — "give each periodic worker a `TimeProvider` seam and
advance a `FakeTimeProvider` in tests." A measured grounding + a 7-agent design pressure-test
(4 full-design generators, 2 judges, 1 completeness critic — run because the original options
varied only *worker count*, not *mechanism* or *time-abstraction coherence*) **re-scoped the
slot on three load-bearing, evidence-backed points:**

1. **The mechanism (`FakeTimeProvider.Advance` driving the worker loop) re-imports the exact
   timing-race anti-pattern C1/C2 were built to eradicate.** Verified by grep: **no test in the
   suite `StartAsync`-es a seamed worker or drives a `BackgroundService` loop via `Advance`.**
   The established house idiom (the 5 already-seamed workers) is `TimeProvider` as a *fixed*
   clock for `GetUtcNow()` **+ a direct public `SweepOnceAsync` call** — the `PeriodicTimer(interval,
   clock)` overload exists so a *hosted* worker under a fake clock doesn't burn wall-clock, **not**
   to drive ticks in a unit test. `Advance`-driving a loop introduces an *Advance-before-armed*
   race (the `PeriodicTimer` isn't constructed until the initial `Task.Delay` completes), a
   cross-thread fault-observation race, and a dual-clock hazard with the real
   `CancellationTokenSource(TimeSpan)` — precisely the flake class the program exists to remove.

2. **The single slowest test (WebhookDeliveryService, 35s) is mislabeled.** Both its "fatal"
   resilience tests assert `fault.Should().BeNull()` through an outer-rethrow path their own
   comments concede is "not trivially injectable through the public surface" — i.e. they pay 30s
   of real `Task.Delay` to prove an *inner-catch-recoverable* contract a direct call proves in
   ~0s. Seaming its timer would be theatre.

3. **The measured debt is small and concentrated, not a broad cohort.** Running the suite with
   trx: 372s / 3068 tests; the C3-addressable worker-loop debt is **~50–55s across 3 tests / 2–3
   workers** (`WebhookDeliveryService` 35s, `ConversationTimeoutWorker` ~10s ×2,
   `QueueDistributionWorker` ~3s warm-up). The earlier "138s / 16 tests" estimate was a
   theoretical timeout-max, not the trx. The **larger** slow category — ~75s of
   `WebApplicationFactory` integration spin-up — is out of C3's charter and is named here as the
   next, separately-scoped opportunity.

**Chosen mechanism: the house idiom, not `TimeProvider` in the loop** — options-overridable
intervals (small *real* ticks, exactly how `QueueDistributionWorker.PollIntervalMs` already runs
fast) + a direct single-cycle public method, each test completing on a *causal* signal (the
`ExecuteTask` fault) bounded by the existing `AwaitExecuteFaultAsync` GUARD-TIMEOUT. **No
`FakeTimeProvider` enters any worker loop; no `TimeProvider` param is added; no `IClock` is
converted.** This reclaims the same ~50–53s with the smallest, safest, most idiom-consistent
change and without re-importing the timing race.

## What Changes

**Three Api workers — production change is additive options + one method extraction, defaults
byte-identical to today (behaviour-preserving):**

- **`ConversationTimeoutWorker`** (`:57` `Task.Delay(5s)` + `:59` `new PeriodicTimer(5s)`):
  make the initial startup delay and the sweep interval **options-overridable** (default 5s/5s on
  the options class the worker already binds). The two genuine outer-fatal resilience tests
  (`heartbeat.RecordTick` throws *inside* the loop, *outside* the inner catch — only reachable
  via the real `ExecuteAsync`) keep driving the **real** loop, but at a ~ms interval, observing
  the fault causally via `AwaitExecuteFaultAsync`. ~20s → sub-second. **No `Advance`, no
  `TimeProvider`.**
- **`QueueDistributionWorker`** (`:60` hardcoded `Task.Delay(3s)` warm-up; period already
  `PollIntervalMs`): make the startup warm-up **options-overridable**. ~3s → ~0.
- **`WebhookDeliveryService`** (`PollPendingRetriesAsync` `:84`, poll `Task.Delay(30s)` `:92`):
  extract `internal Task ProcessPendingRetriesOnceAsync(CancellationToken)` (the per-iteration
  body, mirroring the existing `DeliverForTestAsync` `:301` idiom) and **rewrite** the two
  `fault==null` tests to call it directly — same inner-catch-swallow + loop-continue contract,
  instant. 35s → ~0. **No timer seam.**

**Time-abstraction coherence (Tension B) — sidestepped, not resolved:** because no
`FakeTimeProvider` enters any loop, no `TimeProvider` is added and no `IClock` is converted, so
no worker becomes the codebase's first dual-clock class. Honors C1's standing decision (`IClock`
and `TimeProvider` coexist; convert only at genuine fence/timer sites; never a `MutableClock`).

**Honest coverage note:** the WebhookDeliveryService outer-loop rethrow is structurally
unreachable from unit scope (per the existing tests' own comments). C3 records this gap
explicitly rather than silently dropping it.

## Capabilities

### New Capabilities

<!-- No new product capability. Extends the existing `test-determinism` capability with
     worker-loop test-determinism requirements. Production behaviour is byte-identical
     (default intervals unchanged; the extracted method is a refactor of existing loop body). -->

### Modified Capabilities

<!-- `test-determinism`: + worker-loop determinism via options-overridable intervals / direct
     single-cycle calls; an explicit ban on FakeTimeProvider-Advance loop-driving and on
     wall-clock sleeps in worker tests (ADDED requirements). -->

## Impact

- **Affected (production, additive + behaviour-preserving):**
  `src/Verbara.Platform.Api/Services/ConversationTimeoutWorker.cs` (+ its bound options class),
  `QueueDistributionWorker.cs` (+ startup-delay option), `WebhookDeliveryService.cs` (internal
  method extraction). Default intervals unchanged → production runs exactly as today.
- **Affected (tests):** `ConversationTimeoutWorkerResilienceTests`,
  `QueueDistributionWorkerResilienceTests`, `WebhookDeliveryServiceResilienceTests` (shrink
  intervals via options / rewrite to the direct call; trim the now-oversized
  `CancellationTokenSource(TimeSpan)` backstops).
- **No new package; no `TimeProvider`/`IClock` change; no AOT/serialization surface.** The C2
  fence-guard is unaffected (no new test `Task.Delay`/`Thread.Sleep`; the only timed wait remains
  the already-`fence-allow`'d GUARD-TIMEOUT in `WorkerResilienceTestHelpers`).
- **Cross-repo:** none (the ecosystem `TimeProvider` ADR is deliverable **C4**).

## Architectural Risk

- **Level:** LOW. Production edits are additive optional options (defaults = current hardcoded
  values) + a pure refactor extracting an existing loop body into an internal method. No new
  concurrency pattern — it reuses the proven `QueueDistributionWorker.PollIntervalMs` small-real-
  interval idiom + the causal `AwaitExecuteFaultAsync` wait already in the suite.
- **Affected if wrong:** a mis-set default could change production cadence — mitigated by keeping
  defaults byte-identical and asserting them in a test. A too-tight test interval could flake —
  mitigated because tests complete on the causal `ExecuteTask` fault (not a fixed sleep), with the
  GUARD-TIMEOUT as the hang backstop (the C1/C2-approved pattern).
- **Mitigation / explicitly rejected:** `FakeTimeProvider.Advance` loop-driving (re-imports the
  Advance-before-armed + cross-thread + dual-clock races); adding `TimeProvider` params or
  converting `IClock` (unverifiable churn + dual-clock smell); migrating the ~10 other unseamed
  periodic workers (no loop-driven test → no measured ROI → C4's job by policy).

## Program context (C3 of 4)

| # | Change | Tier | Status |
|---|--------|------|--------|
| **C1** | `authwritequeue-deterministic-test-harness` | MEDIANO | SHIPPED (#102, archived #103) |
| **C2** | `sync-fence-regression-guard` | PEQUEÑO | SHIPPED (#104, archived #105) |
| **C3** | `worker-loop-test-determinism` (this; planned as `-timeprovider-`, re-scoped after pressure-test) | MEDIANO | options-overridable intervals + single-cycle extraction; **no `TimeProvider` in loops**. Depends on C1/C2. |
| **C4** | `verbara-meta` ADR (docs) | — | codify `System.TimeProvider` as the forward standard for *new/hosted* workers (by policy, not by retrofitting the 10 untested existing ones); authorize seam-on-demand. |

**Deferred to C4 (named, so the audit isn't re-run):** ~10 unseamed periodic workers
(`TokenRefreshService`, `CampaignMetricsPoller`, `AuditRetentionService`, `RetentionPurgeService`,
`RealtimeReconciliationService`, `ReportSchedulerService`, `DunningService`,
`CreditLotExpiryReclaimWorker`, `CreditGrantMintWorker`, `OverageInvoiceIssuanceWorker`,
`TimerPollingService`) — each is either public-sweep-tested (already fast) or has no loop-driven
test, so a `TimeProvider` retrofit now is unverifiable churn; **seam on demand when next edited**.
**Permanently SKIP (recorded):** event/subscription-driven hosts, one-shot migrators, and
`VerbaraCapacitySyncService` (`Task.Delay(Timeout.Infinite)`, no periodic cycle).

**Explicitly OUT (evidence-refuted by the pressure-test):** `FakeTimeProvider`-driven worker
loops; `TimeProvider` params / `IClock`→`TimeProvider` conversion in C3; full convergence of all
13 unseamed workers; "fixing" the ~75s `WebApplicationFactory` integration spin-up here (named as
the next separate opportunity).
