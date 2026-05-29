# Plan: ADR-0026 Phase B — Membership executive gate (digital routing parity)

## Context

ADR-0026 ([`docs/decisions/0026-queue-membership-executive-routing.md`](../../../media/Data/Source/Verbara/Verbara.Platform/docs/decisions/0026-queue-membership-executive-routing.md)) identified que `queue_memberships` es ejecutivo para voz (vía Asterisk `app_queue` + sync ya implementado) pero **decorativo para canales digitales**. Hoy [`InMemoryAgentPresenceService.GetAvailableAgentsAsync`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Queues/Services/InMemoryAgentPresenceService.cs) — el corazón del routing digital — ignora `queue_memberships` y filtra solo por skills + state + capacity. Resultado: si un agente está en `queue_memberships` pero no tiene los skills, no recibe chats; si tiene los skills pero no la membership, sí recibe chats. Una configuración, dos comportamientos.

Phase A (shipped 2026-05-28) entregó el modelo channel-aware en la DB + el editor UI. Phase B cierra el lado ejecutivo: el routing digital empieza a respetar `queue_memberships.allowed_channels`. **Esto cierra la última deuda técnica de routing del producto SMB.**

**Ventana óptima**: el pivot 2026-05-25 deroga la disciplina de ≥6 semanas entre minors de Pro (no hay producción que proteger). El bump a Pro v2.6.0-pro se puede hacer ya. El track SMB Docker queda completo, sin deudas, listo para el primer cliente real.

**Resultado esperado**: `queue_memberships` se vuelve fuente ejecutiva única para TODOS los canales. Un mismo agente con `AllowedChannels=['WebChat']` no recibe llamadas de voz (Asterisk no le timbra — ya garantizado en Phase A) **ni chats** (Verbara routing lo excluye — nuevo en Phase B). Mismo modelo, mismo comportamiento.

## Decisión arquitectónica

### Insertion point: NO es un middleware del InboundRouter

ADR-0026 dijo "MembershipGateMiddleware en `InboundRouter`" — incorrecto. La cadena del InboundRouter ([`InboundRouter.cs`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Routing.Inbound/InboundRouter.cs)) solo enriquece `RouteResult` (QueueId, Priority, PreferredAgentId) o short-circuit; no toca candidate pool. El candidate pool sale de [`IAgentPresenceService.GetAvailableAgentsAsync`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Queues/Services/IAgentPresenceService.cs) llamado por [`RoundRobinAgentSelector`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs).

**Diseño:** nuevo servicio `IRoutingEligibilityService` que reemplaza la llamada directa al presence service desde el selector. Combina presence + membership + channel-filter + penalty sort en una sola operación. `IAgentPresenceService` queda intacto para otros consumidores (supervisor UI, status reporting).

```csharp
public interface IRoutingEligibilityService
{
    Task<IReadOnlyList<RoutableAgent>> GetEligibleAgentsAsync(
        TenantId tenantId, EntityId queueId, ChannelType channel, CancellationToken ct);
}

public sealed record RoutableAgent(Agent Agent, int Penalty);
```

Default impl `MembershipAwareRoutingEligibilityService`:
1. Carga `IAgentPresenceService.GetAvailableAgentsAsync(tenantId, queueId, channel)` → baseline pool (routable + capacity + skill-match)
2. Carga `IQueueMembershipStore.ListByQueueAsync(tenantId, queueId)` → memberships en esta queue
3. Filtro: agente debe ser member, `IsExcluded=false`, y `AllowedChannels IS NULL OR channel.ToString() IN AllowedChannels` (case-insensitive)
4. Sort ASC por `membership.Penalty` (0 = highest priority, alineado con Asterisk app_queue)
5. Retorna `IReadOnlyList<RoutableAgent>`

**Sin feature flag** (coherente con cierre de Phase A.6 + ADR-0027 — el pivot 2026-05-25 derogó la justificación de doble-modo). El comportamiento channel-aware ejecutivo es canónico desde el primer commit. Suite Api.Tests + lab smoke son la red de seguridad. Si surge regresión, rollback = `git revert` + rebuild (5-10 min) en vez de toggle de config.

### Excepciones encoded en la nueva capa

- **Sticky/last-agent** (`LastAgentMiddleware` setea `PreferredAgentId`): el selector consulta `IQueueMembershipStore.ListByAgentAsync(tenantId, preferredAgentId)` — si el agente preferido tiene membership en **alguna** queue activa del tenant (no necesariamente la queue actual) y cumple routable + capacity, retornarlo aunque no esté en el pool filtrado. CSAT prima sobre membership estricta.
- **Direct-to-agent** (`ConversationSwitchboard.OfferToAgentAsync` / `TransferToAgentAsync`): bypass estructural — estos endpoints NO pasan por el selector. Operador manual prima.
- **Outbound (Pro.Dialer)**: la membership es input para selección de campaña, pero el dispatch AMI Originate no es gated. Estructural — no toca esta capa.

### SDK Pro signature change

[`IRealtimeSyncService.AddQueueMemberAsync`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs) hoy es 6 args. Phase B agrega `IReadOnlyList<string>? allowedChannels = null` (penúltimo, antes de `CancellationToken`). El comportamiento en `RealtimeSyncEngine.AddQueueMemberAsync`:

```csharp
if (allowedChannels is not null && !allowedChannels.Any(c => c.Equals("voice", StringComparison.OrdinalIgnoreCase)))
{
    // Voice no incluido → asegurar que el row NO exista en queue_members
    await RemoveQueueMemberAsync(tenantId, queueName, agentId, ct);
    return;
}
// Voice incluido o null (=todos los canales) → upsert (comportamiento actual)
await _store.UpsertQueueMemberAsync(row, ct);
```

Esto **mueve el "voice gate" del caller al SDK** — los 4 call sites en `AdminEndpoints.cs:512` + `QueueMembersEndpoints.cs:{110,250,261}` simplifican: pasan `allowedChannels` directo en vez de hacer `IncludesVoice()` antes. Contract claro en el SDK boundary.

### RealtimeReconciliationService — bridge Verbara ↔ Asterisk

Nuevo `BackgroundService` en `Verbara.Platform.Api`. Cada 60s (configurable via `RealtimeOptions.ReconcilerIntervalSeconds` existente):
1. Para cada tenant activo: carga `queue_memberships` (Verbara DB) + lista Asterisk `queue_members` vía [`IRealtimeVerifier`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs) que ya existe.
2. Compute diff: rows en Verbara con voice-allowed pero no en Asterisk → `AddQueueMemberAsync`. Rows en Asterisk pero no en Verbara (o con voice removido) → `RemoveQueueMemberAsync`.
3. Emite metrics (`verbara.platform.realtime_drift_total`, label tenant + direction) + audit event si > umbral.

Espejo de pattern de [`ConversationTimeoutWorker`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Services/ConversationTimeoutWorker.cs): `PeriodicTimer` + ResiliencePolicy + heartbeat + `BackgroundServiceExceptionBehavior.StopHost` (ya configurado en `Program.cs:88`).

### Migration script `infer-memberships-from-skills.sh`

Bash idempotente espejando `scripts/quickstart-smb.sh` conventions. Para cada `(agent, queue)` donde `agent.skills ∩ queue.required_skills ≠ ∅` y NO existe row en `queue_memberships`, inserta `(Source=Skill, Penalty=penalty_from_proficiency, AllowedChannels=NULL)`. SQL one-shot via `docker exec verbara-postgres psql`. Idempotente vía `ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING`. Espeja la lógica de [`QueueMembershipService.ComputeEffectiveMembersAsync`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Services/QueueMembershipService.cs) en SQL puro.

Para SMB Docker happy path: típicamente 0 rows porque el wizard ya crea membership explícita en Phase A. El script es para upgrades de instalaciones que tengan agentes pre-Phase A con skills pero sin membership.

## Fases

### Phase B.1 — SDK Pro v2.6.0-pro (~1 día)

**B.1.1** — Extender [`IRealtimeSyncService`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs) con `allowedChannels: IReadOnlyList<string>? = null` antes de `CancellationToken`. **Default value preserva backward-compat** — old callers compilan sin cambios, comportamiento idéntico al actual.

**B.1.2** — Actualizar [`RealtimeSyncEngine.AddQueueMemberAsync`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/Engine/RealtimeSyncEngine.cs) con la lógica voice-gate: si `allowedChannels` populado y no incluye "voice" → delegate a `RemoveQueueMemberAsync` (idempotente — no-op si no existe). Else → upsert (comportamiento actual).

**B.1.3** — Tests SDK Pro (`tests/Verbara.Sdk.Pro.Realtime.Tests/`):
- `AddQueueMemberAsync_ShouldUpsert_WhenAllowedChannelsNull` (regression)
- `AddQueueMemberAsync_ShouldUpsert_WhenAllowedChannelsContainsVoice` (case-insensitive)
- `AddQueueMemberAsync_ShouldDeleteExistingRow_WhenAllowedChannelsExcludesVoice` (new contract)
- `AddQueueMemberAsync_ShouldBeNoOp_WhenAllowedChannelsExcludesVoiceAndRowDoesNotExist` (idempotency)

**B.1.4** — Bump version: `Verbara.Sdk.Pro.Realtime.csproj` → 2.6.0-pro + `Verbara.Sdk.Pro.Realtime.Storage.Postgres.csproj` → 2.6.0-pro. `dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/`. Verificar .nupkg presente en local feed.

**Criterio salida B.1**: SDK Pro suite green, .nupkg 2.6.0-pro en local feed, 0 warnings.

### Phase B.2 — Verbara.Platform consumer + eligibility service (~2 días)

**B.2.1** — Bump pin: `Verbara.Platform/Directory.Packages.props:62` → `Verbara.Sdk.Pro.Realtime Version="2.6.0-pro"` + Storage.Postgres. `rm -rf ~/.nuget/packages/verbara.sdk.pro.realtime*/` + `dotnet restore`.

**B.2.2** — Simplificar 4 call sites: `AdminEndpoints.cs:512`, `QueueMembersEndpoints.cs:{110,250,261}`. Pasar `membership.AllowedChannels` (Phase A field) directo al SDK; eliminar `IncludesVoice` gating local. El comportamiento end-to-end es idéntico — la lógica solo se movió de capa.

**B.2.3** — Nuevo `IRoutingEligibilityService` + `RoutableAgent` record en `src/Verbara.Platform.Routing.Inbound/Services/` (paquete existente).

**B.2.4** — Default impl `MembershipAwareRoutingEligibilityService`:
- Inyecta `IAgentPresenceService` + `IQueueMembershipStore`
- Carga baseline pool: `presenceService.GetAvailableAgentsAsync(tenantId, queueId, channel)` → agentes con routable + capacity + skill match
- Carga memberships de la queue: `membershipStore.ListByQueueAsync(tenantId, queueId)`
- Filter: agente debe (a) tener membership, (b) `IsExcluded=false`, (c) `AllowedChannels IS NULL OR AllowedChannels.Contains(channel.ToString(), OrdinalIgnoreCase)`
- Sort ASC por `membership.Penalty` (0 = highest)
- Retorna `IReadOnlyList<RoutableAgent>`

Channel comparison: `AllowedChannels.Any(c => c.Equals(channel.ToString(), OrdinalIgnoreCase))` — PascalCase format storage-side desde Phase A.6 (`"WebChat"`, `"Voice"`, etc., matching `ChannelType.ToString()`).

**B.2.5** — Refactor `RoundRobinAgentSelector`: inyecta `IRoutingEligibilityService` + `IQueueMembershipStore`. Cambia llamado de `GetAvailableAgentsAsync` a `GetEligibleAgentsAsync` (retorna `IReadOnlyList<RoutableAgent>`).
- Sticky exception: si `preferredAgentId.HasValue` y el agente preferido NO está en el eligible pool, consultar `membershipStore.ListByAgentAsync(tenantId, preferredAgentId)` — si tiene membership en alguna queue activa, retornarlo. CSAT prima.
- Round-robin within same penalty: ordenar primero por penalty ASC, después round-robin counter dentro de cada grupo de igual penalty.

**B.2.6** — DI registration en [`ServiceCollectionExtensions.AddInboundRouting`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Routing.Inbound/ServiceCollectionExtensions.cs): `services.AddSingleton<IRoutingEligibilityService, MembershipAwareRoutingEligibilityService>()`.

**Criterio salida B.2**: build 0 warnings. Suite Api.Tests existente puede tener algunos failures en tests que asumen routing sin membership (probablemente 0-5 tests — los tests legacy ya usan factories que setean tanto skills como membership "happy path"). Cada test fallido se actualiza para incluir membership explícita o se documenta como "test del comportamiento viejo, ya no aplica". Updates a tests aceptables como parte de B.2 (no constituyen regresión sino re-baseline).

### Phase B.3 — RealtimeReconciliationService + migration script (~1 día)

**B.3.1** — Nuevo `RealtimeReconciliationService : BackgroundService` en `src/Verbara.Platform.Api/Services/`. Espejo de `ConversationTimeoutWorker`. `PeriodicTimer` + `ResiliencePolicy` + `IServiceHeartbeat`. Configurable via `RealtimeOptions.ReconcilerIntervalSeconds` (default 60).

**B.3.2** — Lógica de reconciliación:
```
foreach tenant in ListActiveTenants:
  memberships = queue_membership_store.ListByTenant(tenant)
  asterisk_members = realtime_verifier.VerifyAllAsync(tenant)
  diff = compute_diff(memberships, asterisk_members)
  foreach add in diff.MissingInAsterisk:
    if includes_voice(add.AllowedChannels): realtime_sync.AddQueueMemberAsync(...)
  foreach remove in diff.MissingInVerbara:
    realtime_sync.RemoveQueueMemberAsync(...)
  emit_metric("verbara.platform.realtime_drift_total", tenant, count)
```

**B.3.3** — Register HostedService en `Program.cs` después de los otros workers: `builder.Services.AddHostedService<RealtimeReconciliationService>()`. Tras `AddVerbaraRealtime` (que ya existe).

**B.3.4** — Nuevo `scripts/infer-memberships-from-skills.sh` siguiendo conventions de [`quickstart-smb.sh`](../../../media/Data/Source/Verbara/Verbara.Platform/scripts/quickstart-smb.sh): header docstring, `set -euo pipefail`, env vars `POSTGRES_CONTAINER`/`POSTGRES_USER`/`POSTGRES_DB`, color helpers, idempotente. SQL one-shot:

```sql
INSERT INTO queue_memberships
  (tenant_id, queue_id, agent_id, penalty, source, is_excluded, created_at, allowed_channels)
SELECT
  a.tenant_id, q.queue_id, a.agent_id,
  GREATEST(0, 10 - COALESCE(avg_proficiency, 5))::int AS penalty,
  'skill' AS source, FALSE, NOW(), NULL
FROM agents a
JOIN queues q ON q.tenant_id = a.tenant_id AND q.is_active = true
JOIN LATERAL (
  SELECT AVG(s.proficiency) AS avg_proficiency
  FROM agent_skills s
  WHERE s.agent_id = a.agent_id
    AND s.skill_name = ANY(q.required_skills)
) sk ON true
WHERE array_length(q.required_skills, 1) > 0
  AND a.extension IS NOT NULL AND a.sip_password IS NOT NULL
  AND EXISTS (SELECT 1 FROM agent_skills s WHERE s.agent_id = a.agent_id AND s.skill_name = ANY(q.required_skills))
ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING;
```

(Schema exacto a verificar contra migración 001 + 025; ajustes mínimos durante implementación.)

**Criterio salida B.3**: HostedService boot clean, script smoke produce expected output en lab.

### Phase B.4 — Tests + activación + closure (~1-2 días)

**B.4.1** — Nuevos Api.Tests en `tests/Verbara.Platform.Api.Tests/MembershipGateRoutingTests.cs`:
- `Routing_ShouldExcludeAgent_WhenNoMembership`
- `Routing_ShouldExcludeAgent_WhenChannelNotInAllowedChannels` (WebChat-only agent + Voice conversation)
- `Routing_ShouldIncludeAgent_WhenAllowedChannelsContainsConversationChannel`
- `Routing_ShouldIncludeAgent_WhenAllowedChannelsIsNull` (all-channels default)
- `Routing_ShouldSortByPenalty_WhenMultipleEligibleAgents` (penalty=0 before penalty=5)
- `Routing_ShouldHonorPreferredAgent_WhenAgentHasMembershipInAnyQueue` (sticky bypass)
- `Routing_ShouldExcludePreferredAgent_WhenAgentHasNoMembershipsAtAll` (no sticky si zero memberships)
- `Routing_ShouldRespectIsExcluded_WhenExplicitlyExcluded`

**B.4.2** — Tests para `RealtimeReconciliationService`:
- `Reconciliation_ShouldAddMember_WhenVerbaraHasMembershipMissingFromAsterisk`
- `Reconciliation_ShouldRemoveMember_WhenAsteriskHasMemberMissingFromVerbara`
- `Reconciliation_ShouldSkipNonVoiceMemberships_WhenAddingToAsterisk`

**B.4.3** — Update [`docs/decisions/0026-queue-membership-executive-routing.md`](../../../media/Data/Source/Verbara/Verbara.Platform/docs/decisions/0026-queue-membership-executive-routing.md) Implementation status section: Phase B → SHIPPED. Permanent artifacts list. Plan move `docs/plans/active/2026-05-29-membership-executive-gate.md` → `docs/plans/completed/`.

**B.4.4** — Update [`docs/manuales/smb/03-setup-inicial.md`](../../../media/Data/Source/Verbara/Verbara.Platform/docs/manuales/smb/03-setup-inicial.md) + [`04-canal-webchat.md`](../../../media/Data/Source/Verbara/Verbara.Platform/docs/manuales/smb/04-canal-webchat.md) — agregar sección "Routing executive: un agente sin membership NO recibe conversaciones (digital + voz)" + apuntar al editor channel-aware de Phase A.6. Cierra C.2 deferred de ADR-0027 también (manuales actualizados de una sola pasada).

**Criterio salida B.4**: suite Api.Tests pasa con ~1050-1060 tests verde (1013 baseline +/- ajustes de B.2 + ~12 nuevos de B.4). Manuales SMB documentan el contrato ejecutivo unificado.

## Critical files

**Nuevos**:
- `src/Verbara.Platform.Routing.Inbound/Services/IRoutingEligibilityService.cs`
- `src/Verbara.Platform.Routing.Inbound/Services/MembershipAwareRoutingEligibilityService.cs`
- `src/Verbara.Platform.Routing.Inbound/Services/RoutableAgent.cs`
- `src/Verbara.Platform.Api/Services/RealtimeReconciliationService.cs`
- `tests/Verbara.Platform.Api.Tests/MembershipGateRoutingTests.cs`
- `scripts/infer-memberships-from-skills.sh`
- `docs/plans/active/2026-05-29-membership-executive-gate.md` (mirror del plan file)

**Modificados (SDK Pro)**:
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs` — agrega `allowedChannels` param
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/Engine/RealtimeSyncEngine.cs` — voice-gate logic
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/Verbara.Sdk.Pro.Realtime.csproj` — version 2.6.0-pro
- `Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime.Storage.Postgres/Verbara.Sdk.Pro.Realtime.Storage.Postgres.csproj` — version 2.6.0-pro
- tests en `Verbara.Sdk.Pro/tests/Verbara.Sdk.Pro.Realtime.Tests/` para los 4 nuevos casos

**Modificados (Platform)**:
- `Verbara.Platform/Directory.Packages.props` — pin Pro Realtime 2.5.1-pro → 2.6.0-pro
- `src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs` — usa `IRoutingEligibilityService` + sticky bypass + penalty sort
- `src/Verbara.Platform.Routing.Inbound/ServiceCollectionExtensions.cs` — register `IRoutingEligibilityService`
- `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs:505-514` — simplifica IncludesVoice gating, pasa allowedChannels al SDK
- `src/Verbara.Platform.Api/Endpoints/QueueMembersEndpoints.cs:{105-113, 245-264}` — idem
- `src/Verbara.Platform.Api/Program.cs` — `AddHostedService<RealtimeReconciliationService>()`
- Tests legacy que asuman routing-sin-membership (probablemente 0-5 archivos identificados durante B.2) — update para setear membership explícita en setup

**Re-usar (no modificar)**:
- [`IAgentPresenceService` + `InMemoryAgentPresenceService`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Queues/Services/) — intactos, otros consumidores siguen llamándolos
- [`IQueueMembershipStore` + `ListByQueueAsync` + `ListByAgentAsync`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Queues/IQueueMembershipStore.cs) — Phase A.6 ya entregó lo necesario
- [`IRealtimeVerifier.VerifyAllAsync`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs) — input al ReconciliationService
- [`InboundRouter`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Routing.Inbound/InboundRouter.cs) chain — sin cambios (la gate no es middleware del router)
- [`QueueMembershipService`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Services/QueueMembershipService.cs) — referencia de skill-intersection logic para el script SQL

## Verification

**B.1 (SDK Pro)**:
```bash
cd /media/Data/Source/Verbara/Verbara.Sdk.Pro
dotnet test                                              # Realtime.Tests passes
dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
ls /media/Data/Source/Verbara/local-nuget-feed/ | grep Realtime  # 2.6.0-pro present
```

**B.2 (Platform restore + build)**:
```bash
cd /media/Data/Source/Verbara/Verbara.Platform
rm -rf ~/.nuget/packages/verbara.sdk.pro.realtime*/
dotnet restore
dotnet build Verbara.Platform.slnx                       # 0 warnings, 0 errors
dotnet test tests/Verbara.Platform.Api.Tests
# Expected: la mayoría 1013/1013. Si fallan algunos, son tests que asumen
# routing-sin-membership — actualizar en B.2.5 con setup explícito de membership.
```

**B.3 (script smoke)**:
```bash
bash scripts/infer-memberships-from-skills.sh
# Expected SMB Docker happy path: "0 memberships inferred (all already in store)."
# Expected legacy upgrade with 5 skill-matched orphans: "5 memberships inferred."
```

**B.4 (tests del gate + closure)**:
```bash
dotnet test tests/Verbara.Platform.Api.Tests --filter "FullyQualifiedName~MembershipGateRouting"
# Expected: ~8 new MembershipGate tests pass
dotnet test tests/Verbara.Platform.Api.Tests --filter "FullyQualifiedName~Reconciliation"
# Expected: ~3 new Reconciliation tests pass

# Full suite
dotnet test tests/Verbara.Platform.Api.Tests
# Expected: ~1024/1024 (1013 baseline +/- B.2 adjustments + 11 nuevos B.4)
```

**Manual smoke** (lab):
```bash
# Crear agente sin membership en ninguna queue
curl -X POST .../admin/agents -d '{"userId":"u1","displayName":"Orphan"}'
# Crear conversación WebChat → Atención General queue
# Verificar: el agente no aparece en GET /api/v1/queues/{id}/members + la conversación queda Queued sin offer
# Agregar membership: POST .../queues/{id}/members {agentId:u1, allowedChannels:["WebChat"]}
# Verificar: la conversación se ofrece a Orphan
# Cambiar a allowedChannels:["Voice"]
# Verificar: el agente vuelve a no recibir el chat (digital filtering)
```

**Living-docs regression**: el spec `02-agent-channel-routing` (Phase A.6.7) ya cubre el end-to-end UI. Re-run después de Phase B activado debe pasar idéntico — el badge UI ya refleja el contrato, ahora el backend lo enforce además de mostrarlo.

## Riesgos + mitigaciones

| Riesgo | Mitigación |
|---|---|
| SDK Pro v2.6.0-pro bump rompe restore en Platform | Cambio mínimo signature con default param value `null` → backward-compat con 6-arg callers. 4 call sites simplifican opcionalmente. |
| Sin flag → algún test legacy del routing rompe | B.2.5 incluye explícitamente la actualización de tests legacy. Estimado 0-5 tests (los factories Api.Tests ya setean membership "happy path"). Si > 10, escalo: el plan asume scope acotado. |
| Sticky bypass en selector permite cualquier agente que tenga membership en cualquier queue → demasiado permisivo | Documentado como decisión consciente: CSAT (retornar a agente conocido) prima sobre estricta membership por queue. Test explícito cubre que el bypass NO aplica para agentes con cero memberships. |
| RealtimeReconciliationService double-write con SDK Pro reconciler interno | Verificar: SDK Pro reconciler trabaja sobre Postgres Realtime store interno (no consulta Verbara `queue_memberships`). El nuevo service es bridge externo. Sin solape. |
| Penalty sort cambia el orden de round-robin → cambio observable de comportamiento | Documentar en manuales + ADR cierre. Operadores que asignaron mismo penalty (default 0) verán comportamiento idéntico (round-robin within same penalty preserved). |
| Migration script INSERT genera contención en upgrades grandes | Script corre dentro de transacción única + `ON CONFLICT DO NOTHING` — operación idempotente, segura para re-run. Tipical SMB tiene < 100 agentes < 10 queues = < 1000 rows máximo. |
| Channel comparison case sensitivity | Phase A.6 almacena AllowedChannels en PascalCase ("WebChat", "Voice"). `Conversation.Channel.ToString()` retorna mismo casing. Comparación `OrdinalIgnoreCase` defensive. Tests cubren ambos casings. |

## Out of scope / deferred

- **UI changes** en `Verbara.Platform.Web` — el editor de Phase A.6 ya muestra el contrato correcto. No hay UX nuevo. La única consideración: el badge "Digital only" ahora es realmente enforced (no solo descriptive).
- **Predicate engine completo** (B7 del ADR-0026 análisis original) — válido evolución futura, hoy over-engineered.
- **ABR proficiency** (B9) — requiere modelo de skill-level que aún no existe en producción.
- **Deprecar `Source=Manual` vs `Source=Skill` como distinción semántica** — mantener por ahora como audit metadata. Cambio cosmético, no urgente.
- **Multi-region/multi-cluster reconciliation** — RealtimeReconciliationService asume single-region. Si llegara K8s multi-region, refactor a leader-elected reconciler (mismo patrón que Phase A.5 ya usa).
- **Feature flag para activación gradual** — descartado conscientemente. Phase A.6 + ADR-0027 también shippearon sin flag. Coherencia y simplicidad ganan. Rollback = `git revert` (5-10 min, aceptable sin clientes).
