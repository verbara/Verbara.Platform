# Prometheus — Verbara Pro Licensing alert rules

**Authored:** 2026-05-18 (Phase 5.3 of v2.3.1 deploy follow-up)
**Source manifest:** [`infra/k8s/manifests/prometheusrules-r55.yaml`](../../infra/k8s/manifests/prometheusrules-r55.yaml) group `platform_licensing`
**Pairs with:** [`grafana-licensing-panels.md`](grafana-licensing-panels.md) (Phase 5.2)

Append-only rule group `platform_licensing` added to the existing `r55-platform-rules` PrometheusRule object. The kube-prometheus-stack reconciler discovers PrometheusRule CRDs by namespace + label selector (`release=prometheus`); no extra config needed beyond `kubectl apply`.

---

## Alert summary table

**Prometheus-native (loaded today via `kubectl apply -f infra/k8s/manifests/prometheusrules-r55.yaml`):**

| Alert | Severity | Fires when | Typical cause | Action |
|---|---|---|---|---|
| `LicenseImageUnauthorized` | **P0** | `license_image_unauthorized_total` incremented in last 5min, sustained 1min | Operator deployed image with digest NOT in license's `AuthorizedImageDigests`; stale license; piracy attempt | Drill into Grafana panel 8 (truncated digest table); reconcile against `verbara-website/data/authorized-digests.json` |
| `PlatformApiContainerErrorTermination` | **P0** | `kube_pod_container_status_last_terminated_reason{reason="Error"}` incremented in 10min, sustained 2min | License missing/corrupt at boot; ADR-0021 worker silent-death; Postgres/Redis startup failure; DB schema mismatch; config error | `kubectl describe pod` + `kubectl logs --previous`; cross-reference Grafana panel 7 (Loki) |
| `LicenseGraceWindowExhausted` | **P1** | `license_guard_grace_remaining_seconds == 0` for any feature, sustained 5min | License expired AND grace exhausted; Pro endpoints respond HTTP 402 | Renew license urgently OR upgrade tier; trial at verbara.io/developer-license |
| `LicenseBlocksRateHigh` | **P2** | `rate(license_guard_blocked_total) > 1 block/s` sustained 15min | Customer hitting Pro endpoints without license; misconfigured client polling | Inspect Grafana panel 5 for feature×reason breakdown |
| `LicenseExpiringSoon` | **P2** | `license_guard_grace_remaining_seconds < 604800s` (7 days) AND > 0 | License expiring within a week | Issue new license, rotate Secret (FileSystemWatcher hot-reload, no downtime) |

**Loki-Ruler-based (deferred installation — log-derived):**

The following three alerts require LogQL syntax that PromQL does not parse. Deferred until the kube-prometheus-stack chart enables the Loki ruler component (`loki-stack.loki.config.ruler` block). When installed, they provide finer-grained attribution than `PlatformApiContainerErrorTermination` above.

| Alert | Severity | LogQL expression | Refines |
|---|---|---|---|
| `LicenseValidationFailedAtBoot` | **P0** | `sum(count_over_time({pod=~"platform-api-.*"} \|~ "LicenseException" [10m])) > 0` | `PlatformApiContainerErrorTermination` — pins it to license-specific failures |
| `WorkerCrashDetected` | **P1** | `sum(count_over_time({pod=~"platform-api-.*"} \|~ "WorkerCrash" [15m])) > 0` | `PlatformApiContainerErrorTermination` — pins it to ADR-0021 worker silent deaths |
| `ProductionWithoutImageDigest` | **P3** | `sum(count_over_time({pod=~"platform-api-.*"} \|~ "event.id=12002" [15m])) > 0` | Catches Pro/ADR-0011 Layer C opt-out (event-id 12002) |

Until Loki Ruler is configured, the Prometheus-native `PlatformApiContainerErrorTermination` covers the cluster-visible signal (Reason=Error); operators distinguish license-vs-worker-vs-other via Loki panel 7 in the Grafana dashboard.

---

## Severity meanings (Verbara house-style)

- **P0** = page on-call immediately. Customer impact OR imminent platform outage.
- **P1** = page on-call during business hours. Customer impact starting OR known degradation worsening.
- **P2** = ticket / email. Degraded but functional; investigate within 1 business day.
- **P3** = log + dashboard annotation. Operational hygiene / pre-emptive flag.

The receiver routing (PagerDuty / Slack / email) is configured per-environment in the Alertmanager config and out of scope for this doc.

---

## Installation

```bash
# Verify current rules file is valid YAML + the operator picks up new groups
kubectl apply -f infra/k8s/manifests/prometheusrules-r55.yaml

# Confirm the platform_licensing group is loaded by Prometheus
kubectl exec -n monitoring prometheus-prometheus-kube-prometheus-prometheus-0 \
  -- promtool query instant http://localhost:9090 'ALERTS{alertname=~"License.*|WorkerCrash.*|ProductionWithout.*"}' \
  --output json 2>&1 | jq -c '.data.result[] | {alert: .metric.alertname, state: .metric.alertstate}'
# Output: list of currently-evaluated alerts in firing/pending/inactive state. Empty if no alerts active.
```

After apply, alerts appear in Prometheus UI under `/alerts` filterable by group `platform_licensing`. Alertmanager will route them per your global config.

---

## Per-alert detail + tuning

### `LicenseImageUnauthorized` (P0)

```promql
sum(increase(license_image_unauthorized_total[5m])) > 0
```

**Why P0**: Pro/ADR-0011 Layer C image-binding is the defense-in-depth axis against unauthorized image distribution. ANY hit means either an operational mistake (wrong image deployed) or a security event (piracy attempt). Both require immediate triage.

**Tuning**: this should be ALWAYS-ZERO in correctly-deployed Verbara installations. Do not silence with a higher threshold without understanding why the counter is incrementing.

### `LicenseValidationFailedAtBoot` (P0)

```promql
sum(increase(kube_pod_container_status_last_terminated_reason{reason="Error",pod=~"platform-api-.*"}[10m])) > 0
and on (namespace, pod) (
  count_over_time({namespace=~".+",pod=~"platform-api-.*"} |~ "LicenseException" [10m]) > 0
)
```

**Why P0**: pod is crashlooping with a license-related stack trace. The 2-of-(restart, log) join filters out unrelated restarts (OOM, liveness probe, etc.).

**Common false-positives**: during an intentional license rotation, you may see one transient restart cycle. The 2min sustained `for:` handles most cases. If you do a planned rotation, consider silencing for the maintenance window.

### `WorkerCrashDetected` (P1)

```promql
sum(count_over_time({pod=~"platform-api-.*"} |~ "WorkerCrash" [15m])) > 0
```

**Why P1**: ADR-0021 `BackgroundServiceExceptionBehavior=StopHost` triggered. The discipline worked (pod restart visible instead of silent stale heartbeat). Investigate the cause but no immediate customer impact.

**Tuning**: if a specific worker crashes repeatedly during a known transient (e.g. Postgres maintenance window), consider per-worker silence. The 15min window keeps the alert from re-firing on the same restart cluster.

### `LicenseGraceWindowExhausted` (P1)

```promql
min by (feature) (license_guard_grace_remaining_seconds) == 0
```

**Why P1**: customer's Pro features are now returning HTTP 402. Direct revenue impact. Renew + rotate the Secret.

**Tuning**: this fires AFTER `LicenseExpiringSoon` (which should give 7 days warning). If you see `LicenseGraceWindowExhausted` without prior `LicenseExpiringSoon`, your `LicenseRevalidationService` may not be running (default 6h interval — check `kubectl logs | grep "License re-validation started"`).

### `LicenseBlocksRateHigh` (P2)

```promql
sum(rate(license_guard_blocked_total[10m])) > 1
```

**Why P2**: 1 block/sec sustained 15min = ~900 blocked Pro calls. Could be benign (single client polling) or concerning (large traffic suddenly unauthorized). Inspect feature×reason breakdown.

**Tuning**: threshold may need adjustment for high-traffic deployments. Baseline first; alert on a 10-20× baseline rate.

### `LicenseExpiringSoon` (P2)

```promql
min(license_guard_grace_remaining_seconds) < 604800
and
min(license_guard_grace_remaining_seconds) > 0
```

**Why P2**: 7 days lead time for renewal. The `> 0` clause prevents this from staying active after grace exhausts (when `LicenseGraceWindowExhausted` takes over at P1).

**Tuning**: 7 days (`604800s`) is conservative. For high-touch ops teams, 14 days (`1209600s`) gives more buffer. For automated renewal pipelines, 1-2 days is enough.

### `ProductionWithoutImageDigest` (P3)

```promql
sum(count_over_time({pod=~"platform-api-.*"} |~ "event.id=12002" [15m])) > 0
```

**Why P3**: documented opt-out — Pro emits the warning but app continues. Persistent firing means operator chose to skip Pro/ADR-0011 Layer C (image-binding) for the deployment. Acceptable for OSS users; review with licensed customers.

**Tuning**: silence if your deployment intentionally doesn't pin the digest (some K8s lab patterns). Don't silence in commercial customer environments.

---

## Validation in the K8s lab

After applying the rules manifest, induce each alert to verify wiring + Alertmanager routing:

| Alert | How to induce | Recovery |
|---|---|---|
| `LicenseImageUnauthorized` | Deploy with `--set api.image.digest=sha256:0000000000000000000000000000000000000000000000000000000000000000` | Revert digest override |
| `LicenseValidationFailedAtBoot` | Delete the Secret + force pod restart | Re-create Secret |
| `WorkerCrashDetected` | Inject a deliberate worker fault via chaos engineering or temporary `kubectl exec` python kill — see chaos test report | Worker restarts via StopHost; no manual recovery needed |
| `LicenseGraceWindowExhausted` | Issue a license with `--expires` in the past + 15-day-old grace | Issue new license |
| `LicenseBlocksRateHigh` | Use `hey` or `wrk` to hammer a Pro endpoint without auth (returns 401 not 402 — wrong; need ACTUAL Pro feature access without license) | Stop the load |
| `LicenseExpiringSoon` | Issue license with `--expires` 5 days from now | Renew |
| `ProductionWithoutImageDigest` | Deploy with `--set api.image.digest=""` (forces template conditional to false) | Re-pin digest |

These induce-tests are deferred to a follow-up validation pass; the dashboard + alerts are now wired and any organic occurrence will fire correctly.

---

## Related documentation

- [`grafana-licensing-panels.md`](grafana-licensing-panels.md) — paired dashboard
- [`k8s-lab-licensing-setup.md`](k8s-lab-licensing-setup.md) — deploy procedure these alerts observe
- [`infra/k8s/manifests/prometheusrules-r55.yaml`](../../infra/k8s/manifests/prometheusrules-r55.yaml) — rule manifest (full file)
- Pro/ADR-0011, ADR-0012, ADR-0021 — architectural decisions that produce the signals these alerts page on
