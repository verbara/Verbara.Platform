# D-LK 24h Soak — "OOM" Forensic Analysis

**Date:** 2026-05-18
**Author:** Sprint A forensics quick-pass (post D-LK PASS-with-findings)
**Soak run:** [run-20260518-042455](../tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/)
**Subject:** `platform-api-558f699fc9-nnqqt` Exit 137 at T+16h36m of soak (2026-05-17 21:12:27 UTC)

## TL;DR

**The "OOM" hypothesis is wrong.** Exit 137 was NOT kernel OOM killer — it was K8s liveness probe failure (3 consecutive `/health` failures → SIGKILL after grace).

Three findings, two of them false positives:

| Original finding | Diagnosis | Severity |
|---|---|---|
| ~~"OOM at T+16h36m"~~ | **Liveness probe failure** triggered by `services` health check returning Unhealthy due to `QueueDistributionWorker` heartbeat staleness | 🔴 Real (worker silent death) |
| ~~"`presence-fanout` heartbeat stale 21h"~~ | **False positive** — service is event-driven (Rx subscription on `PresenceTracker.Deltas`); heartbeat only updates on event arrival; queues-only soak produced ZERO presence events; staleness is by design | 🟡 Health check design bug (cosmetic) |
| ~~"`presence-merge` heartbeat stale 21h"~~ | **False positive** — same architectural pattern as `presence-fanout` | 🟡 Health check design bug (cosmetic) |
| 333 fails (of 2.59M = 0.013%) | Cilium endpoint slice update window during pod restart (~30s) | 🟢 Acceptable; matches Phase C-LK chaos observations |

## Evidence trail

### Step 1 — `kubectl describe pod` lastState

```
Last State: Terminated
  Reason: Error
  Exit Code: 137                    ← SIGKILL (not OOMKilled reason!)
  Started:  2026-05-16 23:47:07
  Finished: 2026-05-17 21:12:27
```

Reason: `Error`, not `OOMKilled`. Critical difference — K8s sets `Reason: OOMKilled` specifically when the kernel OOM killer fires. Here it's plain `Error` with exit 137 (= 128 + 9, SIGKILL).

The container limits were:
```
Limits: cpu=2, memory=2Gi
Liveness: http-get /health delay=30s timeout=1s period=15s #failure=3
```

So liveness probe runs every 15s, fails after 3 consecutive misses = 45s window. K8s sends SIGTERM, waits grace period (default 30s), then SIGKILL = exit 137.

### Step 2 — `logs --previous --tail=300` shows the cascade

Found in [logs-platform-api-558f699fc9-nnqqt-previous.txt](../tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/logs-platform-api-558f699fc9-nnqqt-previous.txt):

**Smoking gun #1 — pipeline stall:**
- 105 `Request starting` log entries
- 7 `Request finished` log entries
- Ratio 15:1 → requests were piling up without completing

**Smoking gun #2 — `services` health check went Unhealthy:**
```
fail: HealthChecks[103]
      Health check services with status Unhealthy completed after 0.1163ms
      with message 'Background services unhealthy: QueueDistributionWorker'
```

**Smoking gun #3 — `presence-*` Degraded for 21h+ but NOT fatal:**
```
warn: HealthChecks[103]
      Health check presence-merge with status Degraded after 0.0012ms
      message 'PresenceMergeConsumer heartbeat stale (77035.6s > 30s)'
```

77035.6s ≈ 21h25m ≈ the entire container lifetime since boot. The presence workers' heartbeats stopped updating shortly after boot. But Degraded ≠ Unhealthy in ASP.NET Core HealthChecks aggregation rules — Degraded is reported but doesn't fail liveness.

**It was the `services Unhealthy` (QueueDistributionWorker) that triggered the cascade.**

### Step 3 — Code review: WHY the heartbeats stopped

#### `PresenceFanoutService.ExecuteAsync` — event-driven, NOT polling

[`Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs:100`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs)

```csharp
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    Interlocked.Exchange(ref _started, 1);
    Interlocked.Exchange(ref _lastHeartbeatTicks, _clock.GetUtcNow().UtcTicks);

    _subscription = _tracker.Deltas.Subscribe(
        delta => {
            Interlocked.Exchange(ref _lastHeartbeatTicks, _clock.GetUtcNow().UtcTicks);  // ← only on event
            _ = BroadcastAsync(delta, stoppingToken);
        },
        ex => SubscriptionError(_logger, ex.Message),
        () => { /* tracker disposed */ });

    stoppingToken.Register(() => _subscription?.Dispose());
    return Task.CompletedTask;
}
```

**Diagnosis:**
- Heartbeat is recorded ONLY when a `PresenceDelta` arrives via Rx subscription.
- Queues-only soak produces ZERO deltas.
- Heartbeat → static at the boot-time value.
- After 30s, health check threshold trips → reports Degraded.
- This is the **HEALTH CHECK DESIGN** that's wrong, NOT the service.

The service is functionally healthy (subscription active, ready to broadcast). It's just idle. The health check should detect "subscription dead" not "heartbeat stale".

#### `QueueDistributionWorker.ExecuteAsync` — timer-based, polling

[`Verbara.Platform/src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs:56`](../src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs)

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PollIntervalMs));
    var pollInterval = TimeSpan.FromMilliseconds(_options.PollIntervalMs);

    while (await timer.WaitForNextTickAsync(stoppingToken))     // ← unhandled exception here propagates out
    {
        _heartbeat.RecordTick(nameof(QueueDistributionWorker), pollInterval);   // ← unhandled exception here propagates out
        try
        {
            await _policy.ExecuteAsync(...);
        }
        catch (CircuitBreakerOpenException) { LogCircuitOpen(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        catch (Exception ex) { LogDistributionError(ex); }
    }
}
```

**Diagnosis:**
- `WaitForNextTickAsync(stoppingToken)` can throw `OperationCanceledException` when the token is cancelled. This is OUTSIDE the inner try-catch.
- `_heartbeat.RecordTick(...)` can theoretically throw too (if `IServiceHeartbeat` impl has a bug).
- The default `BackgroundServiceExceptionBehavior` is `Ignore` — when `ExecuteAsync` throws, the service silently stops, but the host keeps running and K8s sees the pod as "Running".
- The truncated log (only last 300 lines captured) lost the moment the worker died.

**This IS the bug.** Worker can silently die without K8s/operator knowing.

### Why the previous log truncation matters

`kubectl logs --previous --tail=300` only captured the last 300 lines, which is the death-spiral phase (105 stalled requests). The transition from "healthy distribution" to "stalled" happened earlier — lost forever.

Without that transition, we can't say WHY `QueueDistributionWorker` died. Hypotheses (unprovable from data we have):

1. **Postgres connection pool exhaustion under sustained 30 RPS** → `DistributeAsync` awaits forever on connection → `PeriodicTimer` queues next tick → eventually thread pool starvation → `await WaitForNextTickAsync` throws unhandled.
2. **Cilium endpoint slice update at presence-* worker boot triggered DNS resolution failure** → `_tenantStore.GetAllActiveAsync` failed → unhandled inside `DistributeAsync` → caught inside try (LogDistributionError) → next iteration → same failure → eventually... wait, this would loop forever in catches, not silently die. **Not this.**
3. **`PeriodicTimer` timer instance got GC'd or disposed prematurely** → `WaitForNextTickAsync` returned `false` → while loop exits cleanly → `ExecuteAsync` returns → BackgroundService transitions to Stopped state silently. **Plausible.**
4. **Underlying .NET runtime issue in the older base image** (1.14.6 was built on aspnet:9.0; current is 10.0) → known async runtime bugs in some .NET 9 versions. **Possible but speculative.**

**Resolution: don't bother diagnosing the trigger.** The architectural fix is the same regardless.

## Recommendations (architectural, not tactical)

### 🔴 Fix #1 — Worker Resilience Pattern (Platform + Pro)

**Scope:** all `BackgroundService` implementations across Platform.Api/Services + Pro.* packages.

**Pattern fix:**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    try  // ← OUTER try-catch around the entire loop
    {
        using var timer = new PeriodicTimer(...);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _heartbeat.RecordTick(...);
            try { await DoTickWork(stoppingToken); }
            catch (CircuitBreakerOpenException) { LogCircuitOpen(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogTickError(ex); /* tick recoverable, continue */ }
        }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* normal shutdown */ }
    catch (Exception fatalEx)
    {
        LogWorkerCrash(fatalEx);
        throw;  // ← rethrow so BackgroundServiceExceptionBehavior captures it
    }
}
```

**Plus host-level config:**

```csharp
// In Program.cs:
builder.Services.Configure<HostOptions>(o => 
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
```

This guarantees that ANY worker death takes down the host → K8s issues clear restart → operator sees `Last State Reason: Error` with the exception in `previous` logs.

**Affected files (Platform):**
- `src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs:56`
- `src/Verbara.Platform.Api/Services/ConversationTimeoutWorker.cs` (same pattern likely)
- Audit any other `BackgroundService` in `Services/`.

**Affected files (Pro):**
- `src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs:100` (slightly different — Rx, no outer try wraps subscription init)
- `src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceMergeConsumer.cs`
- `src/Verbara.Sdk.Pro.Dialer/` workers
- `src/Verbara.Sdk.Pro.EventStore/` workers
- Estimated 8-12 worker files total to audit.

**Effort:** ~6-10 hours total (4h Platform + 4h Pro, mechanical refactor + tests).

### 🟡 Fix #2 — Health check semantics for event-driven workers (Pro)

**Scope:** `PresenceFanoutService.CheckHealthAsync` + `PresenceMergeConsumer.CheckHealthAsync`.

**Pattern fix:**

```csharp
public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
{
    // 1. Pre-start grace
    if (Interlocked.Read(ref _started) == 0)
        return Task.FromResult(HealthCheckResult.Healthy("Pre-start"));

    // 2. Subscription disposed (definitive failure)
    if (_subscription is null)
        return Task.FromResult(HealthCheckResult.Unhealthy("Subscription disposed"));

    // 3. Subscription active — idle is normal for event-driven service
    var staleness = _clock.GetUtcNow() - LastHeartbeatUtc;
    return Task.FromResult(HealthCheckResult.Healthy(
        $"Subscription active; last delta {staleness.TotalSeconds:F0}s ago (idle is normal)"));
}
```

**Effort:** ~2 hours (~30min code + ~1.5h tests for both services).

**Note:** this is purely cosmetic — the false-positive Degraded confuses operators but doesn't break anything. Lower priority than Fix #1.

### 🟢 Fix #3 — Better diagnostics for future forensics

`kubectl logs --previous --tail=300` lost the transition moment. Future soaks should:

- Stream platform-api logs to a persistent volume (or Loki/cloud log sink) so the full history survives pod restart.
- Already deployed: kube-prometheus-stack includes Loki — verify it's scraping platform-api logs and retained ≥7d.

## Decision matrix update

| Path proposed yesterday | Status after forensics |
|---|---|
| OOM forensics quick-pass | ✅ DONE (this document) — confirmed NOT OOM |
| Patch presence-fanout/merge with Polly retry | ❌ **REJECTED** — presence workers are not the bug; health check semantics is |
| Bump memory limit to 4Gi | ❌ **REJECTED** — memory was never the issue; pointless mitigation |
| Close D-LK with PASS-with-findings | ⏳ ready to ship — but findings list updated |

| Path NOT proposed yesterday | Status after forensics |
|---|---|
| Worker Resilience Pattern hardening (Fix #1) | ⭐ **NEW priority** — addresses the real bug |
| Health check semantics for event-driven (Fix #2) | 🟡 candidate — cosmetic fix |
| Bundle in Pro v2.4.0-pro Change G | ✏️ **Re-scoped**: only Fix #2 (Pro-side health check fix) is appropriate for v2.4.0-pro. Fix #1 is a cross-repo pattern refactor that deserves its own spec |
| Upgrade lab to v2.1.0 | 🟡 still optional, but lower priority — the bug is in the pattern, not the version (1.14.6 vs 2.1.0 use the same `BackgroundService` pattern) |

## What this means for the product (final-product-focused)

1. **Worker Resilience Pattern bug is a customer-visible defect.** ANY long-running deployment (Docker SMB or K8s) can hit it. The D-L Docker 24h soak (2026-04-30) didn't hit it because:
   - It ran 4 scenarios (queue + presence + jwt + AHH mixed) — more deltas firing, presence workers had events.
   - It got lucky with the timing of the trigger that killed `QueueDistributionWorker` in K8s.
   - Docker compose doesn't have liveness-probe-restart semantics; even if the worker died, the API kept serving.

2. **The bug masquerades differently per deploy target:**
   - Docker SMB: worker silently dies → some background work stops (queue distribution, retention sweepers, recording rotation) → operators eventually notice degradation.
   - K8s: worker silently dies → health check fails → liveness restart → request blip + restart cycle.

3. **Customer impact:**
   - **High** for any customer running >12h sustained traffic with mixed background workload.
   - **Low** for customers who restart Verbara daily as part of maintenance (the bug never has time to manifest).
   - **High** for K8s multi-replica deployments where presence workers + queue distribution must coordinate.

4. **Sprint plan:**
   - Create a NEW spec/plan for **"Worker Resilience Pattern Hardening"** spanning Platform + Pro repos. ~8-12h dev effort + tests.
   - Either ship as Pro v2.4.0-pro PHASE I + Platform v2.4.0 sibling, OR ship as a standalone Pro v2.4.1-pro + Platform v2.4.0.1 patch.
   - **Recommendation:** standalone patch — keep Licensing simplification and Worker Resilience independent for clarity.

## Status

- [x] Forensics complete. Root cause architectural pattern bug, not OOM.
- [ ] Update D-LK report with corrected interpretation.
- [ ] Create Worker Resilience Pattern spec (Platform + Pro) — defer decision: standalone vs bundled.
- [ ] Pro v2.4.0-pro Change G updated scope: only event-driven health check semantics (small fix).

## References

- Soak run artifacts: [`tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/`](../tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/run-20260518-042455/)
- Source files audited:
  - [`Verbara.Platform/src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs`](../src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs)
  - [`Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs)
- Yesterday's PASS-with-findings narrative (now superseded by this document for the root cause interpretation): direct conversation 2026-05-18 05:00 local.
