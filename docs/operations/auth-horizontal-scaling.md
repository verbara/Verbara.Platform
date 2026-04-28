# Auth Horizontal Scaling — Runbook

**Last updated:** 2026-04-27 · **Train:** AHH Phase 5 (v1.14.0) · **Status:** v1-projected

This runbook codifies what an operator does to deploy
`Asterisk.Platform.Api` at multi-replica with the AHH train applied. It
combines the multi-replica gate from
[ADR-0012](../decisions/0012-jwt-rotation-pool-wireup-and-multi-replica-gate.md)
with the post-Phase-4 throughput envelope projected from the Phase 0
baseline + Phase 4 algorithmic switch.

## TL;DR — pre-flight checklist

Before flipping the deployment to N>1 replicas:

- [ ] `Identity:JwtKeyRotation:UseRotationPool = true`
- [ ] `Identity:JwtKeyRotation:RequireRedisStore = true` (fail-fast on missing Redis)
- [ ] `ConnectionStrings:IdentityRedis = "<redis>:6379"` configured
- [ ] `ConnectionStrings:Postgres` includes pool sizing (see §"Postgres pool tuning")
- [ ] DataProtection wired to `PlatformDataProtectionDbContext` (ADR-0003)
- [ ] All Identity Redis caches registered: `IJwtKeyStore`, `IJtiRevocationCache`,
      `IMfaPendingCache`, `IPasswordResetCache` (verified by `AddAsteriskPlatformIdentityRedis`)
- [ ] `RedisAuthCacheInvalidator` listening on `asterisk:auth:invalidate` (ADR-0010)
- [ ] AuthWriteQueue registered as `IHostedService` (ADR-0011)
- [ ] Server GC enabled in `Asterisk.Platform.Api.csproj`:
      `<ServerGarbageCollection>true</ServerGarbageCollection>` (ADR-0013 §"Memory pressure")
- [ ] PostgreSQL `max_connections` ≥ `replicas × api_pool_max + 20`

If ANY box is unticked, do not scale beyond 1 replica. The `RequireRedisStore`
flag will fail-fast at startup if Redis is missing — that's the single
canonical guard.

## Knee envelope

Source numbers from
[Phase 0 baseline](../research/2026-04-27-auth-hotpath-baseline.md) +
post-Phase-4 algorithmic projection. All measured on **AMD Ryzen 9 9900X
(12 cores / 24 threads) · 60 GB DDR5 · NVMe SSD · single-instance docker-compose**.

| Stage | Single-replica knee | 4-replica projection | p99 ≤ 250 ms? |
|---|--:|--:|---|
| R5.5 baseline (BCrypt12, no caching, sync writes) | 75 req/s | n/a (would need rotation pool) | ⚠ at 50 req/s exactly |
| Post-Phase-1 (read caches) | ~95 req/s | n/a | ✓ at 95 req/s |
| Post-Phase-2 (write deferral) | ~120 req/s | n/a | ✓ at 120 req/s |
| Post-Phase-3 (multi-replica gate) | ~120 req/s | ~480 req/s | ✓ aggregate |
| Post-Phase-4 (Argon2id) | ~220 req/s | **~880 req/s** | ✓ aggregate target |

The 4-replica aggregate assumes near-linear scaling because:

- `/auth/login` is stateless after Phase 3 (shared signing key via Redis).
- Per-request CPU dominates wall time (Argon2id is the bottleneck);
  Postgres I/O is small compared to crypto cost.
- Phase 1 cache decorators are per-replica IMemoryCache — the cache
  hit rate per replica is independent of replica count after a brief
  warm-up window.

Phase 5 horizontal validation (Phase 5 follow-up after this commit ships)
will replace the projection with measured numbers via `jwt-sweep.sh` against
the multi-replica docker-compose stack.

## Postgres pool tuning

`AddPostgresStorage` accepts the connection string verbatim plus an
optional `Action<NpgsqlDataSourceBuilder>` hook. Pool sizing is set via
the connection string — Npgsql reads the standard parameters:

| Parameter | Single-replica | Multi-replica (4×) | Rationale |
|---|--:|--:|---|
| `Maximum Pool Size` | 100 | 50 per replica | At 220 req/s × 1 query per login, 50 conns is comfortable; total = 200 across 4 replicas. |
| `Minimum Pool Size` | 10 | 10 per replica | Avoids cold-start latency on bursts. |
| `Connection Idle Lifetime` | 300 (s) | 300 (s) | Default; releases unused conns after 5 min idle. |
| `Pooling` | `true` (default) | `true` | Required. |

Example connection string for a 4-replica deployment:

```
Host=postgres;Database=asterisk;Username=app;Password=…;\
Maximum Pool Size=50;Minimum Pool Size=10;Connection Idle Lifetime=300;\
Pooling=true
```

PostgreSQL server-side `max_connections` must be sized to absorb the total:

```sql
-- For 4-replica × 50 conn pool + 20 admin headroom
ALTER SYSTEM SET max_connections = 220;
ALTER SYSTEM SET shared_buffers = '15GB';            -- 25% of host RAM
ALTER SYSTEM SET effective_cache_size = '45GB';      -- 75% of host RAM
SELECT pg_reload_conf();
```

The 60 GB host budget assumed above is the AHH Phase 0 baseline hardware.
For other host classes scale the absolute values proportionally; the 25 % /
75 % ratios stay constant.

## What to NOT do

- **Do not run pgBouncer in transaction-pool mode.** It breaks
  `LISTEN/NOTIFY`, which `Asterisk.Sdk.Pro.Cluster.Storage.Postgres`
  and `Asterisk.Sdk.Pro.Push` rely on. Session-pool mode preserves
  `LISTEN/NOTIFY` but loses most of pgBouncer's win. Skip pgBouncer
  until those Pro packages refactor away from `LISTEN/NOTIFY`, or
  until measured demand shows the current Npgsql pool is the
  bottleneck.
- **Do not deploy multi-replica without the rotation pool wired
  (ADR-0012).** Each replica generates its own RSA key on first boot
  → tokens issued by replica A reject on replica B. The
  `RequireRedisStore=true` flag prevents this by failing startup loud.
- **Do not turn `IdentityRedis` off at runtime.** The hot-read caches
  (Phase 1) lose cross-replica invalidation; stale role grants for up
  to 60 s, stale tenant config for up to 60 s. Acceptable per ADR-0010
  but worth noting for incident response.
- **Do not assume linear scaling beyond 4 replicas.** Postgres
  `max_connections` becomes the next ceiling. Beyond 4 replicas, plan
  for read-replica routing (out of scope for v1.14.0).

## Verifying the knee post-deploy

```bash
# 1. Bring up the staging stack at 4 replicas (docker-compose override).
ASPNETCORE_REPLICAS=4 docker compose -f docker/docker-compose.full.yml \
                                     -f docker/docker-compose.scale.yml \
                                     up -d --wait --scale platform-api=4

# 2. Re-seed (idempotent).
./scripts/seed-staging.sh

# 3. Run the JWT sweep — produces per-stage p50/p95/p99.
./scripts/jwt-sweep.sh

# 4. Inspect dotnet meters during the sweep window.
curl -s http://localhost:5000/metrics | \
    grep -E '(http_server_request_duration_seconds|auth\.write\.|argon2)'
```

Expected results post-v1.14.0:

| Rate (req/s) | OK %    | p99 ms (post-AHH) | Verdict |
|--------------|---------|--:|---------|
| 100          | 100.0   | ≤ 100 | comfortable |
| 250          | 100.0   | ≤ 200 | within SLO |
| 500          | 100.0   | ≤ 300 | within SLO at scale |
| 1 000        | ~95–100 | ≤ 400 | aggregate-knee territory |

If the sweep returns numbers materially worse than the table:

1. Check `auth.write.dropped` counter → if non-zero, queue is saturating; inspect logs for `AuthWriteQueue is full` warnings.
2. Check Postgres `pg_stat_activity` → if `state='idle in transaction'`
   count is high, a long-running transaction is blocking the pool;
   correlate with `Microsoft.AspNetCore.Hosting` request-duration
   histogram.
3. Check `dotnet_collection_count_total{generation="2"}` → if > 0.5 / sec,
   Argon2id memory is creating GC pressure; retune Argon2 parameters
   per ADR-0013 §"Memory pressure".
4. Check Redis client list (`CLIENT LIST`) → connection storms indicate
   `RedisAuthCacheInvalidator` reconnect loops.

## Update cadence

This runbook ages quickly. Refresh:

- **After every v1.x.y patch** that materially touches the auth path
  (changes to PasswordService, JwtTokenService, AuthWriteQueue,
  cache decorators).
- **After Phase 5 horizontal validation lands measured 4-replica
  numbers** — replace the v1-projected table values with v1-measured.
- **When the host-class hardware changes** (e.g. cloud VM instance
  family migration). Capture the new baseline via `jwt-sweep.sh` and
  amend the knee envelope §.
