# ADR-0011: Auth write-path deferral

**Status:** Accepted
**Date:** 2026-04-27
**Context:** AHH Phase 2 (v1.13.2)

## Context

AHH Phase 0 evidence
([baseline doc](../research/2026-04-27-auth-hotpath-baseline.md))
attributed ≥99.9 % of the per-request crypto cost to BCrypt12 verify.
The remaining wall time on `POST /auth/login` is dominated by three
synchronous Postgres round-trips that fire on every successful login:

| Operation | File | Cost |
|---|---|---|
| `users` upsert (`AccountLockoutService.ResetAttemptsAsync`) | `Services/AccountLockoutService.cs` | ~5–10 ms |
| `users` upsert (`LastLoginAt` mutation, currently un-persisted bug) | `Endpoints/AuthEndpoints.cs:885` | ~5–10 ms (when fixed) |
| `auth_events` insert (`AuthEventService.LogAsync` for `LoginSuccess`) | `Services/AuthEventService.cs:14` | ~5–10 ms |

Each is independent of the JWT response shape — none of the post-write
state is read back into the response. Phase 2 moves them off the critical
path so the request returns ~10–20 ms sooner without changing user-visible
behavior.

## Decision

**Introduce an in-process bounded background queue
(`Asterisk.Platform.Api.Services.AuthWriteQueue`) that defers the three
success-path writes. Failure-path audit logs and refresh-token persistence
remain strictly synchronous.**

Concrete shape:

1. **`AuthWriteCommand`** (`Services/AuthWriteCommand.cs`) — abstract
   discriminated record set:
   - `UpdateLastLoginAtCommand(TenantId, UserId, At)`
   - `ResetLockoutCountersCommand(TenantId, UserId)`
   - `LogSuccessEventCommand(TenantId, UserId, EventType, IpAddress, UserAgent)`

2. **`AuthWriteQueue`** (`Services/AuthWriteQueue.cs`) — a
   `BackgroundService` wrapping a
   `Channel<AuthWriteCommand>.CreateBounded(capacity=4096)` with
   `BoundedChannelFullMode.Wait` so that producer-side `TryWrite`
   returns `false` synchronously when the channel is saturated. The
   producer treats `false` as a drop (increments the
   `auth.write.dropped` counter + logs a warning) and never blocks the
   request thread on the queue. The consumer batches up to 64
   items, **coalesces user-mutating commands by `(tenantId, userId)`**
   so one DB upsert covers both the lockout reset and the `LastLoginAt`
   update for the same user, and processes `auth_events` inserts as
   independent rows. Emits four counters under the
   `Asterisk.Platform.Auth.WriteQueue` meter: `auth.write.{enqueued,
   dropped, processed, failed}` with a `type` dimension.

   *Why not `DropWrite`?* `DropWrite` mode silently discards the
   incoming item while still returning `true` from `TryWrite`, so the
   producer can't distinguish "accepted" from "dropped" — the meter
   would always read 0. `Wait` mode preserves the synchronous-only
   contract (no blocking) and surfaces saturation to the meter.

3. **`AuthEventService.EnqueueLogSuccess`** (new method) — enqueues a
   `LogSuccessEventCommand`; falls back to a fire-and-forget sync log if
   the queue is not registered (single-process tests). The original
   `LogAsync` stays synchronous and is the canonical entry point for
   `LoginFailure`, `Lockout`, and any other adversarial signals.

4. **`AccountLockoutService.ResetAttemptsAsync`** — updates the in-memory
   `User` snapshot synchronously so the rest of the request path
   (JWT issuance) sees post-reset state, then enqueues a
   `ResetLockoutCountersCommand`. New
   `EnqueueLastLoginAtUpdateAsync` follows the same pattern for the
   previously unpersisted `LastLoginAt` field.

5. **`IssueTokensAsync`** (`Endpoints/AuthEndpoints.cs`) — calls
   `lockoutService.ResetAttemptsAsync` and
   `lockoutService.EnqueueLastLoginAtUpdateAsync` instead of the old
   sync save, and `authEvents.EnqueueLogSuccess` instead of the old
   `await authEvents.LogAsync(...)`.

6. **DI**: `AuthWriteQueue` is registered as `Singleton` plus
   `HostedService` so it both can be injected into other services and
   runs the consumer loop on its own task.

## Failure-path invariant

The audit log MUST stay synchronous on the failure path. An attacker
fishing credentials (`LoginFailure`, `mfa_enrollment_required`,
`invalid_password`, `Lockout`) cannot be allowed to outpace the
audit log — if we enqueued failure events, an attacker driving 10×
the queue capacity could DoS the queue and silently slip subsequent
failures past the audit. Therefore:

- `AuthEventService.LogAsync` (the canonical sync method) is the ONLY
  path used by `Login` line 94 (invalid credentials),
  line 106 (invalid password), line 120 (mfa enrollment required),
  and `AccountLockoutService.RecordFailedAttemptAsync` (Lockout event).
- Only `LoginSuccess` rides the queue. There are no other event types
  with pre-existing success-event semantics that should join — the
  list is intentionally minimal.

This is enforced by:

- The `EnqueueLogSuccess` method name — anything not "success" obviously
  doesn't belong.
- A regression test
  (`AuthEndpointsTests.Login_ShouldLogFailureSynchronously_WhenCredentialsInvalid`)
  asserts the failure-path call site invokes `LogAsync` directly,
  not via the queue.

## Bounded-channel + producer-side drop policy

- **Capacity 4 096**: at the documented 75 req/s knee, this absorbs
  ~55 s of sustained backlog before saturating, an order of magnitude
  larger than any realistic flush-interval-induced jitter window.
- **Producer-side drop (`Wait` mode + non-blocking `TryWrite`)**: under
  sustained > 10× knee load the newest write fails with the drop
  counter incrementing. Acceptable
  because all three deferred operations are advisory:
  - `LastLoginAt` is a non-security-critical timestamp; missing one
    makes the user appear briefly idle in the admin UI.
  - `FailedLoginAttempts = 0` reset can race with a concurrent failed
    login that increments it; the deferred zero stomps the increment.
    Acceptable because the canonical lockout test runs fresh each time
    `AccountLockoutService.RecordFailedAttemptAsync` is called.
  - Missing a `LoginSuccess` event leaves a one-row gap in
    `auth_events` for the affected login. Forensics can still
    reconstruct from the JWT (signed, dated, identifies user + tenant).

The wire-level update on the `AuthWriteCommand` source comment + the
`AuthWriteQueueTests.TryEnqueue_ShouldReturnFalse_WhenChannelIsFull`
regression both mention the producer-side drop semantic.

## Batch flush + coalesce strategy

- **Inter-batch delay**: 250 ms between successive `WaitToReadAsync`
  cycles so multiple commands for the same user (e.g. a login that
  enqueues both `ResetLockoutCounters` and `UpdateLastLoginAt`) land
  in one batch.
- **Coalesce by `(tenantId, userId)`**: a single user appearing N times
  in a batch yields one `IUserStore.GetByIdAsync` + one `SaveAsync`,
  not N+N. With Phase 1's `CachedUserStore` decorator the read is a
  cache hit; the write goes to Postgres + invalidates the cache.
- **`auth_events` rows are independent**: each `LogSuccessEventCommand`
  produces its own row; no coalesce.

## Graceful-shutdown drain

`ExecuteAsync` consumes `WaitToReadAsync(stoppingToken)` until cancelled,
then drains any remaining items from the channel reader before exiting.
On `kubectl rollout` / SIGTERM this means in-flight enqueues at the time
of shutdown are flushed instead of silently dropped. The drain is
bounded by the channel's current contents — no new writes are allowed
once the consumer exits the main loop.

## Why not sync + a global write throttle?

Considered: keep all three writes sync but throttle the entire endpoint
under load. Rejected because (a) throttling masks the real bottleneck
without removing it, and (b) ASP.NET Core's RateLimiter middleware fires
**before** auth runs, which means a successful login still pays the
100 % cost. Phase 2 attacks the cost itself.

## Why not a per-replica AOT-friendly hot-loop?

Considered: skip `BackgroundService` and use a dedicated `Thread` with
`PriorityBoost`. Rejected because:

- ASP.NET Core's IHostedService model integrates with the host lifetime
  (graceful shutdown, restart, health checks) — re-implementing it is
  pure cost.
- `Channel<T>` + `BackgroundService` is the canonical .NET pattern,
  AOT-clean, and well-understood.

## Tested invariants

- `AuthWriteQueueTests`:
  - `TryEnqueue_ShouldReturnTrue_AndIncrementEnqueuedMeter_WhenChannelHasCapacity`
  - `TryEnqueue_ShouldReturnFalse_AndIncrementDroppedMeter_WhenChannelIsFull`
  - `Consumer_ShouldCoalesceUserMutations_WhenSameUserHasMultipleCommandsInBatch`
  - `Consumer_ShouldShutdownGracefully_WhenStopRequested`
  - `Consumer_ShouldDrainPendingItems_OnGracefulShutdown`
- `AuthEndpointsTests` (regression):
  - `Login_ShouldEnqueueLastLoginAt_WhenSuccessful` (success path uses queue)
  - `Login_ShouldLogFailureSynchronously_WhenCredentialsInvalid` (failure path stays sync)

## Considered alternatives

- **Always-sync, optimize Postgres upserts.** A single combined upsert
  (user fields + auth_event in one transaction) saves 1 round-trip but
  still pays 1 sync round-trip per login. Phase 2 saves all of them.
- **Polly retry on the deferred queue.** Rejected: retries amplify
  Postgres load under outage; a missed `LastLoginAt` is acceptable per
  §"Bounded-channel + DropWrite".
- **Queue per replica + Redis-backed shared queue.** Rejected: the
  three operations are tenant-and-user scoped; queuing them via Redis
  adds cross-replica coordination cost without benefit. The local
  in-process queue is the right granularity.
- **Source-generate the AuthWriteCommand JSON context for AOT.**
  Rejected as unnecessary: commands live in-memory only, never
  cross a serialization boundary.

## Future migration

- **Phase 4 (Argon2id)** adds a `PasswordRehashCommand` that rides the
  same queue. The user-coalesce path in `ProcessBatchAsync` extends
  naturally — the rehash is just another field on the
  `UserMutation` struct. ADR-0013 will cover the schema change.
- **Phase 3 (multi-replica)** does not change anything in the queue;
  each replica owns its own in-process consumer. Cross-replica DB
  contention on the same user is bounded by Postgres row-level
  locking on the upsert.
