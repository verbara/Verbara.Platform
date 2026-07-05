# Verbara.Platform.Identity.Redis

Redis-backed implementations of `IMfaPendingCache` and `IPasswordResetCache`
for multi-instance Platform API deployments.

The in-memory defaults registered by `Program.cs` keep MFA challenge tokens
and password-reset tokens inside a single process's
`ConcurrentDictionary`. In a horizontally scaled deployment (load balancer +
multiple Platform API instances) a token issued by instance A cannot be
verified by instance B, so the user silently fails MFA on the next hop.

This package adds a drop-in replacement that stores both caches in Redis
with per-entry TTL and atomic read-delete semantics (`StringGetDeleteAsync`)
so a token is consumed exactly once across the cluster.

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
| MFA pending challenge | `{prefix}mfa:pending:{challengeToken}` | 5 minutes |
| Password reset token | `{prefix}mfa:passwordreset:{resetToken}` | 15 minutes |

TTL is set to the remaining `ExpiresAt` window at `StoreAsync` time, so Redis
naturally evicts stale tokens. `TakeAsync` additionally re-checks
`ExpiresAt` on read to guard against clock skew between API nodes and the
Redis server.

## AOT

JSON serialization goes through `IdentityJsonContext` (source-generated,
camelCase), so the package is trim- and AOT-safe.
