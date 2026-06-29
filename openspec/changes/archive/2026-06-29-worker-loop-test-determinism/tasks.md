## 1. Foundation (Phase A — batch)

- [x] 1.1 BEFORE-baseline (trx) on the 3 resilience classes: Webhook `_ShouldLogCriticalAndRethrow` 35.0s; ConversationTimeout `_ShouldPropagate` + `_ShouldLogCriticalAndRethrow` 10.0s each; QueueDistribution `_ShouldPropagate` + `_ShouldLogCriticalAndRethrow` ~3.05s each (Webhook `_ShouldPropagate` already 0.004s)
- [x] 1.2 Confirmed both `ConversationTimeoutWorker` + `QueueDistributionWorker` bind `IOptions<DistributionOptions>` (PollIntervalMs already lives there, int-ms style)

## 2. Production seams (Phase B — focused, behaviour-preserving; defaults byte-identical)

- [x] 2.1 `ConversationTimeoutWorker`: `DistributionOptions.ConversationTimeoutStartupDelayMs` (5000) + `ConversationTimeoutSweepIntervalMs` (5000); read in `ExecuteAsync` (sweep read once into a local, also fed to `RecordTick` so heartbeat cadence mirrors). No `TimeProvider`/`IClock` change
- [x] 2.2 `QueueDistributionWorker`: `DistributionOptions.QueueDistributionStartupDelayMs` (3000) replaces the hardcoded `:60` warm-up; period stays `PollIntervalMs`
- [x] 2.3 `WebhookDeliveryService`: extracted `internal Task ProcessPendingRetriesOnceAsync(CancellationToken)` (one poll iteration, inner catch verbatim); `PollPendingRetriesAsync` loops `Task.Delay(30s)` + it. Mirrors `DeliverForTestAsync` internal idiom. No timer seam
- [x] 2.4 Build 0 warnings; `DistributionOptionsDefaultsTests` locks 5000/5000/3000 == prior `FromSeconds(5/5/3)`. (Options are code-overridable only — deliberately NOT config-bound, so an operator cannot accidentally change production cadence; documented via XML-doc)

## 3. Test migration (Phase C — batch)

- [x] 3.1 `ConversationTimeoutWorkerResilienceTests`: 5ms startup/sweep via `FastLoopOptions`; still drives the real `ExecuteAsync`; causal fault via `AwaitExecuteFaultAsync`; backstop 15s→2s. 10s+10s → 0.018s each. NO `Advance`
- [x] 3.2 `QueueDistributionWorkerResilienceTests`: `QueueDistributionStartupDelayMs=0` (+ existing `PollIntervalMs=50`); backstop 10s→2s. ~3s → 0.05s each
- [x] 3.3 `WebhookDeliveryServiceResilienceTests`: rewrote the 35s `_ShouldLogCriticalAndRethrow` → `ProcessPendingRetriesOnceAsync_ShouldSwallowAndLog_WhenNonCancellationThrows` (direct call, same inner-catch contract). 35s → 0.002s. Outer-rethrow gap recorded (comment + OpenSpec). `_ShouldPropagate` left unchanged (already fast — its 2s CTS cancels the first delay)
- [x] 3.4 Verified: 3 classes 12/12 sub-second (~61s → <0.15s); build 0 warnings; C2 fence-guard 19/19; Workers area 23/23

## 4. Ship

- [x] 4.1 `openspec validate worker-loop-test-determinism --strict` passes
- [x] 4.2 Two-stage review = SHIP (no blockers): reviewer ran 20/20 under full CPU saturation (no flake), confirmed causal fault (timeout → loud FAIL not false-pass), behaviour-preserving Webhook extraction, defaults locked, recorded gap honest
- [x] 4.3 PR #106 → CI green (Build+Unit Tests, Analyze, Dependency Review, Coverage Ratchet, CodeQL all SUCCESS) → enqueued (pos.1) → merged to main as `5048852f` (squash)
- [x] 4.4 `openspec archive worker-loop-test-determinism` via this `docs(openspec):` PR
