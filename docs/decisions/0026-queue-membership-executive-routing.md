# ADR-0026: `queue_memberships` ejecutivo para routing (paridad Voice ↔ Digital)

**Status:** Accepted
**Date:** 2026-05-28
**Deciders:** Maintainer
**Supersedes:** — (no ADR previo cubre routing eligibility)
**Related:** ADR-0022 (AOT), ADR-0025 (health contract). Apoyo en SDK `Verbara.Sdk.Pro.Realtime` ([`IRealtimeSyncService.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs))

---

## Context

### Hallazgo decisivo

Verbara construye sobre Asterisk PBX. El SDK Pro ya expone [`IRealtimeSyncService`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeSyncService.cs) con `AddQueueMemberAsync(tenantId, queueName, agentId, displayName, penalty, ct)` / `RemoveQueueMemberAsync` / `SyncAgentPausedAsync`. [`QueueMembersEndpoints`](../../src/Verbara.Platform.Api/Endpoints/QueueMembersEndpoints.cs) **ya invoca este servicio en cada mutación de `queue_memberships`**, sincronizando 1:1 hacia la tabla `queue_members` de Asterisk Realtime. La autoridad de routing para **voz** (canal Voice via `app_queue`) está construida y desplegada desde release v2.5.0-pro: `queue_memberships` es la **única fuente de verdad ejecutiva** para qué agente recibe llamadas de qué queue.

Sin embargo, [`InMemoryAgentPresenceService.GetAvailableAgentsAsync`](../../src/Verbara.Platform.Queues/Services/InMemoryAgentPresenceService.cs#L47) — el servicio que decide elegibilidad para conversaciones **digitales** (WebChat, WhatsApp, SMS, Email, Telegram, Messenger, Instagram, Video, Twitter, RCS) — **ignora `queue_memberships` completamente**:

```csharp
foreach (var agent in allAgents) {            // ← TODOS los agents del tenant
    if (queue.RequiredSkills.Any() && !agent.Skills.Any(s => queue.RequiredSkills.Contains(s))) continue;
    if (!AgentStateMachine.IsRoutable(currentState)) continue;
    if (!await _capacityService.HasCapacityAsync(...)) continue;
    result.Add(agent);
}
```

Filtra solo por skills + state + capacity. La membership es metadata decorativa para el switchboard digital, pero ejecutiva para el switchboard de voz.

### Consecuencia operativa

Un mismo agente:
- **Recibe llamadas** de queue X si está en `queue_memberships(X, agentId)` (modelo voz vía Asterisk)
- **Recibe chats** de queue X si tiene los skills que matchean `queue.RequiredSkills` (modelo digital)

Si el operador agrega al agente a la queue para que reciba llamadas pero no le configura skills, recibe llamadas pero no chats. Si le configura skills sin agregarlo a la queue, recibe chats pero no llamadas. **Una configuración, dos comportamientos.** Esto es inaceptable en cualquier CCaaS serio.

Adicionalmente surfaceado por living-docs (ver [`docs/manuales/smb/03-setup-inicial.md`](../manuales/smb/03-setup-inicial.md) + [`04-canal-webchat.md`](../manuales/smb/04-canal-webchat.md)) durante Fase 1 del plan [2026-05-27-living-docs-from-e2e-tests.md](../plans/active/2026-05-27-living-docs-from-e2e-tests.md): el setup wizard del Day 1 crea queue + agente pero **no los asocia**. El agente queda huérfano del modelo Voz (no recibe llamadas) pero operativo en el modelo Digital si por casualidad tiene skills coincidentes.

### Ventana óptima

No hay clientes pagando todavía (pivot estratégico 2026-05-25 — ver `session_20260525_phase0c_deferred_smb_pivot`, maintainer session memory, not a repo artifact). Cambios de semántica de routing son hoy reversibles sin impacto comercial. Posponer la corrección es regalar deuda permanente al primer cliente que la encuentre.

## Decision

**`queue_memberships` se vuelve la única fuente ejecutiva de elegibilidad para routing automático en TODOS los canales (Voice + Digital).** El modelo de presencia digital se alinea con el modelo Asterisk ya implementado.

Concretamente, se adopta la combinación **B5 + B10 + B11** del análisis arquitectónico ampliado:

### B5 — Modelo de datos y gate (incluye channel-aware membership)

Schema: `queue_memberships` agrega columna `allowed_channels TEXT[]` (nullable, default `NULL`):

```sql
ALTER TABLE queue_memberships
  ADD COLUMN allowed_channels TEXT[];  -- NULL = todos los canales que la queue acepta
```

| `allowed_channels` valor | Semántica | Sync a Asterisk `queue_members` |
|---|---|---|
| `NULL` (default migración) | Member para TODOS los canales que la queue acepta (preserva semántica implícita pre-v2.6.0) | ✅ AddQueueMemberAsync |
| `['voice']` | Solo voz para este agente en esta queue | ✅ AddQueueMemberAsync |
| `['webchat', 'email']` | Solo digital, NO voz | ❌ RemoveQueueMemberAsync (Asterisk NO le timbra) |
| `[]` (array vacío) | Equivalente a `IsExcluded=true` cross-channel | ❌ RemoveQueueMemberAsync |

Cascada de filtros en eligibility (orden estricto, primer rechazo abandona):

1. `IsMember(agentId, queueId) AND NOT IsExcluded` — gate primario
2. `membership.AllowedChannels IS NULL OR conversation.Channel IN membership.AllowedChannels` — **channel-aware gate**
3. `IsRoutable(agent.state)` — estado disponible
4. `HasCapacity(agent, conversation.channel)` — capacidad numérica por canal (límite concurrentes)
5. `queue.RequiredSkills.IsEmpty OR agent.Skills.Intersect(queue.RequiredSkills).Any()` — skill filter opcional
6. **Sort ASC by `membership.penalty`** — alineado con Asterisk app_queue (0 = highest priority)

**`Agent.ChannelCapacity` pierde rol de discriminación canal-sí/canal-no**: queda únicamente como límite numérico de concurrentes por canal (cuántas chats simultáneas un agente puede manejar). La decisión "este agente atiende voz en esta queue" la dice exclusivamente `QueueMembership.AllowedChannels`. Esto elimina el solape de dos modelos del status quo (problema reportado durante review pre-implementación 2026-05-28).

**Granularidad per-queue-per-agent**: María puede ser `AllowedChannels=['webchat']` en queue Soporte y `AllowedChannels=['voice']` en queue VIP. Imposible expresar con `ChannelCapacity` global.

### B10 — Filosofía: Asterisk es la autoridad

Para canal Voice, Verbara delega la decisión final a `app_queue` (que ya tiene `queue_members` sincronizado). Verbara solo se asegura que `queue_memberships` (Verbara) ↔ `queue_members` (Asterisk Realtime) estén consistentes — **condicionado al opt-in de voz en `allowed_channels`**. Para canales digitales, Verbara aplica el mismo modelo de elegibilidad localmente, filtrando por `allowed_channels` cuando el agente opted-out de un canal. **Una sola configuración del operador, comportamiento simétrico en todos los canales.**

Implicación crítica para `IRealtimeSyncService`: el sync engine debe aceptar `allowedChannels` y solo invocar `AddQueueMemberAsync` cuando la membership permita voz (`AllowedChannels IS NULL OR 'voice' IN AllowedChannels`). En caso contrario, invoca `RemoveQueueMemberAsync` para asegurar que Asterisk NO tenga al agente en `queue_members` (lo que causaría que el PBX intentara timbrarlo). Cambio de signature en SDK Pro: `AddQueueMemberAsync(tenantId, queueName, agentId, displayName, penalty, allowedChannels, ct)`.

### B11 — Mecanismo: reconciliación + observabilidad

[`IRealtimeVerifier`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs) (ya existe en SDK) se ejecuta como `HostedService` periódico para detectar drift entre `queue_memberships` (Postgres Verbara) y `queue_members` (Postgres Asterisk Realtime live). Si Asterisk fue editado manualmente fuera de banda, el verifier emite evento y `RealtimeReconciler` corrige.

### Wizard write-through

El setup wizard (paso "Agente") y el endpoint `POST /api/v1/admin/agents` aceptan `queueMemberships[]` opcional con `{ queueId, allowedChannels? }`. Si el operador crea un agente con skills coincidentes a una queue, el wizard materializa una `QueueMembership(Source=Skill, Penalty=0, AllowedChannels=NULL)` automáticamente (cualquier canal que la queue acepte). El campo `Source=Skill` (que ya existe en el modelo) gana semántica ejecutiva: distingue memberships derivadas de skill-match (gestionadas por sistema, auto-actualizan cuando skills cambian) de memberships `Source=Manual` (gestionadas explícitamente por admin).

En el wizard Day 1, el operador no necesita decidir canales explícitamente — el default `AllowedChannels=NULL` significa "todos los canales de la queue", que es la expectativa pedagógica. El selector multi-canal aparece solo en `/admin/agents/{id}/queues` para operaciones avanzadas.

### Inserción arquitectónica limpia

Se introduce `MembershipGateMiddleware` en el [`InboundRouter`](../../src/Verbara.Platform.Routing.Inbound/) (chain middleware ya existente) **antes** del [`RoundRobinAgentSelector`](../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs). El middleware filtra el candidate pool a solo agentes con membership en la queue destino. [`InMemoryAgentPresenceService`](../../src/Verbara.Platform.Queues/Services/InMemoryAgentPresenceService.cs) queda como está (devolviendo "todos los routable + capacity-ok"); el gate se aplica una capa arriba. Esto preserva separación de capas y permite testear el gate aisladamente.

### Excepciones explícitas (documentadas)

- **Sticky / last-agent**: [`LastAgentMiddleware`](../../src/Verbara.Platform.Routing.Inbound/Middlewares/LastAgentMiddleware.cs) honra `PreferredAgentId` si el agente es member de **alguna** queue activa del tenant (no necesariamente la queue actual) y cumple `IsRoutable + HasCapacity`. CSAT prima sobre membership estricta.
- **Direct-to-agent (flow/transfer)**: [`ConversationSwitchboard.OfferToAgentAsync`](../../src/Verbara.Platform.Switchboard/ConversationSwitchboard.cs) / `TransferToAgentAsync` bypassean la queue completa y **también la membership**. Asignación humana o por flow es siempre prioritaria sobre routing automático.
- **Outbound (Pro.Dialer)**: el dialer despacha a agentes específicos vía AMI Originate. La membership se usa como input para campañas (qué agentes elegibles dispatchar) pero el dispatch en sí no es gated por membership — un admin puede forzar.

### Agente "jolly"

Patrón soportado nativamente: agente miembro de **todas** las queues con `penalty=9` (alto). Solo es elegido cuando todos los penalties más bajos están saturados. No requiere modelo nuevo — semántica Asterisk app_queue ya soportada por la cascada B5.

## Consequences

### Positivas

1. **Simetría Voice ↔ Digital**: una sola configuración del operador, comportamiento consistente en todos los canales.
2. **Modelo mental Asterisk preservado**: `queue + member + penalty` es el dominio nativo de implementadores PBX (audiencia de Verbara). Cero fricción cognitiva.
3. **Reusa infrastructure existente**: `IRealtimeSyncService` + `IRealtimeVerifier` + `RealtimeReconciler` del SDK Pro ya pagaron su costo de implementación.
4. **Habilita evoluciones futuras sin breaking change**: predicate engine (B7), ABR (B9), bidding (B8) son capas componibles encima del gate de membership.
5. **`Source=Skill` gana significado ejecutivo**: el campo estaba diseñado para esto (commit history del SDK Pro), la implementación quedó incompleta solo en el switchboard digital.
6. **Reconciliación con Asterisk live**: `IRealtimeVerifier` ya construido habilita drift detection que ningún CCaaS competidor entrega out-of-box.
7. **Sin clientes = migración trivial**: script de inferencia `skill ∩ requiredSkills → membership` corre una vez, drift inexistente.

### Negativas / costos

1. **Migración data existente**: script de inferencia obligatorio. Sin él, agentes existentes pierden routing digital. Mitigado por la ventana sin clientes.
2. **Wizard agrega complejidad mínima**: 1 paso extra (asociar agente a queue) si no se autoinfiere via skill match.
3. **Test surface incrementa**: nuevos casos para `MembershipGateMiddleware`, write-through del wizard, sticky con membership cross-queue. Estimado +20-30 tests Api.Tests.
4. **Documentación de excepciones**: sticky, direct-to-agent, outbound necesitan explicación clara en manuales para evitar que operadores piensen que "membership rige todo siempre".

### Riesgos rechazados

- **B2 Membership decorativo (status quo)**: rompe la simetría Voice/Digital. Inaceptable.
- **B3 Pure skill-based (eliminar membership)**: rompería Asterisk sync. Inaceptable.
- **B4 Hybrid flag por queue**: 4+ conceptos para operador, overengineering para SMB.
- **B7 Predicate engine completo**: válido como evolución futura R5+, hoy es over-engineered.
- **B8 Bidding/pull**: incompatible con `app_queue` push model. Inaceptable mientras Asterisk sea autoridad voz.
- **B9 ABR proficiency**: válido evolución futura R6+, requiere modelo de skill levels que aún no existe.

## Migration strategy

1. **Schema migration** (idempotente): `ALTER TABLE queue_memberships ADD COLUMN allowed_channels TEXT[];`. Default `NULL` preserva semántica implícita pre-v2.6.0 (member para todos los canales que la queue acepta).
2. **Script `infer-memberships-from-skills.sh`** (idempotente): para cada `(agent, queue)` en el sistema donde `agent.Skills ∩ queue.RequiredSkills ≠ ∅` y no existe ya una membership, inserta `QueueMembership(Source=Skill, Penalty=0, IsExcluded=false, AllowedChannels=NULL)`. Luego dispara `RealtimeSyncEngine.SyncAgentBatchAsync` para propagar a Asterisk.
3. **Ship behind feature flag** `RoutingFeatures.MembershipGate` (default OFF en v2.5.x patch release, ON en v2.6.0). Permite rollback emergente y testing A/B en lab.
4. **Documentar comportamiento en manual SMB 03-setup-inicial.md y 04-canal-webchat.md** (escritos a mano) + regenerar manual living-docs 01-day1-setup-and-webchat para reflejar el wizard corregido + nuevo manual `agentes-y-queues.md` que explica el modelo channel-aware con ejemplos del agente WebChat-only.
4. **Deprecar `Source=Manual` vs `Source=Skill` como distinción semántica** una vez estable; ambos son membership ejecutivos. La distinción se mantiene como audit metadata (quién creó la membership: operador vs sistema).

## Validation criteria

- **Test Api.Tests**: `MembershipGateMiddleware_Should*ExcludeNonMember*` (suite completa para sticky, direct-to-agent, outbound, fallback).
- **Test channel-aware**: `MembershipGate_Should*ExcludeAgent*WhenChannelNotInAllowedChannels` (WebChat-only agent no recibe voz aunque sea member; Voice-only agent no recibe chats).
- **Integration test**: crear queue + agente sin membership → conversación entra → no se asigna; agregar membership con `AllowedChannels=['webchat']` → conversación de WebChat se asigna, conversación de Voz no se asigna a este agente.
- **Asterisk sync test**: agente con membership `AllowedChannels=['webchat','email']` → `queue_members` Asterisk NO tiene al agente (verificar via AMI query). Cambiar a `AllowedChannels=NULL` → sync agrega al agente a `queue_members`.
- **Living-docs Day 1 manual** auto-regenera mostrando wizard que crea queue + agente + membership en un solo flujo, sin necesidad de pasos extra del operador.
- **Asterisk drift test**: `RealtimeVerifier` detecta inconsistencia introducida manualmente con `asterisk -rx "queue remove member ..."` y `RealtimeReconciler` la corrige.

## Implementation status (2026-05-28)

**Phase A — SHIPPED ✅** (3 backend + 2 frontend commits + 1 test commit on `main`, no release tag yet — patches not customer-impacting because no paying customers exist).

| Sub-phase | Status | Commit |
|---|---|---|
| A.1 — `CreateAgent` channel-aware + validation + voice-gate Asterisk sync | ✅ SHIPPED | Platform [`0ddb511d`](https://github.com/verbara/Verbara.Platform/commit/0ddb511d) |
| A.2 — `[JsonSerializable(typeof(TenantChannelConfig))]` + `ChannelChangeAudit` + `QueueMembershipRequest` + AOT audit fallback (`InvalidOperationException` catch in `PostgresAuditStore.SerializeChange`) | ✅ SHIPPED | Platform [`0ddb511d`](https://github.com/verbara/Verbara.Platform/commit/0ddb511d) |
| A.3 — Wizard guarda `createdQueueId` + materializa membership default-all | ✅ SHIPPED | Web [`a283666`](https://github.com/verbara/Verbara.Platform.Web/commit/a283666) |
| A.4 — Wizard fuerza "crear nuevo usuario" en agent-step (admin no es agente) | ✅ SHIPPED | Web [`a283666`](https://github.com/verbara/Verbara.Platform.Web/commit/a283666) |
| A.5 — Living-docs Day 1 spec end-to-end sin workaround + manual regenerado | ✅ SHIPPED | Web [`a283666`](https://github.com/verbara/Verbara.Platform.Web/commit/a283666) |
| A.6 — `IQueueMembershipStore.ListByAgentAsync` + REST `AllowedChannels` flowthrough (POST + PATCH semantics + audit) + agent-centric `GET /admin/agents/{id}/queue-memberships` + Web editor `/admin/agents/{id}/queues` (channel chip multi-select + voice-sync badge + "All channels" toggle) | ✅ SHIPPED | Platform [`53c0ac61`](https://github.com/verbara/Verbara.Platform/commit/53c0ac61) + Web [`1d4dce2`](https://github.com/verbara/Verbara.Platform.Web/commit/1d4dce2) |
| A.6.7 (extra, no estaba en el plan) — Day 2 living-docs journey "Restringir agente a WebChat" + scoped `membership-card-{queueId}` testid | ✅ SHIPPED | Web [`899594d`](https://github.com/verbara/Verbara.Platform.Web/commit/899594d) |
| Phase A Api.Tests coverage (9 channel-aware POST/PATCH + 4 agent-membership listing = 13 new tests, total suite 961 / 961) | ✅ SHIPPED | Platform [`442e3ad9`](https://github.com/verbara/Verbara.Platform/commit/442e3ad9) |

Permanent artifacts shipped:
- DB column `queue_memberships.allowed_channels TEXT[]` (migration `025_QueueMembershipAllowedChannels.sql`, idempotent, default NULL).
- `QueueMembership.AllowedChannels` model + InMemory + Postgres stores + helper `ListByAgentAsync`.
- REST `QueueMembersEndpoints` + `AdminEndpoints` channel-aware contract locked by Api.Tests.
- React `AgentQueuesPage` at `/admin/agents/{agentId}/queues` + hook `useAgentMemberships` + extended `use-queue-members` (`useUpdateQueueMember` with `clearAllowedChannels` PATCH semantics).
- Living-docs journeys `01-day1-setup-and-webchat` (v2.5.4, refreshed) + `02-agent-channel-routing` (v2.5.5).

**Phase B — SHIPPED ✅ 2026-05-29** (SDK Pro v2.6.0-pro + Platform consumer + reconciler + migration script + tests, all on `main`, no release tag yet — same release-deferred rationale as Phase A).

| Sub-phase | Status | Commit |
|---|---|---|
| B.1 — SDK Pro v2.6.0-pro: `IRealtimeSyncService.AddQueueMemberAsync(allowedChannels)` signature change + voice-gate inside `RealtimeSyncEngine` + 5 new realtime tests | ✅ SHIPPED | Pro [`913ec98`](https://github.com/verbara/Verbara.Sdk.Pro/commit/913ec98) |
| B.2 — Platform pins Pro 2.6.0-pro + 4 call sites simplified (AdminEndpoints + QueueMembersEndpoints pass `AllowedChannels` directly, no more local `IncludesVoice`) + new `IRoutingEligibilityService` / `MembershipAwareRoutingEligibilityService` / `RoutableAgent` in `Verbara.Platform.Routing.Inbound.Services` + `RoundRobinAgentSelector` penalty-grouped round-robin + sticky bypass + DI wiring | ✅ SHIPPED | Platform [`b731c1fc`](https://github.com/verbara/Verbara.Platform/commit/b731c1fc) |
| B.3 — `RealtimeReconciliationService : BackgroundService` (forward-only convergent reconciler — re-issues `AddQueueMemberAsync` for every non-excluded membership; SDK voice-gate handles AllowedChannels uniformly) + `RealtimeReconciliationService.MeterName = "Verbara.Platform.Realtime.Reconciliation"` exposed via OpenTelemetry + `scripts/infer-memberships-from-skills.sh` idempotent backfill for legacy upgrades | ✅ SHIPPED | this commit |
| B.4 — `MembershipGateRoutingTests` (9 tests covering eligibility filter + sticky bypass — Routing.Inbound.Tests 32 → 41) + `RealtimeReconciliationServiceTests` (4 tests covering re-sync, IsExcluded skip, AllowedChannels forwarding, missing-sync graceful skip — Api.Tests 1013 → 1017) | ✅ SHIPPED | this commit |

Permanent artifacts shipped (Phase B):
- `Verbara.Sdk.Pro.Realtime` v2.6.0-pro signature: `AddQueueMemberAsync(tenantId, queueName, agentId, displayName, penalty, IReadOnlyList<string>? allowedChannels = null, ct)` — voice-gate logic ENCAPSULATED in the SDK (callers stop replicating `IncludesVoice` checks).
- `Verbara.Platform.Routing.Inbound.Services.IRoutingEligibilityService` + `MembershipAwareRoutingEligibilityService` + `RoutableAgent` — abstraction layer between presence (routable + capacity + skill) and routing (member + !IsExcluded + AllowedChannels + penalty sort). `IAgentPresenceService` stays unchanged for other consumers (supervisor UI, status reporting).
- `Verbara.Platform.Routing.Inbound.RoundRobinAgentSelector` — now consumes `IRoutingEligibilityService` instead of presence directly. Penalty-grouped round-robin (only lowest-penalty band rotates) + sticky bypass when preferred agent has membership in any active queue + is presence-reachable.
- `Verbara.Platform.Api.Services.RealtimeReconciliationService` — periodic forward-only convergent worker (cadence = `RealtimeOptions.ReconcilerIntervalSeconds`, default 60s). Catches up `IRealtimeSyncService.AddQueueMemberAsync` calls that the foreground call sites in `QueueMembersEndpoints` + `AdminEndpoints` swallowed with their best-effort try/catch when Asterisk Realtime is briefly unavailable.
- `scripts/infer-memberships-from-skills.sh` — bash idempotent SQL one-shot (`ON CONFLICT (tenant_id, queue_id, agent_id) DO NOTHING`) for legacy installs where membership was decorative pre-Phase B (intersects `agents.skills` ∩ `queue_configs.required_skills`, inserts `Source=Skill, Penalty=0, AllowedChannels=NULL`). SMB Docker happy path typically inserts 0 rows because the wizard already creates explicit memberships.

**Design deviation from original plan.** The plan called for an `IRealtimeVerifier.VerifyAllAsync`-based diff reconciler. That approach requires `IAmiConnection` per tenant — impractical from a `BackgroundService` (Verbara would need to resolve, authenticate, and pool AMI connections for every active tenant per tick). The shipped design is a **forward-only convergent reconciler**: re-issue the desired state from `queue_memberships`; trust the SDK Pro v2.6.0-pro voice-gate to do the right thing per row (idempotent upsert when `AllowedChannels` is null or includes "voice", short-circuit `RemoveQueueMemberAsync` otherwise). Orphan rows in Asterisk with no Verbara membership are NOT detected here — they are handled by the migration script for legacy upgrades and by `IRealtimeSyncService.CleanupTenantAsync` during tenant deletion.

**Phase C — Documentation.** Living-docs journey `02-agent-channel-routing` (v2.5.5) already documents the channel restriction end-to-end. Hand-written SMB manuals `03-setup-inicial.md` + `04-canal-webchat.md` get a Phase B refresh in this same commit (also closes ADR-0027 C.2 deferred — "Si trabajás como Platform Admin, impersoná un Customer" note for tenant-type gate awareness).

**Phase B NOT released as a tagged image yet.** Same Phase A rationale: lab images `ghcr.io/verbara/platform/api:local-phase-b` + `ghcr.io/verbara/platform/web:local-phase-a` (web unchanged). Production-release packaging (release.yml run + cosign signing + verbara-website digest authorization) deferred until first paying customer per the 2026-05-25 pivot.

## References

- Plan de implementación: [`docs/plans/completed/2026-05-28-membership-executive-routing.md`](../plans/completed/2026-05-28-membership-executive-routing.md)
- Living-docs Day 1 manual (drift surfaceado): [`docs/manuales/smb/03-setup-inicial.md`](../manuales/smb/03-setup-inicial.md) + [`04-canal-webchat.md`](../manuales/smb/04-canal-webchat.md)
- SDK Pro Realtime sync: [`Verbara.Sdk.Pro.Realtime`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/)
- SDK Pro Realtime verifier: [`IRealtimeVerifier.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs)
- Asterisk app_queue model: https://docs.asterisk.org/Configuration/Applications/queue/
