# Plan — Typification P1: captura de taxonomía compartida end-to-end

## Context

P0 (shipped 2026-06-07) entregó el módulo `Verbara.Platform.Typification` (schema cascada+condicional, resolver de binding, `TypificationSubmission`, wrap-up dinámico) pero la tipificación sigue siendo **100% manual al cierre**: el agente clasifica desde cero aunque el IVR/bot ya supo el motivo del cliente. P1 cierra la promesa de ADR-0029 D2 — **"una sola taxonomía de motivo-de-contacto hilada end-to-end"**: lo que la captura automática (IVR/bot/routing) sabe del cliente viaja con la conversación y **pre-selecciona la cascada + pre-llena campos** en el wrap-up. El agente confirma/ajusta en vez de re-clasificar.

El análisis profundo (aterrizado en código) reencuadró P1: **no es "elegir un canal de captura"**, su núcleo correcto es **un contrato único de "bolsa de atributos"** (modelo probado: Amazon Connect *contact attributes*, Genesys *participant data*, Talkdesk *context*) — toda fuente escribe hechos `string→string` en `Conversation.Metadata` bajo llaves bien-conocidas y **un solo consumidor** las lee en el wrap-up. Y reveló que la **captura implícita** (DID/canal→motivo, sin esfuerzo del cliente) es de mayor ROI que la interactiva y su infraestructura **ya existe sin usar** (`RouteResult.Metadata` siempre `null`+ignorado; `DidRoute` resuelve el DID). Alcance elegido: **P1 completo** (columna + implícito digital + implícito voz + nodo explícito). **No toca Pro ni Sdk** (`LicenseFeature.AdvancedTypification` ya existe de P0) → baile cross-repo solo Platform + Web.

## Decisiones cerradas (aprobadas)

1. **Contrato bolsa-de-atributos.** Llave `reasonPath` = **JSON array de node `Code`s** (root→leaf; Code es estable entre republicaciones, el consumidor lo mapea a NodeId contra el schema resuelto) + llaves de prefill arbitrarias (`patientId`, …). Una sola constante compartida `TypificationMetadataKeys.ReasonPath = "reasonPath"` referenciada por los 4 escritores y el consumidor.
2. **Precedencia por orden de ejecución** (sin lógica especial): implícito (routing) estampa primero → explícito (bot/flow) sobrescribe después.
3. **`ReasonHint` entidad nueva unificada** (B3+B4) en `Verbara.Platform.Typification` — SoC: routing (`DidRoute`) y motivo separados; una sola página admin. NO se añade columna a `DidRoute`.
4. **`reasonPath` por Code, match parcial tolerante** (prefijo válido más largo; nunca lanza; respeta subtree).
5. **Provenance de prefill diferida a P4** — sin cambios a `TypificationSubmission`/`TypificationField` (`PrefillRef` ya existe).
6. **Licensing = patrón P0 exacto.** Admin (`/admin/reason-hints` + nodo en diseñador) gateado `RequireLicenseFeature(AdvancedTypification)` + `PermissionGuard system:typification:configure` (reusa permiso P0; **no** hay policy backend por-permiso, solo `AdminOnly` rol). Runtime (`/typification-form`, `/typify`, aplicación de metadata) **nunca** gateado.
7. **Sin evento cross-pod nuevo** (P1 no emite SSE; solo escribe metadata y la lee en el form).
8. **Bug de casing del diseñador de flows: arreglo limpio Web.** El diseñador persiste `node.type` PascalCase (`CollectInput`) pero el engine matchea snake_case (`collect_input`) y el validador NO valida tipos → flows del diseñador revientan en runtime. Fix: **mapeo puro bidireccional en `flow-utils.ts`** (`toDomain`→snake, `toReactFlow`→Pascal). Vocabulario wire/dominio/engine = 100% snake_case; PascalCase = solo render de React Flow. Arregla `collect_reason` y todos los nodos. Pre-launch ⇒ sin datos viejos que migrar.

## Convenciones de ejecución

Subagent-Driven + FCM batching + review dos etapas + 🔒 individual en riesgosas + **holística cross-repo final**. Conventional Commits, **sin `Co-Authored-By`/referencias a IA**. Native AOT (sin reflexión, source-gen), `TreatWarningsAsErrors`/WarningLevel 9999, Npgsql crudo (`Verbara.Sdk.Data.Npgsql`, sin Dapper), test naming `Method_ShouldExpected_WhenCondition`. `finishing-a-development-branch` al cierre (**confirmando antes** de Push/PR/merge). Plan reflejado a `docs/plans/active/` tras aprobación; `git mv` a `completed/` al shippear.

## Integración confirmada + deltas (leí los archivos)

- `BotResponse` (`Bot/IVirtualAgent.cs:29`) = `{Action, Messages, TargetQueueId, Priority, HandoffReason}` → añadir `FlowMetadata` trailing-opcional.
- ⚠ **B1 no puede leer vars desde `FlowStepResult`** (no las tiene) y el `execution` local de `BotOrchestrator` queda stale. → Extender `FlowStepResult` (`Flows/IFlowExecutionEngine.cs:46`) con `IReadOnlyDictionary<string,string>? FlowMetadata`, poblado en el único return del engine (`FlowExecutionEngine.cs:143`) filtrando vars que no empiecen con `__`.
- `RouteResult.Metadata` (`Routing.Inbound/RouteResult.cs`) ya existe, siempre `null`, **nunca leído** (callers solo usan `QueueId`: `WebhookEndpoints.cs:108`, `WebChatInboundRouter.cs:83`) → cablear.
- ⚠ **`ReasonHintMiddleware` debe llamar `continuation()` primero** (la cola la resuelve aguas abajo; `RoutingContext` no la trae) y luego resolver el hint, retornando `downstream with {Metadata=…}` (espejo `ChannelQueueMappingMiddleware.cs:23`). Requiere ProjectReference `Routing.Inbound → Typification` (sin ciclo).
- ⚠ **B4**: `VoiceConversationBridge.OnCallQueuedAsync` (`:177`, create block `:210`); patrón AMI GetVar = `ResolveTenantFromChannelAsync` (`:535-562`), canal caller-leg = `session.Participants...Role==Caller).Channel`. `StasisInboundConsumer` (`:295-319`) ya resuelve DID+DidRoute y setea channel-vars vía `client.Channels.SetVariableAsync`; constantes `:44-65`; `_didRoutes`/`_queues` inyectados.
- ⚠ **B2 ProjectReference** `Flows → Typification` (sin ciclo: Typification→Core/Conversations). Handler `NodeType` snake_case `"collect_reason"` (con el fix de casing #8, el diseñador persiste snake_case y matchea).
- ⚠ **`system:typification:configure` no se enforce en backend** (solo `AdminOnly` rol) → mirror P0 exacto.
- `PrefillRef` ya registrado en `PostgresJsonContext`; `IReadOnlyDictionary<string,string>`/`IReadOnlyList<string>` ya registrados ahí. Confirmar/añadir esos colectivos en `ApiJsonContext` (respaldan los miembros nuevos de prefill).
- Migración: `DatabaseMigrationService` escanea embedded `Migrations.*.sql`, orden ordinal por nombre, tracking en `_migrations`; `.csproj` ya hace glob. `002_*.sql` corre tras `001_Baseline`.
- ⚠ **Web hydration**: `dynamic-typification-form.tsx` opera en path **subtree-relativo** y prepende ancestros (`ancestorChainInclusive` `:85,206`). El `PrefilledNodePath` del server es **full**; hay que **quitar el prefijo hasta+incluyendo `subtreeRootNodeId`** antes de sembrar.

---

## PHASE F — Foundational (contratos + dominio; sin cambio de comportamiento)

- **F0 docs** — ADR-0029 → anexar sección "P1 scope"; nuevo spec `docs/specs/2026-06-08-typification-p1-shared-capture.md`; mirror este plan a `docs/plans/active/`.
- **F1 🔒 propagación flow-vars contract** — `BotResponse.FlowMetadata` + `FlowStepResult.FlowMetadata` + poblado en `FlowExecutionEngine.cs:143` (filtrar `__`) + thread en `BotOrchestrator` (Reply+Handoff). Tests: `ProcessMessageAsync_ShouldExposeNonPrivateVariablesAsFlowMetadata_When…`, `_ShouldExcludeDoubleUnderscoreKeys_When…` (Flows.Tests); `_ShouldCopyFlowMetadataIntoBotResponse_When{Handoff,Reply}` (Bot.Tests).
- **F2** — `ReasonHint.cs` (`:ITenantScoped {HintId,TenantId,Scope,ScopeRef,ReasonPath,Priority,IsActive}`) + `ReasonHintScope.cs` (`{Did,Channel,Queue}`) + `TypificationMetadataKeys.cs` (`ReasonPath="reasonPath"`).
- **F3** — `Stores/IReasonHintStore.cs` (espejo `ISchemaBindingStore`: Get/List/`ListByScopeAsync`/Save/Delete).
- **F4 🔒** — `Resolution/IReasonHintResolver.cs` + `DefaultReasonHintResolver.cs`: inputs `(string? did, EntityId? queueId, ChannelType channel)`; most-specific **Did→Queue→Channel**, `IsActive`, tiebreak Priority desc luego HintId ordinal. Tests: `ResolveAsync_ShouldPreferDidOverQueueAndChannel_When…`, `_ShouldFallBackToChannel_When…`, `_ShouldRespectPriorityThenIdTiebreak_When…`, `_ShouldIgnoreInactiveHints_When…`, `_ShouldReturnNull_When…`.
- **F5 🔒** — `Resolution/ITypificationPrefillResolver.cs` + `Default…` + `PrefillResult`. `ResolvePrefill(schema, subtreeRoot, conversation)`: lee `Metadata[reasonPath]` (Codes JSON, source-gen), Code→NodeId, camina validando cadena, **prefijo válido más largo**, respeta subtree, nunca lanza; campos con `PrefillSource.Kind==Metadata` ← `Metadata[Ref]`. Tests: `_ShouldReturnFullNodePath_When…`, `_ShouldReturnLongestValidPrefix_When…`, `_ShouldReturnEmptyPath_WhenReasonPathMissing`, `_ShouldNotThrow_WhenReasonPathJsonMalformed`, `_ShouldRespectSubtreeRoot_When…`, `_ShouldPrefillFieldValues_When…`, `_ShouldOmitField_WhenMetadataKeyAbsent`.
- **F6** — registrar ambos resolvers en `Typification/ServiceCollectionExtensions.AddPlatformTypification`.

## PHASE C — Core (storage, migración, escritores, consumidor)

- **C1** — `InMemoryReasonHintStore` (espejo `InMemorySchemaBindingStore`) + registro InMemory SCE. Tests: `SaveAsync_ShouldUpsert_When…`, `ListByScopeAsync_ShouldReturnOnlyMatchingScope_When…`, `DeleteAsync_…`.
- **C2 🔒** — Migración `002_reason_hints.sql` (`reason_hints(tenant_id,hint_id,scope,scope_ref,reason_path,priority,is_active, PK(tenant_id,hint_id))` + idx `(tenant_id,scope,scope_ref)`, idempotente). Test clean-DB: `Migrations_ShouldCreateReasonHintsTable_WhenAppliedToEmptyDatabase`.
- **C3 🔒** — `PostgresReasonHintStore` (espejo `PostgresSchemaBindingStore`, `ReasonPath` TEXT, sin JSONB nuevo) + registro Postgres SCE. Tests: `SaveAsync_ShouldRoundTrip_When…`, `ListByScopeAsync_…`, `DeleteAsync_…`.
- **C4 🔒 B3** — `Routing.Inbound/Middlewares/ReasonHintMiddleware.cs` (continuation-first, lee `downstream.QueueId`, resuelve por queue/channel, merge en `Metadata` sin pisar llaves existentes) + ProjectReference + registro/inserción en lista de middlewares. Tests: `RouteAsync_ShouldStampReasonPathMetadata_WhenChannelHintExists`, `_ShouldStampUsingResolvedQueue_When…`, `_ShouldLeaveMetadataNull_WhenNoHint`, `_ShouldNotOverrideExistingDownstreamMetadataKeys_When…`.
- **C5 🔒 B3 consumo** — cablear `routeResult.Metadata` → `conversation.SetMetadata` + save en `WebhookEndpoints.cs:108` y `WebChatInboundRouter.cs:83` (antes del bot). Tests: `HandleWebhook_ShouldCopyRouteMetadataOntoConversation_When…`, `RouteFirstInbound_ShouldStampRouteMetadata_When…`.
- **C6 🔒 B1 aplicación** — aplicar `botResponse.FlowMetadata` → `SetMetadata`+save antes de `TransferToQueueAsync` (`WebhookEndpoints.cs:142` + WebChat). Tests: `HandleWebhook_ShouldApplyFlowMetadataBeforeTransfer_When…`, `_ShouldLetFlowMetadataOverrideImplicitReasonPath_WhenBothPresent`.
- **C7 🔒 B4 voz** — `StasisInboundConsumer`: inyectar `IReasonHintResolver`, resolver por `did`+`route.QueueId`+Voice, `SetVariableAsync("VERBARA_REASON", reasonPath)`; `VoiceConversationBridge`: `ResolveReasonFromChannelAsync` (espejo tenant GetVar) → `SetMetadata(reasonPath)`+save. Const `VERBARA_REASON`. Tests: `ResolveReasonFromChannelAsync_ShouldReturnValue_When…`/`_ShouldReturnNull_When…`, `OnCallQueued_ShouldStampReasonPathMetadata_When…`, `HandleInbound_ShouldSetReasonChannelVar_When…`.
- **C8 🔒 B2 nodo** — `Flows/Nodes/CollectReasonNodeHandler.cs` (`NodeType "collect_reason"`, inyecta `ITypificationSchemaStore`+renderer; config `schema_id`/`subtree_root_node_id`; menú numerado por nivel filtrado por `ChannelApplicability`; estado parcial en `__reason_*`; retry como `collect_input`; al leaf escribe `reasonPath` Codes JSON vía source-gen) + ProjectReference + registro SCE. Tests: `ExecuteAsync_ShouldRenderTopLevelMenu_When…`, `_ShouldAdvanceOneLevel_When…`, `_ShouldRetry_WhenInvalidSelection`, `_ShouldFilterByChannelApplicability_When…`, `_ShouldWriteReasonPathCodes_WhenLeafReached`, `_ShouldRespectSubtreeRoot_When…`.
- **C9 🔒 consumidor** — `ConversationEndpoints.cs`: `TypificationFormResponse` += `IReadOnlyList<string>? PrefilledNodePath` + `IReadOnlyDictionary<string,string>? PrefilledFieldValues`; `GetTypificationForm` inyecta `ITypificationPrefillResolver`, lo llama tras el binding-resolver. `/typify` SIN cambios; runtime NO gateado. Tests: `GetTypificationForm_ShouldReturnPrefilledNodePath_When…`, `_ShouldReturnPrefilledFieldValues_When…`, `_ShouldReturnNullPrefill_WhenNoMetadata`, `_ShouldNotBeLicenseGated_WhenTenantUnlicensed`.
- **C10** — `ReasonHintEndpoints.cs` CRUD `/admin/reason-hints` (espejo `DidRouteEndpoints` + grupo gateado de `TypificationEndpoints`: `AdminOnly`+`RequireOperationalTenant`+`RequireLicenseFeature(AdvancedTypification)`; validar Scope/ScopeRef; audit) + DTOs + `Program.cs MapReasonHintEndpoints()`. Tests: `CreateReasonHint_ShouldReturn201_When…`, `_ShouldReturn400_WhenScopeRefMissing`, `ListReasonHints_ShouldReturn402_WhenTenantUnlicensed`, `_ShouldRequireAdmin_When…`.

## PHASE M — Mantle (AOT, Web, i18n)

- **M1 🔒 AOT** — `ApiJsonContext`: `ReasonHintDto(+[])`, `Create/UpdateReasonHintRequest`; confirmar `List<string>`/`Dictionary<string,string>` presentes (respaldan prefill). Sin JSONB nuevo. Gate: `dotnet publish` 0 warnings + `Aot.Probe` compila.
- **M2a 🔒 fix casing** — `flow-utils.ts`: mapa bidireccional explícito PascalCase↔snake_case (registry conocido) en `toDomain`/`toReactFlow`. Test: `flowUtils_ShouldRoundTripNodeTypeCasing_When…` (Pascal→snake→Pascal).
- **M2b** — nodo `collect_reason` en diseñador: `nodes/collect-reason-node.tsx` + registro en `nodes/index.ts` + item en `node-palette.tsx` + config en `property-panel.tsx` (picker schema/subtree, reusa `use-typification`). Test render+persist.
- **M3 🔒** — `dynamic-typification-form.tsx`: sembrar `selectedNodePath` desde `prefilledNodePath` **quitando prefijo hasta+incl. `subtreeRootNodeId`** + `fieldValues` desde `prefilledFieldValues` (efecto one-shot al cargar; agente puede cambiar). `use-typification.ts`: extender `TypificationFormResponse`. Tests: `_ShouldPreselectCascade_When…`, `_ShouldPrefillFields_When…`, `_ShouldAllowAgentOverride_WhenPrefilled`.
- **M4** — admin: `reason-hints/{reason-hints-page,reason-hint-form}.tsx` (espejo `did-routes`) + `use-reason-hints.ts` + ruta lazy+Suspense+`PermissionGuard system:typification:configure` en `router.tsx` + item sidebar. Tests: `useReasonHints_ShouldFetchList_When…` + page.
- **M5** — i18n ×3 (`admin.json` baseline es-419: `sidebar.reasonHints`, `flows.node_types.collect_reason`, page reason-hints, property-panel; + en-US/pt-BR). Gate `npm run i18n:check`.

## FCM ordering

F0 → 🔒F1 → F2→F3 → 🔒F4 → 🔒F5 → F6 ⟶ C1 → 🔒C2 → 🔒C3 → 🔒C4 → 🔒C5 → 🔒C6 → 🔒C7 → 🔒C8 → 🔒C9 → C10 ⟶ 🔒M1 → 🔒M2a → M2b → (🔒M3 ∥ M4) → M5 ⟶ **holística cross-repo final**.

## Verification

**Platform** (`/media/Data/Source/Verbara/Verbara.Platform`): `dotnet build -warnaserror` 0 warnings · `dotnet test` (Typification, Flows, Bot, Routing.Inbound, Storage.InMemory, Storage.Postgres, Api) · `dotnet publish -c Release` **0 warnings AOT** + `Aot.Probe` · clean-DB: aplicar migraciones → assert `reason_hints`+idx existen y `_migrations` contiene `002_reason_hints.sql`.
**Web** (`/media/Data/Source/Verbara/Verbara.Platform.Web`): `npm run build` · `npm run lint` · `npm run i18n:check` · `npx vitest run`.
**E2E manual (4 escritores + prefill + 402):**
1. **B4 voz implícito** — `ReasonHint{Did}` → llamada inbound a ese DID → `VERBARA_REASON` seteada + `Metadata[reasonPath]` al encolar → wrap-up con cascada pre-seleccionada.
2. **B3 digital implícito** — `ReasonHint{Channel}` → webhook inbound → `routeResult.Metadata` copiada a la conversación → wrap-up pre-llenado.
3. **B2 flow explícito** — flow con `collect_reason` (construido en el diseñador, ya ejecuta por fix de casing) → bot, elegir cascada por menú hasta hoja → handoff → `Metadata[reasonPath]` Codes JSON (sobrescribe implícito).
4. **B1 prefill gratis** — flow setea var `patientId` (sin `__`) → handoff → `Metadata[patientId]` → campo con `PrefillSource{Metadata,patientId}` pre-llenado.
5. **Consumidor** — `GET /typification-form` devuelve `prefilledNodePath`+`prefilledFieldValues`; agente puede sobrescribir; `POST /typify` igual.
6. **402** — tenant sin licencia: `/admin/reason-hints` → 402; `/typification-form`+`/typify` siguen OK.

## Critical files

- `Verbara.Platform/src/Verbara.Platform.Typification/Resolution/{DefaultTypificationPrefillResolver,DefaultReasonHintResolver}.cs` (F5/F4, lógica pura)
- `Verbara.Platform/src/Verbara.Platform.Typification/{ReasonHint,ReasonHintScope,TypificationMetadataKeys}.cs` + `Stores/IReasonHintStore.cs`
- `Verbara.Platform/src/Verbara.Platform.Flows/{IFlowExecutionEngine,FlowExecutionEngine}.cs` + `Nodes/CollectReasonNodeHandler.cs` (F1/C8)
- `Verbara.Platform/src/Verbara.Platform.Bot/{IVirtualAgent,BotOrchestrator}.cs` (F1)
- `Verbara.Platform/src/Verbara.Platform.Routing.Inbound/Middlewares/ReasonHintMiddleware.cs` + `RouteResult.cs` wiring (C4)
- `Verbara.Platform/src/Verbara.Platform.Api/Services/{StasisInboundConsumer,VoiceConversationBridge,WebChatInboundRouter}.cs` + `Endpoints/{WebhookEndpoints,ConversationEndpoints,ReasonHintEndpoints}.cs` + `Serialization/ApiJsonContext.cs` + `Program.cs` (C5-C10)
- `Verbara.Platform/src/Verbara.Platform.Storage.{InMemory,Postgres}/**` (stores + `Migrations/002_reason_hints.sql`)
- `Verbara.Platform.Web/src/admin/flows/flow-utils.ts` (M2a fix casing) + `nodes/collect-reason-node.tsx` + `node-palette.tsx` + `property-panel.tsx`
- `Verbara.Platform.Web/src/agent/conversation/dynamic-typification-form.tsx` (M3) · `src/core/api/hooks/{use-reason-hints,use-typification}.ts` · `src/admin/reason-hints/**` · `src/router.tsx` · `src/admin/sidebar.tsx` · `public/locales/{es-419,en-US,pt-BR}/admin.json`

## Docs

ADR-0029 anexar "P1 scope/shipped" · nuevo spec `docs/specs/2026-06-08-typification-p1-shared-capture.md` · mirror este plan a `docs/plans/active/2026-06-08-typification-p1.md`, `git mv` a `completed/` al shippear · spec umbrella §11 marcar P1 done al cierre.
