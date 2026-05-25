# Spec — JWT key store SET-index migration (Tier-2 hardening)

**Date:** 2026-05-25
**Status:** Draft — ready for execution after Tier-1 lab validation (see [`docs/plans/active/2026-05-25-jwt-tier1-lab-validation.md`](../plans/active/2026-05-25-jwt-tier1-lab-validation.md))
**Owner:** Platform team
**Estimated effort:** 3-5 days
**Target version:** v2.5.4 (ship within 2 weeks of Tier-1 lab validation)
**Predecessors:**
- [JTI investigation 2026-05-24](../research/2026-05-24-jti-investigation-presence-vu1500.md) — identifies the SCAN+N×GET cost
- [Phase C-LK report 2026-05-25](../operations/chaos-test-report-k8s-local.md) § "v2.5.2 rerun" — measures the HPA cold-start blast radius
- Commit `a6927f3a` — Tier-1 stale-cache fallback + TTL bump

## Why this exists

Tier-1 hardening (`a6927f3a`) closes the warm-pod failure mode (Redis blip on existing pod with cached keys → fallback to stale cache). It does **NOT** close the cold-start failure mode (new pod from HPA scale-up has no cache yet → first refresh fail-closes → first wave of requests 401).

In C-LK.2b loaded validation we measured 1,980 Unauthorized (1.01% fail rate) at NBomber `presence` VU=1500 sustained 300s on v2.5.2 lab. After Tier-1, the expected residual is still 500-1,500 Unauthorized per HPA scale-up burst (math: 4 new pods × ~500 cold-window requests).

The cold window is dominated by the Redis fetch cost:

| Operation | Round-trips | Latency budget under burst |
|---|---|---|
| Current: `SCAN` (cursor through whole DB) + `N×GET` | 1 + N (N≈5) = ~6 | 50ms-5s (SCAN scales with TOTAL keyspace, not just JWT keys) |
| Proposed: `SMEMBERS` (single SET lookup) + `MGET` (1 multi-get) | 2 | 20-200ms |

Reducing the cold window from 1-5s → 20-200ms reduces the per-pod cold-start blast radius from ~500 requests → ~10-50 requests, an order-of-magnitude reduction.

## Scope

**In scope:**
- New Redis schema: `<prefix>:jwt:keys:index` SET containing active key IDs (in addition to existing per-key `<prefix>:jwt:keys:<keyId>` hashes/strings)
- `RedisJwtKeyStore.GetAllAsync` rewritten: `SMEMBERS` index → `MGET` parallel keys → deserialize
- `RedisJwtKeyStore.UpsertAsync` adds `SADD index keyId` after the per-key write
- `RedisJwtKeyStore.RemoveAsync` (if exists) adds `SREM index keyId` before per-key delete
- Backfill / migration path for existing deployments where the index doesn't exist yet
- Unit tests + integration tests (`Verbara.Platform.Identity.Redis.Tests`)
- Backward-compat behavior: if `SMEMBERS index` returns empty AND the SCAN code path finds keys, do a one-time backfill `SADD` then return the SCAN result (logged at WARN — should fire at most once per deployment-upgrade)

**Out of scope:**
- Tier-3 (`IssuerSigningKeyResolverAsync` + background refresh) — separate spec
- Changes to `JwtTokenService` itself (the change is wholly inside the `IJwtKeyStore` implementation; the abstraction surface stays the same)
- Multi-tenant isolation of the index (single-tenant index per Redis DB — current pattern)

## Design

### Schema

Current Redis key layout (single-tenant, single DB):
```
asterisk:identity:jwt:keys:<keyId> → JSON-serialized JwtKeyEntry
```

Add:
```
asterisk:identity:jwt:keys:index → SET of <keyId> strings
```

Both the index and the per-key entries live in the same Redis DB. The SET is small (typically 1-5 members at steady state; up to 10 during a rotation grace overlap).

### Read path (`GetAllAsync`)

```csharp
public async Task<IReadOnlyList<JwtKeyEntry>> GetAllAsync(CancellationToken ct = default)
{
    var db = _redis.GetDatabase(_options.DatabaseIndex);
    var indexKey = $"{_options.KeyPrefix}:jwt:keys:index";

    var members = await db.SetMembersAsync(indexKey).ConfigureAwait(false);

    if (members.Length == 0)
    {
        // Backwards-compat / migration: index missing OR empty pool.
        // Fall through to the legacy SCAN path. If SCAN finds keys,
        // backfill the index so subsequent reads use the fast path.
        return await GetAllViaLegacyScanAsync(db, indexKey, ct).ConfigureAwait(false);
    }

    var keyRefs = members.Select(m => (RedisKey)$"{_options.KeyPrefix}:jwt:keys:{m}").ToArray();
    var values = await db.StringGetAsync(keyRefs).ConfigureAwait(false);

    var entries = new List<JwtKeyEntry>(values.Length);
    foreach (var val in values)
    {
        if (val.IsNullOrEmpty) continue; // index drift (stale member) — log + skip
        try
        {
            var entry = JsonSerializer.Deserialize(val!, JwtJsonContext.Default.JwtKeyEntry);
            if (entry is not null) entries.Add(entry);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Skipping malformed JwtKeyEntry in pool.");
        }
    }
    return entries;
}
```

**Index drift handling:** if `SMEMBERS` returns a keyId whose corresponding string entry is missing (race with `RemoveAsync` between `SADD` and `SET`, or operator delete), `MGET` returns `null` for that slot. We skip + log; the index entry is cleaned up by the next `UpsertAsync` that touches the index. Optionally a periodic compaction (out of scope here) could `SREM` drifted entries.

### Write path (`UpsertAsync`)

```csharp
public async Task UpsertAsync(JwtKeyEntry entry, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(entry);
    var db = _redis.GetDatabase(_options.DatabaseIndex);
    var entryKey = $"{_options.KeyPrefix}:jwt:keys:{entry.KeyId}";
    var indexKey = $"{_options.KeyPrefix}:jwt:keys:index";

    var json = JsonSerializer.Serialize(entry, JwtJsonContext.Default.JwtKeyEntry);
    var ttl = entry.ExpiresAt - DateTimeOffset.UtcNow;
    if (ttl <= TimeSpan.Zero)
        ttl = TimeSpan.FromDays(1); // safety floor

    // Transaction: SET entry + SADD index atomically.
    var tx = db.CreateTransaction();
    _ = tx.StringSetAsync(entryKey, json, ttl);
    _ = tx.SetAddAsync(indexKey, entry.KeyId);
    var committed = await tx.ExecuteAsync().ConfigureAwait(false);
    if (!committed)
        throw new InvalidOperationException($"Failed to upsert JWT key '{entry.KeyId}'; transaction not committed.");
}
```

### Backfill (one-time per deployment-upgrade)

The legacy SCAN path is preserved as `GetAllViaLegacyScanAsync` and invoked when `SMEMBERS index` returns empty. After SCAN completes successfully and returns N≥1 entries, the implementation does:

```csharp
// Backfill the index from the SCAN result so the next read uses the fast path.
var backfillMembers = entries.Select(e => (RedisValue)e.KeyId).ToArray();
if (backfillMembers.Length > 0)
{
    await db.SetAddAsync(indexKey, backfillMembers).ConfigureAwait(false);
    _logger.LogInformation("Backfilled jwt-keys index with {Count} entries from SCAN fallback.", backfillMembers.Length);
}
```

This makes the migration self-healing: deploy v2.5.4 → first read does SCAN + backfill → subsequent reads use SMEMBERS+MGET. No operator action needed.

### TTL coordination

The index SET should NOT carry its own TTL — its members reference the per-key entries which have their own per-key TTL = `ExpiresAt - now`. When a per-key entry expires, the index member remains as drift until next `UpsertAsync` or compaction. This is OK because `MGET` returns `null` for expired keys; we skip + log.

For long-term cleanliness, a separate `IJwtKeyStoreCompaction` background service could run nightly: `SMEMBERS → MGET → SREM drifted`. Out of scope for this spec.

## Observability

New counters (extend the `verbara.platform.jwt.keystore` meter, currently not created — add it):

- `jwt.keystore.read_path` (tagged `path=index|scan|backfill`) — distinguishes fast-path reads, legacy SCAN reads, and backfill events
- `jwt.keystore.index_drift_skipped` — count of MGET-null slots skipped
- `jwt.keystore.read_latency_ms` — histogram of `GetAllAsync` wall-clock

Existing logger pattern: `[LoggerMessage]` partial class on `RedisJwtKeyStore` (currently has no logger — add one).

## Tests

### Unit tests (in `Verbara.Platform.Identity.Redis.Tests/RedisJwtKeyStoreTests.cs`)

Existing tests use `Testcontainers.Redis` (or similar). Add:

1. `GetAllAsync_ShouldUseSmembersPath_WhenIndexExists` — seed index + 2 entries; verify SCAN is NOT called (intercept via `MeterListener` for `jwt.keystore.read_path` with `path=index`)
2. `GetAllAsync_ShouldFallBackToScan_WhenIndexEmpty` — seed entries only (no index); verify `path=scan` + then `path=backfill` increments
3. `GetAllAsync_ShouldSkipDriftedMembers_WhenIndexReferencesExpiredKey` — seed index with `keyId` whose entry has expired; verify entry skipped + `index_drift_skipped` increments
4. `UpsertAsync_ShouldAddToIndex_Atomically` — verify after Upsert, `SMEMBERS` includes the new key
5. `GetAllAsync_ShouldReturnEmpty_WhenIndexAndScanBothEmpty` — fresh DB; verify no exception, returns empty

### Integration test (E2E with cold-start simulation)

Add a new test that simulates the C-LK.2b scenario at smaller scale:
- Spin up a real Redis container
- Seed pool with 3 keys via the new code
- Drop all per-pod caches (simulate cold pod)
- Concurrent-fetch validation keys × 100 callers
- Assert: all 100 callers receive the keys; latency p99 < 100ms

## Acceptance criteria

| Metric | Pre-Tier-2 (current) | Post-Tier-2 target |
|---|---|---|
| `GetAllAsync` round-trips per call | 1 SCAN + N GETs (~6) | 2 (SMEMBERS + MGET) |
| `GetAllAsync` p99 latency (5 keys, idle Redis) | 5-15 ms | < 5 ms |
| `GetAllAsync` p99 latency (5 keys, Redis under burst) | 50-5000 ms | < 200 ms |
| HPA cold-start blast radius (per new pod) | ~500 Unauthorized | < 50 Unauthorized |
| C-LK.2b NBomber rerun fail count at VU=1500/300s | 500-1,500 (post-Tier-1 estimate) | < 100 |

## Backout

The legacy SCAN path remains in code as `GetAllViaLegacyScanAsync`. If post-deployment monitoring reveals an issue:

```csharp
// Feature-flag environment variable for emergency rollback.
if (Environment.GetEnvironmentVariable("VERBARA_JWT_KEYSTORE_FORCE_SCAN") == "1")
    return await GetAllViaLegacyScanAsync(db, indexKey, ct);
```

The flag would be set via Helm `values.yaml` env injection; pods pick up on next restart (no code revert needed).

## Risks

1. **Transaction failure leaves index drift.** Mitigated by: backfill self-heal on next read miss + idempotent SADD.
2. **Index grows unbounded if `RemoveAsync` isn't called consistently.** Mitigated by: bounded growth (5-10 keys steady-state) + periodic compaction (deferred).
3. **MGET partial response under Redis cluster (multi-shard).** N/A: Verbara uses single-instance Redis (or replica-set for HA, not sharded). If we move to Redis Cluster in the future, MGET would need slot-aware fetching.

## Implementation checklist

- [ ] Add `RedisJwtKeyStoreOptions.IndexKeyName` constant (default `"jwt:keys:index"`)
- [ ] Rewrite `GetAllAsync` per design
- [ ] Update `UpsertAsync` to use transaction
- [ ] Add `RemoveAsync` if not present, or update existing
- [ ] Add `ILogger<RedisJwtKeyStore>` + `IMeterFactory` to constructor (optional params, default `NullLogger.Instance` + `new Meter`)
- [ ] Add `[LoggerMessage]` partial class with 4 log methods
- [ ] Add 3 counters (read_path / drift_skipped / read_latency_ms)
- [ ] Add unit tests #1-5
- [ ] Add E2E integration test
- [ ] Update `RedisJwtKeyStoreTests.cs` to cover transaction failure path
- [ ] Update DI wiring in `Verbara.Platform.Api/Composition/` (if RedisJwtKeyStore has explicit DI registration there)
- [ ] Document the rollback env-flag in `docs/operations/runbook.md`
- [ ] Tag `v2.5.4-rc1` → release.yml → 4 images → authorize digests → helm upgrade lab → C-LK.2b rerun
- [ ] If acceptance criteria pass: tag `v2.5.4` final → ship
- [ ] If acceptance criteria fail: roll back via `VERBARA_JWT_KEYSTORE_FORCE_SCAN=1` + escalate to Tier-3

## Cross-references

- [Tier-1 commit `a6927f3a`](../../) — predecessor stale-cache fallback + TTL bump
- [Tier-1 lab validation plan](../plans/active/2026-05-25-jwt-tier1-lab-validation.md)
- [JTI investigation 2026-05-24](../research/2026-05-24-jti-investigation-presence-vu1500.md) § "Tier 2"
- [C-LK chaos report](../operations/chaos-test-report-k8s-local.md) § "v2.5.2 rerun" findings #3
- ADR-0012 — JWT rotation pool architecture
- ADR-0022 — AOT constraints (any new code must source-gen JSON via `JwtJsonContext`)
