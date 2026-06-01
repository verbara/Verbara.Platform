# Auditoría y diseño funcional: Trunks SIP y DID en Verbara.Platform

> **Tipo:** auditoría + diseño funcional (NO implementación). Evidencia real del repo (file:line). Lo marcado "no verificado" no pudo confirmarse en código.
> **Fecha:** 2026-06-01.
> **Repos auditados:** `Verbara.Platform` (Api + Storage), `Verbara.Platform.Web` (admin React), `Verbara.Sdk.Pro` (Dialer + Realtime — la telefonía vive acá), `Verbara.Sdk` (no aporta modelos de trunk/DID).
> **Estado:** aprobado como diseño funcional. Backlog en §10; ninguna tarea ejecutada aún.

---

## 1. Resumen ejecutivo

Verbara tiene un **backend de telefonía razonablemente completo** (modelo `Trunk` con los campos esenciales de un trunk PJSIP de proveedor + `did_routes` + `outbound_routes` + sync a Asterisk Realtime con defaults seguros hardcodeados), pero el **frontend de telefonía está drásticamente incompleto y desalineado con el backend**. Tres hallazgos dominan:

1. **El formulario de Trunk del front es un stub de 5 campos** (`name`, `displayName`, `type`, `maxChannels`, `isActive`). **NO expone** transport, codecs, autenticación (user/pass), URI de registro, IP-ACL (`matchHost`) ni context. Resultado verificado: **un trunk creado desde la UI no tiene auth ni registro ni identify → es no-funcional contra un proveedor real**. Hoy la única forma de crear un trunk operativo es por `curl`/API (exactamente como lo documenta el manual `06-canal-voz-sip.md`). Evidencia: `Verbara.Platform.Web/src/admin/trunks/trunk-form.tsx:25-31` (schema zod con 5 campos) y `:90-96` (la mutación solo envía esos 5).
2. **No existe ninguna pantalla de DID / rutas entrantes en el front.** El backend está completo (`DidRouteEndpoints` + `IDidRouteStore` + migración `026_DidRoutes.sql`), pero no hay ruta en el router, ni entrada en el sidebar, ni componente. Los DID solo se configuran por `curl`.
3. **Bug de persistencia de `MatchHost` (IP-ACL).** `Trunk.MatchHost` está en el modelo, en los DTOs y se sincroniza a Asterisk Realtime (`ps_endpoint_id_ips`), **pero NO se persiste en la tabla `trunks`** (no hay columna ni aparece en SELECT/INSERT/UPDATE de `PostgresTrunkStore`). Consecuencia verificada: `GET /admin/trunks/{id}` siempre devuelve `matchHost=null`, y un `PUT` que no reenvíe `matchHost` **borra el identify IP-ACL en Asterisk** (el sync hace `DeleteIdentify` cuando recibe null) → editar cualquier otro campo de un trunk IP-ACL rompe silenciosamente la identificación de llamadas entrantes.

**Recomendación clara: SÍ implementar un wizard de creación de Trunk**, guiado por plantilla de proveedor, que además cree la ruta saliente y el DID/ruta entrante en un solo flujo — porque hoy la tarea es **imposible desde la UI** y exige conocimiento de Asterisk/PJSIP que un operador SMB no tiene. El wizard convierte una tarea de `curl` + conocimiento PJSIP en un asistente de ~5 minutos. Detalle y justificación en §5 y §11.

---

## 2. Estado actual encontrado

### Backend (`Verbara.Platform` + `Verbara.Sdk.Pro`)

**Modelo Trunk** — `Verbara.Sdk.Pro.Dialer/Models/Trunk.cs`: `Id`, `Name`, `DisplayName?`, `Type` (`TrunkType`: Sip/Iax2/Dahdi/**Pjsip default**), `IsActive` (def `true`), `MaxChannels`, `Transport?`, `Codecs?`, `AuthUsername?`, `AuthPassword?`, `RegistrationUri?`, `ClientUri?`, `Context?`, `MatchHost?` (IP/CIDR para IP-ACL).

**Endpoints Trunk** — `Verbara.Platform.Api/Endpoints/TrunkEndpoints.cs`: CRUD bajo `/admin/trunks` + `/active` + `/by-name/{name}`; `.RequireAuthorization("AdminOnly").RequireOperationalTenant()` (operativo → rechaza tenant `platform` con 409). DTOs `CreateTrunkRequest`/`UpdateTrunkRequest`/`TrunkDto` exponen **todos** los campos (excepto `AuthPassword` que **no se serializa de vuelta** — bien). Valida `MatchHost` como IP o CIDR (`IsValidMatchHost`, `:197-211`); `ParseTrunkType` cae a `Pjsip` por default. Audita create/update/delete (`category="config"`).

**Persistencia Trunk** — `Verbara.Sdk.Pro.Dialer.Storage.Postgres/PostgresTrunkStore.cs`: columnas `id,name,display_name,type,is_active,max_channels,transport,codecs,auth_username,auth_password,registration_uri,client_uri,context`. **`match_host` ausente** del SELECT/INSERT/UPDATE y de la migración `V001__DialerSchema.sql` (tabla `trunks`). ⇒ **bug §1.3**.

**Sync a Asterisk** — `RealtimeSyncingTrunkStore` (decorador) llama `IRealtimeSyncService.SyncTrunkAsync` en cada create/update y `RemoveTrunkAsync` en delete (`Verbara.Sdk.Pro.Realtime/Decorators/RealtimeSyncingTrunkStore.cs`).

**DID / rutas entrantes** — modelo `DidRoute` (`Verbara.Platform.Routing.Inbound/DidRoute.cs`): `RouteId`, `TenantId`, `Did`, `QueueId`, `IsActive`, timestamps. **Destino = SOLO cola** (no IVR/flow/agente/campaña). Migración `026_DidRoutes.sql`: PK `(tenant_id,route_id)`, **UNIQUE `(tenant_id,did)`**, FK a `tenants` con `ON DELETE CASCADE`, índice parcial `WHERE is_active`. `IDidRouteStore` (InMemory + Postgres, ambos enforzan unicidad → 409). `DidRouteEndpoints.cs` CRUD `/admin/did-routes` (`AdminOnly` + `RequireOperationalTenant`). **No valida formato E.164** (solo no-vacío + `QueueId` válido).

**Rutas salientes** — `OutboundRoute` (`Verbara.Sdk.Pro.Dialer/Models/OutboundRoute.cs`): `Pattern`, `PatternType` (Prefix/Regex/DigitMask), `TrunkId`, `OverflowTrunkId?`, `DialPrefix?`, `Priority`, `CampaignId?`. `OutboundRouteEndpoints.cs` CRUD `/admin/routes` + `/reorder`. Resolver `PostgresOutboundRouteResolver`: **solo prefix-match** (`LIKE pattern || '%'`), campaña-scoped primero, `priority DESC`. ⚠️ Latente: **`OverflowTrunkId` no se consulta** en el resolver; `Regex`/`DigitMask` no se implementan (solo prefix).

**Caller-ID / Holiday** — `CallerIdPool`/`CallerIdEntry` + `HolidayCalendar`/`Holiday` (Pro Dialer). Nota: `CallerIdEntry.AreaCode`, `Holiday.AllowedStart/EndTime` están en el modelo pero **no en el schema** (mismatch menor, no bloqueante). Caller-ID saliente del agente vive en `TenantOptions.OutboundCallerId` (3B.2d.1, JSONB).

**Queue (campos de routing entrante)** — `Queue.cs`: `Hours` (HoursOfOperation JSONB), `OverflowRule`, `SlaTargets`, `AutoAnswerDefault`. **No** hay recording/IVR/fallback en el DID; horario de negocio vive en la cola, no en el DID.

### Frontend (`Verbara.Platform.Web`)

| Pantalla | Archivo | Campos expuestos | Estado |
|---|---|---|---|
| Trunks (lista) | `src/admin/trunks/trunks-page.tsx` | name, displayName, type, maxChannels, status | ✅ existe |
| Trunk (form) | `src/admin/trunks/trunk-form.tsx` | **solo 5**: name, displayName, type, maxChannels, isActive | ⚠️ **stub** |
| Rutas salientes | `src/admin/routes/{routes-page,route-form}.tsx` | priority, pattern, patternType, trunk, overflowTrunk, dialPrefix | ✅ existe (drag-reorder, moderadamente técnico) |
| Caller-ID Pools | `src/admin/caller-id-pools/*` | name + entries (phone, areaCode) | ✅ existe |
| **DID / rutas entrantes** | — | — | ❌ **NO existe** |
| Setup wizard | `src/admin/setup/setup-wizard.tsx` | queue → agent → channel(Voice incluido) → test | ⚠️ NO configura trunk/DID/extensión |

Hooks: `use-trunks.ts` `TrunkSummary` = `{id,name,displayName,type,isActive,maxChannels}` (6 campos — **sin** los de conexión). No hay `use-did-routes`. **No hay componente `Stepper` reutilizable**; `setup-wizard.tsx` y `campaign-wizard.tsx` (steps: basic/dialing/schedule/compliance/contacts) usan un indicador de pasos manual + `FormProvider` → **patrón a espejar**. i18n telefonía ≈95 claves (en-US/es-419/pt-BR).

### Documentación

`docs/manuales/smb/06-canal-voz-sip.md` (reconstruido 2026-06-01) documenta trunk vía `POST /admin/trunks` con **todos** los campos (`matchHost`, `authUsername/Password`, `registrationUri`, `codecs`, `transport`, `context`) y DID vía `POST /admin/did-routes` — todo por `curl`. Esto **confirma** que la UI no cubre el flujo: el manual asume API. Hay también `07-validacion-e2e.md` (SIPp) y `08-troubleshooting-sip.md`.

### Infraestructura / Asterisk

**Mapeo `SyncTrunkAsync`** (`Verbara.Sdk.Pro.Realtime/Engine/RealtimeSyncEngine.cs:279-403`), `id = t-{trunk.Id}`:

- **Configurables desde el Trunk:** `transport` (def `transport-udp`), `allow`=codecs (def `ulaw,alaw`), `context` (def `from-trunk`), `auth` user/pass (`authtype=userpass`; solo si `AuthUsername`), `ps_registrations` (solo si `RegistrationUri`; `outbound_auth` reusa el auth), `ps_endpoint_id_ips.match`=`MatchHost` (solo si seteado; si null → **DeleteIdentify**).
- **Hardcodeados (no per-trunk):** `disallow=all`, `direct_media=no`, `force_rport=yes`, `rewrite_contact=yes`, `rtp_symmetric=yes`, `dtmf_mode=rfc4733`, `aor.max_contacts=1`/`qualify_frequency=30`, `set_var=TENANT_ID={tenant}` (con validación anti-inyección de `;=\r\n`).
- **NUNCA se setean (gaps):** `media_encryption`/**SRTP**, `outbound_proxy`, `from_user`, `from_domain`, `nat`, `identify_by`, `callerid` del trunk. No hay propiedad en `PjsipEndpointRow`/`PjsipRegistrationRow` para ellos.
- **Transports disponibles** (reference-smb): `transport-udp/tcp/ws/wss`. **NO hay `transport-tls`** ⇒ un proveedor que exija TLS+SRTP **no es soportable hoy** (gap real para algunos proveedores; la mayoría de trunks PSTN SMB aceptan UDP+ulaw).

**Dialplan** `docker/asterisk-config/extensions.conf`: `[from-trunk]`→`Stasis(verbara,inbound,${EXTEN})`; `[stasis-queue]`→`Queue(${QUEUE_NAME})`; `[outbound-agent]`→`Set(CALLERID)`+`Dial(PJSIP/${TRUNK}/${EXTEN})`; `[transfer-agent]`; `[from-internal]`, `[agentassist]` (AudioSocket).

---

## 3. Modelo mínimo recomendado para Trunk SIP

> El sistema **ya aplica defaults seguros** para todo lo media/NAT/DTMF (§2 infra). El wizard **no debe preguntar** por eso: lo deja en avanzado/solo-lectura.

| Campo | Obligatorio | Básico/Avanzado | Descripción | Default recomendado | Riesgo si falta |
|---|---|---|---|---|---|
| `name` | Sí | Básico | Id lógico del trunk | — | No se puede referenciar en rutas |
| `displayName` | No | Básico | Nombre visible | =`name` | Solo cosmético |
| Proveedor (template) | Sí (UI) | Básico | Twilio/Telnyx/Flowroute/VoIP.ms/Genérico — preselecciona auth-mode/codecs/host | Genérico | Operador debe saber PJSIP a mano |
| Modo de auth | Sí | Básico | **IP-ACL** vs **Registro (user/pass)** — define qué campos pedir | según proveedor | Trunk no autentica → inbound/outbound fallan |
| `matchHost` (IP/CIDR) | Sí *si IP-ACL* | Básico | IP/rango del proveedor que identifica el INVITE entrante | — | **Inbound rechazado (401)** o, peor, sin identify |
| `authUsername`+`authPassword` | Sí *si Registro* | Básico | Credenciales digest | — | No registra contra el carrier |
| `registrationUri` (+`clientUri`) | Sí *si Registro* | Básico | URI del registrar del proveedor | — | No hay registro saliente |
| `maxChannels` | Sí | Básico | Tope de llamadas simultáneas | 10 (o por tier) | Sobre-suscripción / sin límite |
| Caller-ID saliente | No | Básico | Número que se muestra al llamar | trunk default | Llamadas salen "anonymous"/rechazadas |
| `codecs` | No | Avanzado | CSV de codecs | `ulaw,alaw` | Negociación falla si el proveedor no soporta el default |
| `transport` | No | Avanzado | `transport-udp/tcp/ws/wss` | `transport-udp` | Transporte incorrecto → no conecta |
| `context` | No | Avanzado | Contexto de dialplan entrante | `from-trunk` | Romper el ruteo entrante (NO tocar en SMB) |
| `isActive` | Sí | Básico | Habilitado | `true` | Trunk inerte |
| TLS/SRTP, outbound_proxy, from_user/domain, NAT, DTMF | — | Avanzado (**no soportado hoy**) | — | — | Bloquea proveedores TLS-only (gap §9) |

**Defaults seguros que ya da la plataforma (no preguntar):** `disallow=all`, `direct_media=no`, `force_rport=yes`, `rewrite_contact=yes`, `rtp_symmetric=yes`, `dtmf_mode=rfc4733`. Adecuados para el modelo NAT-symmetric de un SMB self-hosted.

**Dependencia de proveedor:** Twilio Elastic → IP-ACL (sin registro) + rangos de origination de Twilio + ulaw/opus. Telnyx → IP-ACL **o** registro. Flowroute/Bandwidth/Skyetel → IP-ACL. VoIP.ms → registro user/pass. ⇒ La **plantilla de proveedor** decide auth-mode + codecs sugeridos + ayuda contextual (dónde encontrar el rango IP / las credenciales).

---

## 4. Modelo mínimo recomendado para DID / números entrantes

> Hoy `did_routes` solo enruta a **cola**. La tabla recomendada abajo marca como "Avanzado / futuro" lo que requiere extender el backend.

| Campo | Obligatorio | Básico/Avanzado | Descripción | Default recomendado | Riesgo si falta |
|---|---|---|---|---|---|
| `did` (E.164) | Sí | Básico | Número marcado, como llega en el INVITE (E.164 sin `+`) | — | DID no matchea → llamada se cae |
| Destino: cola | Sí | Básico | Cola que recibe (única opción hoy) | — | **DID sin destino → fail-closed Hangup** |
| `isActive` | Sí | Básico | Activo/suspendido | `true` | DID inerte |
| Trunk asociado (visual) | No (lógico) | Básico | Por qué trunk entra (informativo; hoy el ruteo es por tenant+DID, no por trunk) | — | Confusión operativa |
| País / formato | No | Básico (UI) | Para validar/formatear E.164 | inferido | DID mal tipeado |
| Tipo (voz/SMS/WA) | No | Avanzado | Hoy solo voz | voz | — |
| Destino: IVR/Flow/Agente/Webhook | No | **Avanzado / futuro** | Requiere extender `did_routes` (hoy solo `queue_id`) | — | — |
| Horario / fallback fuera de horario | No | **Avanzado / futuro** | Hoy el horario vive en la **cola** (`Queue.Hours`), no en el DID | heredar de cola | Llamadas fuera de horario sin manejo explícito |
| Grabación sí/no | No | **Avanzado / futuro** | No existe campo en DID/cola | — | — |
| Caller-ID policy | No | Avanzado | No existe a nivel DID | — | — |

**Validaciones de DID que faltan hoy (backend):** formato E.164 (solo valida no-vacío). La unicidad `(tenant,did)` **sí** está (409).

---

## 5. Wizard recomendado para creación de Trunk

> **Decisión: SÍ.** Justificación en §11. El wizard reusa el patrón de `campaign-wizard.tsx` (indicador de pasos manual + `FormProvider`); idealmente se extrae primero un `WizardLayout` compartido.

**Paso 1 — Proveedor**
- Objetivo: elegir plantilla que preconfigura auth-mode + codecs + ayuda.
- Campos: tarjetas Twilio / Telnyx / Flowroute / VoIP.ms / **SIP genérico**.
- Validaciones: selección obligatoria.
- Errores a prevenir: que el operador tenga que adivinar auth-mode/codecs.
- Resultado: defaults sembrados + se decide si los pasos siguientes piden IP o credenciales.

**Paso 2 — Conexión + Autenticación** (fusiona "datos de conexión" + "auth"; el modo lo fijó el paso 1)
- Objetivo: que el trunk pueda hablar con el carrier.
- Campos (IP-ACL): `name`, `matchHost` (IP/CIDR del proveedor), `maxChannels`. (Registro): `name`, `authUsername`, `authPassword`, `registrationUri`, `clientUri?`, `maxChannels`.
- Validaciones: `matchHost` IP/CIDR válido (ya existe `IsValidMatchHost`); credenciales no vacías si registro; nombre único.
- Errores a prevenir: trunk sin auth ni IP (el stub actual); CIDR mal formado; puerto/transporte incorrecto (transporte en avanzado).
- Resultado: `Trunk` listo para crear.

**Paso 3 — Codecs y media (Avanzado, colapsado)**
- Objetivo: solo si el proveedor no acepta el default.
- Campos: `codecs` (multi-select sobre `ulaw,alaw,opus,g722`), `transport` (select), `context` (oculto/solo-lectura salvo modo experto).
- Validaciones: codecs no vacío.
- Errores a prevenir: trunk sin codecs; transporte TLS sin soporte (avisar que TLS/SRTP no está disponible — §9).
- Resultado: media negociable.

**Paso 4 — Ruta saliente**
- Objetivo: poder **llamar** por este trunk.
- Campos: `pattern` (prefijo, ej. `+`), `dialPrefix?`, `priority` (auto-asignada), `callerId` saliente.
- Validaciones: patrón no vacío; recordar que el resolver hoy es **prefix-match** (regex/digitmask no implementados — §9).
- Errores a prevenir: **trunk creado pero sin outbound route**; colisión de prioridad.
- Resultado: `OutboundRoute` creada apuntando al trunk.

**Paso 5 — DID / ruta entrante**
- Objetivo: poder **recibir** en este trunk.
- Campos: `did` (E.164), cola destino (select de colas activas), `isActive`.
- Validaciones: E.164 (a agregar backend); cola existe; DID único por tenant (ya 409).
- Errores a prevenir: **DID creado sin destino** (cola obligatoria); DID que no pertenece al trunk/tenant correcto (scoping por tenant).
- Resultado: `DidRoute` creada (o "lo configuro después").

**Paso 6 — Prueba de conectividad** (requiere backend nuevo — §9)
- Objetivo: confirmar registro/identify antes de activar.
- Acción: endpoint nuevo que ejecute AMI `pjsip show registrations` / `pjsip show endpoint t-{id}` / `pjsip show identifies` y reporte estado (Registered / Avail / identify presente).
- Validaciones: timeout, estado del módulo realtime.
- Errores a prevenir: activar un trunk que no registra; IP-ACL no recargado.
- Resultado: semáforo verde/rojo con diagnóstico accionable.

**Paso 7 — Resumen y activación**
- Objetivo: revisar + activar.
- Campos: read-only de todo + toggle `isActive`.
- Resultado: trunk + ruta saliente + DID activos en un solo flujo.

> **Reducción vs. el ejemplo de 8 pasos del pedido:** se fusionó "conexión" + "autenticación" (el auth-mode ya lo fija el proveedor) → 7 pasos. Menos fricción, mismo resultado.

---

## 6. Flujo recomendado para configuración de DID

**Ambos.** (a) **Módulo independiente** `/admin/did-routes` (lista + form CRUD) — el backend ya existe, es la pieza más barata y de mayor impacto, y cubre el día-a-día (alta/baja de números). (b) **Paso dentro del wizard de Trunk** (Paso 5) para el alta inicial guiada del primer DID junto con el trunk. El form del módulo y el paso del wizard comparten el mismo componente de campos. **Evitar "DID sin destino":** cola **obligatoria** en ambos (no permitir guardar sin destino); si el operador no la tiene aún, ofrecer "crear cola" inline o bloquear con CTA. Multi-DID por trunk: natural (varias filas `did_routes` por tenant). Multi-tenant: `did_routes` es tenant-scoped (FK + unique por tenant), el endpoint exige `RequireOperationalTenant`.

---

## 7. Validaciones necesarias

**Frontend:** `matchHost` IP/CIDR; E.164 del DID (regex `^\+?[1-9]\d{6,14}$`); credenciales no vacías si registro; codecs no vacío; cola obligatoria en DID; nombre de trunk único (chequeo `by-name`); avisar TLS/SRTP no soportado.

**Backend:** ya valida `MatchHost` (`IsValidMatchHost`), unicidad DID (409), `QueueId` válido, tenant operativo, anti-inyección de `TENANT_ID`. **Falta:** validación E.164 del DID; validar que el `QueueId` del DID **exista** (hoy solo valida formato, no existencia); validar codecs contra una lista soportada.

**Contra Asterisk/PJSIP:** prueba de conectividad (registro/identify) — endpoint nuevo; recargar `res_pjsip_endpoint_identifier_ip.so` tras alta IP-ACL (hoy manual, el manual 06 lo nota); verificar que el transport pedido exista (no pedir `transport-tls` que no está provisionado).

**Negocio multi-tenant:** trunk/DID/route scoped por tenant (FK + `RequireOperationalTenant`, rechaza `platform`); `MatchHost` no debe solaparse entre tenants (dos tenants con el mismo rango IP → ambigüedad de identify — **no verificado** si hay guard; recomendar chequeo); el `did` debe ser único por tenant (ya), idealmente único global por número real (no garantizado hoy).

---

## 8. Impacto en repositorios

**Verbara.Platform**
- **Fix bug `MatchHost`:** persistirlo (depende de Pro, abajo) y/o exponerlo correcto en `TrunkDto`.
- Validación E.164 + existencia de cola en `DidRouteEndpoints`.
- Endpoint nuevo de **prueba de conectividad** (AMI `pjsip show registrations/endpoint/identifies` vía el `VerbaraServer` líder ya cableado) → DTO en `ApiJsonContext`.
- (Opcional) endpoint `reload` IP-ACL.

**Verbara.Platform.Web**
- **Completar `trunk-form.tsx`**: agregar campos de conexión/auth/IP-ACL con split básico/avanzado (accordion), y extender `TrunkSummary`/payloads en `use-trunks.ts`.
- **Nuevo módulo DID** `/admin/did-routes` (page + form + `use-did-routes.ts` + ruta + sidebar + i18n ×3).
- **Wizard de Trunk** (extraer `WizardLayout` del patrón `campaign-wizard`).
- i18n ×3 para todos los campos/pasos nuevos.

**Verbara.Sdk.Pro**
- **Migración + `PostgresTrunkStore`**: agregar columna `match_host` a `trunks` + incluirla en SELECT/INSERT/UPDATE (cierra el bug §1.3). Bump Pro + republicar a GitHub Packages.
- (Futuro) soporte TLS/SRTP: nuevas propiedades en `PjsipEndpointRow` (`media_encryption`) + provisión de `transport-tls` por tenant en `ProvisionTenantAsync`.
- (Futuro) implementar Regex/DigitMask + usar `OverflowTrunkId` en `PostgresOutboundRouteResolver`.
- (Futuro) extender `DidRoute`/sync si se agregan destinos IVR/Flow/Agente.

**Verbara.Sdk**
- Sin cambios esperados (no aporta modelos de trunk/DID). **No verificado** un uso indirecto vía AMI/ARI clients para la prueba de conectividad (ya se consumen).

**Proyecto privado no visible**
- **No verificable.** Si existe lógica de proveedores/branding/licencia fuera de los 4 repos, las plantillas de proveedor podrían vivir ahí. Marcado como información no verificable.

---

## 9. Riesgos y decisiones pendientes

**Hechos encontrados en código:**
- Trunk form = 5 campos; no produce trunk funcional (`trunk-form.tsx:25-31,90-96`).
- No hay UI de DID (sin ruta/sidebar/componente).
- `MatchHost` no se persiste en `trunks` (`PostgresTrunkStore.cs` columnas; sin columna en `V001`).
- `did_routes` solo enruta a cola (`DidRoute.cs`).
- Resolver saliente solo prefix; `OverflowTrunkId` no consultado; Regex/DigitMask no implementados.
- Sin `transport-tls` ni `media_encryption` (SRTP) → proveedores TLS-only no soportados.
- DID sin validación E.164.

**Inferencias técnicas:**
- Un trunk creado por UI hoy queda inutilizable contra cualquier carrier real (combinación: sin auth + sin registro + sin identify).
- Editar un trunk IP-ACL desde una UI que no reenvíe `matchHost` borrará el identify (riesgo operativo alto una vez exista el form completo si no se arregla la persistencia primero).

**Recomendaciones:**
- Wizard SÍ (§5/§11); módulo DID independiente + paso en wizard (§6); arreglar persistencia `MatchHost` **antes** de exponer el campo en la UI (orden importa).

**No verificable por falta de acceso al proyecto privado:**
- Plantillas/catálogo de proveedores, branding por reseller, y cualquier policy de caller-ID/compliance que viva fuera de los 4 repos.

---

## 10. Backlog técnico propuesto

| Prioridad | Tarea | Repositorio | Tipo | Riesgo | Esfuerzo |
|---|---|---|---|---|---|
| **P0** | Persistir `match_host` en `trunks` (migración + store SELECT/INSERT/UPDATE) | Verbara.Sdk.Pro | Bugfix / data-integrity | Alto (rompe IP-ACL en edits) | S |
| **P0** | Completar `trunk-form` (conexión/auth/IP-ACL) + split básico/avanzado + extender `use-trunks` | Verbara.Platform.Web | Feature / UX | Alto (UI hoy no sirve) | M |
| **P1** | Módulo DID `/admin/did-routes` (page+form+hook+router+sidebar+i18n) | Verbara.Platform.Web | Feature | Medio (inbound invisible) | M |
| **P1** | Validación E.164 + existencia de cola en `DidRouteEndpoints` | Verbara.Platform | Hardening | Medio | S |
| **P1** | Wizard de Trunk (7 pasos) + `WizardLayout` extraído | Verbara.Platform.Web | Feature / UX | Medio | L |
| **P2** | Endpoint de prueba de conectividad (AMI registrations/endpoint/identifies) + paso 6 | Verbara.Platform (+Web) | Feature | Medio | M |
| **P2** | Plantillas de proveedor (Twilio/Telnyx/Flowroute/VoIP.ms/genérico) en el wizard | Verbara.Platform.Web | Feature / UX | Bajo | M |
| **P3** | Soporte TLS+SRTP (transport-tls + `media_encryption` en `PjsipEndpointRow` + provisión por tenant) | Verbara.Sdk.Pro | Feature | Medio (proveedores TLS-only) | L |
| **P3** | Resolver saliente: implementar Regex/DigitMask + usar `OverflowTrunkId` | Verbara.Sdk.Pro | Bugfix/Feature | Bajo | M |
| **P3** | Extender destinos de DID (IVR/Flow/Agente/Webhook) | Verbara.Platform + Pro | Feature | Bajo | L |

Esfuerzo: S≈≤1d, M≈2–4d, L≈1–2sem.

---

## 11. Recomendación final: ¿wizard sí/no y cómo?

**SÍ, wizard — con esta secuencia de entrega (no todo de una):**

1. **Primero los fixes que desbloquean (P0):** persistir `MatchHost` (Pro) **y luego** completar el `trunk-form` con los campos de conexión/auth/IP-ACL en un split **básico/avanzado**. Esto solo ya hace que la UI **pueda** crear un trunk funcional (hoy no puede), sin esperar al wizard.
2. **En paralelo, el módulo DID independiente (P1):** alto impacto, bajo costo (backend ya existe), cierra el agujero de "inbound invisible en la UI".
3. **Luego el wizard (P1):** guiado por **plantilla de proveedor**, que orquesta trunk → ruta saliente → DID → prueba → activación en un flujo.

**Por qué el wizard mejora la experiencia (no es cosmético):** la configuración de un trunk SIP tiene **dependencias cruzadas invisibles** para un SMB — un trunk sin outbound route no llama, un DID sin cola se cae, un IP-ACL sin recargar no identifica, credenciales incompletas no registran. Un formulario plano (incluso completo) **no previene** esos estados rotos; un wizard con plantilla de proveedor (a) pide **solo** lo que ese proveedor necesita (IP-ACL vs registro), (b) fija defaults seguros para todo lo media/NAT/DTMF que ya está hardcodeado, (c) **fuerza** crear ruta saliente + DID en el mismo flujo (elimina "trunk huérfano" y "DID sin destino"), y (d) valida conectividad **antes** de activar. Convierte una tarea que hoy exige `curl` + conocimiento de PJSIP en un asistente de ~5 minutos — exactamente lo que necesita el track SMB self-hosted "primer cliente pagando".

**Cómo implementarlo:** reusar el patrón de `campaign-wizard.tsx` (steps manuales + `react-hook-form` `FormProvider`), extrayendo un `WizardLayout` compartido; campos técnicos (codecs/transport/context/SRTP) escondidos bajo "Avanzado" colapsado; plantillas de proveedor como tarjetas en el paso 1; el paso de conectividad detrás de un endpoint nuevo. Mantener el form CRUD plano (ya completado en P0) como el camino "experto/editar", y el wizard como el camino "crear desde cero".

---

## 12. Verificación (cómo validar la implementación cuando se haga)

- **Backend:** `dotnet test` (Api.Tests + Pro Realtime/Dialer); migración `match_host` aplica + round-trip (crear con MatchHost → GET lo devuelve → editar otro campo NO borra el identify); E.164 rechaza inválidos.
- **Front:** vitest del trunk-form (envía todos los campos) + DID form + wizard (mock hooks); i18n parity ×3; tsc/eslint 0.
- **Lab E2E (reusar `/tmp/sipharness` + reference-smb):** crear trunk IP-ACL por wizard → SIPp `-i {matchHost}` entra → DID→cola; crear trunk de registro → `pjsip show registrations` = Registered; ruta saliente → `/voice/dial` sale por el trunk. Gotchas de host-run en la memoria `reference_local_infra_gotchas`.
