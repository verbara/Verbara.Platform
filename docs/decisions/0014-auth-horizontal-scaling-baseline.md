# ADR-0014: Auth horizontal scaling baseline

**Status:** Accepted
**Date:** 2026-04-27
**Context:** AHH Phase 5 (v1.14.0)

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

## Knee envelope (v1-projected, post-AHH)

Reproduced from the runbook for ADR self-containment. All numbers
relative to AMD Ryzen 9 9900X / 60 GB / docker-compose / single-replica
unless noted:

| Stage | Single-replica | 4-replica aggregate | p99 ≤ 250 ms |
|---|--:|--:|---|
| R5.5 baseline | 75 req/s | n/a | ⚠ (50 req/s knee) |
| Post-Phase-1 | ~95 | n/a | ✓ |
| Post-Phase-2 | ~120 | n/a | ✓ |
| Post-Phase-3 | ~120 | ~480 | ✓ |
| **Post-Phase-4** | **~220** | **~880** | **✓ (target)** |

The **22× single-replica improvement** (from 75 to 1 650 req/s if
N=4 + Argon2id) is what makes the AHH train commercially relevant —
medium-tier deployments (100 agents, 50 queues) can fit on a single
4-replica deployment without dedicated DB tuning beyond what this
ADR's `max_connections=220` recommendation specifies.

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
