# Alerts Runbook — Asterisk.Platform

**R5.4 Track A · S5.3** · Authored 2026-04-26 · Audience: On-call operators + SRE

Companion to [`alerts.yml`](alerts.yml). Every alert in `alerts.yml` has an
entry below following the same `### <AlertName>` format with **What** / **Why**
/ **First response** sections.

---

## Severity model (per [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md))

| Tier | Operator action | Examples |
|---|---|---|
| **P0** | **Page on-call.** Customer impact or outage imminent. SLA clock running. | API down, JWT validation failing, license guard blocking pipeline, retention service stalled. |
| **P1** | **Ticket within 24h.** SLO breach, no customer-visible outage. | Circuit open > 5 min, SLO breach without P0 trigger, audit pipeline degraded. |
| **P2** | **Review weekly.** Capacity / hygiene. Aggregate during weekly SRE sync. | Pool > 80%, slow queries, retention divergence. |

---

## Threshold provenance — IMPORTANT

> All thresholds in `alerts.yml` are **v1 provisional estimates** derived from
> architecture review + .NET 10 AOT benchmarks + standard CCaaS industry
> ranges. **They have NOT been validated against measured load tests** — the
> S5.1 NBomber suite is shipped (`tests/Asterisk.Platform.LoadTests/`) but the
> first authoritative run on staging is pending.
>
> Confidence in these thresholds will increase after:
>
> 1. The S5.1 baseline run completes on staging (`./scripts/load-test.sh`).
> 2. The first quarter of production observability shows real per-tenant load
>    distributions.
>
> Until then, treat any P0/P1 firing as a **signal worth investigating**, not as
> proof that the underlying SLO is genuinely breached. Adjust via PR with
> operator approval — see the "How to refresh" section in [`slos.md`](slos.md).

---

## P0 — page on-call

### PlatformApiUnavailable

**What:** Prometheus scrape of the `platform-api` job fails (`up == 0`) for ≥ 2 minutes.

**Why:** No instances are responding to `/metrics`. Customer-facing API is down (or the entire scraping topology has broken — verify Prometheus before assuming the API).

**First response:**
- Verify platform-api containers/pods running: `docker ps` / `kubectl get pods -l app=platform-api`.
- Tail container logs for crash loops, `Program.cs` startup exceptions.
- Check `aspnetcore_healthcheck_status` for the same instance — distinguishes "process up but failing health" from "process down".
- If multi-replica, check load balancer health: a single unhealthy replica should drain, not page (verify this alert isn't firing per-replica before declaring incident).
- Escalate to platform owner if log shows DataProtection key store unreachable or Postgres connectivity gone.

### AuthLoginErrorRateHigh

**What:** ≥ 5% of `/auth/login` requests returning 5xx for ≥ 5 minutes.

**Why:** Identity store, JWT signing key store, or upstream OIDC provider unhealthy. Real users cannot log in.

**First response:**
- Check Postgres `users` + `tenants` table accessibility (PostgresHealthCheck).
- Tail platform-api logs filtered for `JwtBearerHandler` / `IdentityCookieAuthenticationHandler` exceptions.
- Verify DataProtection key store (DB-backed since R5.2) — query the `data_protection_keys` table.
- If OIDC is in play, check `oidc.token-exchange` resilience policy state.
- Roll back recent Identity-related deployments if suspected regression.

### JwtValidationLatencyP99High

**What:** JWT key rotation duration p99 > 500 ms over 5 min (proxy for end-to-end JWT health until per-validation histogram lands in v1.13.x).

**Why:** Per ADR-0009 P0 trigger. JWT validation latency above 500 ms causes downstream auth handshakes to timeout, cascading to user-visible login failures + WebSocket disconnects.

**First response:**
- Check Postgres health — `IJwtKeyStore` is DB-backed.
- Inspect `jwt_keys_active` gauge — should be 1–4. Excessive count (> 10) means rotation cleanup is broken; zero means rotation has never run.
- Restart Platform API instance under load to clear in-process caches if the spike followed a deploy.
- File P1 ticket to add per-validation histogram (`jwt.validation.duration`) so this alert can shift from rotation-proxy to direct.

### LicenseGuardBlockedHigh

**What:** `Asterisk.Sdk.Pro.Licensing.Guard.blocked_total` rate / pipeline-events rate > 1% over 10 min.

**Why:** License grace period expired (7d default per `Pro.Licensing` v1.8.0), feature toggle revoked, or `ILicenseGuard` itself is misconfigured. Pipelines (EventStore / Analytics / AgentAssist) silently drop work — no errors visible to API callers.

**First response:**
- Check license expiry: `GET /admin/license` (Platform Admin role).
- If grace remaining > 0, check `grace_remaining_seconds` gauge — runway before hard stop.
- If grace expired, contact account owner; emergency grace extension via license re-issue.
- Verify `Pro.Licensing` config (`Asterisk__Pro__Licensing__PublicKey` env var) hasn't been mutated.

### RetentionServiceStalled

**What:** No `retention_duration_ms_count` increment in the last 1h AND retention service has been alive > 1h since startup.

**Why:** Per ADR-0009 P0 trigger. Nightly cron didn't fire (BackgroundService crashed, cron expression invalid, license guard blocked, deadlock holding the lock). Storage growth becomes unbounded — Postgres will eventually fill disk.

**First response:**
- Check the `retention` health check entry in `/health/ready`.
- Tail platform-api logs grep `RetentionService`.
- Verify cron expression in `Asterisk__Pro__Retention__CronExpression` (default `0 2 * * *`).
- Manually trigger retention via maintenance endpoint or restart the BackgroundService.
- Check disk free on Postgres host — if already < 20%, file P0 storage ticket immediately.

---

### NodeDiskSpaceLow

**What:** `node_filesystem_avail_bytes / node_filesystem_size_bytes` for `mountpoint="/"` (excluding tmpfs/overlay) drops below 10% sustained 5min.

**Why:** Every stateful service shares the host root fs (Postgres data, Redis AOF, Prometheus TSDB, Loki chunks, Docker overlay/build cache). Sustained low-space → Postgres checkpointer cannot write WAL/checkpoint files → `PANIC: could not write to file ... No space left on device` → crash loop. Postgres `pg_isready` healthcheck reports "healthy" because the listener stays up while the postmaster cycles — this alert is the only signal you'll get before silent data plane outage.

**Origin:** R5.5 D-L incident 2026-04-28 — Docker build cache silently grew to 107 GB over 15 days of CI rebuilds, driving root fs to 100% during a 50-min smoke soak. Postgres entered crash loop; Platform.Api login returned `57P03 recovery mode` after 5 successful soak steps.

**First response:**
- Run `df -h /` to confirm.
- Run `docker system df` — top suspect ranking: build cache > unused images > orphan volumes.
- Run `docker builder prune -f` (recovers build cache; non-destructive — does not touch images, containers, or named volumes).
- If still tight, `docker image prune -a -f` (removes images with no running container; will require re-pull).
- Verify `/etc/docker/daemon.json` has builder GC cap configured:
  ```json
  { "builder": { "gc": { "enabled": true, "defaultKeepStorage": "20GB" } } }
  ```
- If cap missing, apply it + `sudo systemctl restart docker` (containers with `restart: unless-stopped` auto-recover; observability stack needs `docker compose up -d` since its restart policy is `no`).
- Check Postgres logs (`docker logs docker-postgres-1 --tail 50`) for `PANIC ... No space left on device` — if present, file follow-up to verify checkpointer recovered cleanly.

---

## P1 — ticket within 24h

### SloBreachQueueIngestion

**What:** EventStore `eventstore.persist.duration_ms` p99 > 200 ms (SLO target per `slos.md §2`) sustained 15 min.

**Why:** Queue ingestion SLO breach. Postgres write pressure, full WAL, or Pro.EventStore subscriber backlog.

**First response:**
- Check `eventstore_subscriber_inflight` gauge — high value indicates consumer cannot keep up.
- Check Postgres `pg_stat_activity` for long-running queries blocking writes.
- Verify retention not overlapping with ingestion peak (move cron if it does).
- Verify session_events table partitioning healthy — check most recent partition timestamp.

### CircuitBreakerOpen

**What:** Any keyed `Asterisk.Sdk.Resilience.circuit.state` value 2 (Open) for ≥ 5 min.

**Why:** A downstream dependency is failing repeatedly and the policy is now rejecting all traffic to give it room to recover.

**First response:**
- Cross-reference policy key against [`resilience-runbook.md`](resilience-runbook.md) policy catalogue.
- For `channel.*` keys: check provider status pages (Twilio/Meta/etc).
- For `webhook.delivery`: customer webhook endpoint likely down — review per-tenant webhook stats in admin UI.
- For `worker.*`: BackgroundService internal failure — check logs for the worker name.
- Wait one cooldown cycle (typically 30–60 s) for half-open probe; circuit will auto-close if probe succeeds.

### PresenceBacklogGrowing

**What:** `push_postgres_backlog` increased by > 100 over 15 min AND current value > 500.

**Why:** Pro.Push.Postgres relay cannot drain LISTEN/NOTIFY events as fast as publishers emit. Presence merges + cluster bridge events lag, leading to stale presence in the UI.

**First response:**
- Check `push_postgres_listen_healthy` — if 0, the LISTEN connection dropped.
- Check Postgres `pg_listening_channels` — confirm the relay is actually subscribed.
- Restart the relay BackgroundService on one node first; confirm backlog drains before rolling.
- If chronic, scale relay nodes horizontally or move to Pro.Push.Redis backplane.

### AuditWriteLatencyP99High

**What:** HTTP p99 on `/admin/audit.*` routes > 1 s over 15 min (proxy until dedicated `audit.write.duration_ms` histogram ships in v1.13.x).

**Why:** Audit pipeline degraded. Compliance evidence (SOC 2, R5.2 hardening) lags real events. `IHubAuditSink` implementation may be slow or saturating its underlying store.

**First response:**
- Check the audit sink target (Postgres `audit_entries` table by default) — query `pg_stat_user_tables` for that relation.
- Verify `audit_entries` indexes intact + not bloated.
- If pipeline backed by external SIEM, check connectivity to that SIEM.
- File P2 follow-up ticket to ship the dedicated histogram instrument.

### HealthCheckUnhealthy

**What:** Any health check exposed via `aspnetcore_healthcheck_status` reports value 0 (Unhealthy) for ≥ 5 min.

**Why:** A registered `IHealthCheck` (postgres / asterisk-ami / dialer-engine / eventstore-subscriber / analytics-aggregator / agentassist-engine / callanalytics-engine / analytics-live-queue-writer / presence-heartbeat / fanout / merge / retention) reports a problem.

**First response:**
- Look up the health check by `name` label.
- Curl `/health/ready` against the affected instance to see structured `HealthReportJsonWriter` output (R3b shipped).
- Cross-reference with related P0/P1 alerts firing on the same instance.
- If `postgres` health check fails, escalate to P0 immediately even if no other alert has fired yet.

---

## P2 — review weekly

### PgConnectionPoolHigh

**What:** Sum of used Postgres connections / max-pool > 80% sustained 30 min.

**Why:** Capacity warning. Either app-side connection leak (suspect: hand-rolled `NpgsqlConnection` outside DI scope) or genuine load growth.

**First response:**
- Compare against historical baseline (last week same time).
- Check Npgsql `MaxPoolSize` setting in connection string vs Postgres `max_connections`.
- Search code for `new NpgsqlConnection(` outside the standard `IDbConnectionFactory` to find leaks.
- If genuine load: schedule pool bump in next weekly maintenance window.

### SlowQueriesHigh

**What:** > 1% of Postgres queries take > 500 ms over 30 min.

**Why:** Capacity / index health warning. Likely correlated with retention runs, live queue writer bursts, or new query patterns from a recent deploy.

**First response:**
- Query `pg_stat_statements` ordered by `mean_exec_time DESC LIMIT 20`.
- Cross-reference with most recent deploy diff.
- Check that `auto_vacuum` ran recently on hot tables (`session_events`, `live_queue_snapshots`).
- Add missing indexes via migration in next maintenance window.

### RetentionDryRunDivergence

**What:** `retention_purged_total` and `retention_dry_run_would_purge_total` for the same target diverge by > 100 rows over 1 day.

**Why:** Either `DryRun` flag was flipped silently (deploy or env var change), or the retention window definitions are inconsistent between targets.

**First response:**
- Check Pro retention config — `AddProRetention(opt => opt.DryRun = ?)` value in `Program.cs`.
- Verify all `IRetentionTarget` implementations honour the same `Cutoff` semantics (default = `UtcNow - Window`).
- If divergence is intentional (operator just flipped DryRun off), suppress alert for 7 days with rationale.

### LiveQueueWriterErrorWindow

**What:** `live_queue_snapshots_write_error_total` rate / `live_queue_snapshots_published_total` rate > 1% over 30 min.

**Why:** SLO breach for the live queue writer (target < 1% per `slos.md §4`). Causes: Postgres timeout, circuit_open on the keyed `analytics.live-queue-writer` policy, or unexpected exception in upsert.

**First response:**
- Inspect `live_queue.snapshots.write_error` counter `reason` tag — narrows to `timeout` / `circuit_open` / `exception`.
- Check `analytics-live-queue-writer` health check status.
- For `circuit_open`: cross-reference with CircuitBreakerOpen P1 alert.
- For `exception`: tail platform-api logs grep `LiveQueueSnapshotWriter`.

### RedisMemoryHigh

**What:** Redis memory utilization > 75% (used/max) sustained 1h.

**Why:** Capacity warning. JTI revocation cache (Identity.Redis from R5.1) + presence backplane share the instance. When the instance approaches `maxmemory`, eviction policy kicks in and may drop revocation entries — a security-relevant event.

**First response:**
- Check `redis-cli INFO memory` to see current vs max + `mem_fragmentation_ratio`.
- Check `redis-cli INFO keyspace` to see which DB / key prefix is dominating.
- Verify `maxmemory-policy` is set to `volatile-lru` (not `allkeys-lru` — JTI keys must NOT be evictable without explicit revocation).
- Schedule instance resize or shard split in next maintenance window if growth is chronic.

---

## See also

- [`slos.md`](slos.md) — SLO targets these alerts derive from
- [`resilience-runbook.md`](resilience-runbook.md) — Resilience meter detail + per-policy catalogue
- [`load-test-baseline.md`](load-test-baseline.md) — S5.1 results that will refresh both `slos.md` + `alerts.yml`
- [ADR-0009](../decisions/0009-slo-baseline-alert-severity-model.md) — Decision context
