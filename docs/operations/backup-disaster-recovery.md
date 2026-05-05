# Backup & Disaster Recovery Runbook

**Audience:** SRE / on-call operator (assumed unfamiliar with the codebase).
**Scope:** Verbara.Platform stack — Postgres (single primary, optional WAL archive to S3), Redis (RDB + AOF), Platform.Api (HA via Pro.Cluster nodes — see R5.1 ship notes), Asterisk PBX nodes.
**SLO:** Recovery Time Objective (RTO) **30 min**, Recovery Point Objective (RPO) **5 min** (PITR enabled) / **15 min** (Redis RDB).

This runbook covers:

1. Postgres backup strategy — daily full + continuous WAL archive (PITR).
2. Redis snapshot strategy — RDB + AOF.
3. Recovery scenarios — disk failure, accidental DELETE, node failover.
4. Monthly chaos test exercise — cadence + procedure + per-exercise template.

> **Companion scripts** (in `scripts/`):
> - `backup-pg.sh` — daily Postgres pg_dump + optional S3 upload + 30-day local retention
> - `restore-pg.sh` — full restore from a pg_dump backup file
> - `backup-redis.sh` — BGSAVE trigger + dump.rdb copy + optional S3 upload
>
> Companion exercise log: `docs/operations/dr-exercises.md`.

---

## 1. Postgres backup strategy

The Platform Postgres database holds tenant data, identity, billing, audit log, EventStore (session_events + completed_sessions), Analytics interval snapshots, AgentAssist sessions, CallAnalytics results, Cluster registry, and Realtime endpoint config. Loss is **catastrophic** if not recoverable.

### 1.1 Backup tiers

| Tier | Mechanism | Cadence | Retention | RPO contribution |
|------|-----------|---------|-----------|------------------|
| **Daily full** | `pg_dump -F custom -Z 9` via `scripts/backup-pg.sh` | Daily 02:00 UTC (cron) | 30 days local + 90 days S3 | 24h (without WAL) |
| **Continuous WAL archive** | `archive_command` writes WAL segments to S3 | Every 5 min or 16 MB segment | 14 days S3 | **5 min** (with PITR) |

> The cron runs `scripts/backup-pg.sh`; if `S3_BUCKET` is set, the script also uploads to S3 (`s3://$S3_BUCKET/pg/<filename>`). Retention: `find -mtime +30 -delete` for local files. S3 lifecycle policy (set once, IaC) handles the 90-day retention.

### 1.2 WAL archive setup (one-time, infra)

Required for PITR (Point-In-Time Recovery). Set once in `postgresql.conf`:

```
wal_level = replica
archive_mode = on
archive_command = 'aws s3 cp %p s3://YOUR_BUCKET/wal/%f --quiet'
archive_timeout = 300        # force WAL switch every 5 min (caps RPO)
```

Restart Postgres after change. Verify:

```bash
psql -c "SHOW archive_mode;"      # must report 'on'
psql -c "SELECT pg_switch_wal();"  # forces a WAL flush; check S3 bucket
aws s3 ls s3://YOUR_BUCKET/wal/ --recursive | tail -3
```

If WAL archive is **not** configured, drop to **24h RPO** (daily full only) and document it in your acceptance.

### 1.3 Daily full backup — what happens

Run via cron (host crontab or k8s `CronJob`):

```cron
0 2 * * *  /opt/verbara-platform/scripts/backup-pg.sh >> /var/log/verbara-platform/backup-pg.log 2>&1
```

The script:

1. Builds filename `${PG_DB}-YYYYMMDD-HHMMSS.dump`.
2. Runs `pg_dump -F custom -Z 9` (compressed binary format, ~70-90% size reduction vs plain SQL).
3. Optionally uploads to S3 if `S3_BUCKET` env var is set.
4. Deletes local files older than 30 days.
5. Logs to stdout (cron emails on non-zero exit).

Verify daily: `ls -lh /var/backups/pg/*.dump | tail -3` should show file from past 24h, size should be roughly stable day-over-day (sudden drop = alert).

### 1.4 Restore — full restore (disk loss)

**When:** Postgres data directory is gone (disk failure, host loss, cluster rebuild). Acceptable to lose data after the most recent backup if WAL is also lost.

**RTO:** ~10-20 min for a database under 10 GB compressed, longer for larger.

**Procedure:**

1. **Provision new Postgres host / volume.** Same major version. Verify `psql --version` matches source.
2. **Fetch latest backup.**
   ```bash
   # Local copy if available:
   ls -lt /var/backups/pg/*.dump | head -1

   # Or pull from S3 (latest):
   LATEST=$(aws s3 ls s3://YOUR_BUCKET/pg/ | sort | tail -1 | awk '{print $4}')
   aws s3 cp "s3://YOUR_BUCKET/pg/$LATEST" /tmp/restore.dump
   ```
3. **Stop Platform.Api** (and any other writer) so no client writes during restore:
   ```bash
   systemctl stop verbara-platform-api
   # or in k8s: kubectl scale deployment/platform-api --replicas=0
   ```
4. **Run restore script:**
   ```bash
   PG_HOST=newhost PG_USER=postgres PG_DB=asterisk_platform \
     ./scripts/restore-pg.sh /tmp/restore.dump
   ```
   The script will prompt for the database name as a safety confirmation, then DROP + CREATE the database and `pg_restore --no-owner --no-acl`.
5. **Verify schema + row counts:**
   ```bash
   psql -h newhost -U postgres -d asterisk_platform -c "\dt"
   psql -h newhost -U postgres -d asterisk_platform \
     -c "SELECT 'users' AS t, COUNT(*) FROM users
         UNION ALL SELECT 'tenants', COUNT(*) FROM tenants
         UNION ALL SELECT 'session_events', COUNT(*) FROM session_events;"
   ```
6. **Re-point Platform.Api** at new host (update `ConnectionStrings__Default`) and restart:
   ```bash
   systemctl start verbara-platform-api
   curl -fsS http://localhost:8080/health/ready  # must be 200
   ```
7. **Smoke-test:** log in as a known user, list tenants, place a test call. Confirm CDR row appears in `completed_sessions`.

### 1.5 Restore — PITR (corruption / accidental delete)

**When:** Database is intact but a specific bad event happened at known time T (operator deleted wrong tenant, bad migration, ransomware encryption). You want to restore to time T - 1 minute.

**Pre-req:** WAL archive enabled (section 1.2) **and** the daily backup at or before T is available.

**RTO:** 20-40 min depending on data volume + WAL replay distance.

**Procedure:**

1. **Identify target time:**
   ```
   TARGET_TIME='2026-04-26 13:42:00 UTC'
   ```
2. **Identify base backup taken before T:** in S3 `pg/`, pick the most recent `.dump` whose filename timestamp is **before** `TARGET_TIME`.
3. **Provision a fresh Postgres instance** (do **not** touch the live one yet — restore to a parallel instance, validate, then cut over).
4. **Restore base:** same as section 1.4 steps 2-5, against the parallel instance.
5. **Configure recovery on the parallel instance:** create `recovery.signal` file in data dir + add to `postgresql.auto.conf`:
   ```
   restore_command = 'aws s3 cp s3://YOUR_BUCKET/wal/%f %p'
   recovery_target_time = '2026-04-26 13:42:00 UTC'
   recovery_target_action = 'pause'
   ```
6. **Start Postgres** — it replays WAL until `recovery_target_time`, then pauses.
7. **Verify the restored data:**
   ```sql
   SELECT * FROM tenants WHERE id = 'the-deleted-one';
   SELECT MAX(created_at) FROM session_events;  -- should be ~ TARGET_TIME
   ```
8. **If satisfied**, promote: `psql -c "SELECT pg_wal_replay_resume(); SELECT pg_promote();"` then run `pg_dump` on parallel instance and restore into production (section 1.4) **or** swap connection strings to the parallel instance and decommission the original.
9. **If unsatisfied** (target time wrong), edit `recovery_target_time`, restart Postgres, re-verify.

**Safety note:** never run PITR directly on the live primary. Always use a parallel instance, validate, then swap.

---

## 2. Redis snapshot strategy

Redis holds: SignalR connection state (Pro.Push.SignalR), JTI revocation cache (Identity.Redis from R5.1), license guard cache, presence CRDT, push backplane buffers, rate-limit counters.

**Loss tolerance:** higher than Postgres — most Redis state can be **rebuilt from Postgres** within minutes. JTI revocation cache rebuild has a known security implication (see 2.4).

### 2.1 Persistence config

Set in `redis.conf` (or via `CONFIG SET` for runtime):

```
# RDB snapshots
save 900 1        # snapshot if ≥1 key changed in 15 min  → 15 min RPO worst-case
save 300 10       # snapshot if ≥10 keys changed in 5 min
save 60 10000     # snapshot if ≥10000 keys changed in 1 min
dbfilename dump.rdb
dir /var/lib/redis

# AOF (append-only file) — survives mid-snapshot crash
appendonly yes
appendfsync everysec      # fsync every 1s — RPO = 1s for last-second writes
auto-aof-rewrite-percentage 100
auto-aof-rewrite-min-size 64mb
```

With both enabled, Redis on restart prefers AOF (more recent). RDB is the snapshot artefact you ship to S3.

### 2.2 BGSAVE-driven snapshot via script

Cron entry:

```cron
*/15 * * * *  /opt/verbara-platform/scripts/backup-redis.sh >> /var/log/verbara-platform/backup-redis.log 2>&1
```

The script triggers `BGSAVE`, waits for `LASTSAVE` timestamp to advance (confirms snapshot completed), copies `dump.rdb` (Docker variant via `docker cp` or local variant via `cp`), and optionally uploads to S3.

### 2.3 Restore — Redis

**When:** Redis instance lost or corrupted.

**Procedure:**

1. Stop Redis: `systemctl stop redis` (or `docker stop redis`).
2. Replace `/var/lib/redis/dump.rdb` with the latest snapshot:
   ```bash
   aws s3 cp s3://YOUR_BUCKET/redis/dump-latest.rdb /var/lib/redis/dump.rdb
   chown redis:redis /var/lib/redis/dump.rdb
   ```
3. **Optionally** also fetch latest `appendonly.aof` if you care about the last 15-min window. For most scenarios the RDB is sufficient.
4. Start Redis: `systemctl start redis`. Check log line `DB loaded from disk`.
5. Verify: `redis-cli DBSIZE` should be non-zero and roughly match historic baseline.

### 2.4 JTI revocation cache rebuild — SECURITY NOTE

The Identity.Redis JTI cache (R5.1) tracks revoked JWTs. **A restored Redis from a 15-min-old snapshot will not contain JWTs revoked in the last 15 min.** A revoked-but-now-restored token can be used until natural JWT expiry.

**Mitigation after Redis restore:**

1. **Force-rotate the JWT signing key** (via `Verbara.Platform.Identity` admin endpoint or config secret rotation). All existing JWTs become invalid; users must re-authenticate. This is the strongest mitigation.
2. **Or lower JWT TTL temporarily** to 5 min for the next 24h to bound exposure window. Less disruptive but partial.
3. **Audit the JTI table in Postgres** (if persisted) — any JTI explicitly revoked in `auth_token_revocations` should be re-pushed into Redis on startup. This is an existing startup-warmup hook in Identity.Redis (R5.1 spec).

Document in the DR exercise log which mitigation was chosen.

---

## 3. Recovery scenarios

### 3.1 Single Postgres disk failure — primary lost

**Symptoms:** Platform.Api `/health/ready` returns 503 with `database connectivity failed`. Postgres host unreachable.

**Action:** Section 1.4 (full restore) on a new host. Expect ~20 min RTO. Loss bounded by latest backup (24h without WAL, 5 min with WAL + PITR replay to last archived WAL).

### 3.2 Accidental DELETE / bad migration / ransomware

**Symptoms:** Specific rows missing, incorrect data in tables, schema unexpectedly altered. Platform.Api may be functional but serving wrong data. **Do not panic-restart.**

**Action:** Section 1.5 (PITR) to a point 1 minute before the bad event. Validate on parallel instance before swapping. Do **not** restore in-place on production primary.

### 3.3 Platform.Api node failover

**Symptoms:** One Pro.Cluster node `/health/ready` reports `unhealthy` for >30s. Other nodes still healthy.

**Action:**

1. Confirm via `kubectl get pods -l app=platform-api` (or `systemctl status` per host).
2. Cluster routing (Pro.Cluster, R5.1 hardening) should already drain traffic from the bad node automatically. Verify via Grafana `cluster.node.healthy` gauge.
3. Restart the failed node: `kubectl rollout restart deployment/platform-api` for the affected pod, or `systemctl restart verbara-platform-api` on host.
4. Confirm node rejoins: `cluster.node.healthy{node_id="X"}=1` within 60s.
5. **No data restore needed** — node failover is stateless because all persistent state lives in Postgres + Redis.

### 3.4 Redis loss — full DR scenario

**Symptoms:** Redis unreachable. Symptoms in Platform.Api: SignalR clients drop and fail to reconnect (no presence), JWT revocation checks bypass (silent — see 2.4 security note), license cache misses (slower auth checks).

**Action:** Section 2.3 (restore from snapshot) + section 2.4 (JTI cache rebuild + key rotation).

### 3.5 Asterisk PBX node loss

Out of scope of this runbook — refer to Asterisk-specific runbook (`docs/operations/asterisk-pbx-runbook.md` if present, or the upstream Asterisk admin guide). Pro.Cluster + Pro.Realtime endpoint sync should re-provision PJSIP endpoints automatically when a replacement Asterisk node joins.

---

## 4. Monthly chaos test exercise

**Why:** A backup that has never been restored is not a backup. A runbook that has never been executed is not a runbook. This exercise verifies both, monthly, on staging — never production.

### 4.1 Cadence

- **First Monday of every month, 14:00 UTC.**
- **Environment:** staging only. Never production.
- **Duration:** 60 min budget (30 min target RTO + 30 min validation).
- **Required attendance:** primary on-call + one shadow operator (knowledge transfer).
- **Output:** appended entry in `docs/operations/dr-exercises.md` (template below).

### 4.2 Procedure (6 steps)

1. **Pick a scenario at random** from §3 (use `shuf -n1 -e 'disk-loss' 'pitr-corruption' 'redis-loss' 'node-failover'`). Document the picked scenario.
2. **Snapshot pre-state:** `pg_dump` of staging Postgres + `redis-cli BGSAVE` + capture row counts of 3-5 representative tables (`tenants`, `users`, `session_events`, `completed_sessions`, `audit_entries`). Save to a local `/tmp/dr-pre-<date>` directory.
3. **Inject the failure:**
   - `disk-loss`: `docker stop staging-postgres && docker rm staging-postgres && docker volume rm staging-postgres-data`
   - `pitr-corruption`: `psql -c "DELETE FROM tenants WHERE id = 'staging-test-tenant';"` and note timestamp T.
   - `redis-loss`: `docker stop staging-redis && docker rm staging-redis && docker volume rm staging-redis-data`
   - `node-failover`: `kubectl delete pod platform-api-<one-of-three>` (or `systemctl stop` on one host).
4. **Recover** following the matching section in §3. Use only this runbook + scripts — no improvisation, no Slack help. The point is to exercise the documented procedure and discover gaps.
5. **Validate post-recovery:**
   - Row counts match pre-state (within tolerance for active staging traffic).
   - Platform.Api `/health/ready` returns 200.
   - A test login + list tenants + place a synthetic call all succeed.
   - Grafana shows `up{job="platform-api"}=1` across all expected nodes.
6. **Log the exercise** in `docs/operations/dr-exercises.md` using the template (§4.3). Include timestamps, duration vs 30 min target, issues encountered, improvements identified, sign-off.

### 4.3 Per-exercise template

Append a block to `docs/operations/dr-exercises.md` of this exact shape:

| Field | Example |
|-------|---------|
| **Date** | 2026-05-04 |
| **Scenario** | disk-loss |
| **Started** | 14:02 UTC |
| **Backup source** | `asterisk_platform-20260503-020014.dump` (S3: `s3://prod-backups/pg/...`) |
| **Recovery completed** | 14:18 UTC |
| **Total duration** | 16 min |
| **Target** | < 30 min |
| **Met target** | yes |
| **Issues encountered** | `restore-pg.sh` confirmation prompt blocked cron-style automation — operator typed db name manually, fine for manual DR but document this. |
| **Improvements identified** | Add `--force` flag to `restore-pg.sh` for use under explicit operator-supervised automation. |
| **Sign-off** | jdoe — 2026-05-04 |

### 4.4 Exercise success criteria

An exercise is **successful** when:

- RTO target (30 min) met for the chosen scenario.
- Data loss within RPO (5 min with WAL / 15 min for Redis).
- Validation step (§4.2.5) passes without manual data fix-ups.
- Sign-off appended within 24h to `dr-exercises.md`.

An exercise that **fails any** of the above triggers a follow-up plan: open a ticket, prioritise within current release train, re-run within 14 days.

---

## 5. Out of scope (explicit deferrals)

- **Multi-region failover** — no warm-standby in another region today. Single-region recovery only.
- **Postgres streaming replication / hot standby** — would lower RTO to seconds. Deferred to v2.0 (architectural change). For now, the daily-full + WAL-archive PITR pattern is the documented strategy.
- **Redis cluster / Sentinel** — single-instance Redis with snapshot recovery. Sentinel HA deferred (no concrete demand).
- **Backup encryption at rest** — assumed to be handled by S3 SSE (server-side encryption) and on-disk encryption at the OS level. Per-backup encryption envelope (gpg) deferred to compliance pass.
- **Backup integrity verification** — currently the monthly chaos test is the only end-to-end verification. Continuous "restore-and-discard" verification (test-restore-cron) is a R6 enhancement.

---

## 6. Quick reference

| Task | Command |
|------|---------|
| Manual Postgres backup now | `./scripts/backup-pg.sh` |
| Restore Postgres from file | `./scripts/restore-pg.sh /path/to/backup.dump` |
| Manual Redis snapshot now | `./scripts/backup-redis.sh` |
| Verify WAL archiving | `aws s3 ls s3://YOUR_BUCKET/wal/ \| tail -3` |
| List local Postgres backups | `ls -lh /var/backups/pg/*.dump` |
| Force WAL flush | `psql -c "SELECT pg_switch_wal();"` |
| Trigger cluster node restart | `kubectl rollout restart deployment/platform-api` |
| Check `/health/ready` | `curl -fsS http://localhost:8080/health/ready` |

---

**Last reviewed:** 2026-04-26 (R5.4 S5.8). Update after each chaos exercise if the procedure changes.
