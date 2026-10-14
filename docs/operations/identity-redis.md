# Operations — Identity Redis (MFA + Password-Reset Caches)

**Package:** `Verbara.Platform.Identity.Redis` (v1.9.3, R5.1 Task L) ·
**Interfaces:** `IMfaPendingCache`, `IPasswordResetCache` ·
**Backplane:** Redis (StackExchange.Redis 2.12.14).

## Why this exists

Platform API issues two kinds of short-lived tokens that must be resolvable on
any node handling the follow-up request:

| Token | Issued by | Verified by | TTL |
|--|--|--|--|
| MFA pending challenge | `POST /auth/login` (pwd OK) and `/auth/oidc/callback` | `POST /auth/mfa/verify` | 5 minutes |
| Password-reset token | `POST /auth/forgot-password` | `POST /auth/reset-password` | 1 hour |

In a single-instance deploy the default `InMemory*Cache` impls keep tokens in
a `ConcurrentDictionary` and both endpoints are served by the same process —
safe and zero-ops.

In a horizontally scaled deploy (load balancer + N Platform API pods) a token
issued by pod A cannot be verified by pod B, so MFA and password-reset flows
silently fail on any follow-up request that hits a different pod.
`Verbara.Platform.Identity.Redis` stores the tokens in Redis so their
contracts hold across the fleet: an MFA challenge allows three attempts,
reserved atomically by a Lua script before each factor is checked and deleted
by the third reservation or by a successful verify; a password-reset token is
consumed exactly once (atomic `StringGetDeleteAsync`). Redis must allow
scripting (`EVAL`/`EVALSHA`).

## Enable

### 1. Run Redis

Use the `identity-redis` (or `cluster`) profile in `docker/docker-compose.full.yml`:

```sh
docker compose -f docker/docker-compose.full.yml --profile identity-redis up -d redis
```

Or point at any existing Redis (managed, self-hosted, etc.).

### 2. Configure the connection string

Set either environment variable or `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "IdentityRedis": "redis:6379"
  },
  "Identity": {
    "Redis": {
      "KeyPrefix": "asterisk:identity:"
    }
  }
}
```

Environment variable form (used by `docker/docker-compose.full.yml`):

```sh
ConnectionStrings__IdentityRedis=redis:6379
Identity__Redis__KeyPrefix=asterisk:identity:
```

The Platform bootstrap checks `ConnectionStrings:IdentityRedis` on startup. If
present it calls `AddVerbaraPlatformIdentityRedis(...)` which replaces both
in-memory cache registrations with Redis-backed ones. If absent, the in-memory
defaults remain — zero behavioral change.

### 3. Verify

Hit the Redis CLI after issuing a login:

```sh
docker compose -f docker/docker-compose.full.yml exec redis redis-cli \
  KEYS 'asterisk:identity:*'
# asterisk:identity:mfa:challenge:0b3c...
# asterisk:identity:mfa:passwordreset:aa42...
```

After a successful verify / reset, or the third wrong MFA factor, the key
should be gone.

## Key schema

| Purpose | Key | TTL |
|--|--|--|
| MFA challenge (hash: `e` = entry JSON, `n` = attempts used) | `{prefix}mfa:challenge:{challengeToken}` | `ExpiresAt - UtcNow` (~5 min) |
| Password-reset token | `{prefix}mfa:passwordreset:{resetToken}` | `ExpiresAt - UtcNow` (~1 h) |

Redis TTL handles natural expiry. Every read additionally re-checks
`ExpiresAt` so a clock-skewed node never returns a technically expired entry.

Earlier versions kept a one-shot MFA challenge as a JSON string under
`{prefix}mfa:pending:{challengeToken}`. Those keys are no longer read (reading
a key of the other type would fail with `WRONGTYPE`): during a rolling deploy a
challenge minted by the other version is unknown for at most five minutes and
the user signs in again. Old keys expire on their own TTL.

## Sharing a multiplexer

If the Platform process already registers an `IConnectionMultiplexer` (for
example through `AddVerbaraClusterRedis(...)` from
`Verbara.Sdk.Pro.Cluster.Redis`), `AddVerbaraPlatformIdentityRedis` reuses
it instead of opening a second connection pool. Just make sure both
registrations point at the same endpoint.

## Failure modes

- **Redis down at startup** — `ConnectionMultiplexer.Connect(...)` throws, the
  host fails fast. (Desired: operators notice misconfiguration immediately.)
- **Redis goes down at runtime** — the cache calls surface the
  StackExchange.Redis exception to the `AuthEndpoints` handler, which returns
  `500`. The user sees a failed MFA / reset attempt; no silent fall-back to
  in-memory (that would break the single-consumption contract across nodes).
- **Concurrent requests on one token from several nodes** — each MFA
  reservation and each delete is atomic on a single Redis instance, so racing
  requests evaluate at most three factors and at most one signs in; a reset
  token's first `TakeAsync` wins and the second returns `null` (same as
  `InMemory`).
- **Scripting disabled on a Redis-compatible store** — the MFA reservation
  throws and the verify step fails with `500`.

## Tests

Testcontainers-backed integration tests live in
`tests/Verbara.Platform.Identity.Redis.Tests/`. They spin up a throw-away
`redis:7-alpine` container per test class. The MFA cache runs the same contract
suite as the in-memory cache (three attempts, consume-once, expiry, empty
tokens, re-store, and the concurrency cases raced over two connections), plus
TTL eviction, key layout and TTL, the ignored old namespace, key-prefix
isolation, and the DI replace behavior.

Run locally (requires Docker):

```sh
dotnet test tests/Verbara.Platform.Identity.Redis.Tests/
```

## Related

- ADR-0020 (Platform, `docs/decisions/`) — MFA policy evaluator + cache
  interfaces extracted in v1.9.2.
- `docs/plans/active/2026-04-22-r5.1-implementation-plan.md` — Task L.
