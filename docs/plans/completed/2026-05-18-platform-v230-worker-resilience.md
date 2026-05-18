# Plan — Worker Resilience Pattern Hardening (Pro v2.4.1-pro + Platform v2.3.0)

## Context

El soak D-LK 24h en K8s (2026-05-17/18) expuso un bug arquitectónico: `BackgroundService` muere silenciosamente cuando una excepción escapa de `ExecuteAsync`, porque `BackgroundServiceExceptionBehavior.Ignore` (default) la traga. El host queda "Running" desde la perspectiva del orquestador; el worker está muerto. Manifestación concreta en D-LK: `QueueDistributionWorker` paró de heart-beat a T+16h36m → health "Unhealthy" 21h sin restart → 333 fails al final cuando K8s liveness probe falló 3× y mató el pod.

El spec canónico vive en [`Verbara.Platform/docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](../../media/Data/Source/Verbara/Verbara.Platform/docs/specs/2026-05-18-worker-resilience-pattern-hardening.md). Este plan ejecuta esa especificación.

**Audit cross-repo (ejecutado en este planning session):**

| Repo | Workers totales | Vulnerables | Pattern A (polling) | Pattern B (Rx/event) | Mixed/Other |
|---|---|---|---|---|---|
| Platform | 14 | **14** | 12 | 2 | 0 (AuthWriteQueue Channel-based — verify outer try-catch ya OK) |
| Pro | 12 | **11** | 5 | 5 | 2 mixed; RedisEventRelay try-finally pero sin rethrow |

**Total ~25 workers vulnerables** — significativamente más que los 8-12 estimados en el spec original. El usuario eligió **full hardening** (sin atajos) + ambos ADRs (Pro-0013 + Platform-0019).

**Versiones actuales (`Directory.Build.props`):**
- Platform: `2.2.0` → próxima `2.3.0`
- Pro: `2.4.0-pro` → próxima `2.4.1-pro`

**Release pathway** (per spec Decision): standalone patch trains, **Pro v2.4.1-pro ships primero**, luego Platform v2.3.0 consume. Reduce blast radius vs bundling, deja rollback path claro.

---

## Recommended Approach

Dos trains secuenciales, Pro primero, Platform consume después.

### Pattern fixes (del spec)

**Pattern A — polling worker** (timer/while-loop):
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    try  // ← OUTER
    {
        // existing setup + while-loop with INNER try-catch (sin cambios)
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* normal shutdown */ }
    catch (Exception fatalEx) { LogWorkerCrash(fatalEx); throw; }
}
```

**Pattern B — event-driven worker** (Rx Subscribe):
```csharp
protected override Task ExecuteAsync(CancellationToken stoppingToken)
{
    try
    {
        _subscription = source.Subscribe(
            onNext: x => HandleSafely(x, stoppingToken),
            onError: HandleSubscriptionFault,   // ← NUEVO: marca subscription null + log Critical
            onCompleted: HandleCompleted);
        stoppingToken.Register(() => _subscription?.Dispose());
        return Task.CompletedTask;
    }
    catch (Exception ex) { LogWorkerStartFailed(ex); throw; }
}

private void HandleSubscriptionFault(Exception ex)
{
    Log.SubscriptionError(_logger, ex.Message);
    Interlocked.Exchange(ref _subscription, null);  // health check ahora ve Unhealthy
}
```

**Host config en Platform.Api/Program.cs:**
```csharp
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
```

---

## Train 1 — Pro v2.4.1-pro (~15h ≈ 2 días maintainer)

**Working dir:** `/media/Data/Source/Verbara/Verbara.Sdk.Pro/`

### Phase P1 — Audit confirmation + WorkerLog scaffold (~1h)
- Verificar audit con `grep -rl 'class.*BackgroundService' src/ --include='*.cs'` (esperado 12 archivos)
- Crear `src/Verbara.Sdk.Pro.Core/Diagnostics/WorkerLog.cs` con `[LoggerMessage]` source-gen entries, `EventId = 18100-18199`:
  - `WorkerCrash` (Critical, 18100)
  - `WorkerCancellation` (Information, 18101)
  - `SubscriptionFault` (Critical, 18110)

### Phase P2 — Pattern A workers (5 archivos, ~3h)
| Archivo | Notas |
|---|---|
| `Verbara.Sdk.Pro.Dialer/DialerEngine.cs` | Inner try-catch ya existe; agregar outer |
| `Verbara.Sdk.Pro.Realtime/TrunkHealthChecker.cs` | Sin outer; agregar completo |
| `Verbara.Sdk.Pro.EventStore/RetentionService.cs` | Cron-scheduled PeriodicTimer |
| `Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceHeartbeatService.cs` | `while` + `Task.Delay`; fire-and-forget L147 (`_ = PublishAsync()`) — wrap también |
| `Verbara.Sdk.Pro.Realtime/RealtimeReconciler.cs` | Inner try-catch partial; agregar outer |

### Phase P3 — Pattern B workers (5 archivos, ~3h)
| Archivo | Notas |
|---|---|
| `Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs` | Fire-and-forget L109 (`_ = BroadcastAsync()`) → `HandleDeltaSafely` wrapper |
| `Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceMergeConsumer.cs` | Rx subscribe; agregar OnError + outer |
| `Verbara.Sdk.Pro.Analytics/LiveQueueSnapshotWriter.cs` | Fire-and-forget L193 (`_ = TrailingFlushAsync()`) |
| `Verbara.Sdk.Pro.CallAnalytics/CallAnalyticsEngine.cs` | Fire-and-forget L113 (`_ = HandleEventAsync()`) |
| `Verbara.Sdk.Pro.AgentAssist/AgentAssistEngine.cs` | Fire-and-forget L113 (`_ = HandleEventAsync()`) |

### Phase P4 — Mixed/special workers (2 archivos, ~1.5h)
| Archivo | Notas |
|---|---|
| `Verbara.Sdk.Pro.Realtime/AgentStateBridge.cs` | `Task.Run()` untracked L89 → wrap + log; outer try-catch |
| `Verbara.Sdk.Pro.Push.SignalR/.../RedisEventRelay.cs` | Tiene try-finally pero no rethrow; cambiar a outer try-catch con rethrow |

### Phase P5 — Tests (~4h)
- **Per-worker smoke test** (12 × 1 test): `WorkerCrash_TriggersHostFatal` con `ThrowingDependency` substitute. Shared fixture `WorkerResilienceFixture` en `tests/Verbara.Sdk.Pro.Tests.Shared/`.
- **Tier-1 deep tests** — 4 workers críticos × 4 tests cada:
  - `PresenceFanoutService` (alto tráfico SignalR)
  - `DialerEngine` (revenue path)
  - `RetentionService` (DB-heavy)
  - `RedisEventRelay` (cross-server)
- Tests per worker: `ExecuteAsync_OuterException_LogsCritical`, `ExecuteAsync_OperationCancelled_DoesNotRethrow`, `Subscribe_Throws_FaultsHealthCheck` (Pattern B only), `FireAndForget_SwallowsException_LogsWarn`.

### Phase P6 — ADR-0013 + CHANGELOG + version bump (~1.5h)
- **`docs/decisions/0013-stophost-on-worker-crash-house-style.md`** — ADR append-only:
  - Status: Accepted, Date: 2026-05-18, Deciders: Maintainer
  - Context: D-LK forensics + spec
  - Decision: Pattern A/B outer try-catch + rethrow; Verbara house-style usa `BackgroundServiceExceptionBehavior.StopHost` (consumed en Platform.Api)
  - Consequences (positive/negative/neutral)
  - Related: ADR-0012, Worker Resilience spec
- **`CHANGELOG.md`**: entry bajo `[2.4.1-pro] - 2026-05-XX`: "Hardened all 11 BackgroundService implementations with outer try-catch + LogWorkerCrash + rethrow; Pattern B workers now surface OnError to health checks via subscription nullification."
- **`Directory.Build.props`**: `2.4.0-pro` → `2.4.1-pro`
- Move plan → `docs/plans/completed/2026-05-18-pro-v241-worker-resilience.md` (mirror final)

### Phase P7 — Pack + smoke (~1h)
- `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/` (12 paquetes Pro)
- `cp /media/Data/Source/Verbara/local-nuget-feed/*2.4.1-pro* /media/Data/Source/Verbara/Verbara.Platform/local-nuget-feed/`
- `rm -rf ~/.nuget/packages/verbara.sdk.pro*/`
- En Platform: `dotnet restore && dotnet build` — verificar 0 warnings (back-compat con consumer code de 2.4.0-pro)

**Verification Pro:**
- [ ] `dotnet test Verbara.Sdk.Pro.slnx` — 1,329+ tests pasan, 0 warnings
- [ ] Audit grep — 0 matches de `BackgroundService` subclass sin outer try-catch
- [ ] CHANGELOG diff visible vs 2.4.0-pro

---

## Train 2 — Platform v2.3.0 (~18h ≈ 2.5 días maintainer)

**Working dir:** `/media/Data/Source/Verbara/Verbara.Platform/`

### Phase L1 — Consume Pro v2.4.1-pro (~30min)
- `Directory.Packages.props`: bump 12 `Verbara.Sdk.Pro.*` PackageVersions de `2.4.0-pro` → `2.4.1-pro`
- `dotnet restore && dotnet build` — verificar 0 warnings
- `dotnet test tests/Verbara.Platform.Api.Tests/` — 1,180 tests pasan con nuevo Pro

### Phase L2 — Program.cs HostOptions + WorkerLog scaffold (~1h)
- En `src/Verbara.Platform.Api/Program.cs` (después de la sección de configuración de hosting):
  ```csharp
  builder.Services.Configure<HostOptions>(options =>
  {
      options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
  });
  ```
- Crear `src/Verbara.Platform.Api/Services/WorkerLog.cs` con LoggerMessage source-gen entries, `EventId = 18000-18099`. Mismo patrón que Pro, range Platform-reservado.

### Phase L3 — Pattern A workers (12 archivos, ~6h)
| Archivo | Notas |
|---|---|
| `Services/QueueDistributionWorker.cs` | **Confirmed soak failure** — el que murió en D-LK. Tiene `_heartbeat.RecordTick` |
| `Services/ConversationTimeoutWorker.cs` | Tiene heartbeat |
| `Services/CampaignMetricsPoller.cs` | Sin heartbeat |
| `Services/WebhookDeliveryService.cs` | Pattern A+ dual loop (Channel + Task.Delay) — wrap ambos |
| `Services/RetentionPurgeService.cs` | DB-heavy |
| `Services/AuditRetentionService.cs` | Retention sweep |
| `Services/ImpersonationSessionTimeoutService.cs` | Timeout sweep |
| `Services/ReportSchedulerService.cs` | Fire-and-forget L110 — wrap también |
| `Automation/TimerPollingService.cs` | Cross-package; vive en Automation |
| `Mail/Services/TokenRefreshService.cs` | OAuth refresh loop |
| `Billing/DunningService.cs` | Internal catches presentes pero outer falta |

(audit confirmará si hay un 12vo no listado explícitamente)

### Phase L4 — Pattern B workers (2 archivos, ~1.5h)
| Archivo | Notas |
|---|---|
| `Services/BotAnalyticsPersistenceService.cs` | Rx subscribe + fire-and-forget L36-39 |
| `Services/VerbaraCapacitySyncService.cs` | Rx subscribe + fire-and-forget en `HandleCapacityChangedAsync` |

### Phase L5 — AuthWriteQueue verification (~30min)
- Audit reportó outer try-catch L123-164. Verificar que sí rethrow al final; si no, agregar.

### Phase L6 — Tests (~5h)
- **Per-worker smoke tests** (14 × 1 test cada) — patrón compartido con Pro
- **Tier-1 deep tests** — 4 workers críticos × 5 tests cada:
  - `QueueDistributionWorker` (murió en soak)
  - `WebhookDeliveryService` (revenue-impacting)
  - `BotAnalyticsPersistenceService` (Pattern B demonstrates fix)
  - `ConversationTimeoutWorker` (customer-impacting)
- **Integration test wiring** — `HostOptions_BackgroundServiceExceptionBehavior_IsStopHost`:
  ```csharp
  using var host = Program.CreateHostBuilder(...).Build();
  var options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
  Assert.Equal(BackgroundServiceExceptionBehavior.StopHost, options.BackgroundServiceExceptionBehavior);
  ```
- **Integration test E2E** — `WorkerFatalException_TriggersHostStop` con `ThrowingHeartbeat` + 5s timeout (per spec)

### Phase L7 — ADR-0019 + CHANGELOG + version bump (~1.5h)
- **`docs/decisions/0019-stophost-on-worker-crash-house-style.md`**:
  - Status: Accepted, Date: 2026-05-18
  - Context: D-LK, spec, paired con Pro ADR-0013
  - Decision: Platform.Api wires `HostOptions.BackgroundServiceExceptionBehavior = StopHost`; todos workers Platform hardened
  - References: spec, ADR-0013 (Pro counterpart)
- **`CHANGELOG.md`**: entry bajo `[2.3.0] - 2026-05-XX`
- **`Directory.Build.props`**: `2.2.0` → `2.3.0`

### Phase L8 — Verification + ship (~2h)
- `dotnet build` 0 warnings
- `dotnet test tests/Verbara.Platform.Api.Tests/` — 1,180+ tests + nuevos resilience tests green
- Audit grep cross-repo
- Docker image build smoke: `docker build -f docker/Dockerfile.production -t verbara-platform:2.3.0-test .`
- Move plan → `docs/plans/completed/2026-05-18-platform-v230-worker-resilience.md`
- Commit: `feat(workers): outer try-catch hardening across 14 BackgroundService impls (ADR-0019) — v2.3.0`
- Tag: `v2.3.0`

---

## Critical files to reference durante implementación

**Pro repo:**
- `src/Verbara.Sdk.Pro.Push.SignalR/Presence/PresenceFanoutService.cs:100` (Pattern B canonical reference)
- `src/Verbara.Sdk.Pro.Dialer/DialerEngine.cs` (Pattern A largest worker)
- Existing `[LoggerMessage]` style: cualquier `*.Log.cs` en Pro packages
- `Directory.Build.props` (PackageVersion bump)
- `CHANGELOG.md` (Keep-a-Changelog format)
- `docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md` (ADR template)

**Platform repo:**
- `src/Verbara.Platform.Api/Services/QueueDistributionWorker.cs:56-91` (Pattern A canonical — el que murió en soak)
- `src/Verbara.Platform.Api/Services/AuthWriteQueue.cs:38,123-164` (verificar outer try-catch + rethrow)
- `src/Verbara.Platform.Api/Program.cs` (HostOptions wiring)
- `docs/specs/2026-05-18-worker-resilience-pattern-hardening.md` (spec canónico)
- `docs/decisions/0018-*.md` (ADR template; siguiente es 0019)
- `Directory.Packages.props` (Pro version bump)

---

## Subagent execution strategy

Per CLAUDE.md "Always use Subagent-Driven Development with FCM batching":

**Phase P2 + P3 + P4 (Pro workers):** **2 subagents en paralelo** — uno con Pattern A workers (5 archivos), otro con Pattern B + mixed (7 archivos). Cada subagent recibe Pattern template + LoggerMessage entries y aplica mechanically.

**Phase L3 + L4 (Platform workers):** similar — **2 subagents en paralelo** (Pattern A workers, Pattern B workers).

**Phase P5 + L6 (tests):** **subagent dedicado** por tier (Tier-1 deep tests vs Tier-2 smoke tests). Tests son mechanical después del primer worker fijar el patrón.

**Phases ADR + CHANGELOG + verification:** maintainer manual (no batch — review trade-offs antes de tag).

---

## Out of scope

- No nuevos workers.
- No nuevos features.
- No metric/heartbeat instrumentation (algunos workers no tienen heartbeat; queda for futuro train).
- No deep refactor — lógica de negocio idéntica.
- No Polly chaos injection en tests (deferred per spec).
- No D-LK soak repeat acá — separado, post-ship + post-staging deploy.

---

## Verification (post-train)

1. **Pro test suite**: 1,329+ tests pasan, 0 warnings, AOT publish clean.
2. **Platform test suite**: 1,180+ tests pasan, 0 warnings, AOT publish clean.
3. **Grep audit**: ningún `BackgroundService` subclass sin outer try-catch + rethrow cross-repo.
4. **HostOptions wiring**: integration test confirma `StopHost` en Program.cs.
5. **Docker image build**: `docker build` exitoso en compose files (full + production).
6. **D-LK repeat (deferred post-train)**: 24h soak en K8s con build hardened debería surface ANY worker death como pod restart visible, no 21h stale heartbeat silencioso.

---

## Summary del output esperado

**Train 1 — Pro v2.4.1-pro:**
- 12 workers modificados (5 Pattern A + 5 Pattern B + 2 mixed)
- 1 `WorkerLog.cs` nuevo (EventId 18100-18199)
- 1 ADR (0013)
- ~30 tests nuevos
- CHANGELOG + version bump
- 12 nupkgs packed
- ~15h effort

**Train 2 — Platform v2.3.0:**
- 14 workers modificados (12 Pattern A + 2 Pattern B)
- 1 `WorkerLog.cs` nuevo (EventId 18000-18099)
- Program.cs HostOptions wiring
- 1 ADR (0019)
- ~35 tests nuevos (incl. integration test)
- CHANGELOG + version bump
- ~18h effort

**Cross-repo total: ~33h ≈ 4 días maintainer continuos.**

Calendar: arrancando hoy 2026-05-18, Pro v2.4.1-pro tag estimado 2026-05-19/20, Platform v2.3.0 tag estimado 2026-05-21/22.
