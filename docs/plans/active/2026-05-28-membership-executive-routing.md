# Plan: ADR-0026 Implementation — Membership Executive Routing

**ADR:** [0026-queue-membership-executive-routing.md](../../decisions/0026-queue-membership-executive-routing.md)
**Surfaced by:** Living-docs Fase 1 — Day 1 manual auto-generation
**Estimated effort:** Phase A ~2 días · Phase B ~5-7 días · Phase C ~3 días
**Repos touched:** `Verbara.Platform` (mayoría), `Verbara.Platform.Web` (wizard), `Verbara.Sdk.Pro.Realtime` (no cambios — solo consumo)

## Contexto

Living-docs detectó dos bugs reales del wizard de setup en v2.5.4 durante la generación auto del manual SMB Día 1:

1. **El paso "Agente" muestra al platform admin como única opción seleccionable.** El operador termina configurando al admin como primer agente, lo cual es funcional pero conceptualmente incorrecto (admin administra; agente atiende).
2. **El agente creado no queda asociado a la queue creada en el paso anterior.** El wizard llama a `createQueue` y `createAgent` pero nunca a `POST /queues/{queueId}/members`.

El análisis arquitectónico (ver ADR-0026) reveló que el bug #2 es síntoma de una **asimetría más profunda**: `queue_memberships` es ejecutivo para voz (vía Asterisk sync ya desplegado) pero decorativo para canales digitales (`InMemoryAgentPresenceService.GetAvailableAgentsAsync` no lo consulta).

La corrección requiere alinear el routing digital con el modelo de voz: membership ejecutivo en TODOS los canales (`B5 + B10 + B11`).

**Ventana óptima**: no hay clientes pagando todavía (pivot 2026-05-25). Migración de semántica sin impacto comercial.

## Decisión arquitectónica

Adoptada **B5 + B10 + B11** según ADR-0026:
- **B5** — Modelo de datos: cascada `member → not excluded → routable → has capacity → skill filter opcional → order by penalty`
- **B10** — Filosofía: Asterisk es autoridad para voz; Verbara replica el mismo modelo en digital
- **B11** — Mecanismo: read-model + `IRealtimeVerifier` para reconciliación con Asterisk live

## Fases

### Phase A — Wizard fix (2 días, frontend + endpoint extension)

**Objetivo:** el wizard del Día 1 deja agente correctamente asociado a la queue del paso anterior. Sin migración pendiente. Patch release tipo v2.5.5.

#### A.1 — Backend: extender `CreateAgent` endpoint

- Modificar [`AdminEndpoints.cs:414`](../../../src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs) DTO `CreateAgentRequest`:
  ```csharp
  record CreateAgentRequest(
      string UserId,
      string DisplayName,
      string? Extension,
      string? SipPassword,
      IReadOnlyList<string>? QueueIds);  // ← nuevo, default null
  ```
- Handler: si `QueueIds != null && QueueIds.Any()`, después de `SaveAsync(agent)` insertar `QueueMembership(tenantId, queueId, agentId, Source=Manual, Penalty=0)` por cada queueId y disparar `IRealtimeSyncService.AddQueueMemberAsync` en background (no bloquea response).
- Validación: cada queueId debe existir y pertenecer al mismo tenant. HTTP 400 si no.
- Tests: `CreateAgent_ShouldAssociateToQueues_WhenQueueIdsProvided`, `CreateAgent_ShouldReject_WhenQueueIdsBelongToOtherTenant`.

#### A.2 — Backend: serializer fix para `TenantChannelConfig`

Living-docs detectó que `GET /api/v1/admin/channels/{channel}` retorna HTTP 500 por `JsonTypeInfo metadata for TenantChannelConfig was not provided`. Agregar `[JsonSerializable(typeof(TenantChannelConfig))]` en [`ApiJsonContext.cs`](../../../src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs). Esto desbloquea el paso "Test" del wizard que hoy es inalcanzable.

#### A.3 — Frontend: wizard guarda `queueId` y materializa membership

- [`setup-wizard.tsx:73`](../../../../Verbara.Platform.Web/src/admin/setup/setup-wizard.tsx) — extender `SetupFormValues` con `createdQueueId: string | null`. Después de `createQueue.mutateAsync`, capturar `id` retornado y guardar en form state.
- Modificar `handleNext` step 'agent':
  ```ts
  if (currentStepKey === 'agent') {
    let userId = values.agentUserId;
    if (!userId && values.agentEmail) {
      const user = await customFetch({...});
      userId = user.id;
    }
    await createAgent.mutateAsync({
      userId,
      displayName: values.agentDisplayName,
      queueIds: values.createdQueueId ? [values.createdQueueId] : [], // ← nuevo
    });
  }
  ```
- [`use-agents.ts:76`](../../../../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts) — extender tipo de `mutationFn` con `queueIds?: string[]`.

#### A.4 — Frontend: wizard fuerza modo "crear nuevo usuario"

[`agent-step.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx) hoy decide modo por `useUsers().length`. Cambio: **siempre modo "crear nuevo"** en el wizard de Día 1 (el platform admin no es un agente válido conceptualmente). Modo "seleccionar existente" se preserva para crear agentes adicionales fuera del wizard, en `/admin/agents`.

- Eliminar la rama `hasUsers` en `agent-step.tsx`. Renderizar siempre los inputs Email + DisplayName.
- Generar contraseña temporal random y mostrarla en pantalla (matchea lo que dice el manual escrito a mano [`03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) que el wizard ya debería hacer).
- Tests Playwright living-docs: re-ejecutar el spec [`01-day1-setup-and-webchat.spec.ts`](../../../../Verbara.Platform.Web/tests/manuales/personas/smb-owner/01-day1-setup-and-webchat.spec.ts), debe pasar end-to-end sin el workaround `setup-skip`. Regenerar manual auto, validar paridad pedagógica vs `03-setup-inicial.md` manual escrito a mano.

#### A.5 — Living-docs regeneration

- Después de A.1-A.4 shipped, correr el spec contra stack v2.5.5 limpio: `MANUAL_BASE_URL=http://localhost npx playwright test -c tests/manuales/playwright.docs.config.ts`.
- Renderer produce manual auto-actualizado en [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md). Eliminar la sección "Paso 10 — Bug conocido en v2.5.4" del template.

**Criterio de salida A:**
- ✅ `dotnet test` 100% en Api.Tests (incluye nuevos casos CreateAgent + QueueIds).
- ✅ Spec living-docs Day 1 pasa end-to-end sin workarounds, 12/12 capturas reales.
- ✅ Manual auto-generado tiene paridad pedagógica con [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) (review humano).
- ✅ Tagged v2.5.5 + cosign-signed images + verbara-website digest authorization.

### Phase B — Membership executive gate (5-7 días, backend core)

**Objetivo:** `queue_memberships` se vuelve ejecutivo para routing digital. Modelo simétrico Voice ↔ Digital. Ship en v2.6.0 (minor bump por cambio de semántica de routing).

#### B.1 — Nuevo `MembershipGateMiddleware`

- Crear [`src/Verbara.Platform.Routing.Inbound/MembershipGateMiddleware.cs`](../../../src/Verbara.Platform.Routing.Inbound/). Patrón existente: extender `InboundRoutingMiddlewareBase`.
- Lógica: dado `queueId` resuelto por middleware anteriores, leer `queue_memberships(queueId)` filtrando `IsExcluded=false`. El candidate pool del siguiente middleware (`RoundRobinAgentSelector`) se restringe a estos agentIds.
- Excepciones documentadas en ADR-0026:
  - Si `Conversation.AssignedAgentId.HasValue` (direct-to-agent vía flow/transfer) → skip gate, asignar directo si state + capacity OK.
  - Si `LastAgentMiddleware` setea `PreferredAgentId` y el agente es member de **alguna** queue del tenant → honor sticky, skip gate.
- Registro en pipeline: insertar **antes** de `RoundRobinAgentSelector` en `Program.cs` DI composition.
- Behind feature flag `RoutingFeatures.MembershipGate` (default OFF en v2.5.x, ON en v2.6.0).

#### B.2 — Modificar `RoundRobinAgentSelector`

- [`RoundRobinAgentSelector.cs`](../../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs) — agregar **sort por `membership.penalty ASC`** como tiebreaker después del round-robin natural. Patrón Asterisk: penalty 0 prima sobre penalty 1, etc.
- Si feature flag OFF, comportamiento round-robin actual sin penalty sorting.

#### B.3 — Script de inferencia de memberships

- Crear [`scripts/migrations/2026-05-28-infer-memberships-from-skills.sh`](../../../scripts/migrations/).
- Idempotente: `INSERT INTO queue_memberships ... ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING`.
- Lógica: para cada agente con `agent.skills ∩ queue.required_skills ≠ ∅` y `NOT EXISTS membership`, insertar `(Source=Skill, Penalty=0, IsExcluded=false)`.
- Después del INSERT batch, dispara `RealtimeSyncEngine.SyncAgentBatchAsync(tenantId)` para propagar a Asterisk Realtime.
- Logging: cuántas memberships insertadas, cuántas ya existían, cuántos agentes sin queues.

#### B.4 — `IRealtimeVerifier` hosted service

- Crear `RealtimeReconciliationHostedService` en [`src/Verbara.Platform.Api/HostedServices/`](../../../src/Verbara.Platform.Api/). Tick cada 5 min (configurable). Por tenant activo:
  - Llama `IRealtimeVerifier.VerifyAsync()` (ya existe en SDK) que detecta drift entre `queue_memberships` (Verbara) y `queue_members` (Asterisk Realtime live, AMI query).
  - Si drift detectado, log warning + emit OTel metric `verbara.platform.realtime_drift_detected`.
  - Opcional gated por config: dispara `RealtimeReconciler.ReconcileAsync()` para auto-corregir hacia el estado Verbara (Verbara como source of truth).

#### B.5 — Tests

- `MembershipGateMiddlewareTests`: skip gate cuando direct-to-agent, honor sticky cross-queue, exclude non-members, exclude IsExcluded=true, respect feature flag.
- `RoundRobinAgentSelectorTests`: sort por penalty respetando round-robin entre mismo penalty.
- Integration test: queue + agent sin membership → conversación entra → no asignada; agregar membership → siguiente conversación asignada. Bug de v2.5.4 explícitamente cubierto.
- Asterisk drift test (lab Talos): `asterisk -rx "queue remove member ..."` manual → verifier detecta + reconciler corrige.

**Criterio de salida B:**
- ✅ `dotnet test` 100% (incluye suite nueva ~30 tests).
- ✅ Script de migración corrió en lab Talos con dataset ficticio (10 agents, 5 queues, mix de skills): inferencia correcta + Asterisk sync exitoso.
- ✅ Verifier hosted service detecta drift introducido manualmente.
- ✅ Living-docs Day 1 spec sigue verde post-cambio (rebreaker de regression).
- ✅ Tagged v2.6.0.

### Phase C — Documentation + manuales (3 días)

**Objetivo:** operadores entienden el nuevo modelo. Manuales SMB y K8s reflejan membership ejecutivo.

#### C.1 — ADR 0026 ship + actualizar manuales escritos a mano

- [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) — actualizar §3 Step 3 explicar que el wizard ahora crea user nuevo + membership automática.
- [`docs/manuales/smb/04-canal-webchat.md`](../../manuales/smb/04-canal-webchat.md) — agregar nota: "los agentes que reciben conversaciones de esta queue son los que están en `queue_memberships`".
- Crear `docs/manuales/smb/agentes-y-queues.md` (nuevo, ~20 min) que explica el modelo membership + penalty + skills + excepciones (sticky, direct-to-agent, outbound, jolly agent).

#### C.2 — Operations runbook entry

- [`docs/operations/`](../../operations/) — `routing-troubleshooting.md` que documenta:
  - "El agente no recibe conversaciones" → verificar membership + state + capacity en orden de la cascada.
  - "Drift entre Verbara y Asterisk" → command para correr `RealtimeVerifier.VerifyAsync()` manualmente + interpretar output.
  - Cómo trigger reconciliación manual.

#### C.3 — Living-docs regeneration

- Re-correr todos los specs de manuales (no solo Day 1) contra stack v2.6.0 limpio.
- Validar que no hay regressions de regeneración. Si el Day 2/3/etc spec depende del comportamiento corregido, ajustar template.

**Criterio de salida C:**
- ✅ Manuales SMB actualizados, sincronizados con v2.6.0 behavior.
- ✅ Operations runbook commit + linked desde [`docs/operations/production-readiness-review.md`](../../operations/production-readiness-review.md).
- ✅ Living-docs Day 1 + Day 2 (cuando exista) auto-regenerados, paridad pedagógica revisada.

## Critical files

**A crear:**
- [`docs/decisions/0026-queue-membership-executive-routing.md`](../../decisions/0026-queue-membership-executive-routing.md) — **ya escrito**
- `src/Verbara.Platform.Routing.Inbound/MembershipGateMiddleware.cs`
- `src/Verbara.Platform.Api/HostedServices/RealtimeReconciliationHostedService.cs`
- `scripts/migrations/2026-05-28-infer-memberships-from-skills.sh`
- `docs/manuales/smb/agentes-y-queues.md`
- `docs/operations/routing-troubleshooting.md`
- `tests/Verbara.Platform.Api.Tests/Routing/MembershipGateMiddlewareTests.cs`

**A modificar:**
- [`src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs:414`](../../../src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs) — extender `CreateAgentRequest` con `QueueIds`
- [`src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs`](../../../src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs) — `[JsonSerializable(typeof(TenantChannelConfig))]`
- [`src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs`](../../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs) — sort por penalty
- [`src/Verbara.Platform.Api/Program.cs`](../../../src/Verbara.Platform.Api/Program.cs) — registrar `MembershipGateMiddleware` + `RealtimeReconciliationHostedService`
- [`../Verbara.Platform.Web/src/admin/setup/setup-wizard.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/setup-wizard.tsx) — guardar queueId + pasar a createAgent
- [`../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx) — forzar modo "crear nuevo usuario"
- [`../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts`](../../../../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts) — agregar `queueIds` a mutation type
- [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) — actualizar §3 Step 3
- [`docs/manuales/smb/04-canal-webchat.md`](../../manuales/smb/04-canal-webchat.md) — agregar nota membership

**A reusar (sin modificar):**
- [`Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs`](../../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs) — SDK ya consumido por `QueueMembersEndpoints`
- [`Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs`](../../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs) — habilitar hosted service consume

## Verification

**Phase A (after ship v2.5.5):**
```bash
cd /media/Data/Source/Verbara/Verbara.Platform
dotnet test tests/Verbara.Platform.Api.Tests --filter "FullyQualifiedName~CreateAgent"
cd /media/Data/Source/Verbara/Verbara.Platform.Web
MANUAL_BASE_URL=http://localhost npx playwright test -c tests/manuales/playwright.docs.config.ts
npx tsx tests/manuales/manual-renderer/render.ts --journey 01-day1-setup-and-webchat
diff docs/manuales/smb/03-setup-inicial.md docs/manuales/auto/v2.5.5/es-419/smb-owner/01-day1-setup-and-webchat.md  # paridad pedagógica
```

**Phase B (after ship v2.6.0):**
```bash
# Live test routing executive gate in Talos lab
kubectl -n verbara-loadtest exec -it postgres-pooler-0 -- psql -U postgres -d verbara -c "
  INSERT INTO agents (...) VALUES ('agent-orphan', ...);  -- sin queue_memberships
"
# Enviar conversación a la queue → verificar que NO se asigna a agent-orphan
# Insertar membership → siguiente conversación SÍ se asigna

# Test reconciliation
asterisk -rx "queue remove member PJSIP/agent-x from queue-y"
# Esperar 5 min → verifier debería detectar drift + reconciliar
kubectl logs -n verbara-loadtest deploy/platform-api | grep "realtime_drift"
```

**Phase C (qualitative review):**
- Lectura humana de manuales actualizados vs comportamiento real.
- Click-through del living-docs site (cuando MkDocs esté setup en plan Fase 2 living-docs).

## Out of scope / deferrals

- **Predicate engine (B7)**: válido evolución R5+. No bloqueado por este plan.
- **ABR proficiency (B9)**: válido evolución R6+. Requiere modelo skill levels que aún no existe.
- **Channel-aware membership** (`QueueMembership.AllowedChannels`): considerable a futuro, no bloquea esta decisión.
- **Migración de comportamiento outbound (Pro.Dialer)**: el dialer ya respeta membership como input (lee queue + members para campaign assignment). No requiere cambio para este plan.
- **CI gating del release.yml en base a manuales pass/fail**: viene en plan living-docs Fase 4, no bloquea este.

## Risk register

| Riesgo | Mitigación |
|---|---|
| Script de migración inserta memberships incorrectas que rompen routing en lab | Idempotente + dry-run mode + log preview antes de commit. Tested en lab Talos antes de v2.6.0 ship. |
| Verifier hosted service hace queries pesadas a Asterisk AMI bajo carga | Tick configurable (default 5 min, mínimo 1 min). Bypass disponible si AMI saturado. |
| Feature flag confusion: customer corre v2.5.x con flag ON sin querer | Default OFF en patch releases. ON solo en v2.6.0 release explícito. Documentar claramente en CHANGELOG. |
| Sticky cross-queue breaks expectation operador | Documentar explícitamente en operations runbook + en docs/manuales/smb/agentes-y-queues.md. |
| Direct-to-agent bypass de membership es confuso para auditoría | Logging explícito en `OfferToAgentAsync` cuando bypass de membership ocurre. OTel event. |

## References

- ADR-0026 (decisión arquitectónica): [`docs/decisions/0026-queue-membership-executive-routing.md`](../../decisions/0026-queue-membership-executive-routing.md)
- Living-docs plan: [`docs/plans/active/2026-05-27-living-docs-from-e2e-tests.md`](2026-05-27-living-docs-from-e2e-tests.md)
- Living-docs Day 1 manual (que surfaceó los bugs): [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md)
- SDK Pro Realtime: [`Verbara.Sdk.Pro.Realtime/`](../../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/)
- Pivot estratégico 2026-05-25 (ventana óptima): memory `session_20260525_phase0c_deferred_smb_pivot.md`
