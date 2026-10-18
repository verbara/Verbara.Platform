# Manual SMB · 04 — Canal WebChat

> **Audiencia:** operador con stack arriba + setup inicial completo (manual [03](03-setup-inicial.md)).
> **Tiempo:** 20 minutos.
> **Pre-requisitos:** acceso al HTML/template del sitio web del cliente donde se va a embeber el widget. Si todavía no lo tenés, usá la página de demo que sirve Verbara (paso 4).

El canal WebChat permite que un visitante anónimo del sitio web del cliente inicie una conversación que aterriza en una queue de Verbara y es atendida por un agente — sin instalar software, sólo abriendo el sitio.

> ⚠️ **Tenant operacional, no `platform`.** Desde ADR-0027, los endpoints operacionales (canales, queues, agentes) **rechazan el tenant raíz `platform` con HTTP 409**. El setup (manual 03) creó un tenant **Customer** además del `platform`; **todo lo de este manual se hace contra ese tenant Customer**. En los `curl` de abajo, `$CUSTOMER_TENANT_ID` es el id de ese tenant (lo obtenés del paso final del setup o en `/admin/tenants` logueado como Platform Admin). Si trabajás como Platform Admin, también podés **impersonar** el Customer desde la UI.

## Arquitectura real del canal

```
   Browser visitante (sitio del cliente)
         │
         │  (1) carga el SDK JS desde el Web nginx de Verbara
         │      https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js
         ▼
   SDK (script tag) → dibuja la burbuja flotante y, al abrir,
   carga lazy un iframe aislado en /webchat/embed/
         │
         │  (2) el visitante completa el formulario inicial (nombre + email) y,
         │      al enviarlo, el iframe POST /api/v1/webchat/sessions  { "tenantId": "<customer>" }
         │      → { "sessionId", "wsUrl": "/ws/webchat/{sessionId}",
         │          "visitorAddress": "webchat-…", "contactId", "conversationId" }   (anónimo, sin auth)
         │
         │  (3) abre WebSocket  wss://verbara.tu-dominio.com/ws/webchat/{sessionId}
         ▼
   Platform.Api · WebChat connector
         │
         │  (4) primer mensaje → pipeline (dedupe/contact/conversation/persist)
         │      → InboundRouter → queue (defaultQueueId o 1ª queue activa)
         │      → Switchboard.AssignToQueueAsync
         ▼
   QueueDistributionWorker ofrece la conversación a un agente disponible
         │
         │  (5) evento SSE  conversation.offered  → la UI del agente
         ▼
   Agente la ve aparecer en /agent → Aceptar → responde → el visitante recibe
```

> 💡 Cada sesión es **un contacto y una conversación**: al crearla, Verbara crea el contacto anónimo con la dirección `webchat-…` (`visitorAddress`) y su conversación, y todos los mensajes del visitante caen en esa conversación mientras siga abierta; si se cerró, el siguiente mensaje abre una conversación nueva del mismo contacto y entra a la cola. La respuesta del agente se entrega por la dirección del visitante al WebSocket de la sesión (también si el visitante recargó la página o tiene dos pestañas abiertas), y sólo queda como enviada si un WebSocket la recibió.
>
> ⚠️ **Las respuestas del agente sólo se entregan de forma fiable con una única réplica del API.** Las sesiones y los WebSockets viven en el proceso del API, y la respuesta se entrega dentro de la propia petición del agente: sólo llega al visitante si esa petición cae en la réplica que tiene su WebSocket. En otra réplica queda guardada como fallida (`SESSION_NOT_CONNECTED`), igual que si el visitante se hubiera ido. Las sticky sessions no lo resuelven, porque fijan las peticiones del agente por la afinidad del agente, no la del visitante.
>
> **En Kubernetes**, el chart despliega el API con **2 réplicas** por default (`api.replicas: 2`) y un HPA que escala entre 2 y 8 (`api.hpa`). Para usar WebChat, dejá el API en **una sola réplica** hasta que Verbara entregue las respuestas entre réplicas: `--set api.replicas=1 --set api.hpa.enabled=false` (con el HPA activo, su `minReplicas: 2` vuelve a subir el deployment a 2). El compose SMB (`docker-compose.reference-smb.yml`) ya corre un único `platform-api`.

> ⚠️ **El WebSocket del paso (3) entra por `/ws/`.** El `nginx-gateway` del compose ya lo reenvía al API con los headers de upgrade. Si ponés **tu propio proxy inverso** delante de Verbara (TLS, un balanceador, Cloudflare), tiene que reenviar `/ws/` al API como WebSocket: HTTP/1.1, los headers `Upgrade` y `Connection: upgrade`, y un timeout de lectura largo (el gateway usa 1 h; menos de 2 minutos corta una conversación quieta). Sin eso, el navegador recibe la página de la consola en vez de un WebSocket y el visitante no puede chatear. Con nginx:
>
> ```nginx
> location /ws/ {
>   proxy_pass http://<api>:5000;
>   proxy_http_version 1.1;
>   proxy_set_header Upgrade $http_upgrade;
>   proxy_set_header Connection "upgrade";
>   proxy_set_header Host $host;
>   proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
>   proxy_read_timeout 3600s;
> }
> ```

> 💡 El SDK (`verbara-webchat.js`) sólo pinta la burbuja y carga el iframe. Las llamadas al API las hace el **iframe** (`/webchat/embed/`), aislado del sitio del cliente. El operador no necesita saber esto para embeber, pero explica por qué sólo hay un `<script>` y ningún `<div>` que montar.

## 1. Configurar el canal WebChat (default queue)

El paso funcional es **decirle al canal a qué queue mandar los chats**. La sesión de WebChat es anónima y funciona aunque el canal no esté "activo"; lo que importa para el enrutado es la `defaultQueueId` (Tier 1). Si no la configurás, Verbara cae al **Tier 2: la primera queue activa del tenant** (la que creó el wizard). Configurarla explícitamente es lo recomendado.

Necesitás el id de la queue destino. Si seguiste el manual 03, ya tenés una queue (ej. `Atención General`). Listala:

```bash
$ curl -sS -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: $CUSTOMER_TENANT_ID" \
    http://{server-ip}:5000/api/v1/admin/queues | jq '.items[] | {id, name, isActive}'
```

### Vía API

```bash
$ curl -sS -X PUT http://{server-ip}:5000/api/v1/admin/channels/webchat \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: $CUSTOMER_TENANT_ID" \
    -H "Content-Type: application/json" \
    -d '{
      "isActive": true,
      "credentials": {
        "defaultQueueId": "<queue-id-de-arriba>"
      }
    }' | jq
```

> El endpoint es **`PUT`** y **mezcla** con lo guardado, clave por clave (desde Platform 2.28.0): una clave que omitís, o que mandás vacía, conserva su valor; un valor la reemplaza; `null` la borra. El body son sólo dos campos: `isActive` (bool) y `credentials` (mapa string→string, donde un valor puede ser `null`). `defaultQueueId` vive **dentro de `credentials`**. No existen campos `displayName`/`allowedOrigins`/`enabled` — los orígenes CORS se manejan aparte (ver §6).
>
> La respuesta (igual que `GET /admin/channels/webchat`) **nunca devuelve valores**: lista cada clave como `{ "key": "defaultQueueId", "isSet": true }`. Para confirmar qué queue quedó configurada, mirá el enrutado de un chat de prueba (§4), no la respuesta.

### Vía Web UI

1. Logueate como admin. Si sos Platform Admin, impersoná el Customer (o seleccioná el tenant Customer en el selector).
2. Andá a `/admin/channels`.
3. Encontrá la row `WebChat` → **Configurar**.
4. Marcá **Activo** y elegí la **Default queue** (`Atención General`).
5. **Guardar.**

## 2. Snippet HTML para embeber el widget

En el HTML del sitio del cliente, antes del cierre de `</body>`:

```html
<script
  src="https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js"
  data-tenant-id="TENANT_CUSTOMER_ID"
  data-locale="auto"
  data-position="bottom-right"
  data-greeting="¡Hola! ¿Cómo podemos ayudarte?">
</script>
```

Atributos **que el SDK realmente lee** (`dataset`):

| Atributo | Valor | Notas |
|---|---|---|
| `data-tenant-id` | id del tenant **Customer** que recibe las conversaciones | **Obligatorio.** Es el mismo `$CUSTOMER_TENANT_ID`. No es secreto (la sesión es anónima). |
| `data-locale` | `auto` / `es-419` / `pt-BR` / `en-US` | `auto` detecta del navegador del visitante. |
| `data-position` | `bottom-right` / `bottom-left` | Posición de la burbuja flotante. Default: `bottom-right`. No hay posiciones arriba (`top-…`). |
| `data-greeting` | texto | Mensaje de bienvenida del panel. |
| `data-theme` | JSON, ej. `'{"primaryColor":"#1d4ed8"}'` | Theming. Es **un solo atributo con JSON**, no atributos sueltos por color. |
| `data-api-base` | URL base del API | **Opcional.** Default: mismo origen que sirve el JS. Setealo sólo si el API está en otro host que el Web (ej. `https://api.tu-dominio.com`). |

> No existen `data-queue-id`, `data-primary-color`, `data-bubble-bg`, `data-offline-message` ni `data-bubble-icon-color`. Para color usá `data-theme`. Para forzar una queue distinta a la default, hoy se hace por config del canal (§1), no por atributo del widget.

## 3. Cómo se elige la queue y el agente

### 3.1 A qué queue va el chat

El enrutado de un WebChat entrante resuelve la queue en dos tiers (`DefaultQueueFallbackMiddleware`, el eslabón más interno de la cadena de routing):

1. **Tier 1** — la `defaultQueueId` configurada en el canal (§1), validada como **existente + activa**.
2. **Tier 2** — si no hay default (o quedó apuntando a una queue borrada/inactiva), la **primera queue activa del tenant**.

Si el tenant no tiene **ninguna** queue activa, la conversación no se enruta (queda registrada pero sin owner). Por eso el setup del manual 03 crea al menos una queue.

> Las reglas de routing explícitas (ej. mapeo canal→queue) tienen prioridad sobre este fallback; el fallback sólo actúa cuando nada upstream resolvió una queue. En un SMB típico con una sola queue, el Tier 2 alcanza.

### 3.2 Qué agente dentro de la queue — membership ejecutivo

Una vez en la queue, Verbara ofrece la conversación al agente desde el **pool ejecutivo** de `queue_memberships`:

- El agente debe ser **miembro** de la queue (row en `queue_memberships`).
- `IsExcluded` debe ser `false`.
- `AllowedChannels` debe ser `NULL` (todos los canales) **o** contener `"voice"`/`"webchat"`… — para WebChat, debe incluir `WebChat` (case-insensitive) o ser `NULL`.
- El agente debe estar **disponible** (presencia `Available` + capacity libre).

Si querés que un agente reciba **sólo** WebChat (nada de voz/email), editá su membership en `/admin/agents/{agentId}/queues` y dejá `Allowed channels = WebChat`. El badge **"Digital only"** confirma que ese agente no será timbrado por Asterisk (no se escribe en `queue_members` de voz).

`queue_memberships.penalty` reserva la queue por bandas: `0` = prioridad máxima; valores mayores se contactan sólo cuando los `0` están ocupados. El round-robin opera **dentro de cada banda de penalty**.

> 💡 §3.1 decide **a qué queue** va el chat. La membership decide **qué agente** lo atiende. Son dos capas independientes.

## 4. Probar el widget con la página de demo

Verbara sirve una página de demo en `/webchat/demo.html` con el snippet ya embebido. Útil para validar antes de tocar el sitio del cliente.

Abrí en el browser:

```
http://{server-ip}/webchat/demo.html
```

O con DNS configurado:

```
https://verbara.tu-dominio.com/webchat/demo.html
```

La demo trae `data-tenant-id="demo-tenant"` hardcodeado. **Para que el round-trip funcione contra tu instalación**, el tenant `demo-tenant` tiene que existir; en una instalación SMB real probá apuntando la demo a tu Customer: copiá el HTML de la demo a un archivo local y cambiá `data-tenant-id` por tu `$CUSTOMER_TENANT_ID`, o simplemente embebé el snippet de §2 en cualquier página de prueba.

Vas a ver una burbuja flotante en la esquina; click → se abre el panel. También hay un botón "Open chat" que llama `VerbaraWebChat.open()` (API programática del SDK).

### 4.1 Iniciar conversación de prueba

1. En el panel del widget, completá el formulario inicial (nombre y email) y envialo.
2. Escribí un mensaje: `"Hola, esto es una prueba"`.
3. **Enviar.**

(Al enviar el formulario se crea la sesión, con su contacto y su conversación. El primer mensaje enruta la conversación a la queue y dispara la oferta al agente.)

### 4.2 Validar desde el lado del agente

En otra pestaña del browser:

1. Logueate como `agente1` con la password del wizard (`http://{server-ip}/login`).
2. Aterrizás en **`/agent`** ("Select a conversation to begin.").
3. La conversación que iniciaste como visitante aparece **en vivo** en el inbox como **offered** (llega un toast "New WebChat conversation offered" + la card en la lista). Esto es el evento SSE `conversation.offered`.
4. Click **Aceptar** → se abre la vista de mensajes (`/agent/conversation/{id}`).
5. Escribí `"Hola, ¿en qué puedo ayudarte?"` → Enviar.
6. Volvé a la pestaña del widget → la respuesta del agente aparece.

✅ Round-trip completo. WebChat funciona de punta a punta.

> Si la conversación aparece en el inbox pero **no** llegó el toast, revisá que el agente sea miembro de la queue con `WebChat` permitido (§3.2) y que esté `Available`.

## 5. Personalizar look & feel (opcional)

El theming se pasa como **un** atributo `data-theme` con JSON:

```html
<script
  src="https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js"
  data-tenant-id="TENANT_CUSTOMER_ID"
  data-locale="auto"
  data-position="bottom-right"
  data-theme='{"primaryColor":"#1d4ed8","fontFamily":"Inter"}'
  data-greeting="¡Hola! ¿Cómo podemos ayudarte hoy?">
</script>
```

> 💡 El branding base del tenant (logo, colores) se configura en `/admin/tenant-settings → Branding`; el widget toma esos valores por default. `data-theme` los sobre-escribe a nivel embed.

## 6. CORS — permitir el dominio del cliente

El widget (servido por el Web nginx) hace fetch al API. El navegador exige que el origen del sitio del cliente esté en la whitelist CORS del API.

**El CORS no es por canal.** Se configura con la variable de entorno **`CORS_ORIGINS`** del contenedor `api` (CSV de orígenes exactos). En `docker-compose.reference-smb.yml`:

```yaml
    environment:
      # CORS — sólo los orígenes del front que servís al cliente.
      CORS_ORIGINS: ${CORS_ORIGINS:-http://localhost,https://localhost}
```

Para permitir el sitio del cliente, seteá la variable (en tu `.env` o en el compose) y **reiniciá el contenedor api** — `CORS_ORIGINS` se lee **una sola vez al arranque**:

```bash
# .env
CORS_ORIGINS=https://tu-sitio.com,https://www.tu-sitio.com,http://localhost
```

```bash
$ docker compose -f docker/docker-compose.reference-smb.yml up -d --force-recreate api
```

> ⚠️ **No es on-the-fly.** Cambiar `CORS_ORIGINS` requiere recrear el contenedor `api`. Enumerá los hosts exactos (incluí `www.` si aplica); **no uses `*`** en producción — si `CORS_ORIGINS` está vacío el default es `*`, lo que desactiva la protección CORS y sólo debería verse en pruebas locales.

**Síntoma de CORS faltante** (consola devtools del visitante):

```
Access to fetch at 'https://verbara.tu-dominio.com/api/v1/webchat/sessions'
from origin 'https://tu-sitio.com' has been blocked by CORS policy
```

→ agregá `https://tu-sitio.com` a `CORS_ORIGINS` y recreá el `api`.

## 7. Ver la actividad del canal

No hay un endpoint "resumen por canal". La actividad de WebChat se ve con la analítica general:

- **UI:** `/admin/analytics` (dashboard) y la lista de conversaciones (`/admin/conversations`) filtrando por canal `WebChat`.
- **API dashboard:**

```bash
$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: $CUSTOMER_TENANT_ID" \
    "http://{server-ip}:5000/api/v1/analytics/dashboard?from=2026-05-01&to=2026-05-31" | jq
```

- **CDR / intervalos:** `/api/v1/analytics/cdr`, `/api/v1/analytics/intervals` (agregados por agente/queue, incluyen el tráfico de WebChat).
- **Métricas de queue en vivo:** los wallboards de operaciones consumen las métricas por queue, que reflejan los WebChat encolados/atendidos.

## Próximo paso

→ [05-canal-email.md](05-canal-email.md) — configurar SMTP outbound + IMAP inbound (Gmail App Password, M365 OAuth, o SMTP genérico).
