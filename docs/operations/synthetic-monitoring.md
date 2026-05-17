# Synthetic monitoring — R5.5 Phase E-LK

**Status:** deployed + verified passively (induce-failure validation deferred until after D-LK 24h soak completes — scaling platform-api to 0 mid-soak would invalidate the soak run).

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

## Induce-failure validation — DEFERRED

The R5.5 plan's E-LK Step 2 calls for:

```bash
kubectl -n r55-platform scale deploy/platform-api --replicas=0
sleep 360
# Verify BlackboxJourneyDown alert fires in AlertManager
kubectl -n r55-platform scale deploy/platform-api --replicas=2
```

This is **NOT executed during D-LK soak in flight**. Scaling platform-api to 0 mid-soak would falsify the soak data. Scheduled for **post-D-LK** (after T+24h on 2026-05-18 04:36 UTC) — see Phase F follow-up tracker.

Expected behavior when the validation runs:
- After ~30 s, `probe_success{instance=~".*platform-api.*"}` reports 0
- After 5 minutes (default `for: 5m` on critical alerts), `BlackboxJourneyDown` + `PlatformApiUnavailable` transition to `firing` state in AlertManager
- AlertManager routes (per `alertmanagerconfigs` CRD if any) deliver notifications

## Passive verification done (E-LK PASS criteria for now)

- ✅ All 5 ServiceMonitors `Available`
- ✅ blackbox-exporter pod 1/1 Running
- ✅ `BlackboxJourneyDown` alert rule loaded in PrometheusRule
- ✅ Probe targets visible in Prometheus targets list (verified via Grafana → http://grafana.r55.local)
- ⏳ Induce-failure validation: deferred to post-D-LK
- ⏳ AlertManager delivery (slack/email/webhook): no receivers configured in this lab — Phase F follow-up

## References

- Blackbox-exporter chart: kube-prometheus-stack via `infra/k8s/helm/observability/install.sh`
- Custom alert rules: `infra/k8s/manifests/prometheusrules-r55.yaml`
- Alert runbook (Docker baseline): `docs/operations/alerts-runbook.md`
- Phase B-LK envelope (baseline alerts thresholds): `docs/operations/load-test-baseline.md`
