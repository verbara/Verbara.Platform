# Verbara.Platform.Identity.Redis

Redis-backed implementations of `IMfaPendingCache` and `IPasswordResetCache`
for multi-instance Platform API deployments.

The in-memory defaults registered by `Program.cs` keep MFA challenge tokens
and password-reset tokens inside a single process's
`ConcurrentDictionary`. In a horizontally scaled deployment (load balancer +
multiple Platform API instances) a token issued by instance A cannot be
verified by instance B, so the user silently fails MFA on the next hop.

This package adds a drop-in replacement that stores both caches in Redis
with per-entry TTL, so every instance sees the same tokens:

- **MFA challenge** — a challenge allows three attempts. Each attempt is
  reserved atomically before the factor is checked (a Lua script), and the
  reservation that reaches the limit deletes the challenge in the same step;
  a correct factor on an earlier attempt deletes it with a single `DEL`, and
  only the caller whose `DEL` removed the key may issue a session. Racing
  requests on any number of instances therefore never evaluate more than
  three factors, and at most one of them signs in.
- **Password reset** — atomic read-delete (`StringGetDeleteAsync`), so a token
  is consumed exactly once across the cluster.

## Wire-up

```csharp
var identityRedisConn = builder.Configuration.GetConnectionString("IdentityRedis");
if (!string.IsNullOrEmpty(identityRedisConn))
{
    builder.Services.AddVerbaraPlatformIdentityRedis(o =>
    {
        o.ConnectionString = identityRedisConn;
        o.KeyPrefix = "asterisk:identity:";
    });
}
// else: the in-memory caches registered earlier continue to serve traffic.
```

The `AddVerbaraPlatformIdentityRedis` extension is an opt-in override — it
replaces any existing `IMfaPendingCache` + `IPasswordResetCache` registrations
and registers a singleton `IConnectionMultiplexer` if one is not already
present (so it shares the pool with other Redis-backed packages such as
`Verbara.Sdk.Pro.Cluster.Redis`).

## Key schema

| Purpose | Key | TTL |
|--|--|--|
| MFA challenge (hash: `e` = entry JSON, `n` = attempts used) | `{prefix}mfa:challenge:{challengeToken}` | 5 minutes |
| Password reset token | `{prefix}mfa:passwordreset:{resetToken}` | 15 minutes |

TTL is set to the remaining `ExpiresAt` window at `StoreAsync` time, so Redis
naturally evicts stale tokens (a reservation's `HINCRBY` keeps the TTL). Every
read additionally re-checks `ExpiresAt` to guard against clock skew between
API nodes and the Redis server.

## Requirements

- **Scripting (`EVAL`/`EVALSHA`) must be allowed.** Storing and reserving an MFA
  challenge each run as one single-key Lua script (Redis-Cluster safe). Redis,
  Valkey and the managed Redis offerings allow it; a Redis-compatible store
  with scripting disabled fails the MFA step.
- Redis ≥ 6.2 (`GETDEL`, used by the password-reset cache).

## Upgrading from the `mfa:pending:` keys

Earlier versions stored a one-shot challenge as a JSON string under
`{prefix}mfa:pending:{challengeToken}`. The namespace changed on purpose —
reading a key of the other type would fail with `WRONGTYPE` — so during a
rolling deploy a challenge minted by the other version is unknown for at most
its five-minute lifetime and the user signs in again. Old keys expire on their
own TTL; nothing needs to be cleaned up.

## AOT

JSON serialization goes through `IdentityJsonContext` (source-generated,
camelCase), and scripts are sent with
`ScriptEvaluateAsync(string, RedisKey[], RedisValue[])` — never the
prepared-script helper, which binds parameters by reflection — so the package
is trim- and AOT-safe.
