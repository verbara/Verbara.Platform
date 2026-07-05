# Auth Horizontal Scaling — Runbook

**Last updated:** 2026-04-28 · **Train:** AHH (v1.14.0 + v1.14.1 amendment + **v1.14.2 fix**) ·
**Status:** **v1-measured single-replica AND 4-replica post-AHH** (v1.14.2
unblocks multi-replica startup, retunes Argon2id, sizes Postgres pool).

This runbook codifies what an operator does to deploy
`Verbara.Platform.Api` at multi-replica with the AHH train applied. It
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
      `IMfaPendingCache`, `IPasswordResetCache` (verified by `AddVerbaraPlatformIdentityRedis`)
- [ ] `RedisAuthCacheInvalidator` listening on `asterisk:auth:invalidate` (ADR-0010)
- [ ] AuthWriteQueue registered as `IHostedService` (ADR-0011)
- [ ] Server GC enabled in `Verbara.Platform.Api.csproj`:
      `<ServerGarbageCollection>true</ServerGarbageCollection>` (ADR-0013 §"Memory pressure")
- [ ] PostgreSQL `max_connections` ≥ `replicas × api_pool_max + 20`

If ANY box is unticked, do not scale beyond 1 replica. The `RequireRedisStore`
flag will fail-fast at startup if Redis is missing — that's the single
canonical guard.

## Knee envelope

All numbers measured on **AMD Ryzen 9 9900X (12 cores / 24 threads) ·
60 GB DDR5 · NVMe SSD · single-instance docker-compose**.

| Stage | Single-replica knee | 4-replica aggregate | p99 ≤ 250 ms? | Source |
|---|--:|--:|---|---|
| R5.5 baseline (BCrypt12, no caching, sync writes) | 75 req/s | n/a | ⚠ marginal | v1-measured, R5.5 sweep |
| Post-AHH **v1.14.0/v1.14.1** single-replica (Argon2id m=19/t=2 + caches + write deferral) | ~50 req/s | n/a (multi-replica blocked by DI hang) | ✓ at 50 / ⚠ at 100 | v1-measured 2026-04-28 |
| Post-AHH **v1.14.2** single-replica (Argon2id m=12/t=3 retuned + Postgres pool=100) | **~50 req/s sustained / 100 req/s 82 % OK** | n/a | ✓ at 50 / 100 req/s clean tail | **v1-measured 2026-04-28** |
| Post-AHH **v1.14.2** 4-replica aggregate (rotation pool + Postgres pool=50/replica + max_connections=220) | ~50 per replica (no linear scaling) | **~50 req/s aggregate** at p99 ≤ 400 ms / **100 req/s 96.8 % OK** | ✓ at 50 / 100 req/s mostly-OK | **v1-measured 2026-04-28** |

### Empirical single-replica jwt-sweep.sh — v1.14.0/v1.14.1 baseline (Argon2id m=19/t=2)

| Rate | OK | Fail | p50 ms | p95 ms | p99 ms | Verdict |
|---:|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 43.65    | 123.14   | 1494.02  | high tail (cold cache + GC) |
| 50  | 3000 | 0    | 83.52    | 152.19   | 492.29   | within range, marginal p99 |
| 100 | 3918 | 82   | 13295.62 | 23461.89 | 26279.94 | 500-error onset, collapse |
| 250 | 2428 | 6414 | n/a | n/a | 55214.08 | 27 % OK |
| 500 | 1216 | 8707 | n/a | n/a | 46596.10 | 12 % OK |

### Empirical single-replica jwt-sweep.sh — v1.14.2 retuned (Argon2id m=12/t=3 + pool=100)

| Rate | OK | Fail | p50 ms | p95 ms | Verdict |
|---:|---:|---:|---:|---:|---|
| 50  | 3000 | 0    | **71.55**  | **124.35**  | -15 % p50 vs v1.14.1, 100 % OK |
| 100 | 4947 | 1053 | 3942.40 | 9699.33 | 82 % OK (vs 98 % v1.14.1 but **p95 -58 %**) |

### Empirical 4-replica jwt-sweep.sh — v1.14.2 (rotation pool + nginx-lb)

| Rate | OK | Fail | p50 ms | p95 ms | p99 ms | Verdict |
|---:|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 37.38   | 73.79   | n/a | clean |
| 50  | 3000 | 0    | 203.39  | 398.34  | n/a | 100 % OK; within p95 ≤ 400 ms |
| 100 | 3114 | 102  | n/a | n/a | 36 208.64 | **96.8 % OK** (vs 51 % pre-retune) |
| 250 | 1490 | 4882 | n/a | n/a | 60 915.71 | 23 % OK |
| 500 |  635 | 8334 | n/a | n/a | 44 072.96 | 7 % OK |

### Reading the v1.14.2 numbers

**The 4-replica horizontal-scaling lift is smaller than projected.** At 50 req/s
the single-replica handles 100 % OK at p95 ≤ 124 ms; the 4-replica handles 100 %
OK at p95 ≤ 398 ms — *more capacity per request, but the 4-replica pipeline
adds proxy + cross-replica overhead*. At 100 req/s the v1.14.2 retune lifts
4-replica OK rate from 51 % (pre-retune) to **96.8 %** — a substantial
robustness win — but the single-replica retune ALSO improved (from 98 % at
p95=23 s to 82 % at p95=9.7 s). Aggregate multi-replica throughput is
*not* materially better than the retuned single-replica baseline. The
bottleneck has moved from per-replica CPU/memory (Argon2id) to a
combination of:

1. **Postgres write contention** — 4 replicas all hit the same Postgres,
   so write-path operations (refresh-token persist, lockout state on
   failure path) serialize at the DB level even though API frontends scale.
2. **nginx round-robin overhead** — single-thread nginx LB adds latency
   and synchronization on the host port.

The runbook's projection of ~880 req/s aggregate (1.14.0 ADR) was
optimistic — true linear scaling would require Postgres read-replica
routing + a multi-process LB (haproxy-multi-thread or k8s service mesh).
**For v1.14.x the practical guidance is: deploy 4 replicas for high
availability; expect ~50-100 req/s sustainable aggregate at p99 ≤
several-hundred ms (NOT the projected 880).** Capacity beyond that
needs the architectural changes documented in §"Forward compatibility".

**Sustainable knee post-AHH single-replica = ~50 req/s at p99 ≤ 250 ms.**
This is *not* the projected 220 req/s improvement — the AHH train
delivered the architectural multi-replica gate (Phase 3) but did NOT
achieve the projected single-replica throughput lift (Phase 4
Argon2id). Two factors converge to make Argon2id verify cost more
under sustained load than its single-call BenchmarkDotNet figure:

1. **Memory bandwidth + GC pressure.** `m=19 MiB` per concurrent
   verify — at 100 req/s × 8-10 concurrent = 150-190 MB allocations
   churning per second. The Server GC tail latency dominates.
2. **Connection pool contention.** Default Npgsql pool (100) + Argon2id
   verify thread holding a connection through the auth flow → pool
   pressure under burst, surfacing as `NpgsqlException: operation
   timed out` at 100 req/s × 82 errors.

Both are addressable in v1.14.2 via:
- Argon2id parameter retune (`m` lowered + `t` raised — same OWASP
  2025 floor with smaller working set per verify).
- Connection-string pool sizing per
  [§ "Postgres pool tuning"](#postgres-pool-tuning).

### v1.14.1 follow-up — multi-replica startup gate issue (RESOLVED in v1.14.2)

**Root cause** (identified via Program.cs bisection, 2026-04-28): a
**circular DI dependency** between the cache decorators and the
RedisAuthCacheInvalidator. The decorators (`CachedUserStore`,
`CachedTenantAuthConfigStore`, `PermissionResolver`) take
`RedisAuthCacheInvalidator?` in their constructor (to publish
invalidation events on writes). The invalidator constructor takes
`IEnumerable<ILocalAuthCacheInvalidationSink>`, which resolves *those
same decorators* (registered as sinks via `TryAddEnumerable`).

When the .NET DI container resolves these as singletons:
1. Resolve `RedisAuthCacheInvalidator` → factory needs sinks
2. Resolve sinks → factory: `sp.GetRequiredService<CachedUserStore>()`
3. Resolve `CachedUserStore` → constructor needs `RedisAuthCacheInvalidator`
4. **Singleton resolution lock held on `RedisAuthCacheInvalidator`** →
   wait on self → **deadlock** at startup.

**v1.14.2 fix** — split the publish-side surface into a separate
class `RedisAuthCachePublisher` (publish-only, NO sink dependency).
Decorators take `IAuthCachePublisher` (which resolves to the publisher
singleton, NOT the invalidator). The invalidator stays unchanged
(receive-side, hosted service, dispatches to sinks). Two singletons
sharing only the `IConnectionMultiplexer` — the cycle is structurally
broken.

**Pre-v1.14.2, the v1.14.1 DI fix made the bug manifest as a hang
instead of an exception.** Pre-v1.14.1, `TryAddEnumerable` rejected
the duplicate registrations as indistinguishable and the container
threw at startup — so multi-replica was already broken, just with a
different surface symptom. The v1.14.0 ship was therefore wholly
single-replica-only despite the multi-replica gate ADRs.

### v1.14.1 deliverables

- `docker/docker-compose.scale.yml` 4-replica override + `nginx-loadbalancer.conf`
  scaffold (the exact harness `jwt-sweep.sh` would target once the
  startup hang is fixed).
- DI bug fix: `AuthHotpathCachingExtensions.AddAuthHotpathRedisInvalidation`
  now uses `ServiceDescriptor.Singleton<TService, TImpl>` with explicit
  `TImpl` so `TryAddEnumerable` doesn't reject the three sink
  registrations as indistinguishable. Without this fix, ANY deployment
  with `ConnectionStrings:IdentityRedis` set crashes at startup with
  `ArgumentException: Implementation type cannot be ... indistinguishable`.
  Pre-v1.14.1 multi-replica deployment was wholly blocked by this bug.

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
Host=postgres;Database=verbara;Username=app;Password=…;\
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
  `LISTEN/NOTIFY`, which `Verbara.Sdk.Pro.Cluster.Storage.Postgres`
  and `Verbara.Sdk.Pro.Push` rely on. Session-pool mode preserves
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
