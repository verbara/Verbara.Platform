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

Adicionalmente surfaceado por living-docs (ver [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md)) durante Fase 1 del plan [2026-05-27-living-docs-from-e2e-tests.md](../plans/active/2026-05-27-living-docs-from-e2e-tests.md): el setup wizard del Day 1 crea queue + agente pero **no los asocia**. El agente queda huérfano del modelo Voz (no recibe llamadas) pero operativo en el modelo Digital si por casualidad tiene skills coincidentes.

### Ventana óptima

No hay clientes pagando todavía (pivot estratégico 2026-05-25 — ver [`session_20260525_phase0c_deferred_smb_pivot`](../../../../.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/session_20260525_phase0c_deferred_smb_pivot.md)). Cambios de semántica de routing son hoy reversibles sin impacto comercial. Posponer la corrección es regalar deuda permanente al primer cliente que la encuentre.

## Decision

**`queue_memberships` se vuelve la única fuente ejecutiva de elegibilidad para routing automático en TODOS los canales (Voice + Digital).** El modelo de presencia digital se alinea con el modelo Asterisk ya implementado.

Concretamente, se adopta la combinación **B5 + B10 + B11** del análisis arquitectónico ampliado:

### B5 — Modelo de datos y gate

Cascada de filtros en eligibility (orden estricto, primer rechazo abandona):

1. `IsMember(agentId, queueId) AND NOT IsExcluded` — gate primario
2. `IsRoutable(agent.state)` — estado disponible
3. `HasCapacity(agent, conversation.channel)` — capacidad por canal
4. `queue.RequiredSkills.IsEmpty OR agent.Skills.Intersect(queue.RequiredSkills).Any()` — skill filter opcional
5. **Sort ASC by `membership.penalty`** — alineado con Asterisk app_queue (0 = highest priority)

### B10 — Filosofía: Asterisk es la autoridad

Para canal Voice, Verbara delega la decisión final a `app_queue` (que ya tiene `queue_members` sincronizado). Verbara solo se asegura que `queue_memberships` (Verbara) ↔ `queue_members` (Asterisk Realtime) estén consistentes. Para canales digitales, Verbara aplica el mismo modelo de elegibilidad localmente. **Una sola configuración del operador, comportamiento simétrico en todos los canales.**

### B11 — Mecanismo: reconciliación + observabilidad

[`IRealtimeVerifier`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs) (ya existe en SDK) se ejecuta como `HostedService` periódico para detectar drift entre `queue_memberships` (Postgres Verbara) y `queue_members` (Postgres Asterisk Realtime live). Si Asterisk fue editado manualmente fuera de banda, el verifier emite evento y `RealtimeReconciler` corrige.

### Wizard write-through

El setup wizard (paso "Agente") y el endpoint `POST /api/v1/admin/agents` aceptan `queueIds[]` opcional. Si el operador crea un agente con skills coincidentes a una queue, el wizard materializa una `QueueMembership(Source=Skill, Penalty=0)` automáticamente. El campo `Source=Skill` (que ya existe en el modelo) gana semántica ejecutiva: distingue memberships derivadas de skill-match (gestionadas por sistema, auto-actualizan cuando skills cambian) de memberships `Source=Manual` (gestionadas explícitamente por admin).

### Inserción arquitectónica limpia

Se introduce `MembershipGateMiddleware` en el [`InboundRouter`](../../src/Verbara.Platform.Routing.Inbound/) (chain middleware ya existente) **antes** del [`RoundRobinAgentSelector`](../../src/Verbara.Platform.Routing.Inbound/RoundRobinAgentSelector.cs). El middleware filtra el candidate pool a solo agentes con membership en la queue destino. [`InMemoryAgentPresenceService`](../../src/Verbara.Platform.Queues/Services/InMemoryAgentPresenceService.cs) queda como está (devolviendo "todos los routable + capacity-ok"); el gate se aplica una capa arriba. Esto preserva separación de capas y permite testear el gate aisladamente.

### Excepciones explícitas (documentadas)

- **Sticky / last-agent**: [`LastAgentMiddleware`](../../src/Verbara.Platform.Routing.Inbound/LastAgentMiddleware.cs) honra `PreferredAgentId` si el agente es member de **alguna** queue activa del tenant (no necesariamente la queue actual) y cumple `IsRoutable + HasCapacity`. CSAT prima sobre membership estricta.
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

1. **Script `infer-memberships-from-skills.sh`** (idempotente): para cada `(agent, queue)` en el sistema donde `agent.Skills ∩ queue.RequiredSkills ≠ ∅` y no existe ya una membership, inserta `QueueMembership(Source=Skill, Penalty=0, IsExcluded=false)`. Luego dispara `RealtimeSyncEngine.SyncAgentBatchAsync` para propagar a Asterisk.
2. **Ship behind feature flag** `RoutingFeatures.MembershipGate` (default OFF en v2.5.x patch release, ON en v2.6.0). Permite rollback emergente y testing A/B en lab.
3. **Documentar comportamiento en manual SMB 03-setup-inicial.md y 04-canal-webchat.md** (escritos a mano) + regenerar manual living-docs 01-day1-setup-and-webchat para reflejar el wizard corregido.
4. **Deprecar `Source=Manual` vs `Source=Skill` como distinción semántica** una vez estable; ambos son membership ejecutivos. La distinción se mantiene como audit metadata (quién creó la membership: operador vs sistema).

## Validation criteria

- **Test Api.Tests**: `MembershipGateMiddleware_Should*ExcludeNonMember*` (suite completa para sticky, direct-to-agent, outbound, fallback).
- **Integration test**: crear queue + agente sin membership → conversación entra → no se asigna; agregar membership → siguiente conversación se asigna.
- **Living-docs Day 1 manual** auto-regenera mostrando wizard que crea queue + agente + membership en un solo flujo, sin necesidad de pasos extra del operador.
- **Asterisk drift test**: `RealtimeVerifier` detecta inconsistencia introducida manualmente con `asterisk -rx "queue remove member ..."` y `RealtimeReconciler` la corrige.

## References

- Plan de implementación: [`docs/plans/active/2026-05-28-membership-executive-routing.md`](../plans/active/2026-05-28-membership-executive-routing.md)
- Living-docs Day 1 manual (drift surfaceado): [`docs/manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md`](../manuales/auto/v2.5.4/es-419/smb-owner/01-day1-setup-and-webchat.md)
- SDK Pro Realtime sync: [`Verbara.Sdk.Pro.Realtime`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/)
- SDK Pro Realtime verifier: [`IRealtimeVerifier.cs`](../../../Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.Realtime/IRealtimeVerifier.cs)
- Asterisk app_queue model: https://docs.asterisk.org/Configuration/Applications/queue/
