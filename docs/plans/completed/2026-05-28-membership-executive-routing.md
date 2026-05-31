# Plan: ADR-0026 Implementation — Membership Executive Routing

**ADR:** [0026-queue-membership-executive-routing.md](../../decisions/0026-queue-membership-executive-routing.md)
**Surfaced by:** Living-docs Fase 1 — Day 1 manual auto-generation
**Estimated effort:** Phase A ~3 días · Phase B ~6-8 días · Phase C ~3 días
**Repos touched:** `Verbara.Platform` (mayoría), `Verbara.Platform.Web` (wizard + admin agentes), `Verbara.Sdk.Pro.Realtime` (signature change `AddQueueMemberAsync(..., allowedChannels)`)

## Status — ✅ CLOSED 2026-05-30 (shipped in Platform v2.6.0)

> **Closure note (2026-05-30):** All three phases landed and shipped in Platform **v2.6.0** (tag `v2.6.0`, released via `release.yml`). This implementation plan is superseded by the executed Phase B gate plan ([`2026-05-29-membership-executive-gate.md`](2026-05-29-membership-executive-gate.md)) and the ADR closure ([`0026-queue-membership-executive-routing.md`](../../decisions/0026-queue-membership-executive-routing.md) §Implementation status). The original "post-2026-06-28 freeze" calendar gate on Phase B was retired by the 2026-05-25 pivot (no production to protect). Moved `active/` → `completed/`.

| Phase | Status | Notes |
|---|---|---|
| **Phase A — Wizard fix + channel-aware REST + UI editor** | ✅ **SHIPPED 2026-05-28** | A.1–A.6 + A.6.7 + 13 Api.Tests merged to `main` (6 commits; Platform `0ddb511d`/`53c0ac61`/`442e3ad9` + 3 Web). |
| **Phase B — Membership executive gate (digital routing parity)** | ✅ **SHIPPED 2026-05-29** | SDK Pro v2.6.0-pro (`913ec98`, `AddQueueMemberAsync(allowedChannels)` voice-gate) + Platform `b731c1fc`+`a6220698` (`IRoutingEligibilityService`, `MembershipAwareRoutingEligibilityService`, penalty-grouped `RoundRobinAgentSelector`, `RealtimeReconciliationService`, `infer-memberships-from-skills.sh`) + 13 tests. Calendar gate derogated by 2026-05-25 pivot. |
| **Phase C — Documentation + manuales** | 🟢 **DONE (SMB 03/04 refreshed)** | SMB manuals 03 §4b + 04 §3.1 refreshed with routing-ejecutivo semantics (closes ADR-0027 C.2). Broader manuales re-sync for v2.6.0 tracked separately under the post-release manuales audit / living-docs track. |

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

### Phase A — Wizard fix (2 días, frontend + endpoint extension) — ✅ SHIPPED 2026-05-28

> Closure summary at the top of this file. Sub-section checklists below preserved for historical reference.

**Objetivo:** el wizard del Día 1 deja agente correctamente asociado a la queue del paso anterior. Sin migración pendiente. Patch release tipo v2.5.5.

#### A.1 — Backend: extender `CreateAgent` endpoint con channel-aware memberships

- Modificar [`AdminEndpoints.cs:414`](../../../src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs) DTO `CreateAgentRequest`:
  ```csharp
  record CreateAgentRequest(
      string UserId,
      string DisplayName,
      string? Extension,
      string? SipPassword,
      IReadOnlyList<QueueMembershipRequest>? QueueMemberships);  // ← nuevo, default null

  record QueueMembershipRequest(
      string QueueId,
      IReadOnlyList<string>? AllowedChannels = null,  // null = todos los canales que la queue acepta
      int Penalty = 0);
  ```
- Handler: si `QueueMemberships != null && Any()`, después de `SaveAsync(agent)` insertar `QueueMembership(tenantId, queueId, agentId, Source=Manual, Penalty, AllowedChannels)` por cada entrada. Disparar `IRealtimeSyncService.AddQueueMemberAsync(..., allowedChannels)` en background (no bloquea response) **solo si `AllowedChannels IS NULL || 'voice' IN AllowedChannels`** — en caso contrario NO se crea row en Asterisk `queue_members`.
- Validación: cada queueId debe existir y pertenecer al mismo tenant. Cada channel en `AllowedChannels` debe ser un canal soportado (whitelist: voice, webchat, sms, whatsapp, email, telegram, messenger, instagram, video, twitter, rcs). HTTP 400 si no.
- Tests: 
  - `CreateAgent_ShouldAssociateToQueues_WhenQueueMembershipsProvided`
  - `CreateAgent_ShouldSyncToAsterisk_WhenAllowedChannelsIncludesVoice`
  - `CreateAgent_ShouldNotSyncToAsterisk_WhenAllowedChannelsExcludesVoice`
  - `CreateAgent_ShouldReject_WhenQueueIdsBelongToOtherTenant`
  - `CreateAgent_ShouldReject_WhenAllowedChannelsContainsInvalidChannel`

#### A.2 — Backend: serializer fix para `TenantChannelConfig`

Living-docs detectó que `GET /api/v1/admin/channels/{channel}` retorna HTTP 500 por `JsonTypeInfo metadata for TenantChannelConfig was not provided`. Agregar `[JsonSerializable(typeof(TenantChannelConfig))]` en [`ApiJsonContext.cs`](../../../src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs). Esto desbloquea el paso "Test" del wizard que hoy es inalcanzable.

#### A.3 — Frontend: wizard guarda `queueId` y materializa membership (default todos los canales)

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
      queueMemberships: values.createdQueueId
        ? [{ queueId: values.createdQueueId }] // allowedChannels omitido = NULL = todos los canales
        : [],
    });
  }
  ```
- [`use-agents.ts:76`](../../../../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts) — extender tipo de `mutationFn`:
  ```ts
  type CreateAgentInput = {
    userId: string;
    displayName: string;
    queueMemberships?: Array<{
      queueId: string;
      allowedChannels?: string[];  // omitido = todos los canales que la queue acepta
      penalty?: number;
    }>;
  };
  ```
- **Decisión UX para Day 1 wizard**: el wizard NO expone selector de canales en este paso. El agente queda en todas las canalidades de la queue por default (semántica intuitiva: "lo asocié a la queue, me atiende lo que la queue reciba"). La granularidad fina se expone en `/admin/agents/{id}/queues` para casos avanzados (paso A.6).

#### A.4 — Frontend: wizard fuerza modo "crear nuevo usuario"

[`agent-step.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx) hoy decide modo por `useUsers().length`. Cambio: **siempre modo "crear nuevo"** en el wizard de Día 1 (el platform admin no es un agente válido conceptualmente). Modo "seleccionar existente" se preserva para crear agentes adicionales fuera del wizard, en `/admin/agents`.

- Eliminar la rama `hasUsers` en `agent-step.tsx`. Renderizar siempre los inputs Email + DisplayName.
- Generar contraseña temporal random y mostrarla en pantalla (matchea lo que dice el manual escrito a mano [`03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) que el wizard ya debería hacer).
- Tests Playwright living-docs: re-ejecutar el spec [`01-day1-setup-and-webchat.spec.ts`](../../../../Verbara.Platform.Web/tests/manuales/personas/smb-owner/01-day1-setup-and-webchat.spec.ts), debe pasar end-to-end sin el workaround `setup-skip`. Regenerar manual auto, validar paridad pedagógica vs `03-setup-inicial.md` manual escrito a mano.

#### A.5 — Living-docs regeneration

- Después de A.1-A.4 shipped, correr el spec contra stack v2.5.5 limpio: `MANUAL_BASE_URL=http://localhost npx playwright test -c tests/manuales/playwright.docs.config.ts`.
- Renderer produce manual auto-actualizado en [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md). Eliminar la sección "Paso 10 — Bug conocido en v2.5.4" del template.

#### A.6 — Admin agentes UI: channel-aware membership editor

- Página `/admin/agents/{id}/queues` (puede ser tab dentro de `/admin/agents/{id}`) que muestra la lista de queues donde el agente es member, con:
  - Columna **AllowedChannels** mostrando "Todos" si `NULL` o pills con los canales explícitos
  - Edit: multi-select de canales soportados (default seleccionado: todos los canales que la queue acepta)
  - Botón "Solo digital" como shortcut (excluye voice del array)
  - Botón "Solo voz" como shortcut
- Validación cliente + server: rechazar array vacío `[]` (sugerir `IsExcluded=true` en su lugar para claridad audit-wise).
- Tests Playwright living-docs futuro (Fase 4 living-docs en otra entrega): journey "Tenant Admin — Especializar agente a WebChat-only" capturará este flow.

**Criterio adicional A.6**: el editor refleja el sync a Asterisk en banner informativo: "Este agente recibirá llamadas en esta queue" / "Este agente NO recibirá llamadas en esta queue (`voice` no está en canales permitidos)".

**Criterio de salida A:**
- ✅ `dotnet test` 100% en Api.Tests (incluye nuevos casos CreateAgent + QueueIds).
- ✅ Spec living-docs Day 1 pasa end-to-end sin workarounds, 12/12 capturas reales.
- ✅ Manual auto-generado tiene paridad pedagógica con [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) (review humano).
- ✅ Tagged v2.5.5 + cosign-signed images + verbara-website digest authorization.

### Phase B — Membership executive gate (5-7 días, backend core) — ⏸️ QUEUED post-2026-06-28

**Objetivo:** `queue_memberships` se vuelve ejecutivo para routing digital. Modelo simétrico Voice ↔ Digital. Ship en v2.6.0 (minor bump por cambio de semántica de routing).

#### B.1 — Schema migration `allowed_channels`

Antes de tocar el routing, ship la migración del schema:
- Crear `src/Verbara.Platform.Storage.Postgres/Migrations/Vxxx_QueueMembershipAllowedChannels.sql`:
  ```sql
  ALTER TABLE queue_memberships
    ADD COLUMN allowed_channels TEXT[];
  -- NULL preserva semántica implícita pre-v2.6.0
  -- (member para todos los canales que la queue acepta)
  ```
- Extender row class `QueueMembership.cs` con `AllowedChannels: IReadOnlyList<string>?` (nullable).
- Actualizar `QueueMembership.Map(NpgsqlDataReader)` para leer la nueva columna (Npgsql 10 nativo soporte `string[]`).
- Actualizar INSERT/UPDATE statements en `PostgresQueueMembershipStore` para escribir el campo.
- Tests: store roundtrip con `AllowedChannels=NULL`, `AllowedChannels=['voice']`, `AllowedChannels=['webchat','email']`, `AllowedChannels=[]`.

#### B.2 — Nuevo `MembershipGateMiddleware` con channel-aware filter

- Crear [`src/Verbara.Platform.Routing.Inbound/MembershipGateMiddleware.cs`](../../../src/Verbara.Platform.Routing.Inbound/). Patrón existente: extender `InboundRoutingMiddlewareBase`.
- Lógica: dado `queueId` resuelto por middleware anteriores, leer `queue_memberships(queueId)` filtrando `IsExcluded=false` **y** `AllowedChannels IS NULL OR conversation.Channel IN AllowedChannels`. El candidate pool del siguiente middleware (`RoundRobinAgentSelector`) se restringe a estos agentIds.
- Excepciones documentadas en ADR-0026:
  - Si `Conversation.AssignedAgentId.HasValue` (direct-to-agent vía flow/transfer) → skip gate completo (incluye channel-aware), asignar directo si state + capacity OK.
  - Si `LastAgentMiddleware` setea `PreferredAgentId` y el agente es member de **alguna** queue del tenant **con channel-aware compatible al canal actual** → honor sticky, skip gate. Si el agente es member pero `AllowedChannels` excluye el canal de la conversación actual, NO honor sticky (semántica: el agente opted-out de ese canal en TODAS sus queues).
- Registro en pipeline: insertar **antes** de `RoundRobinAgentSelector` en `Program.cs` DI composition.
- Behind feature flag `RoutingFeatures.MembershipGate` (default OFF en v2.5.x, ON en v2.6.0).

#### B.3 — Modificar `RoundRobinAgentSelector`

- [`RoundRobinAgentSelector.cs`](../../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs) — agregar **sort por `membership.penalty ASC`** como tiebreaker después del round-robin natural. Patrón Asterisk: penalty 0 prima sobre penalty 1, etc.
- Si feature flag OFF, comportamiento round-robin actual sin penalty sorting.

#### B.4 — Cambio en `IRealtimeSyncService` (SDK Pro)

- Modificar signature `AddQueueMemberAsync(tenantId, queueName, agentId, displayName, penalty, **allowedChannels**, ct)` en `Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs`.
- Implementación condicional: si `allowedChannels IS NULL || allowedChannels.Contains("voice")` → INSERT en `queue_members` (Asterisk Realtime). Si NO incluye voice → SKIP insert o REMOVE si existía.
- `RemoveQueueMemberAsync` queda igual (no requiere channel info, siempre elimina del PBX).
- Bump SDK Pro version: minor (v2.6.0-pro) — breaking change de signature pero solo callers internos (Platform).
- Tests: `RealtimeSync_ShouldInsertToAsterisk_WhenAllowedChannelsNull`, `RealtimeSync_ShouldInsertToAsterisk_WhenVoiceIncluded`, `RealtimeSync_ShouldNotInsert_WhenVoiceExcluded`, `RealtimeSync_ShouldRemove_WhenVoiceRevoked`.

#### B.5 — Script de inferencia de memberships

- Crear [`scripts/migrations/2026-05-28-infer-memberships-from-skills.sh`](../../../scripts/migrations/).
- Idempotente: `INSERT INTO queue_memberships ... ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING`.
- Lógica: para cada agente con `agent.skills ∩ queue.required_skills ≠ ∅` y `NOT EXISTS membership`, insertar `(Source=Skill, Penalty=0, IsExcluded=false, AllowedChannels=NULL)`.
- `AllowedChannels=NULL` preserva semántica implícita pre-v2.6.0 (member para todos los canales que la queue acepta). Operadores pueden refinar después en `/admin/agents/{id}/queues`.
- Después del INSERT batch, dispara `RealtimeSyncEngine.SyncAgentBatchAsync(tenantId)` para propagar a Asterisk Realtime (todos sync porque `AllowedChannels=NULL` permite voz).
- Logging: cuántas memberships insertadas, cuántas ya existían, cuántos agentes sin queues.

#### B.6 — `IRealtimeVerifier` hosted service

- Crear `RealtimeReconciliationHostedService` en [`src/Verbara.Platform.Api/HostedServices/`](../../../src/Verbara.Platform.Api/). Tick cada 5 min (configurable). Por tenant activo:
  - Llama `IRealtimeVerifier.VerifyAsync()` (ya existe en SDK) que detecta drift entre `queue_memberships` (Verbara) y `queue_members` (Asterisk Realtime live, AMI query).
  - Si drift detectado, log warning + emit OTel metric `verbara.platform.realtime_drift_detected`.
  - Opcional gated por config: dispara `RealtimeReconciler.ReconcileAsync()` para auto-corregir hacia el estado Verbara (Verbara como source of truth).

#### B.7 — Tests

- `MembershipGateMiddlewareTests`: skip gate cuando direct-to-agent, honor sticky cross-queue, exclude non-members, exclude IsExcluded=true, **exclude when channel not in AllowedChannels**, **deny sticky if AllowedChannels excludes current channel**, respect feature flag.
- `ChannelAwareMembershipTests`: agente con `AllowedChannels=['webchat']` no recibe voz; agente con `AllowedChannels=NULL` recibe todo; cambio runtime de `AllowedChannels=[webchat]` → `AllowedChannels=NULL` triggea sync a Asterisk; cambio reverso elimina del PBX.
- `RealtimeSyncEngineTests` (SDK Pro): `AddQueueMemberAsync` respeta `AllowedChannels=null` (insert), `['voice']` (insert), `['webchat']` (skip), `[]` (remove if existed).
- `RoundRobinAgentSelectorTests`: sort por penalty respetando round-robin entre mismo penalty.
- Integration test end-to-end: queue + agent sin membership → conversación entra → no asignada; agregar membership con `AllowedChannels=['webchat']` → conversación WebChat se asigna a este agente, llamada de voz a la misma queue NO se asigna a este agente (PBX no lo timbra porque no está en `queue_members`).
- Asterisk drift test (lab Talos): `asterisk -rx "queue remove member ..."` manual → verifier detecta drift solo para agentes con `AllowedChannels NULL OR contains 'voice'` + reconciler corrige re-insertando solo los que correspondan.

**Criterio de salida B:**
- ✅ `dotnet test` 100% (incluye suite nueva ~30 tests).
- ✅ Script de migración corrió en lab Talos con dataset ficticio (10 agents, 5 queues, mix de skills): inferencia correcta + Asterisk sync exitoso.
- ✅ Verifier hosted service detecta drift introducido manualmente.
- ✅ Living-docs Day 1 spec sigue verde post-cambio (rebreaker de regression).
- ✅ Tagged v2.6.0.

### Phase C — Documentation + manuales (3 días) — 🟡 PARTIAL (Day 2 living-docs journey shipped 2026-05-28)

**Objetivo:** operadores entienden el nuevo modelo. Manuales SMB y K8s reflejan membership ejecutivo.

#### C.1 — ADR 0026 ship + actualizar manuales escritos a mano

- [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) — actualizar §3 Step 3 explicar que el wizard ahora crea user nuevo + membership automática.
- [`docs/manuales/smb/04-canal-webchat.md`](../../manuales/smb/04-canal-webchat.md) — agregar nota: "los agentes que reciben conversaciones de esta queue son los que están en `queue_memberships`".
- Crear `docs/manuales/smb/agentes-y-queues.md` (nuevo, ~30 min) que explica el modelo membership + penalty + skills + **channel-aware (`AllowedChannels`)** + excepciones (sticky, direct-to-agent, outbound, jolly agent). Incluir ejemplos concretos:
  - "Agente WebChat-only en queue Soporte": `AllowedChannels=['webchat']` → no recibe llamadas aunque la queue las acepte.
  - "Agente que atiende WebChat en Soporte y Voz en VIP": dos memberships distintas, una con `AllowedChannels=['webchat']`, otra con `AllowedChannels=['voice']`.
  - "Agente jolly multi-canal": `AllowedChannels=NULL` (default), penalty=9 → recibe overflow de cualquier canal en todas las queues.

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

**A modificar (Verbara.Platform):**
- [`src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs:414`](../../../src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs) — extender `CreateAgentRequest` con `QueueMemberships[{QueueId, AllowedChannels?, Penalty?}]`
- [`src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs`](../../../src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs) — `[JsonSerializable(typeof(TenantChannelConfig))]` + nuevos DTOs `QueueMembershipRequest`
- [`src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs`](../../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs) — sort por penalty
- [`src/Verbara.Platform.Api/Program.cs`](../../../src/Verbara.Platform.Api/Program.cs) — registrar `MembershipGateMiddleware` + `RealtimeReconciliationHostedService`
- `src/Verbara.Platform.Queues/QueueMembership.cs` — agregar `AllowedChannels: IReadOnlyList<string>?`
- `src/Verbara.Platform.Storage.Postgres/Migrations/Vxxx_QueueMembershipAllowedChannels.sql` — nueva migration
- `src/Verbara.Platform.Storage.Postgres/Queues/PostgresQueueMembershipStore.cs` — leer/escribir `allowed_channels`

**A modificar (Verbara.Platform.Web):**
- [`../Verbara.Platform.Web/src/admin/setup/setup-wizard.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/setup-wizard.tsx) — guardar queueId + pasar `queueMemberships[{ queueId }]` a createAgent (allowedChannels omitido = NULL)
- [`../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx`](../../../../Verbara.Platform.Web/src/admin/setup/steps/agent-step.tsx) — forzar modo "crear nuevo usuario"
- [`../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts`](../../../../Verbara.Platform.Web/src/core/api/hooks/use-agents.ts) — agregar `queueMemberships[]` con tipo channel-aware
- Nueva página/tab `../Verbara.Platform.Web/src/admin/agents/agent-queues-tab.tsx` — channel-aware membership editor (paso A.6)
- [`docs/manuales/smb/03-setup-inicial.md`](../../manuales/smb/03-setup-inicial.md) — actualizar §3 Step 3
- [`docs/manuales/smb/04-canal-webchat.md`](../../manuales/smb/04-canal-webchat.md) — agregar nota membership

**A modificar (Verbara.Sdk.Pro — breaking change minor):**
- [`Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs`](../../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs) — agregar parámetro `IReadOnlyList<string>? allowedChannels` a `AddQueueMemberAsync`. Implementación condicional: insert solo si `allowedChannels IS NULL || contains 'voice'`.
- Bump versión SDK Pro a v2.6.0-pro (minor).

**A reusar (sin modificar):**
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
- **Migración de comportamiento outbound (Pro.Dialer)**: el dialer ya respeta membership como input (lee queue + members para campaign assignment). El dialer también debería respetar `AllowedChannels` para outbound voice (no dispatchar a un agente con `AllowedChannels=['webchat']` aunque sea member). Patch defensivo en Phase B SDK Pro v2.6.0-pro.
- **CI gating del release.yml en base a manuales pass/fail**: viene en plan living-docs Fase 4, no bloquea este.
- **Bulk channel-update UI** (cambiar `AllowedChannels` para 10 agentes a la vez): operación administrativa de bajo volumen. Phase B.7 ships con single-agent edit; bulk UI puede agregarse a posteriori si se vuelve dolor.

## Risk register

| Riesgo | Mitigación |
|---|---|
| Script de migración inserta memberships incorrectas que rompen routing en lab | Idempotente + dry-run mode + log preview antes de commit. Tested en lab Talos antes de v2.6.0 ship. `AllowedChannels=NULL` default preserva comportamiento implícito pre-v2.6.0. |
| Operador setea `AllowedChannels=[]` (array vacío) por error → agente queda sin canales | Validation en backend: rechazar `[]` con HTTP 400 sugiriendo `IsExcluded=true` para el mismo efecto pero con audit trail claro. UI replicates same validation. |
| Sync engine entra en loop al cambiar `AllowedChannels` runtime | Cambio dispara exactly-one sync action (insert si añade voice, remove si quita voice, no-op si voice ya en el estado correcto). Idempotente. |
| Verifier hosted service hace queries pesadas a Asterisk AMI bajo carga | Tick configurable (default 5 min, mínimo 1 min). Bypass disponible si AMI saturado. |
| Feature flag confusion: customer corre v2.5.x con flag ON sin querer | Default OFF en patch releases. ON solo en v2.6.0 release explícito. Documentar claramente en CHANGELOG. |
| Sticky cross-queue breaks expectation operador | Documentar explícitamente en operations runbook + en docs/manuales/smb/agentes-y-queues.md. Sticky honor solo si AllowedChannels del agente compatible con el canal actual. |
| Direct-to-agent bypass de membership es confuso para auditoría | Logging explícito en `OfferToAgentAsync` cuando bypass de membership ocurre. OTel event. |
| Wizard Day 1 queda inflexible al no exponer selector de canales | Por diseño: default `AllowedChannels=NULL` mantiene la simplicidad pedagógica del Day 1; granularidad fina en `/admin/agents/{id}/queues` (A.6). Es la elección correcta para el 90% del caso SMB. |

## References

- ADR-0026 (decisión arquitectónica): [`docs/decisions/0026-queue-membership-executive-routing.md`](../../decisions/0026-queue-membership-executive-routing.md)
- Living-docs plan: [`docs/plans/active/2026-05-27-living-docs-from-e2e-tests.md`](2026-05-27-living-docs-from-e2e-tests.md)
- Living-docs Day 1 manual (que surfaceó los bugs): [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md)
- SDK Pro Realtime: [`Verbara.Sdk.Pro.Realtime/`](../../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/)
- Pivot estratégico 2026-05-25 (ventana óptima): memory `session_20260525_phase0c_deferred_smb_pivot.md`
