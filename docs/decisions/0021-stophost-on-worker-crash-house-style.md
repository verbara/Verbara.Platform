# ADR-0021: `StopHost` on Worker Crash — Platform Host Wiring

- **Status:** Accepted
- **Date:** 2026-05-18
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - [Verbara.Sdk.Pro ADR-0013 `StopHost` on Worker Crash — Verbara House-Style](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0013-stophost-on-worker-crash-house-style.md) — Pro-side counterpart that hardens the 13 Pro `BackgroundService` implementations and shipped as v2.4.1-pro on 2026-05-18.
  - Canonical spec: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](../specs/2026-05-18-worker-resilience-pattern-hardening.md)
  - Active plan: [`docs/plans/active/2026-05-18-platform-v230-worker-resilience.md`](../plans/active/2026-05-18-platform-v230-worker-resilience.md) (this train)
  - Forensics: [`chaos-reports/dlk-oom-analysis-20260518.md`](../../chaos-reports/dlk-oom-analysis-20260518.md)
  - Soak report: [`docs/operations/soak-test-report-k8s-local.md`](../operations/soak-test-report-k8s-local.md)
  - ADR-0014 K8s liveness-probe baseline (the orchestrator policy this ADR pairs with)

## Context

The D-LK 24h soak (2026-05-17/18) in the K8s lab exposed a host-level resilience gap: `BackgroundService` implementations whose `ExecuteAsync` faulted were silently swallowed by the default `BackgroundServiceExceptionBehavior.Ignore`. The orchestrator continued to report the pod as Running; the worker was dead; the colocated health check eventually flagged "Unhealthy" via stale heartbeat; the K8s liveness probe took ~30 s × 3 probe failures to escalate to SIGKILL → restart; 333 fail requests fell into the restart window.

Concrete manifestation: `Verbara.Platform.Api.Services.QueueDistributionWorker` stopped heart-beating at T+16h36m of a 24h soak. The pod restarted ~21 h after the worker died.

A cross-repo audit during planning revealed the same shape in **14 of 14 Platform `BackgroundService` implementations** and **11 of 12 Pro implementations**. The vulnerability is structural: the `BackgroundService` cradle the codebase grew up using permits silent death by default, and ~25 worker implementations across both repos had the same gap.

Pro v2.4.1-pro (ADR-0013) hardens every Pro worker with an outer `try/catch + LogWorkerCrash + throw`. Platform v2.3.0 (this ADR) does two things:

1. **Wires** `HostOptions.BackgroundServiceExceptionBehavior = StopHost` in `Program.cs` so that the rethrow from any hardened worker — Platform's or Pro's — causes the host process to stop, K8s observes the exit, and the operator sees `Last State Reason: Error` plus the exception in `--previous` logs.
2. **Hardens** all 14 Platform `BackgroundService` workers with the same outer `try/catch + LogWorkerCrash + throw` discipline that Pro adopted in v2.4.1-pro.

Without the `StopHost` wiring on the Platform host side, the Pro hardening is half-effective: the `WorkerCrash` Critical log entry is emitted, but the rethrown exception is swallowed by the default `Ignore` behavior, and the host never actually stops. The pair (Pro v2.4.1-pro + Platform v2.3.0) is the minimum cohesive shipment.

## Decision

Platform v2.3.0 adopts the same discipline as Pro v2.4.1-pro (ADR-0013) and, in addition, wires the host-level switch that makes the discipline end-to-end effective for any worker — Platform's or Pro's — running inside the Platform.Api host.

### Host-level configuration

In `src/Verbara.Platform.Api/Program.cs` immediately after `var builder = WebApplication.CreateBuilder(args);`:

```csharp
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});
```

This is the **critical change** that makes the pattern work end-to-end. With `StopHost`:

- A worker's rethrow from its outer `try/catch` triggers .NET's host shutdown path.
- The host calls `StopAsync` on all hosted services, then exits the process.
- K8s observes the exit, restarts the pod with `Last State Reason: Error`, and the exception stack is preserved in `--previous` logs.
- Loki / structured-log scraping captures the `WorkerCrash` Critical entry that was emitted *before* the rethrow, attributing the failure to a specific worker by name.

Without this switch, the rethrow is swallowed; the host stays "Running"; the operator sees nothing. That is the exact failure mode D-LK exposed. The `StopHost` value is Verbara house-style going forward — it ships in `Program.cs` and is asserted by an integration test (`WorkerResilienceHostOptionsTests`).

### Platform worker hardening (14 workers)

Each Platform worker gains the outer `try/catch + LogWorkerCrash + throw` discipline. The Pattern templates are identical to those in Pro ADR-0013:

- **Pattern A — polling worker (timer / `while`-loop):** outer `try` wraps the loop body; inner per-tick `try/catch` (already present in most workers) preserved; cancellation filtered by `when (stoppingToken.IsCancellationRequested)`; fatal exceptions logged at Critical and rethrown.
- **Pattern B — event-driven worker (Rx `Subscribe`):** outer `try` wraps subscription setup; `onError` handler now nullifies the subscription via `Interlocked.Exchange` so the colocated health check transitions to "Unhealthy"; fire-and-forget calls (`_ = ProcessAsync(...)`) wrapped in `HandleEventSafely` to capture synchronous throws.
- **Pattern C — Channel consumer:** `AuthWriteQueue` already had a partial outer `try/catch` but only caught `OperationCanceledException`. Extended to a full `try/catch` that logs `WorkerCrash` and rethrows non-OCE exceptions.

### LoggerMessage source-gen (Verbara.Platform convention)

Each Platform worker is `internal sealed partial class`; new `[LoggerMessage]` source-gen methods are added as **colocated `partial void LogXxx(...)` methods inside the worker class itself** (not a separate Log static class — different from Pro Push.SignalR's `XxxLog` convention but matches Platform's existing pattern, e.g. `QueueDistributionWorker.LogDistributionError` at lines 137-148 of the original file).

The new partial methods per worker:

- `LogWorkerCrash(string workerName, string reason, Exception ex)` — Critical, "`[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}`"
- For Pattern B only: `LogSubscriptionFault(string reason)` — Critical
- For Pattern B with fire-and-forget: `LogFireAndForgetSwallowed(string reason)` — Warning

No `EventId` specified — the source generator assigns automatically, matching existing per-worker logger entries in the codebase.

### Test discipline

Each hardened worker gains test coverage in `tests/Verbara.Platform.Api.Tests/Workers/Resilience/`:

- **Tier-1 deep (4 workers × ~4 tests):** `QueueDistributionWorkerResilienceTests`, `ConversationTimeoutWorkerResilienceTests`, `WebhookDeliveryServiceResilienceTests`, `BotAnalyticsPersistenceServiceResilienceTests`. Cover: outer-exception → Critical log + ExecuteTask propagation; cancellation → no rethrow; inner-recoverable → loop continues (Pattern A) or subscription nullification (Pattern B).
- **Smoke (7 in-process workers + 1 Channel consumer):** `SimpleWorkerSmokeTests.cs` covers `CampaignMetricsPoller`, `RetentionPurgeService`, `AuditRetentionService`, `ImpersonationSessionTimeoutService`, `ReportSchedulerService`, `VerbaraCapacitySyncService`, `AuthWriteQueue`. Each asserts that `StartAsync` + cancel + `StopAsync` completes cleanly via `BackgroundService.ExecuteTask` without faulting (cancellation must traverse the outer when-filter, not the fatal handler).
- **Integration:** `WorkerResilienceHostOptionsTests` asserts that the Platform DI composition root resolves `IOptions<HostOptions>` with `BackgroundServiceExceptionBehavior = StopHost`.

25 new tests total. 938 pre-existing Api.Tests pass unchanged.

### Cross-package workers (deferred test coverage)

`Verbara.Platform.Automation.TimerPollingService`, `Verbara.Platform.Mail.Services.TokenRefreshService`, and `Verbara.Platform.Billing.DunningService` live in side-packages whose test projects do not currently mirror the resilience-test fixture from Api.Tests. The workers themselves ARE hardened with the Pattern A discipline; their resilience contract is covered transitively by the `WorkerResilienceHostOptionsTests` integration assertion (any worker registered into the host now participates in `StopHost`). Per-worker smoke tests for these three workers are tracked as follow-up work in the v2.4.0 or v2.4.1 maintenance window — not a release-blocking gap because the hardening pattern is mechanical and the host-level switch makes worker death visible regardless of the test-project coverage.

## Consequences

### Positive

- The host-level switch (`StopHost`) makes every worker death observable: K8s pod restart with `Last State Reason: Error` + exception in `--previous` logs, plus the colocated `WorkerCrash` Critical log emitted before the rethrow.
- Pair of ADRs (Pro-0013 + Platform-0021) defines a complete cross-repo discipline. New workers added going forward are mechanically reviewable (`grep` audit + new tests follow the same templates).
- Pattern B subscription nullification on `OnError` makes the colocated health check honest: subscription fault → Unhealthy → orchestrator can act. The 21h-silent-stale-heartbeat failure mode is impossible by construction.
- Fire-and-forget execution paths in `BotAnalyticsPersistenceService` and `VerbaraCapacitySyncService` no longer swallow synchronous throws silently (now logged at Warning).
- AuthWriteQueue's partial gap (OCE-only outer catch) is now closed — Critical log + rethrow before host stop.
- 25 new resilience tests + 938 pre-existing Api.Tests all green; AOT publish clean; 0 warnings.

### Negative

- Worker death now stops the *entire host*, restarting all 14 Platform `BackgroundService` instances plus all 13 Pro `BackgroundService` instances inside the same process. The amplification is a known trade-off — the alternative (silent false-Healthy) is much worse for operability. Per-worker retry / circuit policies (already in Pro and Platform for many workers) reduce the chance a fatal exception even escapes the inner try; the restart latency is observed within ~35 s (K8s liveness 10s × 3 + restart ~5s).
- Cancellation paths in every hardened worker require explicit `OperationCanceledException when (stoppingToken.IsCancellationRequested)` filter to avoid logging shutdown-OCE as a WorkerCrash. The `SimpleWorkerSmokeTests` per-worker entries catch the omission via assertion that `ExecuteTask` doesn't fault on cancellation.
- The `NuGet.Config` was extended to also map `Verbara.Sdk.Pro*` packages to the `local` feed for the maintainer's dev-iteration loop (otherwise central-package-management's `packageSourceMapping` blocked local pickup). The `Dockerfile` already removes the `local` source before production restore, so this is dev-only — production builds remain GitHub-Packages-exclusive.

### Neutral

- The Platform v2.3.0 release preserves 100 % of public API and existing behavior for workers that never fault. The change is invisible until a worker would have died — at which point it becomes maximally visible.
- ADR-0019 and ADR-0020 already exist (`scope-aware-management-api-keys`, `csat-brownfield-survey-domain-extension`) — this is ADR-0021. The numbering deviates from the plan file (which guessed 0019) — sequential numbering is preserved as the convention.

## Alternatives considered

The same alternatives as Pro ADR-0013 — see that document. The summary for Platform-specific decisions:

1. **Wire `StopHost` but skip Platform worker hardening (rely on Pro only).** Rejected. Pro hardening alone doesn't cover the 14 Platform workers, including `QueueDistributionWorker` — the one that actually died in D-LK.
2. **Harden Platform workers but skip `StopHost` wiring.** Rejected. Without `StopHost`, the rethrow is silently swallowed by the default `Ignore` behavior and the host stays "Running" indefinitely — the failure mode is unchanged from the D-LK observation.
3. **Use a `PostConfigure<HostOptions>` source-gen analyzer to enforce the switch at build time.** Deferred. A future `Verbara.Analyzers` package could surface missing `StopHost` configuration as a build-time diagnostic; for now the integration test catches the regression.
4. **Bundle Platform v2.3.0 into the Pro v2.4.1-pro train.** Rejected. The Pro/Platform separation is structural; the two ADRs are paired but the train shipments are independent so consumers who track Pro and Platform on separate cadences can adopt each at their own pace.

## Implementation notes

- Pro v2.4.1-pro must be installed before Platform v2.3.0 (Pro's new `WorkerCrash` Log surface is referenced transitively by the Platform composition).
- `Directory.Packages.props` bumped from `2.4.0-pro` → `2.4.1-pro` for all 21 `Verbara.Sdk.Pro.*` package pins.
- `Directory.Build.props` bumped from `2.2.0` → `2.3.0`.
- `NuGet.Config` extended with `local` mapping for `Verbara.Sdk.Pro*` (dev-iteration loop; production unaffected).
- Plan moves from `docs/plans/active/2026-05-18-platform-v230-worker-resilience.md` → `docs/plans/completed/` on tag.

## References

- [.NET docs — `BackgroundServiceExceptionBehavior`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.backgroundserviceexceptionbehavior)
- [.NET docs — `HostOptions`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.hostoptions)
- [.NET docs — `BackgroundService.ExecuteTask`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.backgroundservice.executetask)
- Pro ADR-0013 (paired counterpart)
- ADR-0014 K8s liveness-probe baseline (orchestrator-side policy)
