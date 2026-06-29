## 1. Foundation (Phase A — batch)

- [ ] 1.1 Re-confirm the measured debt (trx) on the 3 resilience classes BEFORE any change: `ConversationTimeoutWorkerResilienceTests`, `QueueDistributionWorkerResilienceTests`, `WebhookDeliveryServiceResilienceTests` (record per-test seconds as the before-baseline)
- [ ] 1.2 Confirm the options class each worker binds (`ConversationTimeoutWorker` + `QueueDistributionWorker` both inject `IOptions<DistributionOptions>` — verify) and that `DistributionOptions` is registered/validated where these workers are added in `Program.cs`

## 2. Production seams (Phase B — focused, behaviour-preserving; defaults byte-identical)

- [ ] 2.1 `ConversationTimeoutWorker`: add options for the startup delay (`:57`, default 5s) and the sweep interval (`:59`, default 5s) on its bound options class; read them in `ExecuteAsync` instead of the hardcoded `TimeSpan.FromSeconds(5)`. No `TimeProvider`, no `IClock` change
- [ ] 2.2 `QueueDistributionWorker`: add an options field for the startup warm-up delay (`:60`, default 3s); read it instead of the hardcoded `TimeSpan.FromSeconds(3)`. Period stays `PollIntervalMs`
- [ ] 2.3 `WebhookDeliveryService`: extract `internal Task ProcessPendingRetriesOnceAsync(CancellationToken)` from the per-iteration body of `PollPendingRetriesAsync` (`:84`), leaving the loop+`Task.Delay(RetryPollInterval)` (`:92`) as the production caller. Mirror the existing `DeliverForTestAsync` (`:301`) internal-visibility idiom. No timer seam
- [ ] 2.4 Build checkpoint: `dotnet build Verbara.Platform.slnx -c Release` 0 warnings; add/keep a test asserting each new option's DEFAULT equals the prior hardcoded value (production cadence unchanged)

## 3. Test migration (Phase C — batch)

- [ ] 3.1 `ConversationTimeoutWorkerResilienceTests`: set the startup-delay + sweep-interval options to a few ms; keep driving the real `ExecuteAsync` (the `heartbeat.RecordTick`-throws outer-fatal path) and observing the fault via `AwaitExecuteFaultAsync`; shrink the `CancellationTokenSource(TimeSpan)` backstop. NO `FakeTimeProvider.Advance`
- [ ] 3.2 `QueueDistributionWorkerResilienceTests`: set the startup-delay option to ~0 (period already `PollIntervalMs=50`); same causal-fault assertion
- [ ] 3.3 `WebhookDeliveryServiceResilienceTests`: rewrite the two ~30–35s `fault==null` tests to call `ProcessPendingRetriesOnceAsync` directly, asserting the same inner-catch-swallow + loop-continue contract; record (test comment + OpenSpec) that the outer-rethrow path is structurally unreachable from unit scope
- [ ] 3.4 Verify: the 3 classes now run sub-second (after-trx vs the 1.1 baseline); full `dotnet test Verbara.Platform.slnx -c Release` (CI filter) green, 0 warnings; C2 fence-guard still green (no new test `Task.Delay`/`Thread.Sleep`)

## 4. Ship

- [ ] 4.1 `openspec validate worker-loop-test-determinism --strict` passes
- [ ] 4.2 Two-stage review (spec compliance + adversarial: no re-imported timing race, defaults preserved, no silent coverage loss) = SHIP
- [ ] 4.3 PR → CI green → enqueue → merge
- [ ] 4.4 `openspec archive worker-loop-test-determinism` via its own `docs(openspec):` PR (check 4.3/4.4 first)
