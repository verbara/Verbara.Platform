# Synthetic monitoring — R5.5 Phase E-LK

**Status:** deployed + passively verified during D-LK 24h soak (2026-05-25 → 2026-05-26 on v2.5.4). The probe stack and alert rules stayed loaded for the entire substantive 17h36m run with no `BlackboxJourneyDown` fires (probe_success stayed 1 for all 5 targets — `platform-api` /health and /health/ready, asterisk AMI/ARI, postgres-pooler TCP). See [`r55-blk-evidence/2026-05-26-d-lk-soak-v254/`](r55-blk-evidence/2026-05-26-d-lk-soak-v254/README.md) and [Production Readiness Review](production-readiness-review.md) for the closure context.

The **induce-failure validation** (E-LK Step 2 — scale platform-api to 0 and verify `BlackboxJourneyDown` fires within 5 min) was intentionally **not run** during the soak window (scaling to 0 would falsify the soak baseline). It is **unblocked** now that D-LK closed, and is scheduled as an **on-demand smoke** when the Talos lab is next brought up — the procedure is documented below. No production traffic depends on this validation; it confirms alert-rule reachability, not platform behavior. AlertManager delivery to a real receiver (slack/email/webhook) remains a Phase F follow-up that requires customer-side endpoint provisioning.

## What's running (K8s lab cluster)

**Engine:** `prometheus-blackbox-exporter` Helm release (`blackbox` in `monitoring` ns), pod `blackbox-prometheus-blackbox-exporter-69667fddc-wczxm` (1/1 Running 12d at time of E-LK doc).

**Probes** (configured via `ServiceMonitor` CRDs in `monitoring` ns):

| ServiceMonitor | Target | Module | Purpose |
|---|---|---|---|
| `blackbox-prometheus-blackbox-exporter-platform-api-health` | `http://platform-api.r55-platform.svc:5000/health` | `http_2xx` | Platform.Api liveness |
| `blackbox-prometheus-blackbox-exporter-platform-api-liveness` | `http://platform-api.r55-platform.svc:5000/health/ready` | `http_2xx` | Platform.Api readiness (full DB+Redis check) |
| `blackbox-prometheus-blackbox-exporter-asterisk-ami` | `asterisk-sip.r55-asterisk.svc:5038` | `tcp_connect` | Asterisk Management Interface reachability |
| `blackbox-prometheus-blackbox-exporter-asterisk-ari` | `asterisk-sip.r55-asterisk.svc:8088` | `tcp_connect` | Asterisk REST Interface reachability |
| `blackbox-prometheus-blackbox-exporter-postgres-tcp` | `postgres-pooler.r55-data.svc:5432` | `tcp_connect` | Postgres pooler reachability |

## Alert rules (PrometheusRule `r55-platform-rules`)

Custom alert rules deployed alongside the platform:

| Alert | Expression | Severity |
|---|---|---|
| `BlackboxJourneyDown` | `probe_success == 0` | critical |
| `PlatformApiUnavailable` | `up{job="platform-api"} == 0` | critical |
| `AuthLoginErrorRateHigh` | rate of `auth.login` 5xx exceeding threshold | warning |
| `JwtValidationLatencyP99High` | JWT validate p99 > SLO ceiling | warning |
| `HealthCheckUnhealthy` | `aspnetcore_healthcheck_status == 0` | critical |
| `PgConnectionPoolHigh` | Postgres conn pool usage > 80% | warning |
| `CircuitBreakerOpen` | `max by (key) (circuit_state) == 2` | warning |
| `RetentionServiceStalled` | dry-run retention sweeper stopped emitting | warning |
| `PresenceBacklogGrowing` | SignalR presence-fanout backlog > threshold | warning |
| `AuditWriteLatencyP99High` | audit write p99 > SLO | warning |
| `LicenseGuardBlockedHigh` | excessive Pro.Licensing blocks | info |
| `SloBreachQueueIngestion` | queue ingestion SLO breach | critical |
| `NodeDiskSpaceLow` | Talos node disk < 20% free | warning |
| (+ 23 default kube-prometheus-stack rules) | various | various |

**Total:** 36 PrometheusRule CRDs across `monitoring/` namespace (17 R5.5-custom + ~19 from kube-prometheus-stack chart defaults).

## Active inspection (operator)

```bash
export KUBECONFIG=$HOME/.kube/config-talos

# Port-forward Prometheus (operated headless service via pod-direct)
PROM_POD=$(kubectl -n monitoring get pod prometheus-prometheus-prometheus-0 -o name)
kubectl -n monitoring port-forward "$PROM_POD" 9090:9090 &
sleep 3
curl -sS http://localhost:9090/api/v1/query?query=probe_success | jq

# Or via the Cilium Gateway-exposed Grafana (admin / r55-staging):
#   http://grafana.r55.local
# Built-in dashboards: "Blackbox Exporter" + custom "R55 / Synthetic" if added.

# Port-forward AlertManager
kubectl -n monitoring port-forward svc/prometheus-alertmanager 9093:9093 &
sleep 3
curl -sS http://localhost:9093/api/v2/alerts | jq
```

## Induce-failure smoke (cold-clone procedure)

Run when the Talos lab is up and no soak is in flight. Procedure:

```bash
export KUBECONFIG=$HOME/.kube/config-talos

# Pre-flight: confirm probe baseline
PROM_POD=$(kubectl -n monitoring get pod prometheus-prometheus-prometheus-0 -o name)
kubectl -n monitoring port-forward "$PROM_POD" 9090:9090 &
PROM_PF_PID=$!
sleep 3
curl -sS 'http://localhost:9090/api/v1/query?query=probe_success' | jq '.data.result[] | {instance:.metric.instance, value:.value[1]}'
# Expect: every probe `"1"`

# Induce failure: scale platform-api to 0
kubectl -n r55-platform scale deploy/platform-api --replicas=0
SCALE_T0=$(date -u +%s)

# Wait for alert to fire — `for: 5m` on BlackboxJourneyDown means ≥ 360 s
sleep 360
kubectl -n monitoring port-forward svc/prometheus-alertmanager 9093:9093 &
AM_PF_PID=$!
sleep 3
curl -sS http://localhost:9093/api/v2/alerts | jq '[.[] | select(.labels.alertname=="BlackboxJourneyDown" or .labels.alertname=="PlatformApiUnavailable") | {alertname:.labels.alertname, state:.status.state, startsAt:.startsAt}]'
# Expect: at least one firing state with startsAt ≈ SCALE_T0 + 30s..60s

# Restore
kubectl -n r55-platform scale deploy/platform-api --replicas=2
kill "$PROM_PF_PID" "$AM_PF_PID"
```

Pass criteria (cold-clone smoke):
- `probe_success{instance=~".*platform-api.*"}` reports `0` within ~30 s of the scale-to-zero
- `BlackboxJourneyDown` and/or `PlatformApiUnavailable` reach `firing` state within ~6 min (the 5 m `for:` window + scrape lag)
- After restoring replicas, both alerts return to `inactive` within one full scrape cycle (~30 s)

This is a lab procedure only; it does not need to be repeated per release. Re-run when alert-rule expressions, scrape intervals, or the PrometheusRule chart change in a way that could break the firing path.

## Passive verification (D-LK 24h soak — 2026-05-25 → 2026-05-26 on v2.5.4)

- ✅ All 5 ServiceMonitors `Available` for the entire 17h36m substantive run
- ✅ blackbox-exporter pod 1/1 Running, no restarts
- ✅ `BlackboxJourneyDown` alert rule loaded in PrometheusRule, never fired
- ✅ `probe_success == 1` continuously across all 5 targets (verified via Grafana → http://grafana.r55.local)
- ⏳ Induce-failure smoke: procedure above, unblocked / unscheduled — see opening status block
- ⏳ AlertManager delivery to a real receiver: no slack/email/webhook configured in the lab — Phase F follow-up (gated on customer-side endpoint provisioning)

## References

- Blackbox-exporter chart: kube-prometheus-stack via `infra/k8s/helm/observability/install.sh`
- Custom alert rules: `infra/k8s/manifests/prometheusrules-r55.yaml`
- Alert runbook (Docker baseline): `docs/operations/alerts-runbook.md`
- Phase B-LK envelope (baseline alerts thresholds): `docs/operations/load-test-baseline.md`
