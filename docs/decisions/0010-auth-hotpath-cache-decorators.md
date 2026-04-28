# ADR-0010: Auth hot-path cache decorators

**Status:** Accepted
**Date:** 2026-04-27
**Context:** AHH Phase 1 (v1.13.1)

## Context

R5.5 measured a 75 req/s sustainable knee on `POST /auth/login`. AHH Phase 0
empirically attributed ≥99.9% of the per-request crypto cost to BCrypt12
verify (~162 ms). Phase 4 will replace BCrypt with Argon2id (~33 ms,
4.9× faster) — but Phase 0 also identified 5–10 ms × 2–3 uncached DB
round-trips per login as a secondary contributor:

- `ITenantAuthConfigStore.GetAsync` — fired by
  `TenantAuthConfigMfaPolicyEvaluator.RequiresMfaAsync` on every login,
  plus separately for lockout policy + password policy reads.
- `IUserStore.GetByEmailAsync` — fired by `Login`.
- `IUserStore.GetByIdAsync` — fired by `/auth/me`, `MfaVerify`, refresh flow.

AHH Phase 1 introduces in-process cache decorators that remove these reads
from the hot path on cache hit. Phase 1 ships ahead of Phase 4 because the
work is independent, low-risk, and reversible.

## Decision

**Decorate `IUserStore` and `ITenantAuthConfigStore` with `IMemoryCache`-backed
wrappers, with a 60 s default TTL and explicit invalidation on writes. Cross-replica
invalidation is delivered via Redis pubsub when `Asterisk.Platform.Identity.Redis`
is registered; otherwise staleness is bounded by the local TTL.**

Concrete shape:

1. **`AuthHotpathCacheKeys`** (`src/Asterisk.Platform.Identity/`) exposes three
   constants — two keyed-DI service keys
   (`UserStoreInner` / `TenantAuthConfigStoreInner`) and one Redis pubsub
   channel name (`asterisk:auth:invalidate`).
2. **Storage providers (`AddPostgresStorage` + `AddInMemoryStorage`)** register
   the concrete store **keyed-as-inner** plus an unkeyed alias that points at
   the keyed instance. The alias is what existing call sites continue to
   resolve until the Api bootstrap installs the decorators.
3. **`CachedUserStore` + `CachedTenantAuthConfigStore`** (Api project) wrap an
   inner `IUserStore` / `ITenantAuthConfigStore` resolved via
   `GetRequiredKeyedService`. Reads check `IMemoryCache` first; misses
   populate. Writes pass through to the inner store, then invalidate the
   local cache and (when wired) publish a Redis pubsub message.
4. **`AddAuthHotpathCaching()`** removes the unkeyed aliases and re-registers
   them as the cache decorators. Idempotent.
5. **`RedisAuthCacheInvalidator`** (Identity.Redis project) is a hosted
   service that subscribes to the Redis pubsub channel and dispatches
   invalidation messages to local `ILocalAuthCacheInvalidationSink`
   instances (the cache decorators + `PermissionResolver`). Each message is
   a pipe-delimited `originatorId|type|key…` so the originator suppresses
   its own publishes.
6. **`PermissionResolver`** receives an optional `RedisAuthCacheInvalidator`
   and publishes on every `InvalidateUser` call so role grants propagate
   cross-replica within a network round-trip instead of waiting up to
   5 minutes for the local TTL to expire.

## Trust boundary

The cached `User` object includes the `PasswordHash` field. **This is
intentional and acceptable in `IMemoryCache` because in-process memory is
the same trust envelope as the DataProtection keyring + JWT signing key.
The hash is never serialized to Redis.** The `RedisAuthCacheInvalidator`
pubsub channel transports **invalidation keys only**, never values:
the wire format is `originatorId|type|tenantId|userId|email` where every
field is either a tenant-scoped identifier or an email (already broadcast
in normal user-management flows). Password hashes, MFA secrets, MFA
recovery codes, and TOTP seeds **never cross the process boundary** through
the AHH cache layer.

This invariant is enforced by:

- `CachedUserStore` only stores `User` objects in `IMemoryCache`; nothing
  is written to Redis or any external cache.
- `RedisAuthCacheInvalidator.PublishUserAsync` accepts only
  `(tenantId, userId, email)` and publishes a string with no value
  payload.
- A regression test (`CachedUserStoreTests.GetByEmailAsync_ShouldNotLeakAcrossTenants_…`)
  asserts cross-tenant isolation; a separate test asserts that the cache
  layer never propagates `PasswordHash` to anything that could serialize
  to a non-process boundary.

## TTL choice

60 seconds. Rationale:

- A failed login at 60 s of stale `TenantAuthConfig` cannot escalate
  privilege — MFA / lockout / password policy are all defense-in-depth;
  a window where the policy is briefly stale yields the same security
  posture as before the cache existed (the policy was queried per
  request before; with TTL it is queried at most once every 60 s).
- A failed login at 60 s of stale `User` (e.g. password just changed,
  account just locked) blocks the most adversarial case: an attacker
  who learned the old password keeps trying. The window before
  `PasswordChangedAt` propagates is bounded by the lockout policy, which
  is read fresh by definition (`AccountLockoutService`).
- Cross-replica deployments with Redis configured see invalidation
  delivered within a network round-trip; the TTL is a defense-in-depth
  ceiling on stale data when Redis pubsub itself is degraded.

## Why `IMemoryCache` instead of `HybridCache` / Redis-only

- `HybridCache` (Microsoft.Extensions.Caching.Hybrid 9.0) requires
  source-generated serializers for the cached payload and adds a Redis
  serialization round-trip per read. The Phase 1 win comes from **not
  touching the network at all** on cache hit; a Redis-only or hybrid cache
  re-introduces the network call that Phase 1 is specifically removing.
- The hot-read traffic is bounded (`User` + `TenantAuthConfig` per tenant)
  and survives single-process memory comfortably. There is no eviction
  pressure that a distributed cache would solve.
- `IMemoryCache` is AOT-clean and ships with ASP.NET Core's shared
  framework — no new dependency.

## Why pubsub instead of TTL-only

- 5-minute role-grant propagation (the previous behavior of
  `PermissionResolver` in single-replica mode) is acceptable for a
  single-replica deployment. Multi-replica makes that window N×5 minutes
  in the worst case (each replica's local TTL is independent). Pubsub
  bounds it to a network round-trip.
- The pubsub channel is opt-in: when Redis is not configured, the
  decorators function in single-replica mode with TTL-only invalidation,
  preserving the old behavior. There is no hard dependency.

## Failure modes + mitigation

| Failure | Effect | Mitigation |
|---|---|---|
| Redis unavailable at startup | Hosted service fails to subscribe | `RedisAuthCacheInvalidator` logs + retries via the connection multiplexer's reconnect loop; cache decorators continue to function with TTL-only invalidation. |
| Pubsub message lost | One replica holds stale data for up to 60 s | Bounded by TTL. Acceptable per the threat model in §"TTL choice". |
| Two replicas write the same user concurrently | Last-writer-wins at the DB; both publish invalidation; remote replicas double-invalidate | Idempotent — `IMemoryCache.Remove` on a missing key is a no-op. |
| Decorator misconfiguration (Api forgets to call `AddAuthHotpathCaching`) | Cache layer is bypassed; behavior reverts to pre-Phase-1 | Acceptable graceful degradation. Phase 3's startup assertion (multi-replica gate) will fail-fast if `Replicas>1` and Redis-driven invalidation is missing. |
| Cache stampede on cold cache | First N requests for the same tenant/user hit DB simultaneously | Acceptable — at the documented knee of 75 req/s, even N=10 same-key concurrent misses is well under DB capacity. If measured to be a problem, swap `IMemoryCache.Set` for a coalescing pattern under `LazyCache` or similar. Defer until measured. |

## Tested invariants

- **`CachedTenantAuthConfigStoreTests`** —
  `GetAsync_ShouldReturnCachedValue_WhenCacheIsWarm`,
  `SaveAsync_ShouldInvalidateCache_WhenWriteCompletes`,
  `GetAsync_ShouldRespectTtl_WhenItemExpires`.
- **`CachedUserStoreTests`** — same shape +
  `GetByEmailAsync_ShouldNotLeakAcrossTenants_WhenSameEmailInTwoTenants`
  (multi-tenant safety regression).
- **`RedisAuthCacheInvalidatorTests`** —
  `OnRedisMessage_ShouldDispatchToSinks_WhenTypeIsKnown`,
  `OnRedisMessage_ShouldIgnoreOwnPublishes_WhenOriginatorIdMatches`,
  `OnRedisMessage_ShouldIgnoreUnknownTypes_WhenWireFormatChangesInFuture`.

## Considered alternatives

- **`Scrutor`-based decoration.** Rejected: introduces a new dependency for
  a one-line gain. The manual `RemoveUnkeyed` + keyed-service pattern is
  five lines and AOT-explicit.
- **Cache the entire `IUserStore.ListAsync` paged result.** Rejected:
  admin/back-office paths are not on the throughput-knee; pagination
  invalidation under writes is harder than the win is worth. `ListAsync`
  + `GetByIdsAsync` pass through.
- **Cache `OidcSubject` lookups.** Rejected: not on the password-login hot
  path; users only authenticate via OIDC during cookie session estab —
  rarely under load. Pass through.
- **Use a single global cache key with object-graph subkey.** Rejected:
  multi-tenant isolation is easier to verify with explicit per-tenant
  key prefixes (`user:byid:{tenantId}:{userId}`).

## Future migration (Phase 3)

Phase 3's multi-replica gate adds a startup assertion that fails fast in
production with `Replicas>1` if `RedisAuthCacheInvalidator` is not
registered. Phase 1 remains compatible: the assertion only triggers in
the multi-replica configuration, single-replica deployments are unchanged.
