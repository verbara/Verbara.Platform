# ADR-0025 — Kubernetes liveness vs readiness probe contract

**Status:** Accepted
**Date:** 2026-05-24
**Supersedes:** —
**Superseded by:** —

## Context

`Verbara.Platform.Api` exposes `/health` (liveness probe) and `/health/ready`
(readiness probe) for Kubernetes orchestration. Until this ADR, both endpoints
ran the **same** check suite via `app.MapHealthChecks("/health")` (no predicate
filter; runs all registered checks) and
`app.MapHealthChecks("/health/ready", { Predicate = r => r.Tags.Contains("ready") })`
with every check registered using `tags: ["ready"]`. Effectively:

```csharp
.AddCheck<BackgroundServiceHealthCheck>("services",  tags: ["ready"])
.AddCheck<AsteriskAmiHealthCheck>("asterisk",        tags: ["ready"])
.AddCheck<RetentionService>("retention",             tags: ["ready"])
.AddCheck<PostgresHealthCheck>("postgres",           tags: ["ready"])
```

The `PostgresHealthCheck` performs a live `SELECT 1` over a connection from
the shared NpgsqlDataSource. Under burst load (R5.5 Phase B-LK, 2026-05-24:
sustained 250-500 RPS or VU=1500 presence), the connection pool was
contention-bound by the load-test traffic; the SQL ping inside `/health`
queued behind real requests and the response latency exceeded the chart's
default `livenessProbe.timeoutSeconds: 1`. Kubernetes interpreted three
consecutive 1-s timeouts as "container unhealthy" and restarted the pod —
4 such restarts across 2 platform-api pods during the 30-minute B-LK sweep
window. The restarts were not OOMKill events; they were Kubernetes
*correctly* enforcing the chart's stated liveness contract against a
*misdesigned* `/health` endpoint.

The B-LK evidence (`docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/`)
documents the failure mode. The chart's interim safety net (longer
`timeoutSeconds`, higher `failureThreshold`) treats the symptom but not the
root cause — a `/health` endpoint that does dependency checks under the
liveness probe semantically misuses the K8s contract.

## Decision

`/health` (liveness) becomes a **process-alive-only** check that runs **no**
dependency probes. `/health/ready` (readiness) keeps the full dependency
suite. The split aligns with the Kubernetes documented contract:

| Probe | Question Kubernetes asks | What Verbara answers |
|---|---|---|
| **Liveness** (`/health`) | "Should I restart this pod?" | "Process is alive and the .NET host is responsive." (no dependency checks) |
| **Readiness** (`/health/ready`) | "Should I route traffic to this pod?" | "Process is alive AND Postgres + Asterisk AMI + background services are healthy." |

Implementation:

```csharp
// Liveness — Predicate=_=>false runs zero checks; default formatter
// returns 200 OK with body "Healthy" in <1 ms.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

// Readiness — runs all "ready"-tagged checks (Postgres, Asterisk AMI,
// background services, retention). May take seconds under burst; failure
// removes the pod from Service endpoints but does NOT restart it.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = HealthReportJsonWriter.WriteAsync,
});
```

Chart-side defensive hardening (defence-in-depth, not the primary fix):

| Probe | Before | After | Why |
|---|---|---|---|
| `livenessProbe.timeoutSeconds` | 1 (K8s default) | 3 | Belt-and-suspenders — even at <1 ms, network blips between kubelet and pod can spike |
| `livenessProbe.failureThreshold` | 3 | 5 | Absorbs transient blips without restart |
| `readinessProbe.timeoutSeconds` | 1 | 3 | `/health/ready` legitimately needs seconds under burst |
| `readinessProbe.failureThreshold` | 3 | 5 | Same blip absorption |
| `startupProbe.timeoutSeconds` | 1 | 3 | Cold-start AOT pod may need filesystem warmup |
| `startupProbe.failureThreshold` | 12 | 12 (unchanged) | Already generous (60 s startup window) |

`Verbara.Platform.Realtime` already satisfies this contract — its
`MapHealthEndpoint` returns a static `HealthResponse` JSON with no
dependency checks. No Realtime-side change required.

## Consequences

### Positive

- **Probe-driven pod restarts under burst eliminated.** `/health` responds
  in <1 ms regardless of dependency state. K8s will only restart for
  process-level failures (deadlock, OOM, panic).
- **Standard K8s contract.** Operators familiar with the documented
  liveness/readiness pattern get the behaviour they expect.
- **Customer-visible burst tolerance.** SMB-tier deployments under
  legitimate traffic spikes no longer cycle pods.
- **Defensive chart timeouts are now harmless.** A 3-second `timeoutSeconds`
  on a <1 ms endpoint is over-engineering, but cheap insurance against
  kubelet→pod network blips.

### Negative

- **Hung HTTP listener masquerading as healthy.** If the Kestrel listener
  itself becomes wedged (e.g., a stuck `BackgroundService` deadlocks the
  thread pool), `/health` may still respond 200 OK while the API serves
  nothing useful. **Mitigation:** the `BackgroundServiceHealthCheck` runs in
  `/health/ready`; a wedged background service flags the pod as not-ready,
  K8s drains traffic, and the operator gets an alert from `kube_pod_not_ready`.
  Liveness was never the right probe for thread-pool deadlock — that's an
  application-level concern caught by metrics + dashboards.

### Neutral

- **Dependency outage no longer kills pods.** If Postgres goes down, pods
  stay alive but `/health/ready` fails → traffic drains, pods don't restart
  → when Postgres recovers, pods immediately re-join the Service. This is
  the intended behaviour change; restart-cascade-on-dependency-outage was a
  pre-existing footgun, not a regression.

## Alternatives considered

1. **Bump `livenessProbe.timeoutSeconds` to 5 in the chart, leave code
   unchanged.** Pure band-aid — masks the contract violation without fixing
   it. Defers the inevitable: any future load-test scenario heavier than the
   2026-05-24 B-LK envelope will re-surface the same restart cascade.
   Rejected.

2. **Run `/health` on a separate Kestrel listener on a dedicated port.**
   Highest isolation: even if the main HTTP listener wedges, the probe
   listener stays responsive. Significant chart + Program.cs surface change
   (additional `Listen()` call, port plumbed through values.yaml, K8s probe
   port updated). Worth revisiting if a future incident shows main-listener
   wedge events; not justified for the current evidence. **Filed as future
   work in B-LK README** under "Recommended for chart hardening".

3. **Cached health-flag pattern** — `BackgroundService` runs the checks
   every 10 s, updates an atomic flag; `/health/ready` reads the flag.
   O(1) response, decoupled from request pipeline. Rejected for this ADR
   because it complicates the readiness semantics (operators inspecting
   `/health/ready` get stale data; the SQL ping in a real-time check is more
   trustworthy under most conditions). Could be adopted later if the
   readiness probe itself becomes a performance bottleneck.

## References

- [Kubernetes liveness, readiness, and startup probes documentation](https://kubernetes.io/docs/tasks/configure-pod-container/configure-liveness-readiness-startup-probes/)
- [`src/Verbara.Platform.Api/Program.cs:1257-1287`](../../src/Verbara.Platform.Api/Program.cs) — the implementation
- [`infra/k8s/helm/platform/templates/platform-api-deployment.yaml:191-220`](../../infra/k8s/helm/platform/templates/platform-api-deployment.yaml) — the chart side
- [`docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/README.md`](../operations/r55-blk-evidence/2026-05-24-v251-baseline/README.md) — § "Pod restart events during sweeps", root-cause evidence
- [ADR-0024](0024-v242-shipping-anomaly-and-process-hardening.md) — the previous chart hardening sweep (release pipeline level)
