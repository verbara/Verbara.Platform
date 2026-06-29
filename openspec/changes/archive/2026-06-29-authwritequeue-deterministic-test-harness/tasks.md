## 1. Foundation (Phase A — batch)

- [x] 1.1 Re-confirm the in-CI fence census (5 Api.Tests + 18 PushToHubRelayTests + 6 RemoteEventDispatcherTests) and record the non-goal / allow-list sites (guards, simulated-work, Identity.Redis TTL, E2E settle, LoadTests loops, `RelayOutcomeSink` `Task.Yield`)
- [x] 1.2 Add `Microsoft.Extensions.TimeProvider.Testing` to `Directory.Packages.props` (10.0.0, aligned with Sdk.Pro) + a `<PackageReference>` in `tests/Verbara.Platform.Api.Tests/*.csproj`. **Checkpoint passed:** clean `dotnet build` of Api.Tests (0 warnings) BEFORE any migration
- [x] 1.3 Confirm seams reach their test assemblies: `InternalsVisibleTo` Api.Tests (exists) and `InternalsVisibleTo` Realtime.Tests (exists, Realtime.csproj:16). No `MutableClock` and no `BackgroundServiceTestHarness` built (recorded non-goals)

## 2. Clock / TTL seam migrations (Phase C — batch; all LOW)

- [x] 2.1 `ServiceHeartbeat`: added additive `internal void RecordTickAt(string serviceName, TimeSpan expectedInterval, DateTimeOffset at)` (same tuple shape); migrated `BackgroundServiceHealthCheckTests` stale-path sites to stamp staleness via `RecordTickAt` (no clock injection, ctor + `Program.cs:1383` untouched)
- [x] 2.2 `InMemoryJtiRevocationCache`: added `public InMemoryJtiRevocationCache(TimeProvider clock)` reading `clock.GetUtcNow()`; kept `public InMemoryJtiRevocationCache() : this(TimeProvider.System)` so `Program.cs:647` DI is unchanged; migrated test to inject `FakeTimeProvider` and `Advance` past the TTL
- [x] 2.3 `CachedTenantAuthConfigStoreTests` (test-only): `FakeTimeProvider`-backed `ISystemClock` adapter via `new MemoryCacheOptions { Clock = ... }`; advance past the 50 ms TTL and assert eviction (`inner.Received(2)`). Verified `ISystemClock` is NOT `[Obsolete]` in Caching.Memory 10.0.8 → no pragma needed

## 3. Completion seam migrations (Phase B — individual focused subagents)

- [x] 3.1 `WebhookDispatcher`: split `async void OnEvent` → `void OnEvent(e) => _lastDispatch = HandleEventAsync(e)` + `private async Task HandleEventAsync(...)` (whole-body `try/catch → LogDispatchError` retained); exposed `internal Task WaitForDispatchAsync(CancellationToken)`. Migrated the 3 sites; `:57` (negative `DidNotReceive`) gates on completion, with a comment that the channel-drain harness is structurally invalid there
- [x] 3.2 `RealtimeStateBridge`: `async void OnEvent` → `void OnEvent(e) => _ = HandleEventAsync(e)` + `internal Task HandleEventAsync(PlatformEvent)` (precedent `VoiceConversationBridge`). Rewrote `PublishAndWaitAsync` (11 tests) to `await HandleEventAsync`; kept one `Publish`/`OnNext` TCS wiring test for Rx-path coverage
- [x] 3.3 `PushToHubRelay` (**HOT PATH**): added a send-completion seam — record the most-recently-started send in `_lastSend` on all 5 `Forward*` handlers (3 Pro-typed + 2 Core-typed) and expose `internal WaitForDispatchAsync(ct)`; behaviour-preserving (single field, no list/lock; production still fire-and-forgets). Migrated the 18 `PushToHubRelayTests` sites (multi-emit test awaits after each emit)
- [x] 3.4 `RemoteEventDispatcherTests`: deleted the 6 spurious `Task.Delay` sites outright (work completes synchronously via `GetAwaiter().GetResult()` at `RemoteEventDispatcher.cs:163`); green
- [x] 3.5 `WorkerResilienceTestHelpers.cs:29`: added a 1-line comment that `Task.WhenAny(executeTask, Task.Delay(timeout))` is a GUARD (fault-or-timeout race), not a sync fence — do NOT migrate

## 4. Verification

- [x] 4.1 Full `Api.Tests` (1474/1474) + `Realtime.Tests` (50/50) green, 0 warnings; full solution build 0 warnings (`TreatWarningsAsErrors`, `WarningLevel=9999`)
- [x] 4.2 AOT publish smoke on the Api host — native ELF, 0 managed Verbara DLLs, 0 IL2026/IL3050/IL207x (covers the Api/Identity seams)
- [x] 4.3 In-CI suite (Api.Tests + Realtime.Tests migrated classes) run 10× under 24-core CPU contention — zero flake across all migrated tests
- [x] 4.4 CI green on PR #102 (Build + Unit Tests Release + Analyze (C#) + Dependency Review + Coverage Ratchet + CodeQL all SUCCESS); merged to main as `b02b84b7` (#102); archived via this `docs(openspec)` PR
