# Verbara.Platform.E2E.Harness — Framework reusable E2E para SignalR/Realtime + observabilidad de producción

> **Estado:** Plan inicial — pendiente de aprobación con ExitPlanMode.
> **Predecesor:** [Plan B Talos smoke test 2026-05-24](file:///media/Data/Source/Verbara/Verbara.Platform/docs/operations/phase-a5-talos-smoke-test-2026-05-24.md) cerró 5/6 PASS + 1/6 PARTIAL. Test 5 SignalR exactly-once quedó deferido por falta de tráfico real de clientes.
> **Sucesor inmediato gateado:** R5.5 K8s Phase B-LK / C-LK / D-LK / E-LK.

---

## Context

El Plan B smoke (2026-05-24) validó que el código de leader-gating del Phase A.5 está cableado y que los pods convergen a un único líder bajo cold-start, rollout, y kill ungraceful. Pero **Test 5 (exactly-once delivery vía SignalR) quedó PARTIAL** porque el lab no tiene tráfico real de clientes — solo se pudo inferir la corrección del filtro por la unicidad del líder, no observar directamente que (a) el follower hace short-circuit del forward path y (b) el leader entrega exactamente una vez al backplane.

Esto expone tres gaps acoplados:

1. **Test 5 nunca podrá pasar a PASS** sin clientes SignalR conectados emitiendo y recibiendo eventos.
2. **El `PushToHubRelay` no tiene métricas Prometheus** (comentario explícito en `src/Verbara.Platform.Realtime/Program.cs:183-186`). El único registro de comportamiento son logs Trace con `EventId=3001` — frágil para assertions, invisible para alerting en producción.
3. **R5.5 K8s tiene 8+ tests futuros pendientes** (B-LK baseline, C-LK chaos, D-LK 24h soak, E-LK synthetic, security/DoS suite, multi-leader independence cuando Pro registre más resources, multi-pod fanout scale). Sin un framework reusable, cada uno se construirá ad-hoc con duplicación de auth, topology wiring, assertion plumbing.

La decisión correcta — alineada con un CCaaS tier-1 y con "esta imagen siempre debe ser AOT" + "producto final sin atajos" — es **construir un framework E2E reusable + cerrar deuda de observabilidad del relay** en un solo plan coordinado. El framework es el contrato físico entre los tests de hoy y los de los próximos 6-12 meses. La instrumentación del relay es valor que se cobra dos veces: vuelve las assertions determinísticas y vuelve el comportamiento de fanout observable en producción.

**Decisión de scope confirmada por usuario:** framework completo (8 tests futuros), eventos reales vía Api (no atajos redis-cli ni endpoints admin one-shot). Decisión de topología confirmada: `.NET Aspire orchestrator` (dev loop) + `Talos lab físico` (pre-release). Decisiones restantes (assertions / triggers / arquitectura framework) cerradas por análisis arquitectónico profundo (Plan agent, este turno).

---

## Decisión arquitectónica unificada

| Dimensión | Decisión | Justificación clave |
|---|---|---|
| **Fuente de truth** | **Híbrida: audit endpoint in-memory + Prometheus counters** (Fase 1 endpoint, Fase 2 counters; mismo `RecordOutcome()` alimenta ambos) | Determinístico, AOT-friendly, cierra gap de observabilidad de prod reconocido en `Program.cs:183-186`. Loki queda como fallback forense, no source-of-truth. eBPF/Tetragon descartado (no decodifica WSS/RESP3 sin terminar TLS) |
| **Topología** | **Aspire (dev loop) + Talos (pre-release)** — mismo binario del harness con `--topology aspire\|talos` | Cubre dos casos críticos: feedback < 30s para el dev (Aspire) + validación cercana a prod (Talos). Sin docker-compose intermedio ni k3d efímero, evitando redundancia |
| **Disparador** | **Cascada de 3 capas:** L1 manual Aspire (dev), L2 PR-gate Aspire en GH Actions ephemeral runner, L3 release-gate Talos con self-hosted runner downstream de `release.yml` | Bloquea releases malísimos sin penalizar el dev loop. L3 ejerce el cluster real para cazar regresiones que no salen en Aspire. Canary continuo difer ido a R6+ |
| **Framework** | **Spectre.Console.Cli + source-generated scenario registry + NBomber integrado como uno de varios `IScenarioRunner`** | Source-gen evita reflection (escalable a 50+ tests). Spectre da CLI discoverable. NBomber se reusa para steady-state (fanout scale, soak) sin reinventar el wheel. Harness es NO-AOT (ADR-0023 lo permite), justificado porque SignalR.Client requiere reflection |
| **Contrato producto↔harness** | `GET /admin/realtime/audit?since=<ts>&limit=<n>` — auth `PlatformAdmin`, devuelve ring buffer `{ts, eventType, tenantId, resource, leaderHeldAtTs, outcome, podId}` | Es el único punto de acoplamiento. Tests independientes de topología y trigger; misma assertion corre en Aspire y Talos |

---

## Fases de implementación

### Fase 1 — Instrumentación del relay + audit endpoint (Realtime side)

**Critical files:**
- `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs` — añadir `IRelayOutcomeSink` inyectado, llamar `_sink.RecordOutcome(...)` en cada Forward/Skip/Error path (líneas ~100, ~180, ~184)
- `src/Verbara.Platform.Realtime/Services/RelayOutcomeRingBuffer.cs` — **NUEVO**, thread-safe ring buffer (10k entries default, configurable), `IReadOnlyList<RelayOutcome>` snapshot
- `src/Verbara.Platform.Realtime/Services/RelayOutcomeSink.cs` — **NUEVO**, fan-out a ring buffer + (Fase 1.5) Prometheus meter
- `src/Verbara.Platform.Realtime.Contracts/Dtos/RelayOutcomeDto.cs` — **NUEVO**, record `{Ts, EventType, TenantId, Resource, LeaderHeldAtTs, Outcome, PodId}` + `RealtimeContractsJsonContext.cs` source-gen entry
- `src/Verbara.Platform.Realtime/Endpoints/AdminRealtimeAuditEndpoint.cs` — **NUEVO**, `MapGet("/admin/realtime/audit")` con `[Authorize(Policy="PlatformAdmin")]`, query params `since` (ISO8601) + `limit` (default 1000, max 10000)
- `src/Verbara.Platform.Realtime/Program.cs` — registrar el sink/ringbuffer en DI, mapear endpoint, actualizar comentario líneas 183-186
- `tests/Verbara.Platform.Realtime.Tests/Services/PushToHubRelayTests.cs` — ampliar con assertions sobre `IRelayOutcomeSink.RecordOutcome` (NSubstitute)
- `tests/Verbara.Platform.Realtime.Tests/Services/RelayOutcomeRingBufferTests.cs` — **NUEVO**, tests de concurrencia (paralelo writes + snapshot reads)

**Fase 1.5 — Counters Prometheus** (mismo `RecordOutcome()`, extiende sink):
- Métricas: `verbara_realtime_relay_forwards_total{outcome,event_type,resource}`, `verbara_realtime_leader_transitions_total{resource}`, `verbara_realtime_hub_clients_active` (gauge)
- Reuso `System.Diagnostics.Metrics.Meter` (AOT-compatible, no requiere `Microsoft.AspNetCore.OpenApi.Generators`)
- Endpoint `/metrics` ya expuesto vía `prometheus-net.AspNetCore` si está, validar antes de añadir paquete

### Fase 2 — Aspire AppHost (dev loop)

**Critical files:**
- `tests/Verbara.Platform.AppHost/Verbara.Platform.AppHost.csproj` — **NUEVO**, `<Sdk>Aspire.Hosting.AppHost</Sdk>`, .NET 10
- `tests/Verbara.Platform.AppHost/Program.cs` — orquesta:
  - `redis = builder.AddRedis("redis")`
  - `postgres = builder.AddPostgres("postgres").AddDatabase("verbara")`
  - `api = builder.AddProject<Projects.Verbara_Platform_Api>("api").WithReference(postgres).WithReference(redis)`
  - `realtime = builder.AddProject<Projects.Verbara_Platform_Realtime>("realtime").WithReference(postgres).WithReference(redis).WithReplicas(2)`
- `tests/Verbara.Platform.AppHost/appsettings.json` — JWT signing key dev, tenant seed
- **NO afecta** al `Verbara.Platform.slnx` solution principal (proyecto opt-in, agregado a `Verbara.Platform.slnx` con flag de exclusión para AOT publish)

### Fase 3 — Harness Console + 8 escenarios

**Critical files:**
- `tests/Verbara.Platform.E2E.Harness/Verbara.Platform.E2E.Harness.csproj` — **NUEVO**, console, `<IsAotCompatible>false</IsAotCompatible>`, refs: `Microsoft.AspNetCore.SignalR.Client` 9.x, `Spectre.Console.Cli` 0.50.x, `NBomber` 6.x (reuso), `KubernetesClient` 14.x para Talos disruptor
- `tests/Verbara.Platform.E2E.Harness/Program.cs` — Spectre app con comandos `run <scenario>`, `run-suite <suite>`, `list`, `compare`, `bench`
- `tests/Verbara.Platform.E2E.Harness/Abstractions/IScenario.cs` — **NUEVO**, `string Name; Task<ScenarioResult> RunAsync(HarnessContext ctx, CancellationToken ct)`
- `tests/Verbara.Platform.E2E.Harness/Abstractions/HarnessScenarioAttribute.cs` — `[HarnessScenario("exactly-once")]` para discovery
- `tests/Verbara.Platform.E2E.Harness/Generated/ScenarioRegistry.g.cs` — generado por source generator, `FrozenDictionary<string, Func<IScenario>>`
- `tests/Verbara.Platform.E2E.Harness.SourceGen/` — **NUEVO** project, Roslyn source generator que escanea `[HarnessScenario]` y emite el registry
- `tests/Verbara.Platform.E2E.Harness/Topology/ITopologyProvider.cs` + `AspireTopologyProvider.cs` + `TalosTopologyProvider.cs` (lee KUBECONFIG, port-forward, scrape pods via K8s API)
- `tests/Verbara.Platform.E2E.Harness/Assertions/IAssertionSource.cs` + `AuditEndpointAssertionSource.cs` (única implementación inicial)
- `tests/Verbara.Platform.E2E.Harness/Disruption/IDisruptor.cs` + `AspireDisruptor.cs` + `KubectlDisruptor.cs`
- `tests/Verbara.Platform.E2E.Harness/Auth/HarnessAuthClient.cs` — copy adaptado de `tests/Verbara.Platform.LoadTests/TokenHolder.cs` + login flow
- `tests/Verbara.Platform.E2E.Harness/Scenarios/ExactlyOnceScenario.cs` — **el walking skeleton**
- 7 escenarios adicionales (uno por archivo): `LeaderFailoverScenario`, `MultiPodFanoutScaleScenario`, `MultiLeaderIndependenceScenario` (placeholder, espera Pro registre más resources), `SecurityRateLimitBurstScenario`, `SecurityJwtAbuseScenario`, `SecuritySlowlorisScenario`, `ChaosPodKillScenario`
- `tests/Verbara.Platform.E2E.Harness/Reports/HarnessReportWriter.cs` — JSON + Markdown emit a `harness/reports/<timestamp>/`
- `tests/Verbara.Platform.E2E.Harness/Reports/HarnessReportJsonContext.cs` — source-gen JSON
- `tests/Verbara.Platform.E2E.Harness/baselines/exactly-once-talos.json` — baseline checked-in para regression detection

**Reuso explícito:** `NBomber` ya está en `Verbara.Platform.LoadTests`. Los scenarios de carga sostenida (`MultiPodFanoutScaleScenario`, futuro `SoakScenario`) usan `NBomberScenarioRunner` que envuelve NBomber. Los scenarios de correctness (exactly-once, leader-failover) corren loops propios sin NBomber.

### Fase 4 — CI cascade (release.yml + nuevo workflow)

**Critical files:**
- `.github/workflows/e2e-harness-pr.yml` — **NUEVO**, trigger: PRs que tocan `src/Verbara.Platform.Realtime/**` o paths Pro `Cluster|Push`. Ephemeral runner, levanta Aspire AppHost, corre suite `pr-fast` (exactly-once + leader-failover). Tiempo objetivo < 3 min
- `.github/workflows/release.yml` — modificar para añadir job `e2e-talos` después del job `release` (needs: release), `runs-on: [self-hosted, verbara-talos-lab]`, corre suite `release-full` (8 scenarios). Bloquea creación del GH Release si falla
- `scripts/setup-talos-runner.sh` — **NUEVO**, bootstrap del self-hosted runner en el lab (sudo, systemd unit, gh runner config)
- `docs/operations/runbook-self-hosted-runner.md` — **NUEVO**, runbook (cómo upgrade, cómo rotar credenciales, cómo debuggear)

### Fase 5 — Re-run Plan B Test 5 + cerrar PARTIAL→PASS

**Critical files:**
- `docs/operations/phase-a5-talos-smoke-test-2026-05-24.md` — agregar appendix con resultado Test 5 PASS via harness, vincular reporte
- `docs/operations/phase-a5-test5-harness-result.md` — **NUEVO**, evidencia detallada del run (audit endpoint output, leader pod, follower skip counts, client receive counts)

---

## Funciones / utilities existentes a reusar

| Origen | Reuso en |
|---|---|
| `tests/Verbara.Platform.LoadTests/TokenHolder.cs:1-18` | `tests/Verbara.Platform.E2E.Harness/Auth/HarnessAuthClient.cs` |
| `tests/Verbara.Platform.LoadTests/LoadTestHttpClient.cs:36-87` | `HarnessHttpClient.cs` — adaptar para `LOADTEST_RESOLVE` + Talos gateway |
| `tests/Verbara.Platform.LoadTests/Scenarios/PresenceScenario.cs:51-79` | `HarnessAuthClient` token-refresh loop cada 12 min |
| `src/Verbara.Platform.Realtime.Contracts/Json/RealtimeContractsJsonContext.cs` | Añadir entry para `RelayOutcomeDto` + DTOs del endpoint |
| `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs:100,180,184` | Puntos de inyección de `_sink.RecordOutcome()` |
| `tests/Verbara.Platform.Realtime.Tests/Services/PushToHubRelayTests.cs:96-108` | Patrón mocks de `IPushEventBus` + `IHubContext` para tests del sink |
| `Verbara.Sdk.Pro.Cluster.Leadership.RealtimeLeaderResources.Fanout` | Constante usada en `LeaderHeldAtTs` snapshot |
| `Pro.Cluster IClusterLeader` keyed services | Para snapshot del leader state al recordar outcome |
| `Microsoft.Extensions.Logging.ILogger` source-gen actual del relay | Mantener intacto, sink es complementario |

---

## Verification

### Pre-merge (Fase 1)
- `dotnet build Verbara.Platform.slnx` — 0 warnings 0 errors (mantener TreatWarningsAsErrors)
- `dotnet test tests/Verbara.Platform.Realtime.Tests/` — todos verdes + nuevos tests del sink + ring buffer pasan
- `dotnet publish src/Verbara.Platform.Realtime/Verbara.Platform.Realtime.csproj -c Release` — válido (Realtime ya es no-AOT por ADR-0023, pero validar que ring buffer + endpoint no rompen build)
- `dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -p:PublishAot=true` — 0 IL2*/IL3* warnings (verificar que Contracts compartido no rompe AOT)

### Smoke local (Fase 2 + Fase 3)
- `cd tests/Verbara.Platform.AppHost && dotnet run` — Aspire dashboard sube en `http://localhost:15000`, ver Realtime×2 + Api + Redis + Postgres Healthy
- `dotnet run --project tests/Verbara.Platform.E2E.Harness -- list` — lista 8 scenarios, output Spectre formatted
- `dotnet run --project tests/Verbara.Platform.E2E.Harness -- run exactly-once --topology aspire` — output:
  - 5 clientes SignalR conectados a `wss://localhost:<aspire-realtime-port>/hubs/platform?access_token=<jwt>`
  - Harness dispara 10 cambios de estado vía `POST /api/v1/conversations/{id}/state`
  - Cada cliente recibe exactamente 10 `OnConversationStateChanged`
  - Audit endpoint: exactamente 10 entries `outcome=Forwarded` en 1 pod, 10 entries `outcome=Skipped` en otro pod
  - Exit code 0, report JSON + MD en `harness/reports/<timestamp>/`

### Smoke contra Talos lab (Fase 3)
- `dotnet run --project tests/Verbara.Platform.E2E.Harness -- run exactly-once --topology talos` — mismo resultado pero contra los 4 pods reales
- Validar que `KubectlDisruptor` puede listar pods, port-forward, kubectl delete pod
- `dotnet run --project tests/Verbara.Platform.E2E.Harness -- run-suite release-full --topology talos` — 8 scenarios PASS en ~15 min

### CI cascade (Fase 4)
- Abrir PR ficticio que toca `src/Verbara.Platform.Realtime/Program.cs` — `e2e-harness-pr.yml` se gatilla, corre Aspire suite, devuelve verde en < 3 min
- Crear tag `v2.4.4-rc1` (RETROACTIVE-TAG no aplica) — `release.yml` corre release job + downstream `e2e-talos` contra el lab; bloquea GH Release si falla

### Closure (Fase 5)
- Re-correr Plan B Test 5 con el harness — appendix en smoke report 2026-05-24 marca **PASS**
- Update tabla del smoke report: `5 | SignalR exactly-once delivery via Gateway | exactly 1 receive per client per event, 5/5 trials | ✅ PASS — see harness report` (en vez de PARTIAL)

---

## Trade-offs aceptados explícitamente

1. **Harness es NO-AOT.** Justificado por ADR-0023 (microservicios no-AOT permitidos) + `Microsoft.AspNetCore.SignalR.Client` requiere reflection. El producto (Api + Realtime) sigue AOT-clean.
2. **Self-hosted GH Actions runner** introduce mantenimiento (bootstrap script + actualización del runner). A cambio: cero exposición de KUBECONFIG fuera del lab + latencias de chaos manejables.
3. **PR-gate no ejerce Talos real** — riesgo "passes-on-Aspire, fails-on-Talos". Mitigado por L3 release-gate que sí ejerce Talos antes del GH Release publicado.
4. **Diferimos canary continuo en prod / OTel traces por envelope / multi-leader real (placeholder hasta Pro registre más resources) / ArgoCD PostSync / OpenObserve.** Reabrir si v1 muestra dolor concreto.
5. **NBomber sigue en `Verbara.Platform.LoadTests`** (no se migra). El harness lo invoca como `IScenarioRunner` para escenarios steady-state. Preserva los reports Markdown/CSV/HTML existentes.

---

## Live execution log + v2.4.8 amendment (2026-05-24)

### What shipped this session (8 PRs, tags v2.4.4 → v2.4.7)

Phases 1 + 3 of this plan landed end-to-end against the Talos lab. Detailed escalation chain documented in the appendix of [the smoke report](../../operations/phase-a5-talos-smoke-test-2026-05-24.md#appendix--test-5-escalation-chain-2026-05-24-post-closure-session).

| Phase | Deliverable | PR | Outcome |
|---|---|---|---|
| 1 | Audit endpoint + sink + counter | #18 | Shipped — verified live 200 OK with `RelayOutcomePage` JSON after the v2.4.5-7 chain |
| 3 walking-skeleton | Console harness + `ExactlyOnceScenario` + Talos wrapper | #19 | Shipped — ran end-to-end against 4 v2.4.7 Realtime pods |
| 5 chart hardening side-effects | 4 chart/code fixes surfaced by harness | #21, #22, #23, #24, #25 | All shipped; lab on v2.4.7 rev 23 |

### What Test 5 PARTIAL still blocks on — v2.5.0 (REVISED, was "v2.4.8 Option B")

**⚠️ This subsection's first version (committed 2026-05-24 with PR #26) hypothesised a "Pro.Push.Redis backplane channel topic naming mismatch" + proposed a ~100 LOC Option B `PlatformToProEventBridge` in the API. That diagnosis was WRONG.** Reading the SDK Pro.Push.Redis source ([`RedisEventRelay.cs`](file:///media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Push/Backplane/RedisEventRelay.cs)) the same evening reveals the actual architecture:

- **Backplane channels are `asterisk:push:{tenantId}`** (one per tenant — NOT per event type). Pattern subscribe `asterisk:push:*`.
- **All inbound events on receiving nodes arrive wrapped as [`RemotePushEvent`](file:///media/Data/Source/Verbara/Verbara.Sdk/src/Verbara.Sdk.Push/Events/RemotePushEvent.cs)** — an envelope with `OriginalEventType` discriminator + `RawPayload` bytes. The concrete type is NOT reconstructed.
- **[ADR-0025](file:///media/Data/Source/Verbara/Verbara.Sdk/docs/decisions/0025-push-nats-subscribe-and-loop-prevention.md) explicitly rejects typed round-trip:** *"Rechazo de tipado fuerte de eventos cross-node — `RemotePushEvent` envelope es la API pública; consumers que necesiten más registran un deserializer custom."*
- **PR #25's dual-subscribe is a no-op for cross-node fanout** — `OfType<Core.X>()` AND `OfType<Pro.X>()` BOTH fail because the bus only contains `RemotePushEvent`. Only useful for in-process Pro.Cluster local events on the same pod.
- **Additionally**: lab logs show `[PRESENCE] Remote PayloadJson empty (check PushProOptions.PayloadSerializerOptions)` — API doesn't configure the payload serializer, so `RawPayload` arrives as empty bytes. Even a typed dispatcher can't decode nothing.

### v2.5.0 ACTUAL plan (paired PRs)

| PR | Scope | LOC |
|---|---|---|
| **#1 — API side** | Configure `PushProOptions.PayloadSerializerOptions` with a source-gen `JsonSerializerContext` (`PushPayloadJsonContext`) that knows every `Verbara.Platform.Core` push-event record. Without this `RawPayload` is empty. AOT-clean. | ~100 |
| **#2 — Realtime side** | New `RemoteEventDispatcher` `IHostedService`: subscribes to `_bus.OfType<RemotePushEvent>()`, switches on `OriginalEventType`, deserializes `RawPayload` via the same source-gen context (shared via `Verbara.Platform.Realtime.Contracts`), maps Core records to Pro Payloads, re-publishes the Pro-typed event so existing relay subscriptions fire. Loop-safe — `RedisEventRelay.ForwardToRedisAsync` already short-circuits `RemotePushEvent` so re-publishes don't echo. | ~200 + 100 tests |
| **#3 (optional, can defer to Pro v2.6.0-pro)** | Add `IPushPayloadDeserializer` extension point on `RedisEventRelay` (analog to `INatsPayloadDeserializer` mentioned in ADR-0025). Consumers register typed mappers via DI. Replaces PR #2's HostedService with a small registration. Out of scope for closing Test 5. | ~50 SDK + 10 consumer |

**Effort estimate:** 4-6 hours focused work + 1 release.yml cycle.

After PR #1 + PR #2 ship in v2.5.0 + helm upgrade + harness re-run → expect 10 Forwarded on leader pod + 30 SkippedNotLeader across 3 followers + 10 receives per client → **Test 5 PARTIAL → PASS** → plan moves to `docs/plans/completed/`.

### Decisions for next session

1. Keep this plan in `docs/plans/active/` until v2.5.0 ships + Test 5 PASS verified.
2. v2.5.0 PR scope = paired API serializer + Realtime dispatcher ONLY. Don't bundle the optional SDK extension point.
3. Phases 2 (Aspire AppHost), 4 (CI cascade), and 3-extension (7 remaining scenarios) all remain deferred — proven contract via PR #18+#19+v2.4.7 is the foundation.
4. Once Test 5 PASS, this plan moves to `docs/plans/completed/`.

---

## Cross-references

- [ADR-0022 — Platform.Api AOT shipping path](file:///media/Data/Source/Verbara/Verbara.Platform/docs/decisions/0022-platform-api-aot-shipping-path.md) — Phase A.5 leader election scaffold
- [ADR-0023 — Microservice publishing (no-AOT permitido)](file:///media/Data/Source/Verbara/Verbara.Platform/docs/decisions/) — justifica harness no-AOT
- [ADR-0024 — v2.4.2 anomaly + process hardening](file:///media/Data/Source/Verbara/Verbara.Platform/docs/decisions/0024-v242-shipping-anomaly-and-process-hardening.md) — release.yml es el camino obligatorio
- [Plan B Talos smoke report 2026-05-24](file:///media/Data/Source/Verbara/Verbara.Platform/docs/operations/phase-a5-talos-smoke-test-2026-05-24.md) — Test 5 PARTIAL que este plan cierra
- [Pro.Cluster Leadership scaffold](file:///media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Cluster.Leadership/) — el `IClusterLeader` keyed que el sink snapshotea
- [R5.5 execution plan](file:///media/Data/Source/Verbara/Verbara.Platform/docs/plans/active/2026-04-27-r5.5-execution-plan.md) — B-LK / C-LK / D-LK / E-LK que el harness habilita
