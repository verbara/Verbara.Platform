# Asterisk.Sdk v1.6.0 Sprint 1 — Execution Plan

**Spec base:** `docs/superpowers/specs/2026-04-13-sdk-v160-production-hardening-design.md`
**Fecha:** 2026-04-13
**Duración:** 3 semanas
**Modelo ejecución:** Subagent-Driven Development con FCM batching (Foundation → Critical → Integration)

---

## FCM Batching

### Phase A — Foundation (paralelo, batch subagente)
Trabajo mecánico de scaffolding y audit — bajo riesgo, alto paralelismo.

- **A1 — ARI call-site inventory:** grep todos los lugares donde `AriHttpClient` o equivalente recibe `HttpResponseMessage`; producir lista de métodos + resource + operación esperando 404/409. Output: `artifacts/ari-exception-sites.md`.
- **A2 — Config `#include` fixtures:** crear 4 archivos fixture en `Tests/Asterisk.Sdk.Config.Tests/Fixtures/Include/`: `a.conf` → includes `b.conf`, `b.conf` → includes `c.conf`, `c.conf` terminal, + `cycle-a.conf ↔ cycle-b.conf`, + `missing-tryinclude.conf`.
- **A3 — AMI gauge audit report:** lectura de `AmiMetrics.cs` + `AmiConnection.cs` reconnect path; reporte: ¿existe leak real? ¿en qué línea? ¿cómo reproducir? Output: `artifacts/ami-gauge-audit.md`.
- **A4 — Sdk.Push scaffold completo:** crear directorios + archivos vacíos (con namespace + comment) de la estructura Pilar 2 §3.2. Csproj ya existe; agregar `InternalsVisibleTo("Asterisk.Sdk.Push.Tests")` si falta. Crear proyecto `Tests/Asterisk.Sdk.Push.Tests/` en slnx.
- **A5 — Platform.Core inventory:** listar todos los consumidores de `PlatformEventBus` en Platform repo (grep) — identificar impacto del cambio a facade. Output: `artifacts/platform-eventbus-consumers.md`.

**Batch size:** 5 subagentes en paralelo (sin estado compartido). Duración target: 0.5 día.

### Phase B — Critical (individual, foco)
Componentes de diseño no-trivial — un subagente por tarea, review entre tareas.

- **B1 — ARI exception mapping implementation (2d)**
  - Implementar helper `ThrowIfNotFoundOrConflict(HttpResponseMessage, string resource, string? id)` en `AriHttpClient`.
  - Aplicar en todos los call sites de A1.
  - Tests: por resource, 404 → `AriNotFoundException`, 409 → `AriConflictException`, 200 → ok.
  - Verificar: `grep -c "throw new AriNotFound" src/` → ≥10.

- **B2 — Config `#include` resolution (2d)**
  - Parser: reconocer `#include`, `#tryinclude` con path entre `<>` o `""`.
  - Resolver relativo a directorio del archivo actual.
  - Recursión con `HashSet<string>` canónico (`Path.GetFullPath`) para detección de ciclos.
  - `#tryinclude`: swallow `FileNotFoundException`.
  - `#include` ausente: throw `ConfigParseException("Cycle detected: ...")` o `("Include file not found: ...")`.
  - Tests desde fixtures A2.

- **B3 — AMI gauge fix o regression test (1d)**
  - Si A3 encontró bug: fix (re-bind gauge callback post-reconnect).
  - Si A3 no encontró bug: test explícito que valide reconnect 3x no freeze gauges.
  - Test con `DockerAsterisk` controller: Disconnect → Reconnect → verificar `Meter` values.

- **B4 — AMI `EventsDropped` cleanup (0.5d)**
  - Decisión spec: **eliminar**. Borrar counter, ajustar tests si alguno lo referencia.
  - Verify: build clean, counter removido de `PublicAPI.Shipped.txt`.

- **B5 — Sdk.Push core (4d)**
  - `PushEvent`, `PushEventMetadata`, `IPushEvent` (Events/).
  - `IPushEventBus` + `RxPushEventBus` con `Subject<PushEvent>` bounded via Channel wrapper si buffer capacity configurada.
  - `PushEventBusOptions` con `BackpressureStrategy` enum.
  - `IEventDeliveryFilter` + `DefaultDeliveryFilter` (tenant isolation check; user-targeted si `event.Metadata.UserId is not null`).
  - `SubscriberContext` record.
  - `ISubscriptionRegistry` + `InMemorySubscriptionRegistry` (tracks active subs por tenantId — sólo para metrics).
  - `PushMetrics` con `events_published`, `events_delivered`, `events_dropped`, `subscribers_active`.
  - `ServiceCollectionExtensions.AddAsteriskPush(IConfiguration?)`.
  - Tests: ≥20 unit tests conforme spec §3.2.

- **B6 — Sdk.Push AOT + docs (1d)**
  - Verificar `dotnet publish -c Release /p:PublishAot=true` en test sample → 0 warnings.
  - Escribir `README.md` del paquete (30-50 líneas, sample publish+subscribe+filter).
  - Poblar `PublicAPI.Shipped.txt` para release v1.6.0.

**Batch size:** 1 subagente por tarea, con dependencia: B1+B2+B3+B4 paralelos (Pilar 1), B5 secuencial, B6 tras B5. Duración target: 7 días dev (con solapamientos posibles).

### Phase C — Integration (batch paralelo final)
Conectar todo + validar regresión — puede paralelizarse una vez Pilar 2 completo.

- **C1 — Platform.Core migration (2d)**
  - Agregar `PackageReference Asterisk.Sdk.Push 1.6.0-preview.1` a `Asterisk.Platform.Core.csproj`.
  - `PlatformEvent` pasa a heredar de `Sdk.Push.PushEvent`.
  - Poblar `Metadata` en cada call site de `.Publish(new XxxEvent(...))`.
  - `PlatformEventBus` refactored como facade sobre `IPushEventBus` inyectado.
  - Suite Platform.Core + Platform.Api pasa sin modificación externa.

- **C2 — SseEndpoints migration (1d)**
  - Extraer lambda `IsDeliverableToUser` a `PlatformDeliveryFilter : IEventDeliveryFilter`.
  - `SseEndpoints` consume `IPushEventBus.AsObservable()` + `IEventDeliveryFilter`.
  - Sin cambio de wire format: mismos eventos, mismo JSON (ApiJsonContext unchanged).

- **C3 — DI wiring (0.5d)**
  - `Program.cs`: `builder.Services.AddAsteriskPush()` antes de `AddPlatformCore()`.
  - `AddPlatformCore()` registra `PlatformDeliveryFilter` como `IEventDeliveryFilter`.

- **C4 — E2E regression (1d)**
  - Platform.Api: `dotnet test` → 1,636 tests pass.
  - Platform.Web E2E: `npx playwright test` → 263 pass / 4 pre-existing flaky.
  - Manual smoke: login + SSE stream + notification severity + conversation.assigned targeting.

- **C5 — Release prep (0.5d)**
  - Bump `Directory.Build.props` → `1.6.0`.
  - Pack: `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/`.
  - Clear cache en consumers: `rm -rf ~/.nuget/packages/asterisk.sdk*/1.6.0*/`.
  - Release notes + GitHub release draft + `project_v160_release.md` en SDK memory.

**Batch size:** C1→C2→C3 secuencial (dependencia), C4+C5 tras C3. Duración target: 5 días.

---

## Roles y convenciones

- **Subagent prompt template:** cada tarea incluye spec link + file paths + tests esperados + criterio de done (build green + tests pass).
- **Review gate:** código-reviewer entre Phase A y B, y entre Phase B y C. Phase A es mecánica — skip review.
- **Commits:** uno por tarea (`feat(push):`, `fix(ari):`, `fix(config):`, `chore(ami):`, `feat(platform):`). Nunca `Co-Authored-By`.
- **Plan update:** marcar cada task como `- [x]` en este archivo antes de commit.

---

## Checklist de ejecución

### Phase A (paralelo, 0.5d)
- [ ] A1 ARI call-site inventory
- [ ] A2 Config include fixtures
- [ ] A3 AMI gauge audit report
- [ ] A4 Sdk.Push scaffold completo
- [ ] A5 Platform.Core consumers inventory

### Phase B (paralelo+secuencial, 7d)
- [ ] B1 ARI exception mapping
- [ ] B2 Config #include resolution
- [ ] B3 AMI gauge fix o regression test
- [ ] B4 AMI EventsDropped cleanup
- [ ] B5 Sdk.Push core
- [ ] B6 Sdk.Push AOT + docs

### Phase C (secuencial+paralelo, 5d)
- [ ] C1 Platform.Core migration
- [ ] C2 SseEndpoints migration
- [ ] C3 DI wiring
- [ ] C4 E2E regression (Platform API + Web)
- [ ] C5 Release prep (pack, bump, memory update)

### Post-sprint
- [ ] Tag `v1.6.0` + GitHub release SDK
- [ ] Publish a nuget.org (17 paquetes, +1 Push = 18)
- [ ] Platform consume `1.6.0` (bump dep, smoke test, commit)
- [ ] Memory updates: SDK `project_v160_release.md`, Platform MEMORY.md link

---

## Criterios de salida

Ver spec §5 — reproducidos aquí:
- 0 warnings build SDK + Platform.
- SDK unit tests: ~2,480 pass (2,455 baseline + ~25 nuevos Push).
- SDK functional tests: 640 pass.
- AOT publish: 0 trim warnings.
- ARI: ≥10 throws de `AriNotFoundException`/`AriConflictException` (grep).
- Config `#include`: suite completa pasa.
- AMI reconnect: 3x consecutivos, gauges reflejan estado vivo.
- Platform Api: 1,636 tests pass consumiendo `Sdk.Push 1.6.0`.
- Platform Web E2E: 263 pass / 4 pre-existing flaky (sin regresión nueva).
- `Asterisk.Sdk.Push` en nuget.org + local feed.
