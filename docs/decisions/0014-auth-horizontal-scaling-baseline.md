# ADR-0014: Auth horizontal scaling baseline

**Status:** Accepted (initial 2026-04-27) · **Amended 2026-04-28 (v1.14.1)** —
v1-measured single-replica numbers replace projection; 4-replica still
pending v1.14.2 startup-hang fix. · **Further amended 2026-04-28 (v1.14.5)**
— "1 pool per replica" math corrected; ADR-0015 captures the
14-NpgsqlDataSource sprawl pattern revealed by R5.5 Phase C-L.
**Date:** 2026-04-27 · **Amendments:** 2026-04-28 (v1.14.1, v1.14.5)
**Context:** AHH Phase 5 (v1.14.0) + v1.14.1 empirical follow-up + v1.14.5 sprawl mitigation

## Context

Phase 3 (commit `fe58d28`) closed the multi-replica gate: the JWT
signing key is shared via the rotation pool, the `RedisJwtKeyStore`
upsert is CAS-correct, and a `RequireRedisStore=true` startup flag
fails fast when an operator forgets to wire the shared store. Phase 4
(commit `1c30580`) replaced BCrypt with Argon2id, projecting the
single-replica throughput knee from 75 req/s (R5.5 measured) to
~220 req/s (Phase 0 algorithmic projection).

Phase 5 codifies the horizontal scaling envelope and the operator-side
knobs needed to ship a multi-replica deployment safely. Without this
phase, Phase 3 + 4 are technically deployable but operators would need
to re-derive pool sizing + scaling guidance on their own.

## Decision

**Surface Postgres pool sizing through the existing connection-string
contract (no new abstraction); document the canonical knee envelope +
required-config checklist in a dedicated runbook + ADR; defer the actual
4-replica integration test to a follow-up commit when measurement
infrastructure is provisioned.**

Concrete shape:

1. **`AddPostgresStorage` accepts an optional
   `Action<NpgsqlDataSourceBuilder>` configuration hook**, replacing
   the bare `NpgsqlDataSource.Create(connectionString)` factory.
   Operators set pool size via standard Npgsql connection-string
   parameters (`Maximum Pool Size`, `Minimum Pool Size`,
   `Connection Idle Lifetime`); the new hook lets advanced users plug
   in tracing / type mapping / instrumentation builders without
   forking the library wrapper. Default behavior unchanged when
   neither param is supplied.

2. **`docs/operations/auth-horizontal-scaling.md`** — operational
   runbook covering: pre-flight checklist (multi-replica gate),
   post-Phase-4 knee envelope (single-replica + projected 4-replica),
   recommended pool sizing per tier, Postgres tuning template
   (`max_connections`, `shared_buffers`, `effective_cache_size`),
   what NOT to do (pgBouncer transaction-pool, missing rotation pool,
   Redis-off-at-runtime), and a verify-the-knee script outline using
   the existing `jwt-sweep.sh` harness.

3. **No new code-level scaling abstractions** — the existing connection
   string is the contract; no `PostgresPoolOptions` type, no separate
   options binding section. Reduces surface area; matches how
   StackExchange.Redis is already configured (connection string only).

4. **Phase 5 horizontal validation deferred** — the actual measurement
   of the post-Phase-4 4-replica knee on a multi-replica docker-compose
   stack ships as a follow-up commit (Phase 5 v1.14.1 patch) once the
   measurement window is scheduled. Phase 5 v1.14.0 ships the
   projection + the runbook so operators can flip the deployment with
   confidence in the design; the empirical confirmation lands shortly
   after.

## Considered alternatives

- **Build `PostgresPoolOptions` configuration record + bind from
  `appsettings.json:Postgres:Pool` section.** Rejected: redundant —
  the connection string already carries every parameter. New
  abstraction = more code surface + more ways to drift out of sync
  with what Npgsql actually does.
- **Adopt pgBouncer transaction-pool mode for unbounded conn
  multiplexing.** Rejected: breaks `LISTEN/NOTIFY` which Pro.Push +
  Pro.Cluster.Storage.Postgres rely on. Documented in the runbook's
  "what NOT to do" §.
- **Build a Testcontainers-based multi-replica integration test in
  this commit.** Rejected for v1.14.0 ship — adds Docker-in-Docker
  complexity to the test infrastructure and the result is a
  pass/fail bit, not the measured knee curve operators actually
  need. The `jwt-sweep.sh` harness against the staging
  docker-compose stack is the canonical measurement tool; the
  integration test would only verify that 2 replicas successfully
  exchange tokens (which the Phase 3.B unit test already covers
  via shared `RedisJwtKeyStore`).
- **Auto-detect replica count via env var + bake pool sizing into the
  app.** Rejected: Kubernetes / docker-compose / bare-metal all
  expose replica count differently; better to put one knob (the
  connection string) in the operator's hands and document the math
  than to encode N deployment platforms' detection paths.

## Knee envelope

**v1.14.1 amendment (2026-04-28):** the v1.14.0 ADR shipped with
projection-only numbers. Empirical measurement on the same hardware
shows post-AHH single-replica did NOT achieve the projected 220 req/s
knee — the projected gain from Phase 4 (Argon2id) was eaten by GC
pressure + connection-pool contention under sustained load. The
multi-replica numbers stay projected pending the v1.14.2 fix to the
startup hang documented in the runbook.

| Stage | Single-replica | 4-replica aggregate | p99 ≤ 250 ms | Source |
|---|--:|--:|---|---|
| R5.5 baseline (BCrypt12, no caches, sync writes) | 75 req/s | n/a | ⚠ marginal | v1-measured (R5.5 sweep) |
| **Post-AHH single-replica** | **~50 req/s** | n/a | ✓ at 50, ⚠ at 100 | **v1-measured 2026-04-28** |
| Post-AHH 4-replica aggregate | _projected_ ~50/replica | _projected_ ~200 | _pending v1.14.2 fix_ | projection-only |

The runbook §"Empirical single-replica jwt-sweep.sh post-AHH" carries
the full sweep table (10 / 50 / 100 / 250 / 500 req/s × 60 s) showing
500-error onset at 100 req/s and the same collapse curve at 250 / 500
seen in R5.5. The **AHH train delivered the architectural multi-replica
gate (Phase 3) but did NOT deliver the projected single-replica
throughput lift (Phase 4 Argon2id)**. Path forward documented in
the runbook §"v1.14.1 follow-up".

## Failure modes + mitigation

| Failure | Effect | Mitigation |
|---|---|---|
| Operator deploys N=4 without `RequireRedisStore=true` AND Redis is missing | Tokens flap between replicas | ADR-0012 §"Multi-replica gate checklist" — operators MUST tick `RequireRedisStore=true`. Runbook places it as the first checklist item. |
| Postgres `max_connections` < `replicas × Maximum Pool Size + headroom` | Connection failures + 500s | Runbook gives the exact formula; this ADR's "Postgres pool tuning" table holds the reference numbers. |
| Operator picks pgBouncer transaction-pool | `Pro.Push.Postgres` `LISTEN/NOTIFY` silent break | Runbook explicit "what NOT to do" §; future Pro 2.0 may refactor away from `LISTEN/NOTIFY` but until then this is a hard interop block. |
| Argon2id memory pressure exceeds container budget | OOM kills replicas under sustained traffic | ADR-0013 §"Memory pressure" — Server GC enabled + `2 × concurrent × 19 MiB` headroom; runbook §"Postgres pool tuning" covers the host-side. |
| Beyond-4-replica scaling needed | Postgres `max_connections` becomes the next ceiling | Out of scope for v1.14.0 — read-replica routing + DB-level sharding are R6+ candidates per `Asterisk.Sdk.Pro/docs/roadmap.md`. |

## Tested invariants

- `Asterisk.Platform.Storage.Postgres` builds + the existing 125/125
  `Storage.InMemory.Tests` continue passing — the new optional
  parameter is non-breaking.
- The multi-replica scenario is exercised at the unit-test level by
  `Phase 3.B`'s `JwtTokenServiceRotationTests` (cross-replica token
  validation via shared `InMemoryJwtKeyStore`) and by Phase 3.C's
  `RedisJwtKeyStoreTests.UpsertAsync_ShouldProduceSingleActive_WhenTwoReplicasRotateConcurrently`
  (Testcontainers Redis with two store instances).
- Phase 5 v1.14.1 follow-up will add a dedicated `MultiReplicaSmokeTests`
  Testcontainers integration covering full WebApplicationFactory
  cross-replica auth handshake + measure 4-replica throughput on the
  staging stack.

## Forward compatibility

- **Read-replica routing** (Postgres failover / scale-out reads):
  Npgsql 10.x supports `Host=primary,replica1,replica2;Target Session
  Attributes=read-write` for write-mostly + read-mostly routing. The
  existing `AddPostgresStorage(connectionString, configureDataSource)`
  signature accepts this directly via the connection string; no code
  change needed.
- **Per-tenant DB sharding**: requires `INpgsqlDataSourceProvider`
  abstraction (one data source per tenant). Out of scope for v1.14.0
  but the existing single-source registration doesn't block the
  refactor.
- **Connection multiplexing via pgBouncer** (when Pro 2.0 retires
  `LISTEN/NOTIFY`): drop pgBouncer in front of Postgres; Npgsql's
  pool talks to it transparently. No app-side change.

## Related

- ADR-0010 — Auth hot-path cache decorators (Phase 1 throughput math).
- ADR-0011 — Auth write-path deferral (Phase 2 throughput math).
- ADR-0012 — JWT rotation pool wire-up + multi-replica gate (the
  correctness prerequisite for ANY N>1 deployment).
- ADR-0013 — Password hash algorithm migration (Phase 4 throughput math
  + Argon2id memory budget).
- `docs/operations/auth-horizontal-scaling.md` — the operational runbook
  this ADR points operators at.
- `docs/operations/capacity-planning.md` — the broader capacity model;
  this ADR's auth knee envelope feeds it.

## Update 2026-04-28 (R5.5 Phase C-L · v1.14.5)

The "Postgres pool tuning" prescription in this ADR's `scale.yml`
configuration assumed **1 connection pool per replica** — math:
`4 replicas × Maximum Pool Size 50 = 200 conn demand vs
max_connections=220`. **That assumption is incorrect.**

R5.5 Phase C-L `presence` sweep (`KeepConstant(VU=100)` against
`docker-compose.full.yml`) exposed 13 % HTTP 500 with
`Npgsql.PostgresException (53300): sorry, too many clients already`.
Audit revealed **14 separate `NpgsqlDataSource.Create()` call sites**
across the Pro storage packages + Platform.Storage.Postgres, each
defaulting to `Maximum Pool Size=100`. Real per-instance demand is
therefore `14 × Maximum Pool Size` — for `scale.yml`'s 4-replica
deployment, `4 × 14 × 50 = 2 800` conn demand, far above
`max_connections=220`.

**Remediation strategy** (ADR-0015):

- **Phase 1 (v1.14.5, this release):** `ConnectionStringDefaults`
  helper at the Platform.Api composition root caps per-pool size at 10
  when the operator didn't override. Per-instance demand becomes
  `14 × 10 = 140` for SMB tier (single replica), comfortable under
  `max_connections=200` shipped in `docker-compose.smb.yml` /
  `production.yml`. **Multi-replica deployments using `scale.yml` need
  a follow-up amendment** to either bump `max_connections=600` or wait
  for Pro 1.16.0-pro Phase 2.
- **Phase 2 (Pro 1.16.0-pro, separate plan):** Pro packages gain a
  `Use*Storage(IServiceCollection, NpgsqlDataSource)` overload.
  Platform.Api builds **one shared `NpgsqlDataSource`** and passes it
  to all Pro `Use*` calls — sprawl collapses to `1 × Maximum Pool Size`
  per replica. The original `scale.yml` math (1 pool per replica) holds
  again post-Phase 2, this time for the right architectural reason.

**Status of `scale.yml` per this amendment:** the file's existing
`Maximum Pool Size=50` per replica continues to apply (it overrides the
Phase 1 Platform.Api default), but the sprawl means Phase 1 alone is
insufficient for 4-replica clean operation under high concurrent
burst. Two follow-up paths, in priority order:

1. **Bump `max_connections=600` in `scale.yml`** — covers the worst-case
   `4 × 14 × 50 = 2 800` demand only if it materialises (idle pools
   stay near `Minimum Pool Size`); empirically the steady-state demand
   is dominated by hot-path stores (auth, agents, queues), so 600
   should be sufficient with 100 + 100 buffer for postgres + admin.
   Out of scope for v1.14.5 (no scale.yml re-measurement run); track
   in next sprint.
2. **Wait for Pro 1.16.0-pro** — Phase 2 makes scale.yml's original
   `4 × 50 = 200 + 20 buffer` math correct again.

References:

- ADR-0015 — npgsql-datasource-sharing-strategy
- v1.14.5 CHANGELOG entry "ADR-0015 Phase 1 — Postgres pool sprawl mitigation"
- Pro 1.16.0-pro plan skeleton: `docs/research/archived/2026-04-28-Pro-1.16.0-pro-shared-datasource-skeleton.md`
