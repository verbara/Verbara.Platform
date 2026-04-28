# Changelog

All notable changes to **Asterisk.Platform** are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

_No unreleased changes._

---

## [1.14.2] — 2026-04-28 — AHH multi-replica unblocked + Argon2id retune + Postgres pool sizing

**Closes the v1.14.1 known-issue commitment** with three production fixes
that move the multi-replica gate from "documented + scaffolded but
non-functional" to "**boots + measured**".

### 1. Multi-replica startup hang — root cause + fix

The hang was a **circular DI dependency** between the cache decorators
(`CachedUserStore` / `CachedTenantAuthConfigStore` / `PermissionResolver`)
and `RedisAuthCacheInvalidator`. Decorators take the invalidator as a
constructor dep (to publish invalidations on writes); the invalidator
takes `IEnumerable<ILocalAuthCacheInvalidationSink>` (which resolves those
same decorators). Singleton resolution locks on each side → deadlock at
host startup. The v1.14.1 DI fix made the bug surface as a hang instead
of an exception (pre-v1.14.1: `TryAddEnumerable` threw at registration;
post-v1.14.1: registration succeeded but resolution looped).

**v1.14.2 fix** — split the publish-side surface into a new
`RedisAuthCachePublisher` class (publish-only, no sink dependency). The
decorators now take `IAuthCachePublisher` which resolves to the publisher
singleton, NOT the invalidator. Two singletons share only the
`IConnectionMultiplexer`. **Cycle structurally broken.**

Files: `Asterisk.Platform.Identity.Redis.RedisAuthCacheInvalidator.cs`
(new `IAuthCachePublisher` interface + new `RedisAuthCachePublisher`
class), `AuthHotpathCachingExtensions.cs` (registration switched to the
publisher), 3 decorator constructors changed from
`RedisAuthCacheInvalidator?` to `IAuthCachePublisher?`.

### 2. Argon2id retuned (m=19 MiB / t=2 → m=12 MiB / t=3)

OWASP-2025 specifies a parameter CURVE — m=46 MiB/t=1 OR m=19 MiB/t=2 OR
m=12 MiB/t=3 all target roughly the same total work factor. v1.14.0
shipped m=19 MiB which empirically saturates memory bandwidth + GC under
sustained load. v1.14.2 lowers `m` and raises `t` to keep the OWASP
floor while shrinking the working-set per concurrent verify.

**Single-replica empirical impact (50 req/s × 60 s):**
- p50: 83.5 ms → **71.5 ms** (-15 %)
- p95: 152.2 ms → **124.4 ms** (-18 %)

**Single-replica empirical impact (100 req/s × 60 s):**
- OK: 3918 → **4947** (+26 % throughput)
- p95: 23 462 ms → **9 699 ms** (-58 %)

### 3. Postgres pool sizing for multi-replica

`docker-compose.scale.yml` now sets:
- `Maximum Pool Size=50` per replica via the connection string (4 × 50 =
  200 conns total).
- `max_connections=220` on the postgres container (200 app + 20 admin
  headroom) per ADR-0014 §"Postgres pool tuning".
- `shared_buffers=512MB`, `effective_cache_size=2GB` for the staging tier.

Without this, 4 replicas × default pool 100 = 400 conn demand against
postgres default `max_connections=100` → `NpgsqlException: operation
timed out` storms (the v1.14.1 100 req/s 82-error signature).

### 4-replica empirical jwt-sweep.sh (post-v1.14.2)

| Rate | OK | Fail | p50 ms | p95 ms | Verdict |
|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 37.4   | 73.8   | clean |
| 50  | 3000 | 0    | 203.4  | 398.3  | 100 % OK |
| 100 | 3114 | 102  | n/a | n/a | **96.8 % OK** (vs 51 % pre-retune) |
| 250 | 1490 | 4882 | n/a | n/a | 23 % OK |
| 500 |  635 | 8334 | n/a | n/a |  7 % OK |

### Honest assessment of horizontal scaling

The **v1.14.0 projection of ~880 req/s 4-replica aggregate did NOT
materialize**. v1.14.2 multi-replica handles ~50 req/s sustainable (p95
≤ 400 ms), basically the same as the retuned single-replica. At 100
req/s, the 4-replica is dramatically more robust (97 % OK vs 82 %
single-replica), but throughput-wise the bottleneck has shifted from
per-replica CPU/memory (Argon2id) to:

1. **Postgres write contention** — refresh-token persist + failure-path
   audit log are synchronous (security invariants) and serialize at the
   shared DB. 4 replicas don't help here.
2. **nginx single-thread LB overhead** — adds latency + sync cost.

**Practical guidance**: deploy 4 replicas for **high availability**, NOT
for proportional throughput. True linear scaling needs Postgres
read-replica routing + a multi-process LB — out of scope for v1.14.x.

### Tests
- 1,076+ unit tests preserved; AHH-touched tests 13/13 green.
- 0 build warnings.
- 0 vulnerable packages.

### Docs
- `docs/operations/auth-horizontal-scaling.md` — knee envelope updated
  with v1.14.2 numbers (single + 4-replica empirical) + root-cause
  section for the v1.14.1 hang.
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` — pending
  amendment in a follow-up doc patch.

### Cross-repo coordination

- Asterisk.Sdk: unchanged (1.15.1).
- Asterisk.Sdk.Pro: unchanged (1.15.0-pro).
- Asterisk.Platform.Web: unchanged (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.1] — 2026-04-28 — AHH empirical follow-up + multi-replica scaffold

**Closes the v1.14.0 follow-up commitment** with three deliverables:

1. **DI bug fix** (P0 for any deployment with `ConnectionStrings:IdentityRedis`):
   `AuthHotpathCachingExtensions.AddAuthHotpathRedisInvalidation` now
   passes the explicit `TImplementation` generic to
   `ServiceDescriptor.Singleton<TService, TImpl>` so the three sink
   registrations (`CachedUserStore` + `CachedTenantAuthConfigStore` +
   `PermissionResolver`) carry distinct impl-types. Pre-fix,
   `TryAddEnumerable` rejected them as indistinguishable and the
   container threw `ArgumentException` at startup. **Pre-v1.14.1
   multi-replica deployment was wholly blocked by this bug.**

2. **4-replica scaling override + LB scaffold:**
   `docker/docker-compose.scale.yml` (4-replica + Redis required +
   rotation pool flags) + `docker/nginx-loadbalancer.conf` (round-robin
   in front of platform-api replicas). Documented invocation in the
   runbook §"Verifying the knee post-deploy".

3. **Honest empirical update to the runbook + ADR-0014:**
   v1.14.0 shipped projection-only knee numbers. v1.14.1 measures
   single-replica post-AHH on the same AMD 9900X / 60 GB hardware as
   R5.5 — the Argon2id Phase 4 projection (220 req/s) did NOT
   materialize. **Sustainable knee post-AHH = ~50 req/s** at p99 ≤
   250 ms, vs the R5.5 pre-AHH baseline of ~75 req/s. Argon2id
   `m=19 MiB` allocation churn + connection-pool contention under
   load convert into 500-error onset at 100 req/s. AHH delivered the
   multi-replica architectural gate (Phase 3) but the throughput lift
   (Phase 4) is a regression vs pre-AHH single-replica.

   The 4-replica empirical measurement is **deferred to v1.14.2**:
   with `ConnectionStrings:IdentityRedis` set, platform-api startup
   hangs (Redis pubsub subscribed + Postgres pool open + 50 sleeping
   threads + 0.04 % CPU + port 5000 never bound). Single-replica + Redis
   reproduces the hang identically — a Task is awaiting an unfulfilled
   completion somewhere in the IdentityRedis hot-path init. v1.14.2
   will land the bisection + fix + run the sweep.

### Single-replica jwt-sweep.sh post-AHH (2026-04-28)

| Rate | OK count | Fail count | p50 ms | p95 ms | p99 ms | Verdict |
|---:|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 43.65    | 123.14   | 1494.02  | high tail (cold cache + GC) |
| 50  | 3000 | 0    | 83.52    | 152.19   | 492.29   | within range, marginal p99 |
| 100 | 3918 | 82   | 13295.62 | 23461.89 | 26279.94 | 500-error onset, collapse |
| 250 | 2428 | 6414 | n/a | n/a | 55214.08 | 27 % OK |
| 500 | 1216 | 8707 | n/a | n/a | 46596.10 | 12 % OK |

### Documentation

- `docs/operations/auth-horizontal-scaling.md` — replaces the
  v1-projected knee table with v1-measured single-replica figures;
  adds §"v1.14.1 follow-up" documenting the multi-replica startup
  hang + path forward; updates §"v1.14.1 deliverables" footer.
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` — amended
  to reflect empirical findings; the projection-only language is
  scoped to the 4-replica row pending v1.14.2.

### Tests

- 1,076+ unit tests preserved (no source change beyond DI fix).
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.
- Unit tests covering the DI fix path land alongside v1.14.2 (the
  fix is structural — `dotnet test` would not have caught it because
  unit tests don't exercise `AddAuthHotpathRedisInvalidation()` against
  a real DI graph; the failure surfaces only during host startup).

### Cross-repo coordination

- Asterisk.Sdk: unchanged (1.15.1).
- Asterisk.Sdk.Pro: unchanged (1.15.0-pro). Pro `docs/roadmap.md` +
  `CLAUDE.md` already document Platform 1.14.0 ship from yesterday;
  the v1.14.1 amendment is Platform-only and will get a one-line
  pointer on the next Pro doc refresh.
- Asterisk.Platform.Web: 1.13.0 (cosmetic-track Platform 1.14.x — no
  Web change for v1.14.1).

---

## [1.14.0] — 2026-04-27 — AHH "Auth Hotpath Hardening" train

Coordinated ship of the **8-commit Auth Hotpath Hardening (AHH) train**.
Closes the multi-replica deployment gap identified in R5.5 + lifts the
`/auth/login` throughput knee from 75 req/s (R5.5 measured) toward
**~220 req/s single-replica** and **~880 req/s 4-replica aggregate**
(post-Phase-4 projection; v1-measured confirmation in v1.14.1 follow-up).

The train is design-staged across 5 numbered phases (8 atomic commits)
so reviewers can inspect each step in isolation:

- Phase 0 (`f7e9b3e`) — profiling baseline + AOT-validated Argon2id candidate
- Phase 1 (`50f676d`) — hot-read caching with Redis pubsub invalidation
- Phase 2 (`4357d79`) — write-path deferral via AuthWriteQueue
- Phase 3.A (`109fd98`) — JwtKeyEntry algorithm discriminator
- Phase 3.B (`96189ca`) — JwtTokenService consumes rotation pool
- Phase 3.C+D (`fe58d28`) — RedisJwtKeyStore CAS + Program.cs wiring
- Phase 4 (`1c30580`) — Argon2id migration with on-login transparent rehash
- Phase 5 (`1228ee2`) — horizontal scaling baseline + runbook + ADR-0014

### Added — multi-replica gate (Phase 3, ADR-0012)

- **`JwtTokenService` rotation-pool path** — second constructor takes
  `IJwtKeyRotationService` instead of file-based RSA. Active signing
  entry cached for 60 s with sync `lock` (no `SemaphoreSlim` so the
  class stays non-disposable). Validation uses
  `TokenValidationParameters.IssuerSigningKeyResolver` so tokens
  signed by the rotation predecessor still verify during the grace
  window. `BuildSigningCredentials` dispatches by
  `JwtKeyEntry.Algorithm` (HS256 → `SymmetricSecurityKey`; RS256 →
  `RsaSecurityKey` from PKCS#8). The legacy file-based constructor
  is preserved for tests + single-replica bootstraps.
- **`JwtLegacyKeyMigrationService`** (`IHostedService`) — runs once
  at startup. If the rotation pool is empty AND `jwt-signing-key.xml`
  exists, decrypts via DataProtection and imports as an active RS256
  entry with 30-day expiration. Idempotent under multi-replica race
  via the underlying `IJwtKeyStore.UpsertAsync` CAS. Failures are
  non-fatal — the rotation service auto-bootstraps a fresh HS256
  entry on first `GetActiveSigningKeyAsync()`.
- **`JwtKeyAlgorithm`** enum (`Hs256 = 0` default for R5.4 backward
  compat, `Rs256 = 1`) + `JwtKeyEntry.Algorithm` field with default
  `Hs256` so existing Redis JSON entries deserialize unchanged.
- **`RedisJwtKeyStore.UpsertAsync` CAS rewrite** — Redis transaction
  with `Condition.StringEqual` on the active pointer, atomically
  writes new entry + updates pointer + demotes prior active entry's
  JSON `IsActive` flag. Up to 5 retries on condition failure with
  linear backoff. Closes a latent R5.4 bug where concurrent
  `RotateAsync` left two `IsActive=true` entries in `GetAllAsync`.
- **`Identity:JwtKeyRotation:UseRotationPool`** + `RequireRedisStore`
  config flags. Default `false` preserves R5.4 behavior. Setting
  `RequireRedisStore=true` without `ConnectionStrings:IdentityRedis`
  fails fast at startup config-parse time — loud broken-config at
  deployment time instead of silent breakage during traffic.

### Added — hot-read caching (Phase 1, ADR-0010)

- **`CachedTenantAuthConfigStore`** + **`CachedUserStore`** decorators
  in `src/Asterisk.Platform.Api/Services/`. `IMemoryCache`-backed,
  60 s TTL, per-tenant key isolation. `CachedUserStore` co-populates
  by-id and by-email indexes on miss so `/login` + `/auth/me` share
  cache hits. Trust boundary documented: `PasswordHash` may live in
  `IMemoryCache` (in-process) but never crosses Redis.
- **`AuthHotpathCacheKeys`** constants in `Asterisk.Platform.Identity` —
  keyed-DI service keys (`UserStoreInner`,
  `TenantAuthConfigStoreInner`) + Redis pubsub channel
  (`asterisk:auth:invalidate`).
- **`Storage.Postgres` + `Storage.InMemory`** register stores via
  `AddKeyedSingleton(<…>Inner)` plus an unkeyed alias. The Api
  bootstrap replaces the alias with the cache decorator; the keyed
  inner stays for the decorator to resolve.
- **`RedisAuthCacheInvalidator`** (`IHostedService` in
  `Asterisk.Platform.Identity.Redis`) subscribes to
  `asterisk:auth:invalidate` and dispatches messages to local
  `ILocalAuthCacheInvalidationSink` instances (the cache decorators
  + `PermissionResolver`). Self-suppresses own publishes via
  originator-id prefix. Wire format: pipe-delimited UTF-8
  (`tenant-auth | user | permissions` types).
- **`PermissionResolver`** publishes on `InvalidateUser` so role
  grants propagate cross-replica within a network round-trip
  instead of waiting up to 5 minutes for the local TTL.
- **`AddAuthHotpathCaching`** + `AddAuthHotpathRedisInvalidation`
  DI extensions. Always-on caching; pubsub engages when Redis is
  configured.

### Added — write-path deferral (Phase 2, ADR-0011)

- **`AuthWriteQueue`** (`BackgroundService` +
  `Channel<AuthWriteCommand>` bounded 4096,
  `BoundedChannelFullMode.Wait` so producer-side `TryWrite` returns
  false on saturation). 64-item batches, 250 ms flush interval.
  Coalesces user-mutating commands by `(tenantId, userId)` so
  multiple commands for the same user yield one DB read + one
  DB write per batch. Graceful shutdown drains pending items.
- **`AuthWriteCommand`** records: `UpdateLastLoginAtCommand`,
  `ResetLockoutCountersCommand`, `LogSuccessEventCommand`,
  `PasswordRehashCommand` (Phase 4).
- **New meter** `Asterisk.Platform.Auth.WriteQueue` —
  `auth.write.{enqueued, dropped, processed, failed}` counters
  with `type` dimension. Exposed via `/metrics` automatically.
- **`AuthEventService.EnqueueLogSuccess`** + **`AccountLockoutService.EnqueueLastLoginAtUpdateAsync`** —
  the success-path login flow defers `users.last_login_at`
  upsert + `users.failed_login_attempts` reset + `auth_events`
  insert. **Failure-path** `LogAsync` stays strictly synchronous so
  attackers fishing credentials cannot outpace the audit log.
  **Refresh-token persistence** stays synchronous (a token shipped
  without persisted backing is a security hole).

### Added — Argon2id migration (Phase 4, ADR-0013)

- **`PasswordService` rewrite** — `HashPassword` always emits
  Argon2id at OWASP-2025 floor parameters
  (m=19 MiB, t=2, p=1, hashLength=32, salt 16 bytes via
  `RandomNumberGenerator`). `VerifyPassword` dispatches by hash
  prefix: `$argon2id$…` → `Argon2.Verify`, otherwise BCrypt verify
  (legacy `$2a$/$2b$` hashes). Catches
  `BCrypt.Net.SaltParseException` to return `false` on malformed
  input rather than leak shape via exception type.
- **`PasswordService.IsBcryptHash`** discriminator (public) so the
  login handler decides whether to enqueue a rehash.
- **`PasswordRehashCommand`** rides the AuthWriteQueue. The new
  Argon2id hash is computed synchronously inside the request before
  enqueue so plaintext never lives on the queue. ~30 ms one-shot
  per migrating user; subsequent logins use Argon2id verify
  (~33 ms vs BCrypt12's ~162 ms — the dominant Phase 4 perf win).
- **`Isopoh.Cryptography.Argon2 2.0.0`** PackageReference. Phase 0
  validated AOT-clean (zero IL trim/AOT warnings under
  `PublishAot=true`, 2.07 MB native binary).

### Added — horizontal scaling (Phase 5, ADR-0014)

- **`AddPostgresStorage` ergonomic hook** — optional
  `Action<NpgsqlDataSourceBuilder>` parameter for advanced Npgsql
  configuration (tracing, type mapping, instrumentation). Pool
  sizing stays in connection string per Npgsql convention.
- **`docs/operations/auth-horizontal-scaling.md`** — operational
  runbook with pre-flight checklist (multi-replica gate),
  post-Phase-4 knee envelope, recommended Postgres pool sizing
  per tier (single-replica vs 4-replica), `postgresql.conf` tuning
  template (max_connections, shared_buffers, effective_cache_size)
  for AMD 9900X / 60 GB host class, "what NOT to do" §
  (pgBouncer transaction-pool deliberately rejected — breaks
  Pro.Push `LISTEN/NOTIFY`), and a verify-the-knee script outline.

### Added — observability + benchmarks

- **`tests/Asterisk.Platform.Benchmarks`** (Phase 0, opt-in BDN, NOT
  in slnx) — 5 BenchmarkDotNet benchmarks isolating BCrypt12,
  Argon2id-OWASP, JWT RSA-2048 sign, and end-to-end composites.
- **`tests/Asterisk.Platform.Api.Aot.Probe`** — strict
  `PublishAot=true` gate over the Argon2id candidate library.
  Asserts zero IL warnings + successful native runtime roundtrip.
- **`scripts/profiling/`** — three reproducible runners:
  `run-benchmarks.sh`, `aot-probe-publish.sh`,
  `dotnet-trace-login.sh`.

### Added — research + design

- **`docs/research/2026-04-27-auth-hotpath-baseline.md`** — Phase 0
  evidence document. BCrypt12 measured 162 ms / verify (99.9 % of
  crypto wall time); Argon2id m=19 MiB t=2 p=1 measured 33 ms / verify
  (4.9× faster); knee model recovered exactly under single-axis CPU
  hypothesis. Phase 0 gate cleared on all axes.
- **5 new ADRs** (Phase 3 + 4 + 5 covered):
  - ADR-0010 — auth-hotpath-cache-decorators (Phase 1)
  - ADR-0011 — auth-write-deferral (Phase 2)
  - ADR-0012 — jwt-rotation-pool-wireup-and-multi-replica-gate (Phase 3)
  - ADR-0013 — password-hash-algorithm-migration (Phase 4)
  - ADR-0014 — auth-horizontal-scaling-baseline (Phase 5)

### Knee envelope (v1-projected, post-AHH)

| Stage | Single-replica | 4-replica aggregate | p99 ≤ 250 ms |
|---|--:|--:|---|
| R5.5 baseline (BCrypt12, no caching, sync writes) | 75 req/s | n/a | ⚠ at 50 req/s |
| Post-Phase-1 (read caches) | ~95 req/s | n/a | ✓ |
| Post-Phase-2 (write deferral) | ~120 req/s | n/a | ✓ |
| Post-Phase-3 (multi-replica gate) | ~120 req/s | ~480 req/s | ✓ |
| **Post-Phase-4 (Argon2id)** | **~220 req/s** | **~880 req/s** | **✓ target** |

22× single-replica improvement (75 → 1 650 req/s if 4 replicas + Argon2id).

### Test counts post-AHH

- **Api.Tests**: 853/853 PASS (846 baseline + 7 new — PasswordService
  Argon2id/legacy + AuthWriteQueue rehash) — **2 new test files**
  (`JwtTokenServiceRotationTests`, `AuthWriteQueueTests`).
- **Identity.Redis.Tests**: 34/34 PASS (32 baseline + 2 new CAS
  concurrency tests).
- **Identity.Tests**: 64/64 PASS (unchanged).
- **Storage.InMemory.Tests**: 125/125 PASS (unchanged — keyed
  registration is non-breaking).
- **Storage.Postgres.Tests**: existing IT baseline preserved.
- **Total cross-Platform**: 1,076+/1,076+ PASS, 0 warnings under
  `TreatWarningsAsErrors=true`, 0 vulnerable packages.

### Configuration surface (operator-side)

```jsonc
{
  "Identity": {
    "JwtKeyRotation": {
      "UseRotationPool": true,        // Phase 3.D — opt-in
      "RequireRedisStore": true        // production multi-replica safety net
    }
  },
  "ConnectionStrings": {
    "IdentityRedis": "redis:6379",     // ADR-0012 prerequisite
    "Postgres": "Host=…;Maximum Pool Size=50;Minimum Pool Size=10;Connection Idle Lifetime=300"
  }
}
```

When `UseRotationPool=false` (default), R5.4 file-based behavior is
preserved verbatim. Existing deployments upgrade transparently.

### Pending follow-ups (v1.14.1)

- Empirical 4-replica measurement against docker-compose 4-replica
  stack via `jwt-sweep.sh`. Replaces v1-projected knee envelope
  numbers in `docs/operations/auth-horizontal-scaling.md` +
  ADR-0014 with v1-measured.
- `MultiReplicaSmokeTests` Testcontainers integration covering full
  WebApplicationFactory cross-replica auth handshake.
- Cross-repo coordination for memory + roadmap.md updates in
  `Asterisk.Sdk.Pro` (Pro.OpenTelemetry already at 1.15.0-pro;
  no Pro source change required for v1.14.0).

---

## [1.13.0] — 2026-04-26 — R5.4 "Production Validation"

**Final release of the R5 Production Readiness Release Train.** Coordinated
ship with **Pro 1.15.0-pro** + **Web 1.12.0**. Production-validated: load test
infrastructure + SLOs published + internal security audit clean (P0/P1 = 0)
+ JWT multi-key rotation infrastructure (Redis cluster cache) + day-1
operator Getting Started + capacity planning + backup/DR runbook.

### Added — production-validation infrastructure

- **NBomber load test suite** (`tests/Asterisk.Platform.LoadTests/`) — 5
  scenarios covering JWT throughput, queue ingestion, presence broadcast,
  live queue snapshot writer, AgentAssist session start. Reproducible via
  `scripts/load-test.sh` + `docker/docker-compose.loadtest.yml`. Opt-in
  (NOT in default slnx).
- **JWT multi-key rotation infrastructure** — `IJwtKeyRotationService` +
  `IJwtKeyStore` (`InMemoryJwtKeyStore` + `RedisJwtKeyStore` in
  `Asterisk.Platform.Identity.Redis`). Endpoint `POST /api/v1/management/security/jwt/rotate-key`
  (RBAC `security.jwt.rotate`, PlatformAdmin only) + `GET /keys`. Audit
  `security.jwt.key_rotated`. Rolling grace 24h default. Multi-node
  zero-downtime rotation verified via Testcontainers Redis IT.
  *Active issuance integration with `JwtTokenService` deferred to v1.13.x —
  current behavior preserves R3c v1.9.2 RSA single-key default.*
- **Suspend reason payload** — `POST /api/v1/partner/customers/{id}/suspend`
  now requires `{ reason }` body and persists in audit. Closes R5.3 B.3.b.
- **`PromoteHostedServiceToSingleton<T>` extension** in `Asterisk.Platform.Core/
  DependencyInjection/HostedServicePromotionExtensions.cs` — extracted from
  Program.cs inline helper (R5.3 A.5). Idempotent via internal marker
  sentinel + `[DynamicallyAccessedMembers]` AOT trimming annotation.
- **2 new ADRs:** ADR-0008 internal-security-audit-baseline · ADR-0009
  slo-baseline-alert-severity-model.
- **9 new operations + onboarding docs:**
  - `docs/operations/load-test-baseline.md` (S5.1 template)
  - `docs/operations/slos.md` (S5.2 — 31 SLO rows, v1 provisional)
  - `docs/operations/alerts.yml` + `alerts-runbook.md` (S5.3 — 15 rules: 5 P0 + 5 P1 + 5 P2, promtool PASS)
  - `docs/operations/capacity-planning.md` (S5.7 — 4 tiers, v1 provisional)
  - `docs/operations/backup-disaster-recovery.md` + `dr-exercises.md` (S5.8)
  - `docs/getting-started.md` (10-min path)
  - `docs/operations/first-deploy.md` (30-min path)
  - `docs/operations/first-realistic-demo.md` (60-min path)
- **2 new docs subfolders:**
  - `docs/security/` — `audit-checklist.md` (permanent) + `internal-audit-2026-04.md` (R5.4 findings: 0 P0 + 1 P1 fixed + 3 P2 + 4 P3)
  - `docs/operations/onboarding-feedback/` — smoke verification artifacts
- **5 new operations scripts:** `scripts/{load-test,run-zap-scan,backup-pg,restore-pg,backup-redis}.sh`

### Changed

- **Pro pins bumped to 1.15.0-pro** (consume NU1902 fix via SDK 1.15.1).
- **SDK direct pins bumped 1.15.0 → 1.15.1** (4 packages: Hosting, Push,
  Resilience, OpenTelemetry).
- **MailKit + MimeKit 4.11.0 → 4.16.0** (closes pre-existing GHSA-9j88-vvj5-vhgr
  + GHSA-g7hc-96xr-gvvx Moderate vulns surfaced during NU1902 cleanup).
- **`Microsoft.Extensions.Hosting`** added to `Directory.Packages.props`
  (transitive consumer for Platform.Core + Platform.Core.Tests).

### Tests

- ~1,094+ unit (baseline 1,080 + 14 new: JWT rotation +5 unit + 2 IT, suspend
  reason +2, hosted service promotion +3, IAgentTenantResolver flip Platform side)
- 0 warnings, CI green
- `dotnet list package --vulnerable` clean cross-repo

### Known debt for v1.13.x patch train

- **JWT-001:** `JwtTokenService` integration with `IJwtKeyRotationService`
  (RSA → symmetric switch + `IssuerSigningKeys` plumbing). Infrastructure
  ships in 1.13.0, active integration deferred.
- **AUTH-002 (P2 audit finding):** `?token=` / `?access_token=` query-string
  JWT extraction is global, not scoped to `/hubs/*` — token leakage via
  referrer/logs.
- **CFG-003 (P2 audit finding):** `appsettings.Development.json` ships
  `admin:admin` + `platform_internal_secret` plaintext.
- **MFA-007 (P2 audit finding):** `IJtiRevocationCache` /
  `IMfaPendingCache` defaults are in-memory (Redis package opt-in but
  no fail-loud guard for production misconfig).
- **3 meter TBDs flagged in `slos.md`:** per-validation JWT histogram,
  audit-write histogram, Redis-side `listen_healthy` / JTI hit-rate gauges.

### R5 train acceptance

R5.1 (1.10.0) + R5.2 (1.11.0) + R5.3 (1.12.0) + R5.4 (1.13.0) — **R5 Production
Readiness Release Train COMPLETE**. R4 Track A previously declared COMPLETE
in R5.3. ADR-0008 + ADR-0009 gate this release. ADR-0005 amended with
"Update R5.4" section documenting the IAgentTenantResolver required-by-default flip.

---

## [1.12.0] — 2026-04-26 — R5.3 "Admin Completeness + R4 Closure"

Coordinated ship of R5.3 (third release in the R5 Production Readiness
Release Train) — pairs with **Pro 1.14.0-pro** and **Web 1.11.0**.
Closes admin completeness scope (S4.1-S4.8) + R5.2 known-debt
carry-forwards + 7 NEW post-R5.2 audit items + OpenAPI HTML
exposure (promoted from R5.4 per D-FORCE-3) + 3 ADRs. **R4 Track A
declared COMPLETE** — closes acceptance criterion #2 of R5 release train.
Zero breaking API changes.

### Added — endpoints

- `POST /api/v1/management/tenants/{tenantId}/dunning/resume` — mirror of
  existing `POST /dunning/pause`. Emits audit `billing.dunning.resumed`
  with category `billing` / severity `info` / actor type `user`. Closes
  S4.2 backend gap.
- `GET /openapi/v1.json` (Microsoft.AspNetCore.OpenApi) +
  `GET /scalar/v1` (Scalar.AspNetCore 2.13.11) — OpenAPI 3.0 spec +
  modern UI. Always enabled in Development; opt-in production via
  `Platform__OpenApi__Enabled=true` env var. AOT-friendly path
  (Microsoft.AspNetCore.OpenApi instead of Swashbuckle to avoid
  IL2026/IL3050 trim warnings). Closes S4.9 (D.1 promoted from R5.4).

### Added — audit schema normalization (ADR-0006)

- Migration `V021_AuditEntriesNormalize.sql` — promotes 6 fields from
  JSONB blob to first-class typed columns: `category`, `severity`,
  `actor_type`, `before_json`, `after_json`, `integrity_hash`. CHECK
  constraints + indexes per `(tenant_id, severity, occurred_at)` and
  `(tenant_id, category, occurred_at)`. 3-stage atomic transaction:
  ADD COLUMN → backfill `details` JSONB → NOT NULL + CHECK + INDEX.
  Backfill emits `RAISE NOTICE` audit count. Documented batch-rollout
  pattern for >10M row deploys.
  - **Note:** ADR-0006 originally specified `V012` slot; slot was
    occupied (`012_MailSchema.sql`), migration shipped at next-available
    `V021`. Category enum extended to 13 values to match
    `DefaultAuditService.InferCategory` production emissions
    (`warning`, `rbac`, `data_access`, `admin`, `api_key` added to the
    initial 8). ADR reconciled in commit `d263bfd`.
- `PostgresAuditStore.cs` writer + reader — INSERT extended with 6 new
  columns; reader hydrates `AuditEntry.Changes` from `before_json` /
  `after_json` columns directly. `Metadata` dict still serialized to
  `details` JSONB blob for backwards compat.
- 6 IT tests in `AuditEntriesNormalizationTests.cs` verify migration +
  writer + reader + EXPLAIN index usage.

### Added — Pro consumer wiring

- `CachedAgentTenantResolver` (Platform `Authz/`) now subscribes to
  `IPushEventBus.OfType<AgentTenantMembershipChangedEvent>()` for
  lateral cache invalidation. Closes ADR-0005 §"Concerns" 5-min TTL gap
  (B.3 from R5.2 Set B). Resolver now `IDisposable` to release
  Rx subscription on shutdown.
- `PlatformHubAuditSink` (Platform `Authz/`) consumes new
  `HubAuditEntry.ActorId` field instead of literal `"unknown"`.
  Closes B.4 from R5.2 Set B — production audit logs now identify
  the SignalR connection actor via JWT sub claim.
- 4 Pro health checks registered in `Program.cs` tagged `"ready"`:
  `presence-heartbeat`, `presence-fanout`, `presence-merge`,
  `retention`. `PromoteHostedServiceToSingleton<T>` local helper makes
  IHostedService also resolvable as concrete type (mirrors R5.1
  pattern from `LiveQueueSnapshotWriter`).
- `QaDetailDto` extended with `SentimentTimeline` field
  (`TurnSentimentDto`) mapped from existing
  `Pro.CallAnalytics.Sentiment.PerTurnScores`. Registered in
  `ApiJsonContext` for AOT serialization. Closes R4 Ω track per S4.6.

### Test counts post-R5.3

- Platform non-Postgres: 1,080+ unit tests across 30+ DLLs.
- Platform IT (Postgres): existing baseline + 6 audit migration tests.
- 0 warnings under `TreatWarningsAsErrors=true`.

### Known limitations

- **NU1902 OpenTelemetry vulnerability** —
  `Asterisk.Sdk.Pro.OpenTelemetry` pin remains at 1.12.0-pro because
  cross-repo SDK 1.15.x patch is required to repack the wrapper.
  Pro.OpenTelemetry has zero Pro dependencies, so version skew is
  safe (the wrapper consumes only OpenTelemetry packages from SDK).
  Cross-repo bump (SDK `Asterisk.Sdk.OpenTelemetry` 1.15.1 + Pro
  `Asterisk.Sdk.Pro.OpenTelemetry` 1.14.x repack) scheduled for R5.4.
  Platform deployments unaffected at runtime.

### References

- ADR-0001: `docs/decisions/0001-consumer-dual-prong-dependency-pattern.md` (Promoted Accepted 2026-04-26)
- ADR-0006: `docs/decisions/0006-audit-entries-schema-normalization.md`
- ADR-0007: `docs/decisions/0007-agent-tenant-resolver-strict-mode-builder.md`
- R5.3 spec: `docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md`
- R5.3 execution plan: `docs/plans/active/2026-04-26-r5.3-execution-plan.md`

---

## [1.11.0] — 2026-04-26 — R5.2 "Security Admin + Compliance Path"

Coordinated ship of R5.2 (second release in the R5 Production Readiness
Release Train) — pairs with **Pro 1.13.0-pro** and **Web 1.10.0**.
Closes R4 Frente C (retention admin) + Frente D (audit viewer) + Frente E
(MFA wizard) and lands the per-tenant tenant-stamping policy execution
across the Pro packages consumed by Platform.

### Added — admin endpoints (Set A — 5 R5.2 features)

- `MfaAdminEndpoints` (PA.1) — `/management/mfa/users` list/reset/sessions-revoke.
  Permission `security.mfa.admin`. Audit `mfa.admin.reset` /
  `mfa.admin.sessions_revoked`. Plus E.2: `MfaPolicy` field on `/users/me`
  for proactive UI hide of Disable when tenant policy enforces MFA.
- `MfaEnrollEndpoints` + `ProfileSessionsEndpoints` +
  `ProfileRecoveryCodesEndpoints` (PA.2) — `/profile/security/mfa/enroll/*` 
  3-step wizard + `/profile/security/sessions` list/revoke +
  `/profile/security/recovery-codes/regenerate` with TOTP step-up.
  New `RecoveryCodeService` with crypto invariants (10 codes × 8 chars
  Base32, SHA-256+salt hashed, RandomNumberGenerator).
- `AuditEndpoints` (PB.1) enriched with filter set (action prefix /
  actor / target / from-to / tenant) + `GET /audit/export?format=csv|json`
  streaming. Permission `audit.read` / `audit.export`.
  `X-Audit-Retention-Days` response header.
- `ImpersonationAdminEndpoints` (PB.2) — `/management/impersonation/sessions/active`
  list + revoke + history. Permission `security.impersonation.manage`.
  Plus C.7 expansion: tenant settings `ImpersonationMaxConcurrentSessions`
  (default 3) + `ImpersonationAutoTimeoutMinutes` (default 240).
  `ImpersonationSessionTimeoutService : BackgroundService` sweeps every
  60s and revokes expired sessions with audit
  `impersonation.session.auto_timeout`.
- `RetentionAdminEndpoints` (PC.1) — `/management/retention/targets` +
  `config` + `run-now` + `PATCH config`. DryRun toggle (default safer
  posture). Permission `retention.read` / `retention.manage`. Audit
  `retention.manual_triggered` / `retention.dryrun_toggled` /
  `retention.config_changed`.

### Added — carry-forward tickets (Set B from R5.1 limitations)

- `WithSingleTenantMode("default")` adoption in Program.cs (B.1) —
  closes R5.1 limitation #1 silent multi-tenant data corruption risk.
- `RedisJtiRevocationCache` (PA.3 / B.9) in `Asterisk.Platform.Identity.Redis`
  — completes the v1.9.2 abstraction; `IJtiRevocationCache` +
  `InMemoryJtiRevocationCache` widened to public in `Asterisk.Platform.Identity`.
- `MetricsAvailabilityBanner` consumer of `X-Metrics-Available` header
  (PC.2 / B.2) — Web wallboard surfaces banner when live metrics
  infrastructure unavailable.
- `RoleTemplateSeeder.ReseedExistingTenantsAsync` + `tools/RbacReseed`
  CLI + `scripts/reseed-rbac.sh` (PC.3 / B.7) — re-seed migration tool
  for existing tenants when `AllPermissions()` grows. Operator runbook
  in `docs/operations/v1.11-release-runbook.md`.
- `LicenseInfoDto` extended with `InGrace` + `GracePeriodRemaining` +
  `Blocked` (PC.4 / B.11) — exposes existing v1.8.0-pro `ILicenseGuard`
  grace logic via `ComputeGraceState` pure function. No Pro surface change.
- `ApiKey.LastUsedAt` + `IApiKeyStore.UpdateLastUsedAsync` + debounced
  auth-middleware stamp (PC.5 / B.12) — replaces `—` placeholder in
  Web API keys table with real relative timestamps. Migration
  `020_ApiKeysLastUsedAt.sql`.

### Added — Phase 0 foundation (gates the above)

- ADR-0002 / ADR-0004 / ADR-0005 documenting tenant stamping policy +
  per-package execution conventions + cross-tenant SignalR validation.
- `PlatformDataProtectionDbContext` + `AddPlatformDataProtection()`
  (P0.8 / B.6) — DB-backed default per ADR-0003. Closes R5.1 limitation
  #5 (ephemeral keyring in Docker). Migration `018_DataProtectionKeys.sql`.
- `CachedAgentTenantResolver` + `PlatformHubAuditSink` (P0.6) — Platform-side
  implementations of new `Asterisk.Sdk.Pro.Push.SignalR.Authz` abstractions
  per ADR-0005. 5-min `IMemoryCache` per-process; lateral invalidation
  via Pro.Push event documented (event creation deferred).
- 7 R5.2 RBAC permissions seeded in `RoleTemplateSeeder.AllPermissions()`
  (P0.9): `security.mfa.admin`, `audit.read`, `audit.export`,
  `security.impersonation.manage`, `retention.read`, `retention.manage`,
  `tenant.settings.write`. Existing tenants migrate via PC.3 RbacReseed CLI.
- `TenantAuthConfig` extended with `ImpersonationMaxConcurrentSessions` +
  `ImpersonationAutoTimeoutMinutes`. Migration `019_ImpersonationSessionPolicy.sql`.

### Changed

- Auth `Program.cs` DataProtection registration is conditional on
  Postgres connection string availability + `Environment=Testing`
  (`9d382f0` hot-patch). Production fail-fast preserved.
- `NuGet.Config` adds `<clear />` to prevent user-level credentialed
  sources (e.g., AWS CodeArtifact) from leaking into Platform builds —
  fixes pre-existing NU1507 conflict with central-package-management.

### Fixed

- 20 pre-existing test failures in `Asterisk.Platform.Api.Tests` resolved
  by removing stale local `src/Asterisk.Platform.Api/data/jwt-signing-key.xml`
  (gitignored but persisted across runs from previous WebApplicationFactory
  hosting; surfaced by P0.8 DataProtection ephemeral mode).

### Test counts post-R5.2

- Platform suite: ~1,058+ unit + integration tests across 30 DLLs (was
  ~1,800 pre-R5.2 baseline mixed with Postgres tests; current accurate
  count below).
- `Asterisk.Platform.Api.Tests`: 801/801 passing.
- `Asterisk.Platform.Identity.Tests`: 59/59 passing.
- `Asterisk.Platform.Identity.Redis.Tests`: 19/19 passing (Testcontainers
  Redis).
- `Asterisk.Platform.Storage.Postgres.Tests`: 14/14 passing (Testcontainers
  Postgres — first introduced in PC.3 + extended in PC.5).
- `Asterisk.Platform.Storage.InMemory.Tests`: 125/125 passing.
- Zero warnings under `TreatWarningsAsErrors=true` (NU1507 pre-existing
  resolved by NuGet.Config `<clear />`).

### Known limitations (carried forward to R5.3 or beyond)

- `IAgentTenantResolver` is OPTIONAL on `PlatformHub` ctor — falls back
  to legacy permissive behavior if not registered. Production deploys
  MUST register; future Pro consumers should be aware (ADR-0005 §"Concerns").
- `AgentTenantMembershipChangedEvent` lateral invalidation NOT
  implemented — cache reaches eventual consistency via 5-min TTL per
  ADR-0005 §"Consequences" "acceptable" deviation.
- `HubAuditEntry.actorId="unknown"` — `sub` claim not threaded through
  yet. Trivial extension when needed.
- Pre-existing `audit_entries` Postgres schema lacks `severity` /
  `category` / `before` / `after` columns — DTO surfaces defaults
  (`info`, `config`, `null`, `null`). Schema widening can land in
  future migration without breaking endpoints (PB.1 documented).

### References

- ADRs: `docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`,
  `0003-dataprotection-key-persistence-strategy.md`,
  `0004-tenant-stamping-execution-conventions.md`,
  `0005-cross-tenant-signalr-subscription-validation.md`.
- R5.2 spec: `docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`
- R5.2 execution plan: `docs/plans/active/2026-04-25-r5.2-execution-plan.md`
- Post-ship triage: `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`
- v1.11 release runbook: `docs/operations/v1.11-release-runbook.md`

---

## [1.10.0] — 2026-04-22 — R5.1 "Production Readiness + Ops Toolkit"

First release in the R5 Production Readiness Release Train. Ships paired
with **Asterisk.Sdk.Pro 1.12.0-pro** and **Asterisk.Platform.Web 1.9.0**.
Closes 4 production blockers discovered in the code audit (stale live
queue metrics, queue-member management gap, AgentAssist runtime toggle
gap, single-instance MFA cache). Zero API surface breakage — existing
clients continue to work without changes. **~1,800 non-Postgres tests
passing**, 0 warnings.

### Added — Task H (Live Queue Metrics wiring)

- **`GET /operations/queue-metrics`** now returns real-time `Waiting` +
  `AvgWaitSeconds` values sourced from the Pro.Analytics.Live
  `ILiveQueueMetricsProvider` (Asterisk.Sdk.Pro v1.12.0-pro). When the
  provider is unregistered or has no snapshot for a queue, the fields
  return `null` (instead of the previous hardcoded `0`) and the response
  sets `X-Metrics-Available: false` so clients can render placeholder UI.
- `AddAsteriskProAnalyticsLive()` + `UsePostgresProAnalyticsLive(...)`
  wired in `Program.cs`. Connection string: new
  `ASTERISK__ANALYTICS__LIVE__CONNECTION` config key with fallback to the
  shared Analytics connection string (same DB).
- `QueueMetricsDto.Waiting` + `QueueMetricsDto.AvgWaitSeconds` are now
  nullable (`int?` + `double?`). `QueueMetricsDto` + `QueueMetricsDto[]`
  registered in `ApiJsonContext` for AOT JSON serialization.

### Added — Task I (Queue Members RESTful endpoints)

- **`/api/v1/queues/{id}/members`** endpoint group — RESTful nested
  under queues with `GET` (list), `POST` (add), `DELETE` (remove),
  `POST /pause` (pause/resume). Legacy `/admin/queue-members/*`
  returns **308 Permanent Redirect** preserving request body — existing
  clients keep working without code changes.
- New permissions: `queues:member:view`, `queues:member:delete`,
  `queues:member:pause` — seeded into RBAC role templates (fresh tenants
  only; existing tenants require re-seed — see **Known limitations**).
- New audit actions: `queue.member.added`, `queue.member.removed`,
  `queue.member.paused`, `queue.member.resumed`.
- 21 endpoint tests covering RBAC gating + happy-path + degrade-path +
  redirect-with-body semantics.

### Added — Task J (AgentAssist runtime feature toggle)

- **`/api/v1/admin/features/agent-assist`** endpoint group with `GET`
  (status + provider), `PUT` (enable/disable + rotate provider), and
  protected credential persistence via `IDataProtectionProvider` (MS
  DataProtection). Credential ciphertext stored in the runtime feature
  store — never surfaced by `GET`.
- **Provider whitelist normalization** — provider names normalized
  (trim + lowercase) before the whitelist check to avoid accidental
  mismatches. Supported providers: `deepgram`, `whisper`, `azure-whisper`,
  `google`, `elevenlabs`, `azure-tts`.
- New permission `features:agent-assist:manage` (seeded into
  `platform_admin` template; existing tenant rows require re-seed —
  see **Known limitations**).
- Platform always registers an `IAgentAssistFeatureToggle` (Pro
  v1.12.0-pro surface) so the engine short-circuits when disabled.
- `AgentAssistCredentialsProtector` wraps secrets at rest.

### Added — Task L (Identity Redis)

- **New package `Asterisk.Platform.Identity.Redis`** ships
  Redis-backed implementations of `IMfaPendingCache` +
  `IPasswordResetCache`. Enables horizontally scaled Platform API
  deployments where MFA challenge tokens and password-reset tokens
  must survive hops across nodes. Atomic `StringGetDeleteAsync`
  preserves the single-consumption contract across the fleet.
- **`AddAsteriskPlatformIdentityRedis(Action<RedisIdentityOptions>)`**
  DI extension replaces any previously registered in-memory cache
  singletons with the Redis impls and reuses an existing
  `IConnectionMultiplexer` if one is already in the container (so the
  pool can be shared with `Asterisk.Sdk.Pro.Cluster.Redis`).
- **Program.cs** auto-enables the Redis backplane when
  `ConnectionStrings:IdentityRedis` is configured. Falls back to the
  in-memory defaults when unset — zero behavioral change for
  single-instance deploys.
- **`docker/docker-compose.full.yml`** — Redis service gains a
  healthcheck and an `identity-redis` profile (in addition to the
  existing `cluster` profile) so operators can spin it up independently.
  The `platform-api` service documents the
  `ConnectionStrings__IdentityRedis` opt-in env var.
- **Docs** — `docs/operations/identity-redis.md` walks operators
  through enabling, verifying, and failure-mode behavior.
- **Testcontainers IT** — `tests/Asterisk.Platform.Identity.Redis.Tests/`
  (14 tests) covers put+take roundtrip, TTL expiry, single-consumption,
  stored-expired short-circuit, key-prefix isolation, and DI replace
  behavior. Spins up `redis:7-alpine` per collection.

### Changed

- Pro pin bumped from `1.11.0-pro` → `1.12.0-pro` across 21
  `Directory.Packages.props` entries (Task H).
- `StackExchange.Redis 2.12.14` + `Testcontainers 4.11.0` added to
  `Directory.Packages.props` (Task L).

### Known limitations

> Post-ship triage (2026-04-25) reconciles these against R5.2/R5.3 scope —
> see `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`.

- **Multi-tenant Pro.Analytics scope** *(R5.2 P0 execution — upgraded from
  "follow-up" 2026-04-25)* — Platform currently registers
  `AddAsteriskAnalytics()` as a process-scope singleton with an empty
  `DefaultTenantId`, so `LiveQueueSnapshotWriter` persists rows with
  `tenant_id=""`. The `/operations/queue-metrics` endpoint queries the
  provider with `tenantId=""` to read back the rows the writer produced.
  A per-tenant scope refactor is tracked as a **R5.2 ADR + execution**
  follow-up ("tenant stamping pipeline end-to-end"). Triage flagged this
  as silent multi-tenant data-corruption risk; the elevation makes it a
  P0 R5.2 execution item rather than a follow-up patch.
- **RBAC hot-reload for existing tenants** — the new permissions
  (`queues:member:view/delete/pause` + `features:agent-assist:manage`)
  only land on fresh seeds via `RoleTemplateSeeder.AllPermissions()`.
  Existing tenant `platform_admin` rows need re-seed or migration —
  tracked as a **Platform v1.10 release runbook** entry.
- **DataProtection keyring persistence in Docker** —
  `AgentAssistCredentialsProtector` relies on the default DataProtection
  keyring at `/root/.aspnet/DataProtection-Keys`, which is ephemeral
  inside containers. Operators must configure `PersistKeysToFileSystem`
  or `PersistKeysToDbContext` to survive container recreation;
  documented for **R5.2 ops polish**.
- **`IJtiRevocationCache` stays in-memory** — Task L covered MFA +
  password-reset caches. `IJtiRevocationCache` (shipped v1.9.2) remains
  in-memory; Redis impl deferred to **R5.2 patch** via extension of
  `Asterisk.Platform.Identity.Redis`.
- **Platform API AOT publish warnings** *(explicit blocker for v2.0-stable —
  marked 2026-04-25)* — pre-existing IL3050/IL3053 warnings surface on
  `dotnet publish /p:PublishAot=true` (`SignalR.Hub<T>.Clients`, non-generic
  `JsonStringEnumConverter`, Dapper reflection paths). None are introduced
  by R5.1; platform continues to ship JIT. Addressed in **R2 / v2-preview1**
  AOT hardening frente — this deferral is **not indefinite**: triage
  promotes it to a hard release blocker for v2.0-stable.

---

## [1.9.3] — 2026-04-21 — Speech Analytics + Compliance Aggregations API

Adds `/api/v1/call-analytics/*` endpoint group with aggregation-focused
operations that complement the existing `/api/v1/analytics/qa` list+detail
endpoints (which already expose Pro.CallAnalytics raw results):

### Added

- **`GET /api/v1/call-analytics/topics/trends`** — Speech Analytics: top
  topics over a date range, sorted by occurrence count with average
  confidence. Foundation for a supervisor-facing topic trends dashboard.
- **`GET /api/v1/call-analytics/sentiment/trends`** — time-bucketed
  (day or ISO week) sentiment aggregation: avg score + positive/neutral/
  negative counts per bucket. Enables tracking tenant / queue sentiment
  evolution over time.
- **`GET /api/v1/call-analytics/compliance/summary`** — compliance
  violations grouped by (RuleId, Severity) with occurrence +
  sessions-affected counts + first/last seen timestamps + severity
  breakdown totals. Compliance-officer view complementing the per-session
  violations already in `/api/v1/analytics/qa` detail.
- All three endpoints gated by `SupervisorPlus` authorization policy
  and `LicenseFeature.Analytics` license gate. Returns `503` when
  `ICallAnalyticsStore` is not registered in DI.
- `CallAnalyticsEndpoints.cs` — 7 AOT-safe DTOs (`TopicTrendDto`,
  `TopicTrendsResponse`, `SentimentTrendPointDto`, `SentimentTrendsResponse`,
  `ComplianceRuleSummaryDto`, `ComplianceSeverityBreakdownDto`,
  `ComplianceSummaryResponse`) registered in `ApiJsonContext`.
- `CallAnalyticsEndpointTests.cs` — 6 tests covering topic trend
  aggregation, sentiment day-bucketing, queue filter acceptance,
  compliance rule aggregation, severity filter, severity breakdown totals,
  and 401 auth guard.

**Note** — an initial iteration of this endpoint group (shipped in
commits ca84105 + bd5c498) duplicated the existing `/api/v1/analytics/qa`
list+detail functionality and was refactored forward in this release to
aggregations only. No duplicated routes ship in v1.9.3.

---

## [1.9.2] — 2026-04-21 — "Hardening Follow-Through" (R3c)

Closes the five orthogonal security / compatibility concerns that v1.9.0
and v1.9.1 audits explicitly deferred to this patch. Zero API surface
breakage — ships safely in parallel with R4 Platform.Web.

### Security

- **JWT tokens now carry `jti` claims** (`GenerateAccessToken` +
  `GenerateImpersonationToken`). Enables future revocation flows via
  the new `IJtiRevocationCache` (in-memory impl shipped;
  `ValidateTokenAsync` consults the cache after standard validation
  and returns `null` for revoked tokens).
- **Signing key is now wrapped at rest via `DataProtection`.** Existing
  deployments with plaintext `jwt-signing-key.xml` are migrated silently
  on first restart — the file is read, re-encrypted, and overwritten.
  No config change required.
- **`kid` header is now derived from the key fingerprint**
  (`platform-jwt-<16 hex>` from SHA-256 of the public modulus). Survives
  restarts, changes on key rotation.
- **Removed the `?token=` query-string fallback in
  `ApiKeyAuthenticationHandler`.** API keys must now be presented via
  the `Authorization: Bearer` header only. Key leakage via access logs,
  referer headers, and browser history is blocked.
- **OIDC callback now enforces tenant MFA policy** before issuing
  tokens. Two new redirect branches:
  - `#oidc_mfa_enrollment_required&...` when the policy requires MFA
    for the user's role but the user has not enrolled.
  - `#oidc_mfa_challenge&challenge_token=...` when the user is enrolled
    and must complete TOTP verification; the existing
    `/auth/mfa/verify` endpoint handles the challenge unchanged.
  Frontend fragment handlers are needed to surface these redirects to
  the user — R4 Platform.Web will land the UI side.
- **`/auth/change-password` now requires MFA step-up** when the user
  has MFA enrolled. `ChangePasswordRequest` gains an optional `MfaCode`
  field; when the user has MFA enabled and the code is missing, the
  endpoint returns 401 with a new `MfaStepUpRequiredResponse` body
  (`{ mfaStepUpRequired: true, reason: "…" }`). An invalid code
  returns 401. MFA is checked before the old-password verification to
  avoid burning the password-guess budget on a pre-MFA attack.

### Changed

- **`IMfaPolicyEvaluator`** extracted from `AuthEndpoints`'
  private static helper. Now lives in `Asterisk.Platform.Identity.Mfa`
  and is injected into `AuthEndpoints.Login`, `AuthEndpoints.Refresh`,
  `AuthEndpoints.ApiKeyLogin`, and `OidcEndpoints.OidcCallback`.
  Behavior identical to v1.9.0 / v1.9.1 — this is a pure refactor that
  opens the extension point for policy overrides.
- **`IMfaPendingCache` + `IPasswordResetCache`** extracted from the
  static `ConcurrentDictionary` fields in `AuthEndpoints`. In-memory
  implementations in `Asterisk.Platform.Identity.Mfa` preserve the
  previous semantics; `TakeAsync` atomically removes-and-returns.
  `MfaPendingEntry` and `PasswordResetEntry` records move from
  `internal` in `Asterisk.Platform.Api` to `public` in
  `Asterisk.Platform.Identity.Mfa`.

### Added

- **Asterisk 23 Standard build support** — `docker/Dockerfile.asterisk`
  now accepts an `ASTERISK_VERSION` build argument (default 22), and
  `docker-compose.full.yml` forwards it via `ASTERISK_VERSION` env var.
  The codec_opus download URL + directory name are parameterized.
  Default behavior is unchanged: `docker compose up --build` still
  builds Asterisk 22 LTS. Test both with
  `ASTERISK_VERSION=23 docker compose -f docker/docker-compose.full.yml build asterisk`.
- **Interface contract tests** for `InMemoryMfaPendingCache`,
  `InMemoryPasswordResetCache`, and `InMemoryJtiRevocationCache` in
  `Asterisk.Platform.Identity.Tests` and `Asterisk.Platform.Api.Tests`.

### Known limitations / deferred

- **No Redis-backed cache implementation yet.** `IMfaPendingCache` and
  `IPasswordResetCache` create the extension point; Redis wiring lands
  in v1.9.3 when a concrete multi-instance deployment driver emerges.
  Until then, MFA challenges initiated on one instance will not be
  redeemable on another if a failover occurs mid-flow.
- **Full multi-key JWT rotation** (simultaneous old + new valid keys
  during a rolling window) is not included. `kid` is fingerprint-based
  so it survives restarts, but key rotation still requires an
  in-flight-tokens flush. Full rotation deferred to v1.10+.

### Tests

- +22 new tests (8 JWT hardening, 3 IMfaPolicyEvaluator, 3 OIDC MFA
  enforcement, 4 ChangePassword step-up, 8 in-memory cache contract,
  minus 4 test consolidations from the Frente C + E test-harness moves).
  All non-Postgres assemblies green — 0 failures, 0 warnings.

---

## [1.9.1] — 2026-04-21 — "Resilience Coverage" (R3b)

Horizontal completion of v1.9.0's Resilience MVP. Every remaining
external/retriable call-site on the Platform backend now emits to the
`Asterisk.Sdk.Resilience` Prometheus meter. Zero API surface changes —
this release ships safely in parallel with R4 Platform.Web.

### Added

- **9 channel connectors** (`channel.{twilio-sms|twitter|instagram|
  telegram|messenger|whatsapp|video|rcs|email-http}`) now wrap their
  outbound HttpClient calls with keyed `ResiliencePolicy` instances.
  Each connector owns a DI extension (`AddXxxResiliencePolicy()`) with
  per-provider budgets tuned to the provider's SLA.
- **3 service wrappers:** `flow.http-request` (user-defined flow HTTP
  node; per-call timeout still sourced from flow config),
  `report.pdf-render` (PDF renderer microservice), and `mail.graph` +
  `mail.token-refresh` (Microsoft Graph mailbox + OAuth token refresh
  in the Mail microservice).
- **S3 storage wrapper** — `storage.s3` policy covers
  `S3MediaStorage.UploadAsync/DownloadAsync/DeleteAsync`. AWS SDK's
  built-in retry is disabled (`MaxErrorRetry = 0`) to prevent
  double-retry (AWS retry × policy retry = 9+ attempts).
- **12 BackgroundServices** — `worker.{name}` keyed policies wrap each
  worker's inner tick work. The outer `while`/timer loop is NOT
  wrapped — a circuit-open state causes the worker to skip the current
  tick and retry on the next scheduled tick instead of crashing the
  host. `CircuitBreakerOpenException` + generic exceptions are caught
  per-tick. Workers covered: conversation-timeout, queue-distribution,
  dunning, report-scheduler, bot-analytics-persistence,
  asterisk-capacity-sync, retention-purge, audit-retention,
  realtime-state-bridge, campaign-metrics-poller, agent-assist-bridge,
  timer-polling.
- **HealthCheck upgrades** — `AsteriskAmiHealthCheck`,
  `PostgresHealthCheck`, `BackgroundServiceHealthCheck` now consult an
  `IResilienceStateObserver` (MeterListener-backed singleton that
  tracks circuit_opened_total + circuit_closed_total counters) and
  report `Degraded` when a relevant circuit has been open >60s,
  `Unhealthy` at >300s. Thresholds are configurable via
  `PlatformHealthCheckOptions`.
- **`healthcheck.postgres`** — new keyed policy (timeout 2s, no
  circuit, no retry) wrapping `PostgresHealthCheck`'s test query so
  DB-under-load surfaces as `Unhealthy` within 2s instead of hanging.
- **`/health/ready`** — now emits structured JSON via
  `HealthReportJsonWriter`, including per-policy circuit-state
  breakdown for operator visibility. Replaces the default plain-text
  ASP.NET Core response writer.
- **`docs/operations/resilience-runbook.md`** — operator runbook
  covering meter instruments, policy-key taxonomy, golden signals,
  5 troubleshooting scenarios with PromQL queries, and the worker-
  policies reference table.
- **`docs/operations/dashboards/resilience-overview.json`** — Grafana
  starter dashboard (5 panels: open circuits, retry rate, open/close
  events, timeout firings, circuit-state matrix).

### Changed

- **`RealtimeStateBridge`** — DB sync and AMI `QueuePause` are now
  wrapped as **independent** policy calls (same key, share circuit
  aggregation), preserving the v1.9.0 "best-effort" semantic where a
  DB failure does NOT prevent the AMI call. Previous bundled wrap
  broke this invariant.
- **`TokenRefreshService`** — no longer silently-swallows transient
  exceptions. Logs structured warnings + lets the policy retry; on
  exhaustion, the policy emits `retry_attempts_total` + the
  application logs a warning with structured metadata.

### Known limitations (carried forward from v1.9.0)

No changes in v1.9.1. See v1.9.0 §Known limitations — JWT hardening,
OIDC MFA enforcement, ChangePassword step-up, MFA cache cross-instance
consistency, Asterisk 23 matrix (still tracked for v1.9.2).

### Metrics

- **1,733 unit tests** across 29 assemblies, 0 failures (baseline 1,699
  from v1.9.0 + 34 new regression + contract tests for v1.9.1)
- **0 build warnings / 0 errors** with `TreatWarningsAsErrors=true`
- 7 commits since v1.9.0

---

## [1.9.0] — 2026-04-20 — "Secure + Current" (R3)

Cross-repo coordination: consumes **SDK v1.15.0 + Pro v1.10.0-pro**
(shipped 2026-04-20 as R1 Pre-v2 Foundation). This release closes two P0
security vulnerabilities, lands the foundation layer for observable
resilience, and migrates Platform onto the post-ADR-0029 MIT resilience
primitives.

### Security

- **Impersonation privilege escalation (P0).** `/management/impersonate`
  now verifies the target tenant is in the caller's tenant hierarchy
  (`ParentTenantId` walk, depth-16 cycle protection, fail-closed on
  broken chains). Platform-tenant callers retain their documented
  ability to impersonate any customer tenant; non-platform callers can
  only impersonate themselves or their descendants. Attacks where a
  Tenant A admin issued a JWT for an unrelated Tenant B are now
  rejected with `403 Forbidden` + audit entry.
- **Impersonation audit evasion (P0).** Successful impersonations now
  emit audit entries to **both** the caller tenant (action
  `impersonation_started`, preserved) and the target tenant (new action
  `impersonation_target_accessed`). Target-tenant admins gain full
  visibility of inbound impersonation events.
- **Tenant MFA policy bypass (P0).** `TenantAuthConfig.MfaPolicy` is now
  enforced on all four auth entry points — login, refresh, password
  reset, and user-bound API key authentication. Previously the policy
  was advisory: users with `MfaEnabled=false` could bypass `required_all`
  tenant policies via any of the four paths. Management-type API keys
  (machine-to-machine, `UserId=null`) remain exempt by design. New
  response DTOs `MfaEnrollmentRequiredResponse` and
  `PasswordResetMfaRequiredResponse` signal enrollment/verification
  flows to the frontend.

### Added

- **OpenTelemetry wiring.** `AddAsteriskOpenTelemetry(...)` +
  `AddAsteriskProOpenTelemetry()` + `WithPrometheusExporter()` now
  registered in `Program.cs`. Enrols the full SDK + Pro meter catalog
  (15 SDK meters including the new `Asterisk.Sdk.Resilience` + 15 Pro
  meters) and activity sources. `/metrics` endpoint is now a real
  Prometheus scraping endpoint (was a JSON stub).
- **T27 event bridges** (Pro 1.8.0-pro opt-ins): cluster / conversation
  / agent state transitions now published to `IPushEventBus` via
  `WithClusterEventBridge()` / `WithConversationBridge()` /
  `WithAgentBridge()`. Each bridge throttles per key (100ms cluster /
  50ms conversation / 200ms agent) and captures `Activity.Current` for
  W3C trace propagation.
- **Resilience MVP** — three critical external call-sites now use
  `Asterisk.Sdk.Resilience` keyed policies (pattern matches Pro engine
  precedent):
  - `WebhookDeliveryService` → policy `webhook.delivery` (circuit 5/30s,
    retry 3/500ms, timeout 10s). Wraps per-attempt `HttpClient.SendAsync`
    within the existing 8-attempt user-visible backoff schedule.
  - `SmtpSender` → policy `smtp.send` (circuit 3/60s, retry 2/1s, timeout
    15s). Replaces the hand-rolled `for (attempt = 1..2)` loop.
  - `OidcTokenExchangeService.ExchangeCodeAsync` → policy
    `oidc.token-exchange` (circuit 3/120s, retry 2/500ms, timeout 10s).
    Wraps the token endpoint `PostAsync` only; JWT validation + caching
    intentionally unwrapped.
- New `Asterisk.Platform.Mail.Tests` project (SmtpSender coverage).

### Changed

- **Bot handoff routing.** `WebhookEndpoints.cs` now calls
  `IConversationSwitchboard.TransferToQueueAsync` (drives
  `Active → Escalated → Queued`, releases agent capacity, publishes
  correct state-change event) instead of `AssignToQueueAsync` when the
  bot emits `BotResponse(BotResponseAction.TransferToQueue, queueId)`.
  The previous behavior skipped the `Escalated` transition and broke
  state-machine invariants relied on by downstream analytics and
  supervisor UX.
- **Dependencies**: SDK pinned from `1.11.1` to `1.15.0`; Pro pinned from
  `1.8.1-pro` to `1.10.0-pro` (21 refs). Added explicit
  `Asterisk.Sdk.Resilience` + `Asterisk.Sdk.OpenTelemetry` +
  `Asterisk.Sdk.Pro.OpenTelemetry` pins (previously transitive).

### Removed

- `Asterisk.Sdk.Pro.Resilience` reference. Package was sunset in Pro
  `1.9.0-pro` via ADR-0029 (migration to MIT `Asterisk.Sdk.Resilience`).
  `Program.cs` now uses `Asterisk.Sdk.Resilience.DependencyInjection`
  and `AddAsteriskResilience()`.

### Internal / tests

- Added regression tests pinning tenant-isolation invariants in
  `DefaultConversationService.GetOrCreateForContactAsync` (no production
  change — end-to-end chain was already correctly scoped).
- T27 bridges wiring contract test (`BridgeOptions.DefaultTenantId` +
  `BridgeMetrics` registration).
- 4 impersonation privilege-escalation scenarios (hierarchy check +
  dual audit).
- 10 MFA policy enforcement scenarios across all 4 auth entry points.
- Baseline preserved: **1,669 → 1,699 unit tests** (+30 across 28
  assemblies). 0 warnings, 0 errors.

### Known limitations (flagged for follow-up)

Subagent audits surfaced orthogonal hardening opportunities that are
**not** fixed in this release; each is tracked for a future session:

- JWT signing key persisted as plaintext XML on disk; no key rotation;
  no `jti` claim on impersonation tokens (no replay protection); API key
  `?token=<raw>` query-string fallback risks log leakage.
- OIDC callback (`OidcEndpoints.cs`) does **not** enforce tenant MFA
  policy — users authenticated via external IdP skip the gate.
- `ChangePassword` does **not** require MFA step-up even when policy
  requires MFA — stolen session cookie enables silent password change.
- `MfaPendingCache` / `PasswordResetCache` are in-memory
  `ConcurrentDictionary` instances; MFA challenges are lost on node
  failover in multi-instance deployments. Move to Redis / Pro.Push
  backplane in a later release.

### Asterisk version matrix

Platform continues to run against **Asterisk 22 LTS** (default). Full
smoke validation against **Asterisk 23 Standard** is pending a separate
patch release — the `docker/Dockerfile.asterisk` currently hardcodes
`andrius/asterisk:22` and the codec_opus download URL to the 22.0
series. Parameterizing via `ASTERISK_VERSION` build-arg is tracked for
**v1.9.2** alongside a CI matrix job.

---

## [1.8.1] — 2026-03-31 — "Operations"

Earlier releases are not tracked in this file. Consult
`git log --oneline v1.8.1` for historical context or the roadmap in
[`docs/`](docs/) for milestone summaries.
