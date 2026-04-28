# ADR-0012: JWT rotation pool wire-up + multi-replica gate

**Status:** Accepted
**Date:** 2026-04-27
**Context:** AHH Phase 3 (v1.14.0)

## Context

R5.4 S5.9 shipped the JWT signing-key rotation pool — `IJwtKeyRotationService`,
`InMemoryJwtKeyStore`, `RedisJwtKeyStore`, the
`/management/security/jwt/rotate-key` admin endpoints, and the
`Asterisk.Platform.Auth.JwtKeyRotation` meter — but did **not** wire it into
`JwtTokenService`. The live issuer continued reading a file-based RSA key
from `data/jwt-signing-key.xml`. R5.5 production validation surfaced the
gap as the **#1 multi-replica blocker**: each Platform.Api replica
generates its own RSA key on first boot, so tokens issued by replica A
are rejected by replica B (and vice-versa).

AHH Phase 3 closes the gap. Goal: make a 2+ replica deployment of
`Asterisk.Platform.Api` safe behind a load balancer **without** changing
single-replica behavior. This is **correctness work, not performance** —
the throughput knee work lives in Phases 1, 2, and 4. Phase 3 unblocks
Phase 5 horizontal validation, which then proves the post-Phase-4 ceiling
holds across replicas.

## Decision

**Generalize `JwtKeyEntry` to support both HS256 and RS256, refactor
`JwtTokenService` so it can consume `IJwtKeyRotationService`, ship a
one-shot legacy-file → pool migration as a hosted service, and tighten
`RedisJwtKeyStore.UpsertAsync` with optimistic-concurrency-controlled
atomic transactions. Production deployments opt into the new path with a
single config flag; the file-based default is preserved for tests +
single-replica deploys.**

The wire-up is staged in four atomic commits (3.A–3.D) so reviewers can
inspect each step in isolation; only Phase 3.D ships behavioral change to
production.

### Phase 3.A — Schema generalization

`JwtKeyEntry` adds an `Algorithm` property of type
`JwtKeyAlgorithm { Hs256 = 0, Rs256 = 1 }`. The default is `Hs256` so
R5.4-era Redis entries (which pre-date the field) deserialize unchanged.
The `Key` field becomes algorithm-aware:
- `Hs256` → base64-encoded HMAC-SHA256 secret bytes (unchanged from R5.4).
- `Rs256` → base64-encoded PKCS#8 RSA private key (`RSA.ExportPkcs8PrivateKey()`).

`JwtKeyRotationService.RotateAsync` sets `Algorithm = Hs256` explicitly
on new entries. Behavior identical to R5.4. Backward compatible.
*Commit `109fd98`.*

### Phase 3.B — JwtTokenService refactor (additive)

`JwtTokenService` gains a second constructor that takes
`IJwtKeyRotationService` instead of a file path. The existing
file-based constructor is preserved verbatim so test factories and
single-process bootstraps keep working unchanged.

Pool-path semantics:
- The active signing entry is cached for 60 s with a sync `lock`
  (`System.Threading.Lock`, no `SemaphoreSlim` so the class stays
  non-disposable — JwtTokenService is a long-lived DI singleton; making
  it `IDisposable` triggered a cross-test-class flake in
  `WebApplicationFactory` shared fixtures).
- Validation uses `TokenValidationParameters.IssuerSigningKeyResolver`
  which calls `_rotationService.GetValidationKeysAsync()` (also cached
  60 s) on every validation. Tokens signed by a rotation predecessor
  still verify during the grace window.
- `BuildSigningCredentials` dispatches by `JwtKeyEntry.Algorithm`:
  HS256 → `SymmetricSecurityKey` + HMAC-SHA256; RS256 → `RsaSecurityKey`
  from PKCS#8 + RSA-SHA256.

`JwtLegacyKeyMigrationService` (new `IHostedService`) runs once at
startup: if the pool is empty AND `jwt-signing-key.xml` exists in the
configured data directory, decrypt via DataProtection + import as an
`Algorithm = Rs256, IsActive = true` entry with a 30-day expiration.
Idempotent — multi-replica boot races resolve via the underlying
`IJwtKeyStore.UpsertAsync` CAS (Phase 3.C). Failures are non-fatal +
logged; the rotation service auto-bootstraps a fresh HS256 entry on
first `GetActiveSigningKeyAsync()` so the deployment recovers.
*Commit `96189ca`.*

### Phase 3.C — RedisJwtKeyStore atomic upsert (CAS)

The R5.4-era `RedisJwtKeyStore.UpsertAsync` issued the entry-write
and the active-pointer-update as two independent Redis ops. Two
replicas calling `RotateAsync` concurrently would each "win" the
active flag (last-writer-wins on the pointer; both entries persisting
with `IsActive = true` in their JSON blobs). `GetActiveAsync` would
still return a single entry (the pointer disambiguates), but
`GetAllAsync` returned a confusing `N×IsActive=true` view.

The new implementation:
1. Reads the current active pointer.
2. Loads the prior active entry's JSON if any, builds its demoted
   (`IsActive=false`) version.
3. Builds a Redis transaction with a `Condition.StringEqual` on the
   pointer (so the transaction commits only if the pointer hasn't
   moved between the read and `EXECUTE`).
4. Writes the new entry, updates the pointer, and writes the demoted
   prior entry's JSON — all atomically.
5. On condition failure, retries up to 5 times with linear backoff
   (20–100 ms). After exhaustion throws
   `InvalidOperationException` so the caller knows persistence failed.

Non-active upserts (storing a non-active entry, e.g. a future-dated
key) bypass the CAS dance entirely — they're single-key writes with
no consistency hazard.

Tests: a new `UpsertAsync_ShouldDemotePriorActive_WhenNewActiveKeyArrives`
asserts the JSON-blob demotion;
`UpsertAsync_ShouldProduceSingleActive_WhenTwoReplicasRotateConcurrently`
fires both replicas with `Task.WhenAll` and asserts exactly one
`IsActive = true` entry remains.

### Phase 3.D — Production wiring + fail-fast assertion

Two new config keys:

| Key | Default | Purpose |
|---|---|---|
| `Identity:JwtKeyRotation:UseRotationPool` | `false` | When `true`, `JwtTokenService` is constructed with `IJwtKeyRotationService` + `JwtLegacyKeyMigrationService` runs at startup. |
| `Identity:JwtKeyRotation:RequireRedisStore` | `false` | When `true` AND `UseRotationPool=true` AND `ConnectionStrings:IdentityRedis` is missing → throw at startup config-parse time. |

Default (`UseRotationPool=false`) preserves R5.4 behavior — the
file-based constructor is registered + nothing about the existing
deployment surface changes. Operators upgrade by flipping
`UseRotationPool=true` and (for production multi-replica)
`RequireRedisStore=true`.

The fail-fast assertion intentionally lives at config-parse time
(in `Program.cs` before `builder.Services.AddSingleton<JwtTokenService>`)
rather than at first request — operators want loud broken-config at
deployment time, not silent breakage during traffic.

## Multi-replica gate checklist

For a deployment to safely run 2+ replicas of `Asterisk.Platform.Api`,
the operator must satisfy ALL of:

- [ ] `Identity:JwtKeyRotation:UseRotationPool = true`
- [ ] `Identity:JwtKeyRotation:RequireRedisStore = true`
- [ ] `ConnectionStrings:IdentityRedis = …` set
- [ ] `IJwtKeyStore` resolves to `RedisJwtKeyStore` (auto-wired by
      `AddAsteriskPlatformIdentityRedis` when the connection string is set)
- [ ] DataProtection persisted to DB-backed `PlatformDataProtectionDbContext`
      per ADR-0003 (existing requirement; verified at startup by
      ASP.NET Core's keyring resolver)
- [ ] `RedisAuthCacheInvalidator` from AHH Phase 1 wired (operator-side
      cache coherency; ADR-0010)
- [ ] `IJtiRevocationCache`, `IMfaPendingCache`, `IPasswordResetCache`
      registered as Redis variants (ADR-0007 / ADR-0010)

Each item is verified by the corresponding ADR's tests; this ADR's
own integration test in
`tests/Asterisk.Platform.Identity.Redis.Tests/RedisJwtKeyStoreTests.cs`
exercises the cross-replica scenario end-to-end (two store instances
sharing a Redis prefix, one writes + the other reads).

## Considered alternatives

- **Replace RSA with HMAC entirely.** Rejected: closes the JWKS-publishing
  path forever (downstream services in a future federation can't verify
  symmetric tokens without holding the secret). RS256 stays the default
  algorithm; `Algorithm` is a discriminator, not a forced switch.
- **Use a single shared file on a network volume (NFS, S3, etc.).**
  Rejected: trades the Redis dependency for a shared-FS dependency with
  worse failure modes (NFS lock contention, S3 eventual consistency).
  The existing Redis dependency is already required for `IJtiRevocationCache`
  + `IMfaPendingCache` + `IPasswordResetCache` (R5.1+); the rotation pool
  reuses it.
- **Refresh-token-first as a substitute for the rotation pool.** Rejected:
  longer-lived access tokens reduce login frequency but don't fix the
  multi-replica key-divergence problem. Phase 3 + extending refresh-token
  TTL are orthogonal; the latter is a Phase 4/5 candidate.
- **Per-tenant signing keys.** Rejected for now: out of scope for the
  multi-replica gate. The `kid` resolver makes per-tenant keys feasible
  in a later phase if a customer demands it; the schema already supports
  a fanned-out pool.
- **Lua script instead of WATCH/MULTI/EXEC.** Rejected for Phase 3.C:
  StackExchange.Redis transaction conditions are AOT-clean and well-tested
  in the existing codebase; Lua scripting would add a new operational
  surface (script reload on Redis restart) for marginal benefit.
- **Synchronous file→pool migration in `JwtTokenService` constructor.**
  Rejected: tests construct JwtTokenService many times; constructor-time
  side effects pollute the test environment. Hosted-service pattern
  cleanly separates one-shot startup work from per-request behavior.

## Failure modes + mitigation

| Failure | Effect | Mitigation |
|---|---|---|
| Operator forgets to set `RequireRedisStore=true` in production | Multi-replica deploys silently break (token A→B mismatch) | Recommend `RequireRedisStore=true` in the runbook + a `MultiReplicaStartupAssertion` follow-up that auto-detects N>1 via env var. |
| Migration encounters corrupted `jwt-signing-key.xml` | Pool stays empty; auto-bootstrap fresh HS256 entry; existing tokens fail validation (401 → re-login) | Logged at error level. Operators inspect log + restore file from backup if needed. |
| Concurrent `RotateAsync` from 6+ replicas | CAS exhausts 5 retries; `InvalidOperationException` bubbles to caller | Caller (admin endpoint or rotation service) returns 503; operator retries. Realistic risk: only the explicit `/rotate-key` admin endpoint triggers `RotateAsync`; auto-rotation fires once per `ActiveDuration` (default 1h). 6+ admins clicking simultaneously is the only path; survivable. |
| Redis unavailable at startup | Migration hosted service fails on `_store.GetActiveAsync` | Exception bubbles → host shutdown. Same posture as any storage outage. Auth would be broken anyway. |
| `IJwtKeyStore` resolves to `InMemoryJwtKeyStore` in production with N>1 replicas + `RequireRedisStore=false` | Tokens flap between replicas | Documented as opt-in unsafe configuration. The `RequireRedisStore` flag is the canonical guard. |

## Tested invariants

Phase 3.A:
- Existing `JwtKeyRotationServiceTests` (5/5 PASS unchanged).
- Existing `RedisJwtKeyStoreTests` (5/5 PASS unchanged with backward-compat schema).

Phase 3.B (`JwtTokenServiceRotationTests` — 9 new):
- HS256 issuance + validation round-trip
- RS256 issuance + validation round-trip
- `kid` header matches the active pool entry
- Token signed by predecessor key validates after rotation (multi-key)
- Impersonation token issuance + claim carry
- Token signed by foreign key is rejected
- Migration: legacy file → RS256 entry
- Migration: no-op when pool already has active key
- Migration: no-op when legacy file missing

Phase 3.C (`RedisJwtKeyStoreTests` — 2 new):
- Prior active entry's JSON `IsActive` flag is demoted on new active upsert
- Concurrent rotations from two replicas converge to exactly one active entry

Phase 3.D: covered by the existing 846/846 Api.Tests suite — verifies the
default config path (`UseRotationPool=false`) preserves R5.4 behavior.
End-to-end multi-replica integration is exercised by Phase 5's
`MultiReplicaSmokeTests` (Testcontainers fixture, two Api containers).

## Forward compatibility

- **JWKS publishing**: a future ADR can add `/.well-known/jwks.json`
  on top of `IJwtKeyRotationService.GetValidationKeysAsync()`. The
  per-entry `kid` is already stable; no schema change required.
- **Per-tenant signing**: the schema admits a tenant scoping field on
  `JwtKeyEntry` if needed; Phase 3 adds nothing that blocks it.
- **HS256 default switch**: a v2.0 deployment may flip the default
  algorithm for new keys to HS256 (faster + smaller signatures). The
  rotation service's parameter is already on `JwtKeyRotationOptions`;
  ADR-0028 (planned) covers the algorithmic-default decision.

## Related ADRs

- ADR-0003 — DataProtection key persistence strategy (multi-replica
  prerequisite; verified by this ADR's gate checklist).
- ADR-0007 — Agent tenant resolver + strict-mode builder (the
  fail-fast assertion pattern reused here).
- ADR-0010 — Auth hot-path cache decorators (multi-replica cache
  coherency; complements this ADR).
- ADR-0011 — Auth write-path deferral (uses the same pattern of
  per-replica in-process queues; orthogonal to JWT key sharing).
