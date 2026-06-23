# AuthWriteQueue — deterministic drain test barrier

> Test-reliability fix for `AuthWriteQueueTests`. Replaces a wall-clock `Task.Delay(100)` completion barrier (shared by 8 tests) with a **causal** barrier. Follows up the still-open finding in the `reference_authwritequeue_shutdown` memory and the PR #78 CI flake of `Consumer_ShouldContinueProcessing_WhenIndividualCommandFails`. **No production behavior change** — the SUT gains one test-only `internal` affordance. Not related to the PR #72 double-write bug (that is a real, separate, already-shipped fix).

## 1. Problem

`AuthWriteQueue : BackgroundService` (`src/Verbara.Platform.Api/Services/AuthWriteQueue.cs`) is a single-reader bounded-`Channel` consumer. Its test suite uses a helper `StartAndDrain` that does `StartAsync → Task.Delay(100) → cts.Cancel() → StopAsync`. The 100 ms is a **wall-clock guess** that the consumer was scheduled, drained the channel, and finished `ProcessBatchAsync` before cancellation. **7 tests** call `StartAndDrain` and an 8th (`Consumer_ShouldDrainPendingItems_OnGracefulShutdown`) inlines the same pattern.

Under CI CPU contention the 100 ms can elapse **before or inside** a batch; cancellation truncates processing; the one-shot post-cancel drain sees fewer items than asserted → `NSubstitute Received(N)` fails intermittently. Observed: `Consumer_ShouldDrainPendingItems_OnGracefulShutdown` flaked ~1/1357 in CI; `Consumer_ShouldContinueProcessing_WhenIndividualCommandFails` flaked once during the v2.14.1 PR #78 run.

## 2. Root cause (analysis-first; read-only audit, 4 agents)

**One structural cause, two symptoms.**

- The `stoppingToken` does **double duty**: it is both the loop-entry gate (`await reader.WaitToReadAsync(stoppingToken)`, the only main-loop await) **and** the shutdown signal (`OperationCanceledException` is the only loop exit). `Writer.Complete()` is never called, so the loop never exits on an empty channel. The SUT exposes **no "drained" signal** (`_processed`/`_failed` are write-only OTel counters).
- **Symptom 1 (the flake):** the time-based barrier races the work it is supposed to await.
- **Symptom 2 (the "drains-0 trap"):** every prior hardening attempt failed 6/6 with `processed == 0` because it **cancelled the token to stop the consumer** — but that token is also the loop-entry gate, so *cancel-to-stop == cancel-to-never-process*. Any cancel-then-measure barrier is doomed.

Reconciliation: with items pre-enqueued and the synchronous never-yield NSubstitute mocks, `WaitToReadAsync` on a non-empty channel returns `true` synchronously and the first batch runs; `processed == 0` only occurs when the shape cancels before processing or enqueues *after* the one-shot drain.

## 3. Decision (approved 2026-06-23)

Replace the time-based barrier with the **canonical `System.Threading.Channels` drain idiom**: complete the writer, let the loop exit **naturally** when the channel empties, then await `BackgroundService.ExecuteTask`. It **never cancels `stoppingToken` to terminate**, so it cannot trip the drains-0 trap.

- **SUT change (one line):** `internal void CompleteWriter() => _channel.Writer.Complete();` — reachable from `Verbara.Platform.Api.Tests` via the already-present `InternalsVisibleTo`. Zero public surface; production never calls it (the hosted service runs continuously, stopped via `StopAsync`). AOT-safe (BCL `Channels`).
- **`StartAndDrain` (7 tests):** `await StartAsync(None)` on a never-cancelled token → `CompleteWriter()` → `await ExecuteTask!.WaitAsync(5s)` → assert.
- **`Consumer_ShouldDrainPendingItems_OnGracefulShutdown`:** **keep** its cancel path (it is the only coverage of the cancel-drain branch) but re-barrier on a **counting `TaskCompletionSource`** released from the mock once all 5 (post-coalesce store-call count, log events are not coalesced) `auth_events` saves land, *then* `StopAsync`.
- **Doc fix (folded in):** the class `<remarks>` said `BoundedChannelFullMode.DropWrite`; the code uses `Wait` (L80). Corrected.

Rejected alternatives: public `CompleteWriter` (adds test-only public surface — smell for an AOT/IP-sensitive host); a production `DrainCompleted`/`IdleAsync` signal (heaviest; does not by itself solve the drains-0 trap); zero-SUT-change per-test TCS counting barrier (viable fallback, but does not make loop-exit deterministic).

## 4. Affected tests
`Consumer_Should…`: `PersistAllCommands_WhenBatchHasMixedTypes`, `CoalesceUserMutations_WhenSameUserHasMultipleCommandsInBatch`, `NotMixUsers_WhenBatchSpansMultipleUsers`, `NotPersistUserMutations_WhenUserIsNotFound`, `UpdatePasswordHash_WhenPasswordRehashCommandEnqueued`, `CoalesceRehashAndLastLogin_WhenSameUserHasBoth`, `ContinueProcessing_WhenIndividualCommandFails` (via `StartAndDrain`), `DrainPendingItems_OnGracefulShutdown` (counting-TCS re-barrier). `Consumer_ShouldNotDoubleWrite_…` is untouched (already deterministic via its `okPersisted` TCS gate).

## 5. Verification
- Build clean (warnings-as-errors). ✅
- AuthWriteQueue tests **30/30 passed under 24-core saturating CPU contention** (330 executions, 0 flakes). ✅
- Full `Verbara.Platform.Api.Tests` suite green (regression + real parallel context).
- AOT publish gate (CI): 0 `IL2026`/`IL3050`/`IL207x`, native ELF — the change is a single BCL call, AOT-trivial.

## 6. Risks
Low. `Writer.Complete()` only after all enqueues (post-complete `TryWrite` returns false); `await ExecuteTask` only after `StartAsync` (nullable before); the graceful-shutdown test must NOT convert to natural-completion (would vacuously skip the cancel-drain branch) — re-barriered instead; coalescing tests count post-coalesce store calls (else the TCS never completes and the test hangs to its 5 s `WaitAsync` cap). No reflection, no new deps.
