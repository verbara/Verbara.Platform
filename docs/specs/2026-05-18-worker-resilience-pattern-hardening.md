# Worker Resilience Pattern Hardening (Platform + Pro cross-repo)

**Created:** 2026-05-18
**Status:** Draft
**Author:** Verbara maintainer (post-DLK forensics)
**Related:**
- Forensics that surfaced the bug: [`chaos-reports/dlk-oom-analysis-20260518.md`](../../chaos-reports/dlk-oom-analysis-20260518.md)
- D-LK soak report: [`docs/operations/soak-test-report-k8s-local.md`](../operations/soak-test-report-k8s-local.md) (TBD)
- Adjacent Pro spec (separate concern, NOT this one): [`Verbara.Sdk.Pro/docs/specs/2026-05-17-pro-v240-licensing-simplification-transition.md`](../../../Verbara.Sdk.Pro/docs/specs/2026-05-17-pro-v240-licensing-simplification-transition.md) — Pro v2.4.0-pro Licensing simplification carries a small adjacent Phase G-PRE for `Presence*HealthCheck` semantic fix (cosmetic), distinct from this spec which addresses the real worker-death bug.

## Purpose

D-LK 24h soak (2026-05-17/18) exposed a customer-visible architectural bug: `BackgroundService` implementations can die silently when an exception propagates out of `ExecuteAsync`, with default `BackgroundServiceExceptionBehavior.Ignore` swallowing the failure. The host stays "Running" from orchestrator perspective; the worker is dead.

Observed manifestation in D-LK soak: `QueueDistributionWorker` stopped heart-beating mid-soak (T+16h36m). Health check `services` reported Unhealthy. K8s liveness probe failed 3× → SIGKILL → 333 fail requests during 30s restart window.

The bug pattern affects ANY long-running `BackgroundService` in either repo. Workers known to use the vulnerable pattern (audit needed):

**Platform:**
- `QueueDistributionWorker` (`src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs:56`) — confirmed vulnerable; this is the one that died in D-LK.
- `ConversationTimeoutWorker` (`src/Verbara.Platform.Api/Services/`) — likely same pattern.
- Other workers under `src/Verbara.Platform.Api/Services/*Worker*.cs` — audit needed.

**Pro:**
- `PresenceFanoutService` + `PresenceMergeConsumer` (`Verbara.Sdk.Pro.Push.SignalR/Presence/`) — slightly different (Rx-driven), but `BroadcastAsync` fire-and-forget can swallow exceptions silently.
- `Verbara.Sdk.Pro.Dialer/` workers — audit needed.
- `Verbara.Sdk.Pro.EventStore/` workers — audit needed.
- `Verbara.Sdk.Pro.Realtime/` workers — audit needed.

Estimated 8-12 worker files cross-repo.

This spec defines the hardening pattern and lists the work to apply it everywhere.

## Goal

After execution, ANY `BackgroundService` worker death in Platform.Api or Pro packages:

1. Surfaces as a host-fatal exception (via `BackgroundServiceExceptionBehavior.StopHost`).
2. Logs the death cause at `Critical` level BEFORE the host shuts down.
3. Triggers K8s pod restart with `Last State Reason: Error` and the exception visible in `--previous` logs.
4. Does NOT silently hang forever masquerading as healthy.

Operators see worker death as an actionable incident, not a 21h-stale-heartbeat mystery.

## Non-goals

- **No new feature work.** This is hardening of existing services.
- **No worker logic changes** (business logic of each worker remains identical).
- **No new dependency injection** for resilience mid-pattern (don't introduce `IRetryPolicy` parameter to every worker — the resilience belongs in the OUTER catch, not in every tick).
- **No coupling to specific orchestrator** (K8s vs Docker behave identically — both see process exit).

## The hardening pattern

### Pattern A — Polling worker (timer-based, e.g. `QueueDistributionWorker`)

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    try  // ← OUTER try-catch wraps the entire loop
    {
        // Existing startup delay, timer construction, etc.
        await Task.Delay(StartupDelay, stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _heartbeat.RecordTick(nameof(MyWorker), PollInterval);
            try
            {
                await DoTickWork(stoppingToken).ConfigureAwait(false);
            }
            catch (CircuitBreakerOpenException) { LogCircuitOpen(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogTickError(ex); /* tick-level error, continue loop */ }
        }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        // Normal shutdown path — host is stopping.
    }
    catch (Exception fatalEx)
    {
        LogWorkerCrash(fatalEx);
        throw;  // ← rethrow so BackgroundServiceExceptionBehavior captures it
    }
}
```

**Two layers of catch:**
- **Inner try-catch (per-tick):** swallow recoverable errors, log, continue iterating. This is what `QueueDistributionWorker` already does correctly.
- **Outer try-catch (around-the-loop):** captures fatal exceptions thrown by `WaitForNextTickAsync`, `RecordTick`, `PeriodicTimer.Dispose`, or any code path outside the inner try. Logs and rethrows.

### Pattern B — Event-driven worker (Rx subscription, e.g. `PresenceFanoutService`)

```csharp
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    Interlocked.Exchange(ref _started, 1);
    try
    {
        _subscription = _tracker.Deltas
            .ObserveOn(Scheduler.Default)
            .Subscribe(
                delta => HandleDeltaSafely(delta, stoppingToken),
                ex => HandleSubscriptionFault(ex),     // ← surface to health check + log Critical
                () => HandleSubscriptionComplete());
        stoppingToken.Register(() => _subscription?.Dispose());
        return Task.CompletedTask;
    }
    catch (Exception ex)
    {
        LogWorkerStartFailed(ex);
        throw;
    }
}

private void HandleDeltaSafely(PresenceDelta delta, CancellationToken ct)
{
    try
    {
        _ = BroadcastAsync(delta, ct);  // fire-and-forget intentional; BroadcastAsync has own try-catch
    }
    catch (Exception ex)
    {
        LogBroadcastQueueError(ex);  // very rare; only if scheduler fails
    }
}

private void HandleSubscriptionFault(Exception ex)
{
    PresenceFanoutLog.SubscriptionError(_logger, ex.Message);
    // Mark service as fault state visible to health check:
    Interlocked.Exchange(ref _subscription, null);  // CheckHealthAsync now returns Unhealthy
}
```

**Key change vs current code:**
- `HandleSubscriptionFault` now sets `_subscription = null` so the (concurrently-being-fixed in v2.4.0-pro Phase G-PRE) health check detects definitive failure.
- BackgroundService cleanup is via `_subscription?.Dispose()` in `stoppingToken.Register`, preserving existing pattern.

### Host-level configuration

```csharp
// In Verbara.Platform.Api/Program.cs:
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});
```

This is the **critical change** that makes the pattern work. Without it, even with the outer try-catch + rethrow, the default `Ignore` behaviour swallows the rethrown exception silently. With `StopHost`:

- `ExecuteAsync` throws → BackgroundService logs at `Critical` → host invokes `IHostApplicationLifetime.StopApplication()` → all hosted services receive `StopAsync` → host process exits.
- K8s observes process exit → restarts pod with clear `Last State Reason: Error` + the exception visible in `--previous` logs.

### LoggerMessage source-generated entries

Each worker file adds (or reuses) two `[LoggerMessage]` source-gen entries:

```csharp
internal static partial class WorkerLog
{
    [LoggerMessage(EventId = 18001, Level = LogLevel.Critical,
        Message = "Worker {WorkerName} crashed fatally — host will shut down for restart. Exception: {ExceptionMessage}")]
    public static partial void WorkerCrash(ILogger logger, string workerName, string exceptionMessage, Exception exception);

    // Existing tick-error logger reused / renamed.
}
```

The `EventId = 18001` series is reserved for worker-resilience events across both repos (Platform uses 18000-18099, Pro uses 18100-18199).

## Files to modify

### Platform (this repo)

| File | Change |
|---|---|
| `src/Verbara.Platform.Api/Program.cs` | Add `services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = StopHost)` |
| `src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs` | Apply Pattern A — outer try-catch + LogWorkerCrash + rethrow |
| `src/Verbara.Platform.Api/Services/ConversationTimeoutWorker.cs` | Apply Pattern A |
| (audit) other `*Worker.cs` files under `Services/` | Apply Pattern A as needed |
| `src/Verbara.Platform.Api/Services/WorkerLog.cs` (new or existing) | LoggerMessage source-gen entries 18001-18099 |

### Pro repo (`Verbara.Sdk.Pro`)

| File | Change |
|---|---|
| `src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs` | Apply Pattern B — try/catch around Subscribe + HandleSubscriptionFault → nullify `_subscription` |
| `src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceMergeConsumer.cs` | Apply Pattern B |
| (audit) `Verbara.Sdk.Pro.Dialer/` workers | Apply Pattern A/B as appropriate |
| (audit) `Verbara.Sdk.Pro.EventStore/` workers | Apply Pattern A/B |
| (audit) `Verbara.Sdk.Pro.Realtime/` workers | Apply Pattern A/B |
| Pro `WorkerLog.cs` (new) | LoggerMessage source-gen entries 18100-18199 |

### Both repos — audit script

A small audit script to identify candidate files:

```bash
# Find all BackgroundService implementations
grep -rl 'class.*BackgroundService' src/ --include='*.cs' | \
    xargs grep -L 'BackgroundServiceExceptionBehavior\|outer try-catch' | \
    sort
```

Output goes to the audit list. Each file is either confirmed safe (already has the pattern) or needs the hardening.

## Tests

### Pattern A — `WorkerResilienceTests.cs` (per worker file)

For each Platform `*Worker` modified, add a sibling test file with these assertions:

| Test | Setup | Expected |
|---|---|---|
| `WaitForNextTickAsync_ThrowsUnhandled_HostStopRequested` | Mock `IPeriodicTimer` throws on second call | `IHostApplicationLifetime.StopApplication()` called once |
| `RecordTick_Throws_HostStopRequested` | Mock `IServiceHeartbeat.RecordTick` throws | `IHostApplicationLifetime.StopApplication()` called once |
| `DoTickWork_ThrowsRecoverable_LoopContinues` | Mock tick work throws once then succeeds | Loop continues, no host stop |
| `Cancellation_PropagatesCleanly_NoHostStop` | Cancel token mid-execution | Worker exits cleanly, no host stop |
| `HealthCheck_ReportsUnhealthy_WhenWorkerStopped` | Force worker.ExecuteAsync to complete | Health check returns Unhealthy |

### Pattern B — `PresenceFanoutResilienceTests.cs` (per Rx worker)

| Test | Setup | Expected |
|---|---|---|
| `Subscribe_Throws_HostStopRequested` | Mock `IObservable.Subscribe` throws | `IHostApplicationLifetime.StopApplication()` called once |
| `OnError_FaultsSubscription_HealthUnhealthy` | Trigger error via Subject.OnError | `_subscription == null`; CheckHealth returns Unhealthy |
| `OnCompleted_Normal_NoHostStop` | Trigger completion via Subject.OnCompleted | Worker stays "Started" but no traffic; CheckHealth returns Healthy (idle) |
| `DeltaArrival_UpdatesHeartbeat_Broadcasts` | Push delta via Subject.OnNext | Heartbeat advances; broadcast called |

### Integration test — host process exit

```csharp
[Fact]
public async Task QueueDistributionWorker_FataException_TriggersHostStop()
{
    var lifetimeCalled = 0;
    using var host = new TestHostBuilder()
        .ConfigureServices(s => {
            s.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
            s.AddSingleton<IHeartbeat>(new ThrowingHeartbeat());  // throws on every RecordTick
            s.AddHostedService<QueueDistributionWorker>();
        })
        .Build();

    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => Interlocked.Increment(ref lifetimeCalled));

    await host.StartAsync();
    // Wait up to 5s for the worker to fail + host to stop.
    var stopped = SpinWait.SpinUntil(() => lifetimeCalled > 0, TimeSpan.FromSeconds(5));
    Assert.True(stopped, "Host did not stop after worker fatal exception");
}
```

## Verification

After the hardening ships:

- [ ] Repeat D-LK 24h soak in K8s lab (after upgrading lab to current image OR using a patched 1.14.6 build).
- [ ] Observe NO heartbeat-staleness false positives for `presence-*` workers (G-PRE fixed those).
- [ ] If ANY worker would die, the pod restarts with `Last State Reason: Error` + clear exception in `--previous` logs.
- [ ] Total request fail rate ≤0.05% (D-LK was 0.013% — should stay at-or-below).
- [ ] No "silent worker death" mode possible — every termination path leaves a customer-visible signal.

## Effort estimate

| Phase | Hours | Notes |
|---|---|---|
| A — Audit + identify candidates | 2 | Run grep script; classify by Pattern A vs B; document scope |
| B — Platform-side hardening | 4 | QueueDistributionWorker + ConversationTimeoutWorker + Program.cs HostOptions + WorkerLog.cs |
| C — Pro-side hardening | 4 | PresenceFanoutService + PresenceMergeConsumer + audit-confirmed others + Pro WorkerLog.cs |
| D — Tests (cross-repo) | 6 | Per-worker resilience tests + 1 integration test for HostOptions wiring |
| E — Docs | 2 | ADR optional (architectural pattern); CHANGELOG entry per repo |
| F — Pack + smoke | 2 | Pro nupkg + Platform build + smoke test with patched build |
| **Total** | **~20h** | ~2.5 días maintainer time |

## Distribution / release pathway

**Option 1 (recommended): standalone patch release**
- Pro v2.4.1-pro = Pro v2.4.0-pro + this hardening (Pro side only).
- Platform v2.4.0.1 = Platform v2.4.0 + this hardening (Platform side only).
- Coordination: ship Pro v2.4.1-pro first, then Platform v2.4.0.1 consuming the new Pro.
- Rationale: keeps the Licensing simplification (Pro v2.4.0-pro) and the Worker Resilience fix (Pro v2.4.1-pro + Platform v2.4.0.1) independent. Easier rollback. Clearer changelog.

**Option 2 (bundle into Pro v2.4.0-pro):**
- Add this hardening as a Phase I of the existing Pro v2.4.0-pro execution plan.
- Stretches v2.4.0-pro execution time from ~28h to ~38h.
- Risk: bundling unrelated changes increases blast radius of any single regression.
- **Rejected** — clarity wins.

**Option 3 (defer to v2.5.0-pro removal release):**
- Add this hardening alongside the `EnforcementMode` removal.
- Rationale: 6-week observability window of v2.4.0-pro gives time to gather more soak data.
- Risk: a real bug worth waiting 6 weeks to fix? No — this is customer-impacting.
- **Rejected** — too slow.

## Decision

**Adopt Option 1.** Ship Worker Resilience hardening as Pro v2.4.1-pro + Platform v2.4.0.1 standalone patches, AFTER Pro v2.4.0-pro + Platform v2.4.0 ship.

Cadence:
1. Pro v2.4.0-pro Licensing simplification (per existing plan): ~28h
2. Platform v2.4.0 consumer migration plan (to be created post-Pro v2.4.0-pro): ~16h
3. Pro v2.4.1-pro Worker Resilience hardening (Pro half of this spec): ~10h
4. Platform v2.4.0.1 Worker Resilience hardening (Platform half + HostOptions wiring): ~10h

Total of all 4 trains: ~64h ≈ 8 maintainer days spread over ~6 weeks.

## Open questions

1. **Should `BackgroundServiceExceptionBehavior.StopHost` be the default in a future major release?** Today it's `Ignore` because it's less surprising for ad-hoc workers. For Verbara, we deliberately want every worker death to be a P0 — StopHost is the right call. Mark this as the Verbara house-style.

2. **Are there workers that SHOULD silently fail?** Possible candidates:
   - Audit log writer — if it can't write, app should continue (audit gap, but app keeps running).
   - But these are NOT `BackgroundService` — they're called inline from request pipeline. So irrelevant to this spec.

3. **Test infrastructure: how to simulate "exception out of `WaitForNextTickAsync`"?**
   - One approach: replace `PeriodicTimer` with an `IAsyncEnumerable<bool>`-returning interface, mock that in tests.
   - Or: use Polly's chaos injection (already used for circuit breaker tests in Pro).
   - Decision deferred to test author.

## References

- D-LK forensics that surfaced the bug: [`chaos-reports/dlk-oom-analysis-20260518.md`](../../chaos-reports/dlk-oom-analysis-20260518.md)
- .NET docs on `BackgroundServiceExceptionBehavior`: https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.backgroundserviceexceptionbehavior
- D-LK soak run artifacts: [`tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/`](../../tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/)
- Existing worker (Platform): [`src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs:56`](../../src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs)
- Existing worker (Pro): `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs:100`
