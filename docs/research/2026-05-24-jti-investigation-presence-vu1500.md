# JTI Revocation Investigation — Presence VU=1500 Unauthorized failures

**Date:** 2026-05-24
**Trigger:** R5.5 Phase B-LK 2026-05-24 sweep (commit `b72ca30b`). The presence VU=1500 step recorded **1,000 Unauthorized failures** out of 35,755 total iterations (~2.8% failure rate). Memory `project_phase_a5_audit_harness_session` + the original B-LK README initially attributed this to "JTI revocation cache turnover by AHH train rotation". This investigation **disproves** that hypothesis and identifies the actual cause.

## TL;DR

**Original hypothesis (REFUTED):** JTI cache rotation invalidates in-flight tokens during sustained load. → ❌ False.

**Actual hypothesis (UNVERIFIED but strongly supported by code evidence):** A sync-over-async pattern inside the per-pod JWT validation-key cache resolver causes thread-pool starvation + Redis timeout during burst-load + cache-expiry coincidence, producing the 1000 Unauthorized via JwtBearer middleware signature-validation failures.

**Recommended next step:** controlled experiment with instrumented logging on the next sweep (post-v2.5.2 deploy) to confirm the failure mode, then ship a layered fix (stale-cache fallback + indexed key-list + cache-TTL bump).

## Investigation — refuting the original hypothesis

### Was JTI cache turnover the cause?

The phrase "JTI cache rotation" suggests `IJtiRevocationCache` (Redis-backed denylist for revoked tokens) is invalidating tokens mid-burst.

**Code evidence:**

- [`src/Verbara.Platform.Identity/Auth/IJtiRevocationCache.cs`](../../src/Verbara.Platform.Identity/Auth/IJtiRevocationCache.cs) — interface contract: `IsRevokedAsync` returns true ONLY when `jti` has been explicitly revoked via `RevokeAsync`. There is no implicit / periodic invalidation.

- [`src/Verbara.Platform.Identity.Redis/RedisJtiRevocationCache.cs`](../../src/Verbara.Platform.Identity.Redis/RedisJtiRevocationCache.cs) — Redis implementation. `RevokeAsync` writes a marker with TTL = `expiresAt - now` so revoked-entries auto-expire when the original token would. `IsRevokedAsync` does `KeyExistsAsync`. **No rotation. No batch invalidation. Pure denylist.**

**Conclusion:** the JTI revocation cache is a **denylist**, not a TTL-based session cache. It does not "rotate" or "turn over" in a way that would invalidate in-flight tokens. The original B-LK hypothesis was a misreading of the architecture.

### Was JWT signing-key rotation the cause?

The signing-key rotation pool (`JwtKeyRotationService`) rotates the symmetric signing key on a configurable cadence. If the active key rotated mid-burst and old tokens couldn't be validated, that would cause 401s.

**Code evidence:**

- [`src/Verbara.Platform.Identity/Auth/Jwt/JwtKeyRotationOptions.cs`](../../src/Verbara.Platform.Identity/Auth/Jwt/JwtKeyRotationOptions.cs):
  - `ActiveDuration = 24 h` (default) — key signs for 24h before next rotation demotes it
  - `GracePeriod = 24 h` — demoted key still validates already-issued tokens for 24h after rotation
  - Total validation window: **48 h**

- [`JwtKeyRotationService.RotateAsync`](../../src/Verbara.Platform.Identity/Auth/Jwt/JwtKeyRotationService.cs) — only triggered on explicit admin call or when `GetActiveSigningKeyAsync` finds no active key. **No periodic auto-rotation.**

**Conclusion:** 60-second burst is 4,320× smaller than the 48h validation window. Key rotation cannot be the cause.

## Investigation — finding the actual cause

### Is the JTI cache even on the JwtBearer validation path?

[`src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs:64-100`](../../src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs) registers the JwtBearer handler with:

```csharp
.AddJwtBearer(JwtScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context => { ... },  // query-token extraction only
        OnTokenValidated = context => { ... },   // tenant resolution only
    };
})
```

**The `OnTokenValidated` event does NOT call `IsRevokedAsync`.** Revocation is checked exclusively via `JwtTokenService.ValidateTokenAsync` (a separate method), and that method is **only called by 1 internal caller** in `JwtTokenService` itself (per `grep -rn "ValidateTokenAsync"`). The middleware path validates signature + lifetime + issuer + audience but never asks "is this JTI revoked?".

**Implication:** even if `IsRevokedAsync` threw exceptions under burst-load Redis contention, the JwtBearer middleware path would not see them. The 1000 Unauthorized do NOT come from the JTI cache.

### Where do the 401s come from, then?

JwtBearer middleware returns 401 when token validation fails. Validation parameters (from [`JwtTokenService.cs:117-129`](../../src/Verbara.Platform.Api/Services/JwtTokenService.cs)):

```csharp
_poolValidationParameters = new TokenValidationParameters
{
    ValidateIssuer = true,
    ValidIssuer = Issuer,
    ValidateAudience = true,
    ValidAudience = Audience,
    ValidateLifetime = true,
    ValidateIssuerSigningKey = true,
    IssuerSigningKeyResolver = (_, _, _, _) => GetCachedValidationKeys(),
    ClockSkew = TimeSpan.FromSeconds(30),
    ...
};
```

Under burst, the only validation parameter that depends on shared state is `IssuerSigningKeyResolver`. If that throws or returns no keys, validation fails with `SecurityTokenSignatureKeyNotFoundException` → 401.

### The smoking gun — `GetCachedValidationKeys`

[`src/Verbara.Platform.Api/Services/JwtTokenService.cs:275-295`](../../src/Verbara.Platform.Api/Services/JwtTokenService.cs):

```csharp
private IEnumerable<SecurityKey> GetCachedValidationKeys()
{
    if (_rotationService is null)
        return [_fileSigningKey!];

    var cached = _cachedValidation;
    if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
        return cached.Keys;

    lock (_cacheLock)
    {
        cached = _cachedValidation;
        if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
            return cached.Keys;

        var entries = _rotationService.GetValidationKeysAsync().GetAwaiter().GetResult();  // ← (1) SYNC OVER ASYNC
        var keys = entries.Select(e => BuildSigningCredentials(e).Key).ToArray();
        _cachedValidation = new CachedValidationKeys(keys, DateTimeOffset.UtcNow);
        return keys;
    }
}
```

With `ActiveKeyCacheTtl = 60s` (line 55), every pod re-fetches validation keys from Redis every 60 seconds. The fetch is:

```csharp
// src/Verbara.Platform.Identity.Redis/RedisJwtKeyStore.cs:54-79
public async Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default)
{
    ...
    foreach (var endpoint in _redis.GetEndPoints())
    {
        var server = _redis.GetServer(endpoint);
        if (!server.IsConnected || server.IsReplica) continue;

        await foreach (var redisKey in server.KeysAsync(_options.DatabaseIndex, pattern)...)
        {
            var json = await db.StringGetAsync(redisKey).ConfigureAwait(false);  // ← (2) SCAN + N×GET
            ...
        }
    }
}
```

`server.KeysAsync` is a Redis `SCAN` (cursor-based key enumeration) + one `GET` per matching key. With ~5 keys in the rotation pool, that's **1 SCAN + 5 sequential GETs = 6 Redis round-trips per refresh**. In a healthy connection ~5-10 ms. Under contention significantly more.

### The failure cascade under VU=1500 burst

1. **T0 + 0 s** — burst starts; ~5 platform-api pods × ~300 VUs/pod = 1,500 concurrent JWT-bearing requests.
2. **T0 + ~60 s** — a pod's `_cachedValidation` expires. Next request's `IssuerSigningKeyResolver` calls `GetCachedValidationKeys`.
3. Lock contention: 1 lock holder enters the critical section, ~299 other requests on that pod queue.
4. Lock holder calls `_rotationService.GetValidationKeysAsync().GetAwaiter().GetResult()` — **sync over async** blocks the worker thread until the Redis SCAN+GETs complete.
5. StackExchange.Redis multiplexes all Redis requests through 1 connection; under VU=1500 concurrent activity (logins + tenants + JTI checks via other paths, etc.), the multiplexer's pipeline is backlog-heavy.
6. SCAN+GET request queued behind backlog. Default `synctimeout` and `asynctimeout` are both 5 s. If the call exceeds 5 s, `RedisTimeoutException` is thrown.
7. Exception propagates **out of `GetAwaiter().GetResult()`** → out of `GetCachedValidationKeys` → into `IssuerSigningKeyResolver` → caught by `JwtSecurityTokenHandler.ValidateToken` → translated to `SecurityTokenSignatureKeyNotFoundException` → JwtBearer middleware → **HTTP 401 Unauthorized**.
8. **All 299 queued requests on that pod**, since they were waiting on the same lock, eventually see either (a) the same Redis timeout if they re-enter the critical section, or (b) success once the cache refills. The blast radius is **per-pod, per-cache-expiry event**.

If 3 of the 5 pods cross their 60s cache boundary within the 60-second burst window, the expected order-of-magnitude is `3 pods × ~300-400 affected requests/event ≈ 1000`. **This matches the observed 1000 Unauthorized within a factor of 1×.**

## Additional contributing factors (defence-in-depth opportunities)

- **`Active Key Cache TTL = 60 s`** is short relative to Verbara's actual key-rotation cadence (24 h `ActiveDuration` + 24 h `GracePeriod`). The cache could be 5 minutes with no semantic change.
- **Redis SCAN** is O(N) over the keyspace; even with `MATCH` patterns it walks the entire DB. With Verbara's `asterisk:identity:` prefix sharing the Redis DB with `asterisk:cache:*` (FeatureGateCache, TenantTierCache, etc.), SCAN cost grows with the noisy-neighbour load.
- **Per-pod cache lock is global** — every request through the pod with an expired cache contends. A reader-writer lock would let cache-hit reads bypass the writer queue.

## Recommended fix path (layered, ranked by risk × impact)

### Tier 1 — ✅ SHIPPED 2026-05-25 (post C-LK validation)

> **Implementation status:** Both Fix A (stale-cache fallback) and Fix B
> (`ActiveKeyCacheTtl 60s → 300s`) shipped in [`src/Verbara.Platform.Api/Services/JwtTokenService.cs`](../../src/Verbara.Platform.Api/Services/JwtTokenService.cs).
> Stale-cache fallback was applied to BOTH `GetActiveSigningCredentials`
> and `GetCachedValidationKeys` (symmetric blast-radius coverage).
> Test coverage added in
> [`tests/Verbara.Platform.Api.Tests/Services/JwtTokenServiceRotationTests.cs`](../../tests/Verbara.Platform.Api.Tests/Services/JwtTokenServiceRotationTests.cs):
> `ValidateToken_ShouldReuseStaleCache_WhenGetValidationKeysAsyncThrows`,
> `GenerateAccessToken_ShouldReuseStaleCachedCredentials_WhenGetActiveSigningKeyAsyncThrows`,
> `GenerateAccessToken_ShouldFailClosed_WhenRotationServiceAlwaysThrowsAndNoCacheYet`
> (946/946 Api.Tests pass, was 943 baseline).
> Trigger reframing: [[project-c-lk-validation-v252]] showed the same
> failure mode is also produced by HPA scale-up cold caches (not just
> Redis blips). Tier-1 fix covers both triggers because the catch
> handler is independent of why the refresh failed.

**Fix A: Stale-cache fallback.** If `GetValidationKeysAsync` throws inside the critical section, fall back to the previously-cached keys (even if past TTL) instead of bubbling the exception:

```csharp
lock (_cacheLock)
{
    cached = _cachedValidation;
    if (cached is not null && DateTimeOffset.UtcNow - cached.CachedAt < ActiveKeyCacheTtl)
        return cached.Keys;

    try
    {
        var entries = _rotationService.GetValidationKeysAsync().GetAwaiter().GetResult();
        var keys = entries.Select(e => BuildSigningCredentials(e).Key).ToArray();
        _cachedValidation = new CachedValidationKeys(keys, DateTimeOffset.UtcNow);
        return keys;
    }
    catch
    {
        // Redis blip → reuse stale cache (better than 401-ing legitimate requests)
        if (cached is not null) return cached.Keys;
        throw;  // No cache at all → fail closed
    }
}
```

**Trade-off:** during a sustained Redis outage, validation keeps using stale keys (potentially missing a freshly-rotated key). Acceptable because (a) rotation is 24h cadence, (b) the alternative is universal 401s.

**Fix B: Bump cache TTL.** `ActiveKeyCacheTtl = 60s → 300s`. Reduces cache-miss frequency 5×. No semantic change (validation window is 48h anyway).

### Tier 2 — ship in v2.6.x or later (~3-5 days, medium risk)

**Fix C: Replace SCAN+N×GET with indexed key list.** Store a `SET asterisk:identity:jwt:keys:index` containing the active key IDs. `GetAllAsync` does `SMEMBERS` (O(N) but a single call) + N parallel `GET`s using `Task.WhenAll`. Removes the SCAN cost entirely.

### Tier 3 — architectural (multi-week, high risk)

**Fix D: Convert IssuerSigningKeyResolver to async.** Microsoft.IdentityModel.Tokens 7.x added `IssuerSigningKeyResolverAsync`. This eliminates the sync-over-async deadlock potential entirely. Requires careful refactor of JwtTokenService + JwtBearer wiring + tests.

**Fix E: Background-refreshed cache.** A `BackgroundService` proactively refreshes the validation-keys cache every 30s. `GetCachedValidationKeys` becomes lock-free read of a `volatile` reference. Eliminates the lock + sync-over-async entirely.

## Validation experiment (before shipping any fix)

To confirm the hypothesis is the actual cause (and not, e.g., some unrelated middleware issue), run the next B-LK measurement against v2.5.2 with these instrumentation additions:

1. **Log on `GetCachedValidationKeys` cache miss** — `_logger.LogInformation("JWT validation key cache miss; refresh starting")`.
2. **Log on `GetValidationKeysAsync` exception** — wrap in try/catch, log exception, then rethrow.
3. **Add `JwtBearerEvents.OnAuthenticationFailed`** — log the exception type + stack trace.

If the hypothesis is correct, the next presence VU=1500 sweep should produce a cluster of 3-5 "cache miss" events correlated with a cluster of `OnAuthenticationFailed` logs carrying `SecurityTokenSignatureKeyNotFoundException` or `RedisTimeoutException` stack traces, and the count of failures should match the per-pod-event multiplier × number of events.

If the hypothesis is wrong, the failure pattern from those logs will point at the actual cause.

## Cross-references

- B-LK evidence: [`docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/README.md`](../operations/r55-blk-evidence/2026-05-24-v251-baseline/README.md) § "Presence broadcast" + "Conclusions"
- ADR-0010 (Argon2id + AuthWriteQueue) — historical context on auth path
- ADR-0012 (JWT rotation pool) — multi-replica key store contract
- ADR-0025 (K8s liveness/readiness contract) — sibling investigation from the same B-LK
- Pro ADR-0011 (image-digest binding) — orthogonal but referenced by JwtKeyRotation comments
