# Auth Hotpath Hardening — Asterisk.Platform v1.13.x → v1.14.0

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Phases ship as independent releases (v1.13.1 / v1.13.2 / v1.14.0).

**Goal:** Lift the `/auth/login` knee from 75 req/s (R5.5 measured) toward ≥220 req/s single-replica + ≥800 req/s 4-replica via evidence-driven attack on the dominant cost (BCrypt12 password verify), uncached DB reads, synchronous write-path operations, and the multi-replica gates that R5.4 left unwired. Argon2id replaces BCrypt at the OWASP-2025 floor; the JWT rotation pool gets correctly wired (with the RS256 schema generalization R5.4 deferred); failure-path audit + refresh-token persistence stay strictly synchronous so security posture holds.

**Tech Stack:** .NET 10 Native AOT · ASP.NET Core minimal APIs · Postgres 17 (Npgsql) · Redis 8 (`Asterisk.Platform.Identity.Redis`) · DataProtection DB-backed via `PlatformDataProtectionDbContext` (ADR-0003) · BenchmarkDotNet (test-side JIT only) · dotnet-trace · NBomber 6.1 (`tests/Asterisk.Platform.LoadTests/`) · Argon2id (library locked by Phase 0).

**Provenance:**
- Plan approved 2026-04-27 in dedicated planning session (system path `/home/orion75/.claude/plans/silly-strolling-fiddle.md`).
- R5.5 evidence: `docs/operations/load-test-baseline.md` (B-L #4 sweep) + `docs/operations/slos.md` + `docs/operations/capacity-planning.md` (v1 partial-measured).
- Roadmap pointer: `Asterisk.Sdk.Pro/docs/roadmap.md` "Known-debt v1.13.x patches" → JWT-001 (this plan corrects the diagnosis and broadens scope).

---

## Context

**The problem.** R5.5 Production Validation (2026-04-27) measured a hard knee on
`POST /auth/login` at **50–75 req/s sustainable** on AMD Ryzen 9 9900X / 60 GB /
single-instance docker-compose. Above the knee, p99 explodes (671 ms at 100 req/s,
collapse at 250 req/s). `docs/operations/load-test-baseline.md`
documented this as **JWT-001** with the hypothesis "per-request DataProtection EF
round-trip on every JWT issuance." `Asterisk.Sdk.Pro/docs/roadmap.md` carries
JWT-001 as the v1.13.x patch path "JwtTokenService ↔ rotation pool integration."

**Why the documented hypothesis is wrong.** Code investigation
(`src/Asterisk.Platform.Api/Services/JwtTokenService.cs:24-80`) shows the
RSA protector is created **once at startup** and the per-request path
(`GenerateAccessToken`, lines 89-126) signs in-memory with a cached
`SigningCredentials`. Neither DataProtection nor EF appear on the hot path.
Likewise the R5.4 rotation pool (`IJwtKeyRotationService` +
`InMemoryJwtKeyStore` + `RedisJwtKeyStore`) generates **symmetric** key bytes,
incompatible with the live RS256 issuer; wiring it as documented would be
neither sufficient nor architecturally clean for the real workload.

**What the hot path actually looks like** (verified by reading endpoint +
service code):

| Operation | Cost / login | Per-request? | File:line |
|---|---|---|---|
| BCrypt verify (workFactor=12) | **~162 ms** (BenchmarkDotNet, AMD 9900X — see [Phase 0 baseline](../../research/2026-04-27-auth-hotpath-baseline.md)) | yes | `src/Asterisk.Platform.Api/Services/PasswordService.cs:7` |
| TenantAuthConfig fetch (MFA + lockout) | 5–10 ms × 2 | yes (uncached) | `src/Asterisk.Platform.Identity/Mfa/TenantAuthConfigMfaPolicyEvaluator.cs:15` |
| User lookup by email | 5–10 ms | yes | `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUserStore.cs:28` |
| User upsert (`LastLoginAt` + lockout reset) | 5–10 ms | yes (synchronous) | `Endpoints/AuthEndpoints.cs:145` → `IssueTokensAsync:883` |
| AuthEvent log | 5–10 ms | yes (synchronous, even on success) | `Services/AuthEventService.cs:14-37` |
| RefreshToken save | 5–10 ms | yes (correctly synchronous) | `Services/RefreshTokenService.cs:14-36` |
| Permission resolution (cache miss) | 10–50 ms | yes first-hit per user | `Services/PermissionResolver.cs:11` |
| RSA-2048 sign | <1 ms | yes (correctly cached) | `Services/JwtTokenService.cs:120` |
| **DataProtection EF roundtrip** | **0 ms** | **no — startup only** | — |

At 75 req/s × 162 ms BCrypt verify ≈ 12.15 CPU-cores demanded vs 12 cores
available — exact saturation, recovering the measured 75 req/s knee within
1% (12,152 vs 12,000 CPU-ms/sec). Phase 0 BenchmarkDotNet evidence
([baseline doc](../../research/2026-04-27-auth-hotpath-baseline.md)) confirms
BCrypt accounts for ≥99.9% of the measurable per-request crypto cost,
clearing the ≥60% Phase 0 acceptance threshold by a wide margin. Lowering
BCrypt cost is a security regression we will not take. The right path is a coordinated set of changes
that (a) attack the dominant cost with a stronger algorithm at a faster
parameter set, (b) remove uncached DB hits from the hot path, (c) defer
non-audit writes off the critical path, and (d) close the multi-replica gate
that the rotation pool was meant to address all along.

**Intended outcome.** A coherent multi-phase patch named **Auth Hotpath
Hardening (AHH)** that:

- Lifts the single-instance knee from ~75 req/s to **≥220 req/s** at p99 ≤ 250 ms.
- Unblocks horizontal scaling (4 replicas → ≥800 req/s).
- Migrates password hashing to Argon2id (OWASP-2025 recommendation) with
  zero user-visible disruption via on-login transparent rehash.
- Wires the R5.4 JWT rotation pool to enable zero-downtime key rotation and
  multi-replica readiness — framed correctly as a **correctness gate**,
  not a performance fix.
- Preserves the security posture: failure-path audit logs stay synchronous,
  hash strength does not regress, no new secret-management surface is
  introduced.
- Honors the AOT-first constraint: every new dependency is validated under
  `PublishAot=true` before it lands.

**What this plan explicitly rejects.** Lowering BCrypt cost factor; deploying
multi-replica without the rotation pool wired; adopting `HS256` for raw
signing speed (saves ~0.5 ms while breaking JWKS-style federation); silently
rehashing passwords on a cron job (DoS pattern); putting `AuthEvent.LoginFailure`
on a deferred queue (security audit gap); wiring the existing R5.4 rotation
pool as-is when its symmetric `JwtKeyEntry` schema is incompatible with the
live RS256 issuer.

---

## Approach

Six phases, sequenced by "evidence first → quick wins → correctness gate →
biggest perf win → horizontal validation." Each phase ships independently
and reverts cleanly. Phase 0 is **gating** — no implementation phase commits
until it confirms the cost breakdown empirically.

| Phase | Title | Knee impact | Risk | Version | Depends on |
|---|---|---|---|---|---|
| 0 | Profiling baseline | none (informational) | none | no bump | — |
| 1 | Hot-read caching | medium (~+25%) | low | v1.13.1 (patch) | Phase 0 |
| 2 | Write-path deferral | medium (~+30%) | low–med | v1.13.2 (patch) | Phase 0 |
| 3 | Multi-replica gate (JWT rotation + DataProtection cluster + cache distribution) | none (correctness) | medium | v1.14.0 (minor) | — |
| 4 | Argon2id migration with on-login rehash | high (~+150–200%) | medium | v1.14.0 (minor) | Phases 0, 1 |
| 5 | Connection pool + DB tuning + horizontal validation | sustains knee under N=4 replicas | medium | v1.14.0 | Phase 3 |

Sequence rationale: Phases 1+2 are cheap reversible patches that buy headroom
while Phase 0 evidence pre-validates Argon2id under AOT. The multi-replica
gate (Phase 3) is its own minor because it changes deployment semantics
(config keys, runbook). Argon2id rides v1.14.0 because the hash format change
is a contract change to the Identity store.

---

## Phase 0 — Profiling Baseline (evidence, no code ships)

**Goal.** Empirically prove BCrypt is the dominant cost on the 9900X before
patching anything. The R5.5 docs already had one wrong hypothesis (JWT-001);
we will not write a second.

**Files to add:**

- [ ] `tests/Asterisk.Platform.Benchmarks/Asterisk.Platform.Benchmarks.csproj` — new
  BenchmarkDotNet project, Release config; **note** BenchmarkDotNet itself
  requires JIT (won't run on AOT-published binary), so this project benches
  source code on JIT but lives next to AOT publish probes.
- [ ] `tests/Asterisk.Platform.Benchmarks/AuthHotPathBench.cs` — four benchmarks:
  `Bcrypt12_Verify`, `Argon2id_Verify_19MiB_t2_p1`, `JwtRsaSign_Issue`,
  `EndToEnd_Login_InMemoryStores` (synthetic in-memory `IUserStore` +
  `ITenantAuthConfigStore`).
- [ ] `tests/Asterisk.Platform.Api.Aot.Probe/` — minimal AOT-publish probe that
  imports the candidate Argon2id library; CI step that runs
  `dotnet publish -p:PublishAot=true` and fails on any trim or AOT warning.
- [ ] `scripts/profiling/dotnet-trace-login.sh` — wraps
  `dotnet-trace collect --providers Microsoft-AspNetCore-Server-Kestrel,Asterisk.Platform.Auth.JwtKeyRotation`
  + a curl loop hitting `/api/v1/auth/login` at the documented sustainable rate.
- [ ] `docs/research/2026-04-XX-auth-hotpath-baseline.md` — captures: machine spec,
  BenchmarkDotNet table for the four benchmarks, dotnet-trace flame graph
  summary attributing wall-time to top 10 methods, and the AOT-probe
  publish output for the candidate Argon2id library.

**Acceptance.** ≥85% of per-request wall time is attributed to a known
operation. **BCrypt verify must account for ≥60% of CPU.** If any other
component (Postgres round-trip, permission resolver) exceeds 20%, replan
Phases 1–4 priorities. Argon2id candidate library MUST publish AOT with zero
trim warnings; if it doesn't, fall back is documented in the research doc
(libsodium P/Invoke wrapper) before Phase 4 starts.

**Tests.** None — these *are* the evidence artifacts.

**Version.** No bump. Test-only project; never ships in `Asterisk.Platform.Api` binary.

**Library locked.** `Isopoh.Cryptography.Argon2 2.0.0` validated AOT-clean
2026-04-27 (zero IL trim/AOT warnings under `PublishAot=true`, 2.07 MB native
binary, runtime hash + verify roundtrip OK). Recorded in
[research/2026-04-27-auth-hotpath-baseline.md §1](../../research/2026-04-27-auth-hotpath-baseline.md).
The libsodium P/Invoke fallback is no longer load-bearing.

---

## Phase 1 — Hot-Read Caching with Explicit Invalidation (v1.13.1)

**Goal.** Move `TenantAuthConfig` and `User-by-email` cache-hit reads off the
synchronous Postgres path. 60 s TTL with Redis pubsub invalidation when
`Asterisk.Platform.Identity.Redis` is registered.

**Files to add:**

- [ ] `src/Asterisk.Platform.Api/Services/CachedTenantAuthConfigStore.cs` —
  decorator implementing `ITenantAuthConfigStore`, `IMemoryCache`-backed,
  60 s TTL, key `tenant-auth:{tenantId}`. `SaveAsync` invalidates locally
  and emits a Redis pubsub message when configured.
- [ ] `src/Asterisk.Platform.Api/Services/CachedUserStore.cs` —
  same pattern for `IUserStore.GetByEmailAsync` and `GetByIdAsync` only;
  writes pass through and invalidate. Cache key includes `tenantId` to
  preserve cross-tenant isolation when same email lives in two tenants.
  **MUST scrub `PasswordHash` from cached projections** — never cache hash material.
- [ ] `src/Asterisk.Platform.Identity.Redis/RedisAuthCacheInvalidator.cs` —
  pubsub channel `asterisk:auth:invalidate`; consumed by both decorators.
  Emits keys `tenant:{id}` / `user:{tenantId}:{userId}`.

**Files to modify:**

- [ ] `src/Asterisk.Platform.Api/Program.cs` —
  register decorators via `Decorate<ITenantAuthConfigStore, CachedTenantAuthConfigStore>()`
  pattern after concrete stores. (Add `Scrutor` only if not already present;
  otherwise emit a small manual decorator helper to preserve AOT.)
- [ ] `src/Asterisk.Platform.Api/Services/PermissionResolver.cs` —
  add Redis pubsub invalidation so role grants propagate cross-replica
  within a network round-trip instead of the current 5-min TTL window.
- [ ] `src/Asterisk.Platform.Identity.Redis/DependencyInjection/IdentityRedisServiceCollectionExtensions.cs` —
  register `RedisAuthCacheInvalidator` as a `BackgroundService` listener
  when invoked.

**Tests to add:**

- [ ] `tests/Asterisk.Platform.Api.Tests/Services/CachedTenantAuthConfigStoreTests.cs` —
  `GetAsync_ShouldReturnCachedValue_WhenCacheIsWarm`,
  `SaveAsync_ShouldInvalidateCache_WhenWriteCompletes`,
  `GetAsync_ShouldRespectTtl_WhenItemExpires`.
- [ ] `tests/Asterisk.Platform.Api.Tests/Services/CachedUserStoreTests.cs` —
  same pattern + `GetByEmailAsync_ShouldNotLeakAcrossTenants_WhenSameEmailInTwoTenants`
  (multi-tenant safety regression) +
  `GetByEmailAsync_ShouldNotCachePasswordHash_WhenCalled` (security regression).
- [ ] `tests/Asterisk.Platform.Identity.Redis.Tests/RedisAuthCacheInvalidatorTests.cs` —
  cross-replica invalidation via two `ConnectionMultiplexer` instances.

**ADR.** `docs/decisions/0010-auth-hotpath-cache-decorators.md` — covers TTL
choice, multi-tenant key isolation, IMemoryCache vs Redis tradeoff, why
the failure path doesn't go through cache (cache eviction surface), and
the password-hash scrubbing invariant.

**Acceptance.** Knee under sustained `jwt-sweep.sh` improves from baseline
by ≥20%. All new tests pass. 0 warnings. 0 vulnerable packages.

**Risk + mitigation.** Up to 60 s of stale `TenantAuthConfig` if Redis is
not configured. ADR documents this; production deployments are already
Redis-mandatory for `IJtiRevocationCache`.

---

## Phase 2 — Write-Path Deferral (v1.13.2)

**Goal.** Remove `LastLoginAt`, `ResetAttemptsAsync`, success-path
`AuthEvent.Log`, and lockout-reset persistence from the request critical
path via a bounded `System.Threading.Channels.Channel`. **Refresh-token
persistence stays synchronous** (a token shipped without persisted backing
is a security hole). **Failure-path audit logs stay synchronous**
(an attacker fishing credentials must not outpace the audit log).

**Files to add:**

- [ ] `src/Asterisk.Platform.Api/Services/AuthWriteQueue.cs` —
  bounded `Channel<AuthWriteCommand>` (capacity 4096, `BoundedChannelFullMode.DropWrite`
  with metric on drop), `BackgroundService` consumer with 250 ms / 64-item
  batched flush, per-command-type counter via meter `Asterisk.Platform.Auth.WriteQueue`.
- [ ] `src/Asterisk.Platform.Api/Services/AuthWriteCommand.cs` —
  discriminated record set: `UpdateLastLoginAt`, `ResetLockoutCounters`, `LogSuccessEvent`.

**Files to modify:**

- [ ] `src/Asterisk.Platform.Api/Services/AuthEventService.cs` —
  split into `LogAsync` (synchronous, always available) and `EnqueueLogAsync` (success-path only).
- [ ] `src/Asterisk.Platform.Api/Services/AccountLockoutService.cs` —
  `ResetAttemptsAsync` becomes enqueue.
- [ ] `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs` —
  `IssueTokensAsync` (line 874) calls `EnqueueLogAsync` + enqueues the
  `LastLoginAt` update. Failure paths in `Login` (lines 94, 106) keep
  synchronous logging.
- [ ] `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` —
  register `AuthWriteCommand` source-generated types (AOT requirement).

**Tests to add:**

- [ ] `tests/Asterisk.Platform.Api.Tests/Services/AuthWriteQueueTests.cs` —
  `EnqueueAsync_ShouldDropWrite_WhenQueueIsFull`,
  `Consumer_ShouldBatchWrites_WhenMultipleEnqueuedWithinFlushInterval`,
  `Consumer_ShouldShutdownGracefully_WhenStopRequested`,
  `Consumer_ShouldFlushPendingWrites_OnGracefulShutdown`.
- [ ] `tests/Asterisk.Platform.Api.Tests/Endpoints/AuthEndpointsTests.cs` —
  extend with `Login_ShouldEnqueueLastLoginAt_WhenSuccessful` and
  `Login_ShouldLogFailureSynchronously_WhenCredentialsInvalid` (the latter
  is a regression guard).

**ADR.** `docs/decisions/0011-auth-write-deferral.md` — covers durability
tradeoffs (queue is in-memory; on container kill the last 250 ms of
`LastLoginAt` updates are lost — acceptable; `AuthEvent.LoginFailure` is
**never** in the queue, so no audit gap).

**Acceptance.** Knee improves an additional ≥20% over Phase 1 baseline.
Audit-completeness regression test passes (process-kill mid-failure does
not lose the `LoginFailure` event).

**Version.** v1.13.2 (patch).

---

## Phase 3 — Multi-Replica Gate (v1.14.0)

**Goal.** Make 2+ `Asterisk.Platform.Api` replicas safe behind a load balancer.
**This is correctness, not performance** — without it, multi-replica
deployment breaks JWT validation, role-cache propagation, and DataProtection
key sharing.

**What breaks today with N>1 replicas** (verified by code inspection):

1. **JWT signing key divergence** —
   `src/Asterisk.Platform.Api/Services/JwtTokenService.cs:32-57` generates
   per-replica RSA keys. Tokens issued by replica A reject on replica B.
   **#1 multi-replica blocker.**
2. **`PermissionResolver` in-memory cache divergence** —
   `src/Asterisk.Platform.Api/Services/PermissionResolver.cs:11` has
   5-min TTL with no cross-replica invalidation.
3. **`InMemoryJwtKeyStore` is the default** in
   `Program.cs` (~line 484). A deployment that forgets to register
   `RedisJwtKeyStore` is still single-replica-only.
4. **DataProtection key divergence** is *partially* addressed by
   ADR-0003 (DB-backed via `PlatformDataProtectionDbContext`), but
   production startup currently does not fail-fast if it isn't wired.
5. **Failed-login lockout race** between two replicas updating
   `users.failed_login_attempts` for the same user.
6. **`MfaPendingCache` / `PasswordResetCache`** Redis impls exist
   (R5.1 Identity.Redis package) but registration is opt-in.

**Files to modify:**

- [ ] `src/Asterisk.Platform.Identity/Auth/Jwt/JwtKeyEntry.cs` —
  generalize from symmetric-only to a discriminated structure
  `KeyAlgorithm { Hs256, Rs256 }` + `RsaParametersJson` (or split into a
  sealed `RsaJwtKeyEntry`). Current symmetric-only schema is incompatible
  with the live RS256 issuer; this is the architectural blocker the R5.4
  ship deferred. Update the AOT serialization context for `IJwtKeyStore`.
- [ ] `src/Asterisk.Platform.Api/Services/JwtTokenService.cs` —
  refactor to take `IJwtKeyRotationService` injection. `GenerateAccessToken`
  reads the active key per call but caches it for 60 s in a `Lazy<Task<JwtKeyEntry>>`
  to avoid Redis hit per request. `ValidationParameters` becomes
  `IssuerSigningKeyResolver` consulting `GetValidationKeysAsync` with
  `kid` lookup → in-process `MemoryCache` keyed on `kid` with 10-min TTL.
- [ ] `src/Asterisk.Platform.Api/Auth/AuthSchemeConfiguration.cs` —
  switch to dynamic key resolver.
- [ ] `src/Asterisk.Platform.Api/Program.cs` —
  register `JwtKeyRotationBackgroundService` that calls `RotateAsync` on
  the configured cadence. Add startup assertion: in `Production` env with
  `Replicas>1` configured (or env var `ASPNETCORE_REPLICAS>1`), fail fast
  if `IJwtKeyStore is InMemoryJwtKeyStore`, if any of the Redis caches
  default to in-memory, or if `DataProtection` is not DB-backed.
- [ ] `src/Asterisk.Platform.Identity.Redis/RedisJwtKeyStore.cs` —
  audit `RotateAsync` for CAS correctness. Two replicas on the same
  rotation cadence MUST NOT both succeed and clobber the active flag.
  Add `WATCH/MULTI/EXEC` (or Lua script) if not already present.

**Migration shim**: on first boot, if `jwt-signing-key.xml` exists on disk
and the rotation pool is empty, import the legacy RSA key as a one-shot
`JwtKeyEntry { KeyAlgorithm = Rs256, RsaParametersJson = ..., IsActive = true }`
so already-issued tokens continue validating during cutover. Delete
the legacy file after successful import. Documented in the ADR.

**Tests to add:**

- [ ] `tests/Asterisk.Platform.Api.Tests/Services/JwtTokenServiceRotationIntegrationTests.cs` —
  `GenerateAccessToken_ShouldUseActiveKey_AfterRotation`,
  `ValidateToken_ShouldAcceptToken_WhenSignedWithGracePeriodKey`,
  `Boot_ShouldImportLegacyFileKey_WhenRotationPoolIsEmpty`.
- [ ] `tests/Asterisk.Platform.Identity.Redis.Tests/RedisJwtKeyStoreConcurrencyTests.cs` —
  `RotateAsync_ShouldNotProduceTwoActiveKeys_WhenCalledByTwoReplicasConcurrently`
  (Testcontainers Redis fixture).
- [ ] `tests/Asterisk.Platform.Api.Tests/Startup/MultiReplicaStartupAssertionTests.cs` —
  `Startup_ShouldFailFast_WhenInMemoryJwtKeyStoreInProductionWithReplicasGt1`.

**ADR.** `docs/decisions/0012-jwt-rotation-pool-wireup-and-multi-replica-gate.md` —
covers RS256-vs-HS256 choice (we keep RS256: `kid` resolver enables JWKS
publishing later, asymmetric signing is the right default for an open-core
product where downstream services may verify without holding the signing
key), legacy-file migration, rotation cadence, `RedisJwtKeyStore`
concurrency contract, fail-fast startup assertions.

**Version.** v1.14.0 (minor — config schema change, deployment runbook addition).

---

## Phase 4 — Argon2id Migration with Transparent On-Login Rehash (v1.14.0)

**Goal.** Move per-request hash-verify cost from ~75 ms (BCrypt12) to
~25–35 ms (Argon2id m=19 MiB t=2 p=1) — the dominant knee mover.
Zero user-visible disruption: legacy BCrypt hashes continue to verify;
on successful login the password is rehashed with Argon2id and the row
is updated via the AuthWriteQueue (Phase 2 consumer).

**Pre-flight (gated by Phase 0 evidence):**

- Argon2id library validated AOT-clean by Phase 0 probe; library locked
  in the `docs/research/...auth-hotpath-baseline.md` artifact.
- Phase 1 cache decorator is shipped (so first-login rehash storm doesn't
  cause a TenantAuthConfig DB stampede).
- Phase 2 write queue is shipped (so the rehash row update is async).

**Files to add:**

- [ ] `src/Asterisk.Platform.Identity/Auth/PasswordHashFormat.cs` —
  `enum PasswordHashFormat { Bcrypt, Argon2id }` + static
  `Detect(string hash)` discriminating by prefix (`$2a$/$2b$` → Bcrypt,
  `$argon2id$` → Argon2id).
- [ ] `src/Asterisk.Platform.Identity/Auth/IPasswordHasher.cs` —
  strategy interface: `string Hash(string password)`, `bool Verify(string password, string hash)`.
- [ ] `src/Asterisk.Platform.Identity/Auth/Argon2idPasswordHasher.cs` —
  wraps the chosen library; parameters m=19 MiB, t=2, p=1.
- [ ] `src/Asterisk.Platform.Identity/Auth/BcryptLegacyPasswordHasher.cs` —
  verify-only legacy path; throws on `Hash()` so no new BCrypt hashes are
  emitted post-cutover.
- [ ] `src/Asterisk.Platform.Identity/Auth/CompositePasswordHasher.cs` —
  dispatches `Verify` by `PasswordHashFormat.Detect`; always emits Argon2id from `Hash`.
- [ ] `src/Asterisk.Platform.Api/Services/PasswordRehashCommand.cs` —
  AuthWriteQueue command: `(UserId, NewHash)`. Consumer hashes with
  Argon2id, writes via `IUserStore.SaveAsync`, logs new event type
  `AuthEventTypes.PasswordRehash`.

**Files to modify:**

- [ ] `src/Asterisk.Platform.Api/Services/PasswordService.cs` —
  delegate to `IPasswordHasher` strategy; remove direct BCrypt static calls.
- [ ] `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs` —
  in `Login` immediately after successful `VerifyPassword`, if
  `PasswordHashFormat.Detect(user.PasswordHash) == Bcrypt` enqueue a
  `PasswordRehashCommand`. Request returns immediately; queue does the I/O.
- [ ] `Directory.Packages.props` —
  add the chosen Argon2id package; keep `BCrypt.Net-Next` for verify-legacy-only.
- [ ] `src/Asterisk.Platform.Api/Services/MfaService.cs` (line ~40) —
  recovery-code hashing currently uses BCrypt workFactor=10. **Leave alone.**
  Recovery-code verify is rare, off the hot path, and keeping BCrypt there
  reduces Argon2id surface area.

**Tests to add:**

- [ ] `tests/Asterisk.Platform.Identity.Tests/Auth/CompositePasswordHasherTests.cs` —
  `Verify_ShouldAcceptLegacyBcrypt_WhenHashHasBcryptPrefix`,
  `Verify_ShouldAcceptArgon2id_WhenHashHasArgon2idPrefix`,
  `Hash_ShouldAlwaysEmitArgon2id`.
- [ ] `tests/Asterisk.Platform.Api.Tests/Endpoints/AuthEndpointsTests.cs` —
  `Login_ShouldEnqueuePasswordRehash_WhenLegacyBcryptHashSucceeds`,
  `Login_ShouldNotEnqueueRehash_WhenAlreadyArgon2id`.
- [ ] `tests/Asterisk.Platform.Benchmarks/AuthHotPathBench.cs` — extend with
  `Argon2id_Verify_19MiB_t2_p1` benchmark; **must be ≤ 40 ms p99 on 9900X**
  or trigger a parameter retune.
- [ ] AOT regression: extend the Phase 0 probe to publish the Api project itself
  (not just the Argon2id library) and verify zero new trim warnings.

**ADR.** `docs/decisions/0013-password-hash-algorithm-migration.md` — covers
Argon2id parameter choice (m=19 MiB, t=2, p=1 per OWASP-2025), library
AOT validation, transparent on-login rehash, dual-prefix verification,
no forced lockout for not-yet-rehashed users. **Must additionally specify**:
- Server GC enabled in `Asterisk.Platform.Api.csproj` (`<ServerGarbageCollection>true</ServerGarbageCollection>`).
- Production alert: Gen2 GC rate > 0.5/sec triggers re-tuning (drop memory
  cost to 12 MiB or raise time cost to 3).
- Container RAM headroom: at least `2 × max_concurrent_logins × 19 MiB`
  reserved per replica (negligible vs 60 GB host, ~130 MB at the 220 req/s
  knee — noteworthy for 4 GB containers).
Source: [research/2026-04-27-auth-hotpath-baseline.md §2.4](../../research/2026-04-27-auth-hotpath-baseline.md).

**Acceptance.** Knee improves to ≥220 req/s p99 ≤ 250 ms single-replica.
100% of pre-existing BCrypt hashes converted to Argon2id within 3 logins
per user. The rehash queue MUST NOT drop `PasswordRehashCommand` items;
if the queue is full, log a warning and retry on the next login (don't
treat the rehash itself as best-effort the way `LastLoginAt` is — a
non-rehashed user is correct, just not optimized).

**Version.** v1.14.0 (minor — hash format change is a contract change to
the Identity store).

---

## Phase 5 — Connection Pool, DB Tuning, Horizontal Validation (v1.14.0)

**Goal.** Sustain the post-Phase-4 knee under 4-replica deployment.

**Files to modify:**

- [ ] `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs:39` —
  surface `Maximum Pool Size` + `Minimum Pool Size` + `Connection Idle Lifetime`
  as configuration. Document recommended values in the runbook (100/10/300 for the staging/dev tier; calibrate for production from this baseline).
- [ ] `docker/postgres.conf` *(new)* —
  tune `max_connections=200`, `shared_buffers=15GB`, `effective_cache_size=45GB`
  for the AMD 9900X / 60 GB host class. Mount via docker-compose override.
- [ ] `docker/docker-compose.full.yml` —
  add 4-replica scaling profile guarded by env var.

**Tests to add:**

- [ ] `tests/Asterisk.Platform.LoadTests/Scenarios/AuthLoginScalingScenario.cs` —
  ramp 0 → 1000 req/s over 60 s, hold 5 min; measure p50/p95/p99
  per-replica + aggregate.
- [ ] `tests/Asterisk.Platform.Api.IntegrationTests/MultiReplicaSmokeTests.cs` —
  Testcontainers spinning two `Asterisk.Platform.Api` containers behind a
  Traefik fixture; full login → token validation cross-replica passes
  100 iterations with no token rejection.

**Docs to add:**

- [ ] `docs/operations/auth-horizontal-scaling.md` — runbook for 4-replica
  deployment: required config keys, fail-fast startup assertions enumerated,
  capacity planning numbers from Phase 5 measurements.

**ADR.** `docs/decisions/0014-auth-horizontal-scaling-baseline.md` — codifies
the pool sizing, Postgres tuning, and validated scaling envelope.

**What we explicitly skip:**

- **pgBouncer transaction pooling.** Breaks `LISTEN/NOTIFY` which
  `Pro.Cluster.Storage.Postgres` and `Pro.Push.Postgres` rely on.
  Session-mode pgBouncer keeps `LISTEN/NOTIFY` but loses most of the win.
  Skip pgBouncer until those Pro packages are refactored away from
  `LISTEN/NOTIFY`, or until measured demand shows it's the next bottleneck.

**Version.** v1.14.0.

---

## Cross-Cutting Concerns

### Multi-replica readiness summary

Track all gates in one place so operators have a single checklist. Phase 3
ADR 0012 contains the canonical list:

- [ ] `IJwtKeyStore` registered as `RedisJwtKeyStore` (not `InMemoryJwtKeyStore`)
- [ ] DataProtection persisted to DB-backed `PlatformDataProtectionDbContext`
- [ ] `IJtiRevocationCache` / `IMfaPendingCache` / `IPasswordResetCache` registered as Redis variants
- [ ] `RedisAuthCacheInvalidator` listening on `asterisk:auth:invalidate`
- [ ] `JwtKeyRotationBackgroundService` registered with cadence per ADR 0012
- [ ] Startup fail-fast assertions pass

### Security invariants preserved across all phases

- Failure-path `AuthEvent.LogAsync` stays synchronous (Phase 2 invariant).
- Hash strength does not regress (Phase 4 ships at higher OWASP rating, not lower).
- No new secret-management surface (no peppers, no client-side hashing, no JWKS export until Phase 3 follow-up).
- Refresh token persistence stays synchronous (security; Phase 2 invariant).
- No password-hash data leaves the trust boundary (no caching `PasswordHash` in Redis or `IMemoryCache`; the `CachedUserStore` MUST scrub the hash field from cached projections).

### AOT discipline

- Every new dependency: AOT-publish probe in Phase 0 or in the corresponding
  phase before merge.
- `[JsonSerializable]` on every new DTO (`AuthWriteCommand`, `JwtKeyEntry`
  RS256 variant, `PasswordRehashCommand`).
- No reflection: enforce via `IsAotCompatible=true` on touched projects.

### Versioning + release packaging

- v1.13.1: Phase 1 (cache decorators).
- v1.13.2: Phase 2 (write deferral).
- v1.14.0: Phases 3 + 4 + 5 coordinated, with paired Pro 1.16.0-pro and Web 1.13.0 if anything cross-repo touches them (none currently planned). Spec the train at start of v1.14.0 work.

---

## Acceptance Criteria (measured on AMD 9900X / 60 GB / docker-compose / single replica unless noted)

| Phase | Knee req/s p99 ≤ 250 ms | Other |
|---|---|---|
| 0 baseline | confirm 75 ± 5 | dotnet-trace + bench published |
| 1 cumulative | ≥ 95 | cache hit ratio ≥ 95% steady |
| 2 cumulative | ≥ 120 | audit completeness regression test passes |
| 3 cumulative | ≥ 120 | 2-replica smoke test passes 100 iterations |
| 4 cumulative | **≥ 220** | 100% rehash within 3 logins, all AOT-clean |
| 5 (4-replica) | **≥ 800 aggregate** | p99 ≤ 400 ms under 1000 req/s ramp |

**Cross-cutting always required:**

- 0 vulnerable packages cross-repo (`dotnet list package --vulnerable` clean)
- 0 build warnings (TreatWarningsAsErrors holds)
- 100% pre-existing tests pass
- All new tests pass
- All new dependencies AOT-clean
- All ADRs land Accepted

---

## Verification (end-to-end)

**Per phase:**

1. Run `dotnet test Asterisk.Platform.slnx` — all green.
2. Run `dotnet test --filter Category=Integration` (Postgres available) — all green.
3. Run `LOADTEST_PROFILE=staging ./scripts/load-test.sh` against the docker-compose stack.
4. Run `./scripts/jwt-sweep.sh` and confirm the knee number per the table above.
5. Run `dotnet publish -p:PublishAot=true -c Release` for the Api project — zero trim warnings.
6. Confirm Grafana dashboards show the new meters (`Asterisk.Platform.Auth.WriteQueue`, `Asterisk.Platform.Auth.JwtKeyRotation` — already exists).

**Phase-3 specific (multi-replica):**

7. Spin docker-compose with 2 Api replicas behind a Traefik fixture; run
   `./scripts/load-test.sh` and confirm tokens issued by either replica
   validate on the other.
8. Run the rotation-pool concurrency test against a real Redis: two
   replicas calling `RotateAsync` simultaneously must not produce two
   active keys (CAS validation).

**Phase-4 specific (Argon2id):**

9. Pre-deploy: every existing user has BCrypt hash. Post-deploy first
   login: row updated to Argon2id. Confirm via SQL spot-check.
10. `Argon2id_Verify_19MiB_t2_p1` benchmark must measure ≤ 40 ms p99.
11. AOT publish of the Api project: zero trim warnings.

---

## Risks + Scope Cuts

**What could blow up:**

- Argon2id library has hidden reflection paths under AOT trim — Phase 0
  must AOT-publish a probe before any commit; if the candidate fails,
  fall back to a libsodium P/Invoke wrapper (more work but bulletproof).
- `RedisJwtKeyStore.RotateAsync` may not be CAS-correct as shipped in R5.4.
  Phase 3 concurrency test will surface this; fix lands as part of Phase 3.
- `JwtKeyEntry` schema change is a serialization break. Update
  `IdentityJsonContext.cs` source-generated DTOs; provide a Redis flush
  command in the runbook for in-flight state if backward-compat isn't
  worth the cost.
- AuthWriteQueue saturation under DoS — bounded queue with `DropWrite`
  policy means under sustained 10× knee load some `LastLoginAt` updates
  are lost. Acceptable. Audit failures never use the queue.

**Scope cuts if time-constrained, in order:**

1. Cut Phase 5 horizontal validation (defer to a follow-up sprint) —
   single-replica with Phases 1–4 still ships a 3× knee improvement.
2. Cut Phase 4 Argon2id and ship Phases 1–3 alone — knee moves 75 → ~120 req/s,
   multi-replica unblocked, security posture unchanged. **This is the
   safe-mode shipping target if Argon2id AOT validation fails.**
3. Cut Redis pubsub invalidation in Phase 1, keep `IMemoryCache` only,
   document the 60 s staleness window — viable for Phase 1 ship as v1.13.1;
   pubsub joins as a v1.13.2 follow-up.

**Wrong answers disguised as shortcuts (rejected here so we don't backslide later):**

- "Lower BCrypt cost from 12 to 10" — security regression vs OWASP 2025 floor; we're going up to Argon2id, not down.
- "Skip Phase 0, the bottleneck is obvious" — already burned once on JWT-001.
- "Use HMAC-SHA256 for raw signing speed" — saves ~0.5 ms while breaking JWKS-style federation interop.
- "Cache password hashes in Redis with TTL" — straight-up password-hash exposure; no.
- "Wire R5.4 rotation pool as-is" — its `JwtKeyEntry` schema is symmetric-only, incompatible with the live RS256 issuer; Phase 3 generalizes the schema before wiring.
- "Replace RSA with HS256 on issuer side" — same JWKS interop loss; we'd lock ourselves out of the open-core federation story.
- "Silently re-key passwords on cron" — DoS pattern on first deploy; rehash only happens on successful interactive login.

---

## Critical Files (reference)

- `src/Asterisk.Platform.Api/Services/PasswordService.cs` (Phase 4)
- `src/Asterisk.Platform.Api/Services/JwtTokenService.cs` (Phase 3)
- `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs` (Phases 2, 4)
- `src/Asterisk.Platform.Api/Program.cs` (Phases 1, 2, 3)
- `src/Asterisk.Platform.Identity/Auth/Jwt/JwtKeyEntry.cs` (Phase 3)
- `src/Asterisk.Platform.Identity/Auth/Jwt/JwtKeyRotationService.cs` (Phase 3 — already shipped, consumed here)
- `src/Asterisk.Platform.Identity.Redis/RedisJwtKeyStore.cs` (Phase 3 — CAS audit)
- `src/Asterisk.Platform.Identity/Mfa/TenantAuthConfigMfaPolicyEvaluator.cs` (Phase 1)
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUserStore.cs` (Phase 1)
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs` (Phase 1)
- `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` (Phase 5)
- `Directory.Packages.props` (Phase 4)

## Related ADRs (existing)

- `docs/decisions/0003-dataprotection-key-persistence-strategy.md` — DB-backed keyring (Phase 3 verifies fail-fast).
- `docs/decisions/0007-agent-tenant-resolver-strict-mode-builder.md` — pattern reference for fail-fast startup assertions.
- `docs/decisions/0008-internal-security-audit-baseline.md` — security baseline this plan upholds.
- `docs/decisions/0009-slo-baseline-alert-severity-model.md` — SLO targets this plan moves toward.

## ADRs introduced by this plan

- `docs/decisions/0010-auth-hotpath-cache-decorators.md` (Phase 1)
- `docs/decisions/0011-auth-write-deferral.md` (Phase 2)
- `docs/decisions/0012-jwt-rotation-pool-wireup-and-multi-replica-gate.md` (Phase 3)
- `docs/decisions/0013-password-hash-algorithm-migration.md` (Phase 4)
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` (Phase 5)
