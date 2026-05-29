# Plan: ADR-0027 — Tenant-type operational gate (`RequireOperationalTenant`)

## Context

Verbara reconoce **3 tipos de tenant** (`Platform=0` / `Partner=1` / `Customer=2`, definidos en [`Verbara.Sdk.Pro.MultiTenant.TenantType`](../../../media/Data/Source/Verbara/Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.MultiTenant/TenantType.cs)). El contrato de diseño es claro:

- `platform`: administrativo puro (gestiona tenants hijos, licensing, system config, impersonation, audit cross-tenant). **NO debe** tener agentes/colas/campañas/conversaciones propias.
- `Partner`: comercial-administrativo (gestiona SUS Customers, rate cards, revenue, settings white-label). **NO debe** atender clientes finales directamente.
- `Customer`: tenant operativo donde realmente vive el contact center.

La jerarquía estructural sí está enforced (DB-unique platform vía índice parcial, max profundidad 3, Partner-must-be-child-of-Platform, suspend/delete/impersonate blocked para platform, X-Tenant-Id cross-tenant solo para Platform+Partner). **Pero el contrato operacional NO está enforced**: ningún `/admin/*`, `/queues/{queueId}/members`, `/conversations`, `/operations/*` verifica `TenantType`. Hoy un Platform Admin o Partner Admin con rol Admin puede entrar al UI, crear una queue sobre el tenant `platform` (o sobre su Partner), asociar agentes, configurar canales — y el sistema lo acepta. La auditoría 2026-05-28 (durante el cierre de ADR-0026 Phase A.6) lo confirmó vía grep + lectura de endpoints. La asimetría — jerarquía enforced + operación no-enforced — es el gap que cerramos ahora.

**Ventana óptima**: no hay clientes pagando (pivot 2026-05-25). El cambio de contrato no rompe nada en producción porque no hay producción. Cuando llegue el primer Partner-tier customer (incluso como design-partner pilot), el riesgo se materializa: un Admin del Partner puede mal-clasificar datos sin que el sistema lo impida, y la limpieza posterior es manual a nivel Postgres + audit retroactivo.

**Resultado esperado**: 1 filter reutilizable + ~23 sitios de aplicación + tests, convertir el contrato implícito a explícito, dar al operador un error 409 con remediation hint en vez de datos en el tenant equivocado. Costo estimado: 1 día sólido, cero impacto comercial.

## Decisión arquitectónica

Nuevo `EndpointFilter` `RequireOperationalTenant()` que retorna **HTTP 409 Conflict** (no 403) cuando el tenant resuelto no es `Customer`. Aplicado a cada `MapGroup` operacional. Espejo del patrón ya existente [`PlanFeatureGateExtensions.RequirePlanFeature`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Endpoints/PlanFeatureGateExtensions.cs).

- **409 ≠ 403**: 403 implica "no tenés permiso" — engañoso, el usuario sí tiene el rol Admin. 409 ("Conflict") señala "el tenant actual no admite esta operación" — accionable.
- **Body** = RFC 7807 ProblemDetails con `type=https://verbara.platform/errors/tenant-type-mismatch`, `tenantType` + `expectedType` + `remediation` ("usá `POST /api/v1/management/impersonate` o cambiá a un tenant Customer").
- **Bajo impersonation**: el `Tenant` resuelto en `HttpContext.Items["Tenant"]` ya es el del Customer impersonado (comportamiento actual de [`ManagementImpersonationEndpoints`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs)). El filter pasa naturalmente. **Sin caso especial.**
- **Pre-requisito**: el `Tenant` aggregate completo debe estar en `HttpContext.Items["Tenant"]` antes del routing. Hoy lo setea `TenantStatusMiddleware` ([`Program.cs:1257`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Program.cs#L1257)) después de `TenantResolutionMiddleware` ([`Program.cs:1250`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Program.cs#L1250)). Si falta (request anónimo o tenant invalid) el filter retorna 401 — defensa en profundidad ante regresiones del pipeline.

### Application sites (23 grupos OPERATIONAL)

Catalogados por exploración 2026-05-28. Todos reciben `.RequireOperationalTenant()` después del `.RequireAuthorization(...)` actual.

| Endpoint group | Archivo |
|---|---|
| `/admin/agent-assist` config | `AgentAssistFeatureEndpoints.cs` |
| `/agent-assist/sessions` runtime | `AgentAssistEndpoints.cs` |
| `/agents` (per-agent state) | `AgentEndpoints.cs` |
| `/analytics/*` | `AnalyticsEndpoints.cs` + `AnalyticsLiveEndpoints.cs` |
| `/admin/bots` | `BotEndpoints.cs` |
| `/call-analytics` | `CallAnalyticsEndpoints.cs` |
| `/admin/call-attempts` | `CallAttemptEndpoints.cs` |
| `/admin/caller-id-pools` | `CallerIdPoolEndpoints.cs` |
| `/admin/campaigns` | `CampaignEndpoints.cs` |
| `/admin/canned-responses` | `CannedResponseEndpoints.cs` |
| `/admin/channels` | `ChannelConfigEndpoints.cs` |
| `/admin/dispositions` | `DispositionEndpoints.cs` |
| `/admin/dnc-lists` | `DncListEndpoints.cs` |
| `/admin/flows` | `FlowEndpoints.cs` |
| `/admin/holiday-calendars` | `HolidayCalendarEndpoints.cs` |
| `/media` | `MediaEndpoints.cs` |
| `/admin/routes` | `OutboundRouteEndpoints.cs` |
| `/queues/{queueId}/members` | `QueueMembersEndpoints.cs` (Phase A.6 surface) |
| `/operations` queue metrics | `QueueMetricsEndpoints.cs` |
| `/recordings` | `RecordingEndpoints.cs` |
| `/admin/skills` + `/admin/agents/{id}/skills` | `SkillEndpoints.cs` (2 grupos) |
| `/supervisor` | `SupervisorEndpoints.cs` |
| `/admin/surveys` (admin + `/analytics/surveys`) | `SurveyEndpoints.cs` (2 grupos) |
| `/admin/trunks` | `TrunkEndpoints.cs` |

Adicionalmente: dentro de [`AdminEndpoints.cs`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs) hay sub-grupos operacionales (queues, agents, teams) **mezclados** con NEUTRAL (users, audit). Decisión: NO aplicar el filter al grupo padre `/admin`; en su lugar romper el archivo a nivel sub-grupo o aplicar `.RequireOperationalTenant()` selectivamente vía un `.MapGroup("/admin/queues")` separado dentro del mismo archivo. Detalle en Phase A.2.

### Grupos NEUTRAL (NO reciben el filter)

| Endpoint group | Por qué neutro |
|---|---|
| `/admin` (users, audit, scheduled-reports, settings) | Administrativo para cualquier tenant type — gestión propia |
| `/admin/audit` | Auditoría del propio tenant (cualquier tipo) |
| `/admin/auth` | Configuración de auth del propio tenant |
| `/admin/permissions` (RBAC) | Roles + permisos del propio tenant |
| `/admin/reports` (scheduled-reports) | Reportes administrativos del propio tenant |
| `/admin/tenant` (settings) | Settings del propio tenant |

### Ya-restringidos (NO necesitan el filter)

`/management/*` (8 grupos, PlatformAdminOnly) y `/partner/*` (4 grupos, PartnerAdminOnly) ya hacen el work — no aplicamos `RequireOperationalTenant` a esos.

### Casos borderline a verificar caso por caso en Phase A.4

- `ConversationEndpoints`, `KnowledgeBaseEndpoints`, `WebChatEndpoints`, `OnboardingEndpoints`, `WebhookEndpoints`, `WebhookSubscriptionEndpoints`, `WebhookEventTypeEndpoints`, `CaseEndpoints`, `ContactEndpoints`, `RealtimeEndpoints` — el explorador no los listó con `MapGroup` directo o son públicos. Inspección caso por caso en Phase A.4 para decidir si reciben el filter (probable OPERATIONAL) o no (probable NEUTRAL/público).

## Fases

### Phase A — Filter + application + AOT JSON contract (~4 horas)

**A.1** — Crear [`src/Verbara.Platform.Api/Endpoints/TenantTypeGateExtensions.cs`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Endpoints/) con el filter `RequireOperationalTenant()`. Espejo del patrón de `PlanFeatureGateExtensions.RequirePlanFeature`. Retorna 409 ProblemDetails con shape:
```json
{
  "type": "https://verbara.platform/errors/tenant-type-mismatch",
  "title": "Operational endpoint not available on this tenant type",
  "status": 409,
  "detail": "Operational endpoints are only available on Customer tenants (this is a {Type} tenant). Use POST /api/v1/management/impersonate {tenantId} to drive operational endpoints as that Customer.",
  "tenantType": "Platform" | "Partner",
  "expectedType": "Customer"
}
```
Si `Items["Tenant"]` es null → 401 (defensa en profundidad).

**A.2** — Definir `TenantTypeMismatchProblem` record + registrar en [`ApiJsonContext.cs`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs) (`[JsonSerializable]` — requerido por AOT, no negociable per [hard constraint AOT](../../../media/Data/Source/Verbara/Verbara.Platform/CLAUDE.md)). Espejo del patrón de `ErrorResponse`.

**A.3** — Aplicar `.RequireOperationalTenant()` a los 23 grupos catalogados en la sección "Application sites" arriba. Una línea por archivo (excepto SkillEndpoints + SurveyEndpoints + AdminEndpoints que tienen 2 grupos cada uno). Total ~27 sitios.

**A.4** — Auditar los 10 endpoints borderline (`ConversationEndpoints`, etc.). Para cada uno: leer el archivo, decidir OPERATIONAL/NEUTRAL/público, aplicar filter si corresponde. Documentar la decisión en el commit message.

**A.5** — Refactor de [`AdminEndpoints.cs`](../../../media/Data/Source/Verbara/Verbara.Platform/src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs): el grupo padre `/admin` agrupa users (NEUTRAL) + queues + agents + teams (OPERATIONAL). Romper en 2 sub-grupos: `var operationalGroup = app.MapGroup("/admin").RequireAuthorization("AdminOnly").RequireOperationalTenant();` para queues/agents/teams; el `users` queda en el grupo neutro. Verificar que los route patterns siguen siendo idénticos (no se mueve nada de URL, solo se reagrupa el filter).

**Salida A**: `dotnet build` 0 warnings + 0 errors. `dotnet test tests/Verbara.Platform.Api.Tests` continúa pasando 961/961 (el factory usa Customer-typed tenant, no rompe nada).

### Phase B — Test coverage (~3 horas)

**B.1** — Nuevo factory `PlatformTenantAuthenticatedApiFactory` (o helper en el factory existente) que autentica con un usuario seeded en un tenant `Type=Platform`. Mirror de [`PartnerApiFactory`](../../../media/Data/Source/Verbara/Verbara.Platform/tests/Verbara.Platform.Api.Tests/PartnerApiFactory.cs).

**B.2** — Nuevo archivo `tests/Verbara.Platform.Api.Tests/TenantTypeGateTests.cs` con un test parametrizado por endpoint group:
```csharp
[Theory]
[InlineData("/api/v1/admin/queues", "POST", "{\"name\":\"X\"}")]
[InlineData("/api/v1/admin/agents", "POST", "{\"userId\":\"u\",\"displayName\":\"X\"}")]
[InlineData("/api/v1/admin/campaigns", "POST", "{...}")]
// ...23 endpoint groups × representative verb each
public async Task OperationalEndpoint_ShouldReturn409_WhenCallerIsPlatformTenant(...)
```
Espejo del patrón en [`QueueMembersEndpointsTests.cs:144-155`](../../../media/Data/Source/Verbara/Verbara.Platform/tests/Verbara.Platform.Api.Tests/QueueMembersEndpointsTests.cs#L144). Aserta status 409 + verifica que el body parsea como `TenantTypeMismatchProblem` con `tenantType="Platform"`, `expectedType="Customer"`.

**B.3** — Mismo test parametrizado con `PartnerApiFactory` para asertar 409 cuando caller es Partner.

**B.4** — Test de impersonation happy path: Platform Admin hace `POST /management/impersonate` → recibe token impersonation → `GET /admin/agents` con ese token → 200 (porque el `Tenant` resuelto pasa a ser el Customer). Confirma que la ergonomía de Platform-Admin-drives-Customer no se rompe.

**B.5** — Test de NEUTRAL endpoint: caller Platform → `GET /admin/users` retorna 200 (NO debe ser gated). Caller Platform → `GET /admin/audit` retorna 200. Aserta que el filter NO aplica donde no debe.

**Salida B**: 961 + ~30 nuevos tests = ~991 pasando. Sin regresiones.

### Phase C — Data inventory + docs + closure (~1 hora)

**C.1** — Script `scripts/tenant-type-misplaced-data.sh` (idempotente, read-only). Lista cualquier row en `agents`/`queues`/`queue_memberships`/`channels_config`/`campaigns`/`flows`/`bots`/`articles`/`surveys` con `tenant_id IN (SELECT tenant_id FROM tenants WHERE type <> 2)` — es decir, datos operacionales que viven en un tenant NO-Customer. SMB Docker happy path debería retornar 0 rows; cualquier otro número se triagea manualmente antes de promover el gate a producción cuando llegue el primer cliente.

**C.2** — Actualizar [`docs/manuales/smb/`](../../../media/Data/Source/Verbara/Verbara.Platform/docs/manuales/smb/) (manuales escritos a mano) con una sección "Si trabajás como Platform Admin" explicando que `/admin/*` operacional ya no es accesible desde el tenant `platform` y que la forma correcta es impersonar a un Customer.

**C.3** — Actualizar ADR-0027 con "Implementation status" section (espejo del patrón de ADR-0026): commits + sub-fases + permanent artifacts.

**C.4** — Mover plan `docs/plans/active/2026-05-28-tenant-type-operational-gate.md` → `docs/plans/completed/`. Update `MEMORY.md` index pointer.

**Salida C**: `scripts/tenant-type-misplaced-data.sh` reporta 0 rows misplaced en el SMB Docker de referencia. Manuales SMB mencionan el contrato. Plan archivado.

## Critical files

**Nuevos (Phase A + B + C)**:
- `src/Verbara.Platform.Api/Endpoints/TenantTypeGateExtensions.cs` (filter)
- `src/Verbara.Platform.Api/Endpoints/TenantTypeMismatchProblem.cs` (DTO + `[JsonSerializable]` annotation)
- `tests/Verbara.Platform.Api.Tests/TenantTypeGateTests.cs` (~30 tests)
- `tests/Verbara.Platform.Api.Tests/PlatformTenantAuthenticatedApiFactory.cs` (factory)
- `scripts/tenant-type-misplaced-data.sh` (inventory script)

**Modificados (Phase A.3 + A.4 + A.5)**:
- ~23 archivos en `src/Verbara.Platform.Api/Endpoints/` — agregar `.RequireOperationalTenant()` después de `.RequireAuthorization(...)` actual. Patrón mecánico, batchable.
- `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs` — refactor de `MapAdminEndpoints` para separar OPERATIONAL (queues/agents/teams) de NEUTRAL (users) en sub-grupos.
- `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — `[JsonSerializable(typeof(TenantTypeMismatchProblem))]`.

**Re-usar (no modificar)**:
- `src/Verbara.Platform.Api/Endpoints/PlanFeatureGateExtensions.cs` — patrón de referencia.
- `src/Verbara.Platform.Api/Middleware/TenantStatusMiddleware.cs` — fuente de `Items["Tenant"]`.
- `tests/Verbara.Platform.Api.Tests/PartnerApiFactory.cs` — patrón para `PlatformTenantAuthenticatedApiFactory`.
- `tests/Verbara.Platform.Api.Tests/QueueMembersEndpointsTests.cs` — patrón de 401/403 a espejar para 409.

## Verification

**Phase A**:
```bash
cd /media/Data/Source/Verbara/Verbara.Platform
dotnet build Verbara.Platform.slnx     # 0 warnings, 0 errors
dotnet test tests/Verbara.Platform.Api.Tests/Verbara.Platform.Api.Tests.csproj  # still 961/961 (Customer-typed factory passes)
```

**Phase B**:
```bash
dotnet test tests/Verbara.Platform.Api.Tests/Verbara.Platform.Api.Tests.csproj --filter "FullyQualifiedName~TenantTypeGate"
# Expected: ~30 new tests pass, total ~991
```

**Phase A end-to-end manual smoke** (después del rebuild de imagen `local-phase-a`):
```bash
# Login como admin@verbara.local (Platform tenant — el seed por defecto)
TOKEN=$(curl -sf -X POST http://localhost/api/v1/auth/login \
  -H "Content-Type: application/json" -H "X-Tenant-Id: platform" \
  -d '{"email":"admin@verbara.local","password":"DocumentationDemo2026!"}' | jq -r .accessToken)

# Intentar crear una queue sobre platform → debe retornar 409
curl -sv -X POST http://localhost/api/v1/admin/queues \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
  -H "Content-Type: application/json" -d '{"name":"Should-Fail"}' \
  | jq '{status: .status, type: .type, tenantType: .tenantType}'
# Expected: status=409, type=tenant-type-mismatch, tenantType="Platform"

# Mismo POST pero después de impersonar a un Customer → 201
curl -sf -X POST http://localhost/api/v1/management/impersonate \
  -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
  -d '{"tenantId":"<customer-id>"}' | jq -r .impersonationToken
# usar el nuevo token contra /admin/queues → expect 201
```

**Phase C** (inventory):
```bash
bash scripts/tenant-type-misplaced-data.sh
# Expected en SMB Docker happy path: "0 misplaced rows across 9 operational tables."
```

**Living-docs regression** — el spec `01-day1-setup-and-webchat` opera sobre el seeded Customer tenant (el `/api/setup` crea `platform` + adopta orphans como Customer); debe seguir pasando 13/13. Si por alguna razón el wizard intenta crear queue sobre platform, el spec falla fast en step 7, lo que es la señal correcta.

## Riesgos + mitigaciones

| Riesgo | Mitigación |
|---|---|
| Algún test existente autentica como Platform y llega a `/admin/*` operacional, falla con 409 inesperado | Audit ya hecho: `AuthenticatedPlatformApiFactory` seeds Customer-typed tenant (no Platform). Confirmar leyendo `AuthenticatedPlatformApiFactory.cs:36-48` antes de aplicar el filter. Si algún test rompe, migrar a la nueva infraestructura de impersonation para llegar al endpoint como Customer. |
| El filter ejecuta en hot-path (cada request `/admin/*`); overhead inaceptable | Medir antes/después con el suite NBomber del SMB Docker baseline. Cost esperado: 1× lookup en `HttpContext.Items` (string-keyed dictionary) + 1× enum comparison. Sub-microsegundo. No debería mover p99. |
| Existen rows operacionales ya creados sobre `platform` por instalaciones pre-Phase A.6 (admin agente, queue, etc.) | El script `tenant-type-misplaced-data.sh` los enumera ANTES de habilitar el filter. Triagear caso por caso: borrar (si son artefacto de prueba) o migrar `UPDATE ... SET tenant_id=<customer-id>` (si son legítimamente del Customer pero quedaron mal). En SMB Docker happy path no debería haber ninguno; en mi propia lab sí los hay (Phase A en runs anteriores), serán cleanup manual. |
| AOT JSON falla en runtime si olvidamos `[JsonSerializable]` para `TenantTypeMismatchProblem` | Cubierto por el patrón conocido — toda DTO serializada va en `ApiJsonContext`. Tests B.2/B.3 que parsean el body lo detectan inmediatamente. |
| Sub-grupos dentro de `AdminEndpoints.cs` requieren un refactor delicado (mismas URLs, agrupación distinta) | Hacer A.5 antes de aplicar el filter al resto. Tests del suite Api.Tests existente cubren todas las rutas; si A.5 mueve algo accidentalmente, el suite rompe inmediatamente. |
| Los endpoints borderline (Phase A.4) caen en la categoría equivocada | Documentar cada decisión en el commit message + en la sección "Application sites" del ADR-0027 ya escrito. Si el criterio luego se demuestra equivocado, el cambio es 1-line revert por endpoint. Reversibilidad total. |
| Phase C.2 manual update inflated → el manual SMB se vuelve confuso | Mantener la sección "Si trabajás como Platform Admin" corta (~10 líneas) + apuntar al ADR-0027 para profundidad. Editor humano. |

## Out of scope / deferred

- **Permisos cross-tenant para Partners** (e.g., Partner Admin que puede crear/editar agentes de SUS customers sin impersonation). El gate los manda a impersonation; mejorarlo a "Partner Admin opera customer's data with partner JWT" es feature aparte, no urgente.
- **UI change** en `Verbara.Platform.Web` para mostrar el error 409 con un dialog de impersonation. Hoy el frontend muestra el toast genérico — alcanza para la primera iteración. Mejorar UX es follow-up post primer Partner real.
- **Tenant-type-aware navegación** en la sidebar admin (e.g., esconder `/admin/queues` cuando el caller es Platform). UX mejora, no es product safety. Follow-up.
- **Refactor de Management endpoints** para que también el Platform Admin pueda CRUD-ear queues/agents/etc. de un Customer SIN impersonar. Ergonomía nice-to-have; impersonation cubre el caso hoy. Deferred.
- **Foreign key `parent_tenant_id REFERENCES tenants(tenant_id)`** — el gap T1 del audit 2026-05-28. Plan aparte (require orphan-cleanup + migration), deferred.
- **Recursive `ListAllDescendantsAsync`** — el gap T5/T6 del audit. Plan aparte (requiere SQL CTE), deferred.
