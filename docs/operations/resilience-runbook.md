# Asterisk.Platform — Resilience Runbook

**Audience:** On-call operators + SRE. **Scope:** Operating, interpreting, and troubleshooting the `Asterisk.Sdk.Resilience` meter exposed by Platform API at `/metrics`.

Baseline of keyed policies and their budgets is in [`../../src/Asterisk.Platform.Api/Program.cs`](../../src/Asterisk.Platform.Api/Program.cs) (and per-package DI extensions for channel connectors / workers). This runbook stays deliberately provider-neutral — the same PromQL queries work against any OTLP → Prometheus backend.

---

## What the meter emits

Every keyed `ResiliencePolicy` registered in Platform emits to the **`Asterisk.Sdk.Resilience`** meter with the following instruments:

| Instrument | Type | Labels |
|---|---|---|
| `retry_attempts_total` | Counter | `policy` — attempts after the first (exhausted retries = budget retry count) |
| `circuit_opened_total` | Counter | `policy` — incremented each time circuit transitions `Closed → Open` |
| `circuit_closed_total` | Counter | `policy` — incremented each time circuit transitions `Open → Closed` (or `HalfOpen → Closed`) |
| `circuit_state` | Gauge | `policy`, `state` ∈ {`closed`,`open`,`half_open`} — 1 for active state, 0 otherwise |
| `timeout_fired_total` | Counter | `policy` — per-attempt timeout fired (not necessarily a user-visible failure) |

Policy keys follow Platform conventions:
- **External HTTP:** `webhook.delivery`, `smtp.send`, `oidc.token-exchange`, `channel.{provider}`, `flow.http-request`, `report.pdf-render`, `mail.graph`, `mail.token-refresh`, `storage.s3`
- **Background workers:** `worker.{name}` (see §"Worker policies" below)
- **Health checks:** `healthcheck.postgres`

---

## Golden signals

For each deployed environment, monitor:

1. **Retry rate** — `rate(asterisk_sdk_resilience_retry_attempts_total[5m]) > 0.5` per policy sustained ≥10 minutes = investigate upstream health.
2. **Circuit open events** — any `increase(asterisk_sdk_resilience_circuit_opened_total[15m]) > 0` is always worth a glance; sustained open circuits (5+ minutes) page.
3. **Current open circuits** — `sum(asterisk_sdk_resilience_circuit_state{state="open"}) > 0` — at least one policy is currently refusing traffic.
4. **Timeout firings** — `rate(asterisk_sdk_resilience_timeout_fired_total[5m])` per policy — spikes indicate downstream latency regression.

---

## Troubleshooting scenarios

### Scenario 1 — Retry storm on `channel.whatsapp`

**Symptom:** `rate(asterisk_sdk_resilience_retry_attempts_total{policy="channel.whatsapp"}[5m])` spikes to the retry budget ceiling.

**What it means:** WhatsApp Business API is returning transient errors (502/503/timeout). The policy retries 2×/500ms per attempt up to the circuit budget (5 failures / 60s cooldown). If retries exhaust, user-visible message sends fail.

**Checklist:**
1. Meta WhatsApp status page — broad outage?
2. Tenant-level rate limit hit? Grep logs for `WhatsAppConnector` + status code `429`.
3. Check if the circuit has tripped — `asterisk_sdk_resilience_circuit_state{policy="channel.whatsapp",state="open"} == 1`. If yes, outbound WA traffic is paused; wait 60s for half-open probe.
4. If the circuit is flapping (multiple `circuit_opened_total` increases in 15min) — upstream is intermittent; consider raising the circuit threshold temporarily via runtime config (not yet implemented — patch v1.10+).

### Scenario 2 — `storage.s3` circuit opens under sustained load

**Symptom:** `asterisk_sdk_resilience_circuit_opened_total{policy="storage.s3"}` increments during peak hours.

**What it means:** S3/MinIO is returning throttling or timeouts. The AWS SDK's built-in retry is disabled (`RetryMode.None`) by design in Platform — ResiliencePolicy owns retry — so this is a real capacity signal, not double-retry noise.

**Checklist:**
1. Check MinIO cluster CPU / IOPS / network. If self-hosted, scale out the MinIO cluster.
2. AWS S3: check CloudWatch for `5xxError` + `TotalRequestLatency` on your bucket.
3. If PUT traffic is the culprit (check media upload metrics), consider enabling S3 Transfer Acceleration or staging uploads through a CDN.
4. The policy budget is `circuit 5/60s + retry 3/500ms + timeout 30s` — timeout 30s is generous; verify no single Upload is stuck >30s (huge files may need tuned timeout).

### Scenario 3 — `healthcheck.postgres` fires timeouts

**Symptom:** `rate(asterisk_sdk_resilience_timeout_fired_total{policy="healthcheck.postgres"}[5m]) > 0` AND `/health/ready` returns Unhealthy/Degraded.

**What it means:** The Postgres health-check query is exceeding the 2-second policy timeout. Platform treats this as a signal that DB is overloaded (not necessarily unreachable).

**Checklist:**
1. `pg_stat_activity` — are there long-running queries blocking the health-check connection?
2. Connection pool exhaustion? Check `asterisk_sdk_pro_storage_common_retention_rows_purged` (if high, retention run may be holding connections).
3. Replication lag on a read replica if the health-check hits one.
4. Short-term: restart the affected Platform API instance (circuit self-recovers). Long-term: scale Postgres vertically OR introduce a dedicated health-check connection pool.

### Scenario 4 — Worker circuit stuck open (`worker.conversation-timeout` > 5min)

**Symptom:** `asterisk_sdk_resilience_circuit_state{policy="worker.conversation-timeout",state="open"} == 1` for ≥5 minutes.

**What it means:** `ConversationTimeoutWorker` has had 5 consecutive tick failures. Its tick loop skips work while circuit is open — conversations are NOT timing out automatically during this window. User visible: conversations that should auto-close on timeout remain Active.

**Checklist:**
1. Check error logs for the worker. Expected cause: `_conversationStore` unreachable (Postgres down) or `_switchboard` throwing on state transition.
2. Is `PostgresHealthCheck` also failing? If yes → root cause is DB, not worker.
3. If DB is healthy, inspect the last exception the policy caught — likely application-level bug.
4. Platform API restart will NOT help unless the underlying cause is resolved — circuit reopens immediately if tick fails again.
5. As a last resort, temporarily disable the worker (comment out `AddHostedService<ConversationTimeoutWorker>()` in Program.cs + redeploy) while investigating — but this risks SLA violations on auto-close; escalate.

### Scenario 5 — Interpreting cost-of-retries

A retry that succeeds is invisible to end-users but consumes resources. To quantify:

```promql
# Ratio of retried calls to total successful calls, per policy
sum(rate(asterisk_sdk_resilience_retry_attempts_total[1h])) by (policy)
/
# approximate success total: policy-wrapped ops ≈ Http client emitted requests minus retry_attempts
# Use downstream provider metric if available
...
```

For a clean ratio, the new v1.9.2+ release is planned to add `policy_executions_total` counter. For now, correlate retry_attempts_total vs upstream provider's request count.

---

## Worker policies reference

| Policy | Budget | Tick cadence | Silent-swallow before v1.9.1? |
|---|---|---|---|
| `worker.conversation-timeout` | c5/60s + r2/500ms + t10s | 5s | yes |
| `worker.queue-distribution` | c5/60s + r2/500ms + t10s | variable | yes |
| `worker.dunning` | c3/600s + r1/5s + t60s | 1h | yes |
| `worker.report-scheduler` | c5/60s + r2/500ms + t10s | 60s | yes |
| `worker.bot-analytics-persistence` | c3/120s + r1/2s + t20s | variable | yes |
| `worker.asterisk-capacity-sync` | c5/60s + r2/500ms + t10s | 30s | yes |
| `worker.retention-purge` | c3/120s + r1/2s + t20s | daily | yes |
| `worker.audit-retention` | c3/120s + r1/2s + t20s | daily | yes |
| `worker.realtime-state-bridge` | c5/60s + r2/500ms + t10s | event-driven | yes |
| `worker.campaign-metrics-poller` | c5/60s + r2/500ms + t10s | variable | no (explicit logging) |
| `worker.agent-assist-bridge` | c5/60s + r2/500ms + t10s | event-driven | yes |
| `worker.timer-polling` | c5/60s + r2/500ms + t10s | 1s | yes |

"Silent-swallow before v1.9.1" = the worker used to swallow exceptions without logging or metrics. After v1.9.1, all of these emit structured warnings + retry metrics.

---

## Cross-instance considerations

Circuit state is **per-process** — a 3-replica Platform API deployment has 3 independent circuits per policy key. This is by design (each instance protects itself). Implications:

- A circuit-open alert for `channel.whatsapp` on one replica doesn't necessarily mean others are broken — inspect per-instance.
- For tenant-facing operations, a failing replica causes load to shift to healthy ones; Kubernetes readiness probe (`/health/ready`) should remove a replica whose aggregate worker circuits are open >5min.
- There is currently NO cross-instance policy shared state. A future release (v2.x) may add Redis-backed distributed circuit tracking via Pro.Push backplane.

---

## Grafana dashboard

A starter dashboard is provided at [`dashboards/resilience-overview.json`](dashboards/resilience-overview.json). Import via Grafana UI → Dashboard → Import → upload JSON. Assumes a Prometheus datasource scraping the Platform `/metrics` endpoint every 15s.

Panels included:
1. Current open circuits (per policy, table)
2. Retry rate heatmap (top 10 policies by retry volume)
3. Circuit opened/closed events (time series)
4. Timeout firing rate (per policy)
5. Per-tenant breakdown (if your scrape config attaches `tenant_id` externalLabel)

---

## Testing against Asterisk 23 Standard

The `docker/Dockerfile.asterisk` image accepts an `ASTERISK_VERSION` build arg (default `22`). To build and run with Asterisk 23 Standard instead of 22 LTS:

```bash
# Build only
ASTERISK_VERSION=23 docker compose -f docker/docker-compose.full.yml build asterisk

# Build and start the full stack with Asterisk 23
ASTERISK_VERSION=23 docker compose -f docker/docker-compose.full.yml up
```

When `ASTERISK_VERSION` is not set, compose defaults to `22` (Asterisk 22 LTS) — no change to existing workflows. The codec_opus download URL and unpacked directory name are both derived from `ASTERISK_VERSION` automatically.
