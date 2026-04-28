# ADR-0015: NpgsqlDataSource sharing strategy across Pro storage packages

**Status:** Accepted (initial Proposed 2026-04-28 → Accepted 2026-04-28 v1.14.5 ship after Phase C-L SMB tier re-measurement)
**Date:** 2026-04-28
**Context:** R5.5 Phase C-L `presence` sweep findings + cross-Pro `NpgsqlDataSource.Create()` audit + post-fix re-measurement

## Context

R5.5 Phase C-L `presence` scenario (`KeepConstant(VU=100)`) against `docker-compose.full.yml` produced a 13 % HTTP 500 error rate with `Npgsql.PostgresException (53300): sorry, too many clients already`. Investigation revealed an architectural sprawl pattern across the Pro storage packages:

```
Inventory of NpgsqlDataSource.Create() call sites in Pro repo (audit 2026-04-28):

Asterisk.Sdk.Pro.AgentAssist.Storage.Postgres            1
Asterisk.Sdk.Pro.Analytics.Storage.Postgres              2 (Extensions + AnalyticsDbContext)
Asterisk.Sdk.Pro.Analytics.Storage.Postgres (Live)       1
Asterisk.Sdk.Pro.CallAnalytics.Storage.Postgres          1
Asterisk.Sdk.Pro.Cluster.Storage.Postgres                1
Asterisk.Sdk.Pro.Dialer.Storage.Postgres                 2 (Extensions + DialerDbContext)
Asterisk.Sdk.Pro.EventStore.Postgres                     2 (Extensions + EventStoreDbContext)
Asterisk.Sdk.Pro.Push                                    1 (TryAddSingleton — shared if first)
Asterisk.Sdk.Pro.Realtime.Storage.Postgres               2 (Extensions + RealtimeDbContext)

Plus Asterisk.Platform.Storage.Postgres                  1 (TryAddSingleton)
                                                       ───
                                                        14
```

When Platform.Api initializes with all Pro features enabled and a single `ConnectionStrings:Postgres` value (the typical SMB single-database deployment), each `Use*Storage(IServiceCollection, string)` call invokes `NpgsqlDataSource.Create(connectionString)` independently. Each resulting `NpgsqlDataSource` carries Npgsql's default `Maximum Pool Size=100`. **Theoretical worst-case demand from a single `platform-api` instance under sustained concurrent load: 14 pools × 100 conns = 1 400 connections.**

This sprawl was not exposed by:

- B-L #4 jwt-sweep (sequential, single pool ejercised — auth tables only)
- B-L #1-3 sequential rate-based scenarios r=10..500 req/s (low concurrent demand)

R5.5 Phase C-L `presence` (`KeepConstant(100)` — first concurrency-shaped scenario) was the first measurement that touched 3-4 stores per request × 100 concurrent VUs simultaneously. Demand crossed `max_connections=100` (postgres-alpine default in `docker-compose.full.yml`) and Postgres rejected new connection requests as "too many clients already".

The result is **not a measurement of the platform's true capacity** — it is a measurement of the connection-pool sprawl architecture's saturation point on a stack with default Postgres tuning.

## Decision

**Adopt a two-phase mitigation strategy: smart pool-sizing defaults at the Platform.Api composition root for immediate ship (Phase 1, this release), and shared `NpgsqlDataSource` overload across Pro packages for the architectural fix (Phase 2, Pro 1.16.0-pro).**

### Phase 1 — `ConnectionStringDefaults` at Platform.Api composition root (this release, v1.14.5)

1. **Introduce `Asterisk.Platform.Api.Services.ConnectionStringDefaults`** — a small static helper that parses an incoming connection string via `NpgsqlConnectionStringBuilder`, applies `Maximum Pool Size=10` + `Minimum Pool Size=2` + `Connection Idle Lifetime=300` defaults **only if the operator did not already specify them**, and returns the augmented string.

2. **Platform.Api `Program.cs` invokes `ConnectionStringDefaults.ApplyPoolDefaults`** on every connection string before passing it to either `AddPostgresStorage` (Platform-side) or any Pro `Use*Storage`/`Use*Transport` registration.

3. **Math:** with 14 known data sources × 10 pool size = 140 conn demand ceiling per `platform-api` instance. Comfortable under `max_connections=200` (the SMB-tier tuning shipped alongside this ADR in `docker-compose.smb.yml` and `docker-compose.production.yml`).

4. **Operator override path is preserved:** any deployment that explicitly sets `Maximum Pool Size=N` in the connection string continues to use that value verbatim — `ConnectionStringDefaults` never overwrites operator intent.

### Phase 2 — Shared `NpgsqlDataSource` overload across Pro packages (Pro 1.16.0-pro, separate plan)

5. **Each Pro storage package gains an additive overload:**
   ```csharp
   IServiceCollection UsePostgresXyzStorage(this IServiceCollection services,
                                            NpgsqlDataSource dataSource);
   ```
   The existing `(IServiceCollection, string)` signature is preserved verbatim so existing consumers (and the Phase 1 mitigation here) remain binary-compatible.

6. **Platform.Api opts into the shared overload** when all Pro stores share the same connection string. A single `NpgsqlDataSource` singleton serves the entire composition. Demand drops to **1 pool × pool-size**, indistinct from how many Pro features are active.

7. **Cross-repo coordination:** Phase 2 is a Pro repo cycle. Platform.Api adopts in a follow-up release once Pro 1.16.0-pro ships. A plan-skeleton archived at `docs/research/archived/Pro-1.16.0-pro-shared-datasource-skeleton.md` captures intent and acceptance criteria so the work can be picked up in any future Pro cycle.

## Consequences

### Positive

- **SMB-tier production deployments stop crashing under concurrent burst** — closes the latent bug exposed by R5.5 Phase C-L.
- **Capacity planning becomes honest:** `docs/operations/capacity-planning.md` SMB tier numbers can reflect the real measured knee post-Phase 1, not the bug-saturation point.
- **Operator override path is preserved** — Phase 1 only fills in the gap when the operator left it blank.
- **Phase 2 path is captured** without blocking R5.5 ship.
- **Forward-compatible:** Phase 1 mitigation continues to work after Phase 2 lands (operator who upgrades Pro keeps the same compose files; the new `NpgsqlDataSource` overload simply collapses N pools to 1).

### Negative

- **Phase 1 is a workaround, not a fix.** 14 pools at size 10 each is still 14 separate connection life-cycles, retry storms, and metrics labels. The architecturally correct end-state is 1 pool serving 1 connection string.
- **`Maximum Pool Size=10` may be tight for some workloads** (Pro.Dialer bulk operations, Pro.EventStore replay). Trade-off accepted for v1.14.5 ship; Phase 2 eliminates the constraint entirely.
- **Pre-existing `docker-compose.scale.yml` math is corrected.** ADR-0014 §"Postgres pool tuning" originally assumed 1 pool per replica → 4 × 50 = 200 conns demand vs `max_connections=220`. With sprawl: 4 replicas × 14 pools × 10 (Phase 1) = 560 demand vs 220 server cap. Either bump `max_connections=600` or wait for Pro 1.16.0-pro Phase 2. **ADR-0014 amendment** documents the correction; scale.yml itself is patched in a Phase D.2 follow-up.

### Neutral

- **Pro repo unaffected by Phase 1.** Cross-repo Pro 1.16.0-pro release is the natural next step but not blocked on this ADR.
- **No breaking change in connection-string contract.** `Maximum Pool Size=…` was always an operator-tunable parameter; Phase 1 only changes the default when the parameter is absent.

## Why not other options

- **Bump `max_connections=600`+ globally** — masks the sprawl but doesn't reduce real conn count, just defers Postgres saturation. Larger memory footprint (each conn ~10 MB shared work_mem × 600 = 6 GB) without architectural improvement.
- **PgBouncer transaction-pool layer** — already explicitly rejected in ADR-0014 §"What NOT to do": breaks Pro.Push `LISTEN/NOTIFY` semantics. Non-starter.
- **Pro repo single-DataSource refactor without shared overload** — would force Pro packages into a specific ordering convention (first-registered wins) which is fragile. Additive overload is cleaner.
- **Document the bug and ship R5.5 with measurement-as-knee** — discards the meaning of capacity-planning.md; the SMB tier numbers would not represent the product, they would represent the bug.

## Phase 1 measured impact (2026-04-28)

`docker-compose.smb.yml` SMB tier stack, AMD Ryzen 9 9900X / 60 GB RAM, NVMe SSD. `presence` scenario (`GET /api/v1/admin/agents`, `KeepConstant(VU)` shape) before vs after Phase 1:

| VU | Pre-fix (sprawl bug) | Post-fix (Phase 1) |
|---:|---|---|
| 100 | 111 824 OK / 16 168 fail (87 % OK) · p99 OK 91 ms · ~1 864 RPS aggregate | **661 738 OK / 0 fail** · p99 **16.62 ms** · **11 029 RPS** |
| 250 | 0 OK / 44 413 Unauthorized (cascade from token-refresh failures triggered by Postgres exhaustion) | **678 772 OK / 0 fail** · p99 **34.59 ms** · 11 312 RPS |
| 500 | 0 OK / 44 299 Unauthorized | **646 262 OK / 0 fail** · p99 **69.50 ms** · 10 770 RPS |
| 1000 | 0 OK / 43 249 Unauthorized | **656 954 OK / 0 fail** · p99 **115.97 ms** · 10 949 RPS |
| 1500 | 0 OK / 37 267 Unauthorized | **662 023 OK / 0 fail** · p99 **174.21 ms** · 11 034 RPS |

**Quantitative gain:**

- Concurrency capacity: from bug-saturation at VU=100 to clean operation through VU=1500 (**15× improvement**).
- Aggregate throughput: ~1.8 k RPS → ~11 k RPS at the same concurrency level (**6× improvement**).
- Postgres connection demand under VU=1500: 21 active client conns idle post-sweep (well under `max_connections=200`); peak under load roughly 140 (14 data sources × 10 pool size), all within budget.
- Zero `Npgsql.PostgresException (53300)` "too many clients already" entries in `platform-api` logs across the entire sweep.

**SMB tier real knee identified (post-fix):** throughput is CPU/Postgres-bound at ~11 k RPS aggregate from VU=100 onward; adding more VUs increases latency without increasing throughput. The product knee is therefore latency-defined:

| Latency budget | SMB tier supported VU |
|---|---:|
| p99 ≤ 50 ms | ≤ 250 |
| p99 ≤ 100 ms | ≤ 750 |
| p99 ≤ 200 ms | ≤ 1 500 |

Numbers refresh `capacity-planning.md` § "Small tier" from v1-provisional to v1-measured in the v1.14.5 doc patch alongside this ADR.

## References

- Plan: `docs/plans/active/2026-04-28-postgres-pool-sprawl-mitigation.md`
- ADR-0014 amendment: `docs/decisions/0014-auth-horizontal-scaling-baseline.md` §"Update 2026-04-28 (R5.5 Phase C-L)"
- Phase C-L sweep findings: `docs/operations/load-test-baseline.md` §"Phase C-L SMB tier stress sweep"
- Pro repo audit: 13 `NpgsqlDataSource.Create()` call sites + 1 in Platform.Storage.Postgres
- Pro 1.16.0-pro Phase 2 plan-skeleton: `docs/research/archived/2026-04-28-Pro-1.16.0-pro-shared-datasource-skeleton.md`
- v1.14.2 CHANGELOG entry "Postgres pool sizing for multi-replica" (the 4-replica math that this ADR corrects)
- v1.14.5 CHANGELOG entry "ADR-0015 Phase 1 — Postgres pool sprawl mitigation"
