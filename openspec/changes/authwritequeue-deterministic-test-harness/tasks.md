## 1. Foundation (Phase A — batch)

- [ ] 1.1 Re-confirm the in-CI fence census (5 Api.Tests + 18 PushToHubRelayTests + 6 RemoteEventDispatcherTests) and record the non-goal / allow-list sites (guards, simulated-work, Identity.Redis TTL, E2E settle, LoadTests loops, `RelayOutcomeSink` `Task.Yield`)
- [ ] 1.2 Add `Microsoft.Extensions.TimeProvider.Testing` to `Directory.Packages.props` (version aligned to the .NET 10 `Microsoft.Extensions.*` train) + a `<PackageReference>` in `tests/Verbara.Platform.Api.Tests/*.csproj`. **Checkpoint:** confirm a clean `dotnet build` of Api.Tests BEFORE any migration (Platform test projects set `EnableAotAnalyzer=false`/`NoWarn AD0001` — verify the package add does not regress that)
- [ ] 1.3 Confirm seams reach their test assemblies: `InternalsVisibleTo` Api.Tests (exists, csproj:44) and `InternalsVisibleTo` Realtime.Tests (verify for the `PushToHubRelay` quiescence seam; add if missing). No `MutableClock` and no `BackgroundServiceTestHarness` are built (recorded non-goals)

## 2. Clock / TTL seam migrations (Phase C — batch; all LOW)

- [ ] 2.1 `ServiceHeartbeat`: add additive `internal void RecordTickAt(string serviceName, TimeSpan interval, DateTimeOffset at)` (same tuple shape as `RecordTick`); migrate `BackgroundServiceHealthCheckTests:33,90` to stamp staleness via `RecordTickAt` (no clock injection, ctor + `Program.cs:1383` untouched)
- [ ] 2.2 `InMemoryJtiRevocationCache`: add `public InMemoryJtiRevocationCache(TimeProvider clock)` reading `clock.GetUtcNow()` at the 2 sites; keep `public InMemoryJtiRevocationCache() : this(TimeProvider.System)` so `Program.cs:647` DI is unchanged; migrate `InMemoryJtiRevocationCacheTests:24` to inject `FakeTimeProvider` and `Advance` past the TTL
- [ ] 2.3 `CachedTenantAuthConfigStoreTests:71` (test-only): add a `FakeTimeProvider`-backed `FakeSystemClock : Microsoft.Extensions.Internal.ISystemClock` helper; build the SUT's `MemoryCache` with `new MemoryCacheOptions { Clock = fake }`; advance past the 50 ms TTL and assert eviction (not just sleep removal)

## 3. Completion seam migrations (Phase B — individual focused subagents)

- [ ] 3.1 `WebhookDispatcher`: split `async void OnEvent` → `void OnEvent(e) => _lastDispatch = HandleEventAsync(e)` + `private async Task HandleEventAsync(...)` (whole-body `try/catch → LogDispatchError` retained); expose `internal Task? LastDispatch` + `internal Task WaitForDispatchAsync(CancellationToken)`. Migrate `WebhookDispatcherTests:39,57,78`; `:57` (negative `DidNotReceive`) gates on completion. Comment that the channel-drain harness is structurally invalid here
- [ ] 3.2 `RealtimeStateBridge`: `async void OnEvent` → `void OnEvent(e) => _ = HandleEventAsync(e)` + `internal Task HandleEventAsync(PlatformEvent)` (precedent `VoiceConversationBridge.cs:135`). Rewrite `PublishAndWaitAsync` (11 tests) to `await HandleEventAsync`; **keep one `Publish`/`OnNext` TCS wiring test** for Rx-path coverage
- [ ] 3.3 `PushToHubRelay` (**HOT PATH**): add a quiescence seam recording the `_ = SendXAsync(...)` in-flight Tasks (`:156/186/213`) and an `internal` awaitable (e.g. `WaitForQuiescenceAsync(ct)`); behaviour-preserving (production still fire-and-forgets). Migrate the 18 `PushToHubRelayTests` `Task.Delay` sites to await it. Route faults so recorded Tasks never fault unobserved
- [ ] 3.4 `RemoteEventDispatcherTests`: delete the 6 spurious `Task.Delay` sites outright (work completes synchronously via `GetAwaiter().GetResult()` at `RemoteEventDispatcher.cs:163`); confirm green
- [ ] 3.5 `WorkerResilienceTestHelpers.cs:29`: add a 1-line comment that `Task.WhenAny(executeTask, Task.Delay(timeout))` is a GUARD (fault-or-timeout race), not a sync fence — do NOT migrate

## 4. Verification

- [ ] 4.1 `dotnet test tests/Verbara.Platform.Api.Tests/ tests/Verbara.Platform.Realtime.Tests/ -v q` — all green, zero warnings (`TreatWarningsAsErrors`, `WarningLevel=9999`)
- [ ] 4.2 AOT publish smoke on the Api host (covers the Api/Identity seams: `WebhookDispatcher`, `RealtimeStateBridge`, `InMemoryJtiRevocationCache`) — 0 IL2026/IL3050/IL207x, native ELF
- [ ] 4.3 Run the in-CI suite (Api.Tests + Realtime.Tests) 10× under synthetic CPU contention — confirm zero flake across all migrated tests (esp. the 18 PushToHubRelay sites)
- [ ] 4.4 Confirm CI green (build + Unit Tests Release + Analyze + Coverage Ratchet + CodeQL); then `openspec archive` on merge
