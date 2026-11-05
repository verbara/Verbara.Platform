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
         │      al enviarlo, el iframe POST /api/v1/webchat/sessions  { "tenantId": "<customer>", … }
         │      → { "sessionId", "wsUrl": "/ws/webchat/{sessionId}",
         │          "visitorAddress": "webchat-…", "contactId", "conversationId",
         │          "identityLevel" }   (sin auth; el nivel de identidad está en §8)
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

> 💡 Cada sesión tiene **su propia dirección y su propia conversación**: al crearla, Verbara genera la dirección `webchat-…` (`visitorAddress`) y una conversación nueva, y todos los mensajes del visitante caen en esa conversación mientras siga abierta; si se cerró, el siguiente mensaje abre una conversación nueva del mismo contacto y entra a la cola. Sin identidad (`Anonymous`), cada sesión es además un contacto propio; un visitante identificado (§8) junta sus sesiones en un solo contacto. La respuesta del agente se entrega por la dirección de la sesión de esa conversación al WebSocket (también si el visitante recargó la página o tiene dos pestañas abiertas), y sólo queda como enviada si un WebSocket la recibió.
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

Antes de embeber el widget, el canal WebChat del tenant tiene que estar **configurado y activo** (`isActive: true`): sin eso `POST /api/v1/webchat/sessions` responde `404` (el mismo `Tenant not found` que un tenant inexistente) y el widget no abre la conversación. Un tenant creado por API (sin pasar por el wizard) tiene que hacer el `PUT` de abajo con `"isActive": true`.

Además, conviene **decirle al canal a qué queue mandar los chats**: lo que importa para el enrutado es la `defaultQueueId` (Tier 1). Si no la configurás, Verbara cae al **Tier 2: la primera queue activa del tenant** (la que creó el wizard). Configurarla explícitamente es lo recomendado.

Necesitás el id de la queue destino. Si seguiste el manual 03, ya tenés una queue (ej. `Atención General`). Listala:

```bash
$ curl -sS -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: $CUSTOMER_TENANT_ID" \
    http://{server-ip}/api/v1/admin/queues | jq '.items[] | {id, name, isActive}'
```

### Vía API

```bash
$ curl -sS -X PUT http://{server-ip}/api/v1/admin/channels/webchat \
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
| `data-tenant-id` | id del tenant **Customer** que recibe las conversaciones | **Obligatorio.** Es el mismo `$CUSTOMER_TENANT_ID`. No es secreto (la sesión se abre sin autenticación). |
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
    "http://{server-ip}/api/v1/analytics/dashboard?from=2026-05-01&to=2026-05-31" | jq
```

- **CDR / intervalos:** `/api/v1/analytics/cdr`, `/api/v1/analytics/intervals` (agregados por agente/queue, incluyen el tráfico de WebChat).
- **Métricas de queue en vivo:** los wallboards de operaciones consumen las métricas por queue, que reflejan los WebChat encolados/atendidos.

## 8. Identidad del visitante

Cada `POST /api/v1/webchat/sessions` responde `identityLevel`, y el contacto (`GET /api/v1/contacts/{id}`) lleva el mismo dato calculado de sus direcciones:

| `identityLevel` | Cuándo | El contacto |
|---|---|---|
| `Verified` | `identityToken` verificó (§8.2) | Uno por `external_id`, en todas las sesiones y dispositivos. Tiene la dirección `webchat-external:<external_id>`. |
| `Unverified` | El navegador manda un `visitorId` que es un GUID versión 4 y el host activó el nivel 1 (§8.1) | Uno por navegador. Tiene la dirección `webchat-visitor:<hash>`; el `visitorId` nunca se guarda tal cual. |
| `Anonymous` | Ninguno de los dos | Uno por sesión, como siempre. |

- **El formulario no identifica.** `visitorName` y `visitorEmail` sólo completan datos: el nombre si el contacto no tiene, y `visitorEmail` como campo `webchatEmail` si no tiene uno. Nunca encuentran ni unen contactos, y `visitorEmail` nunca es una dirección del canal Email. Dos navegadores con el mismo email son dos contactos. `GET /api/v1/contacts?search=` encuentra el contacto por `webchatEmail`.
- **Los niveles no se fusionan.** Si alguien chatea sin login y después inicia sesión en tu sitio, queda con dos contactos: el del navegador y el verificado.
- **Las direcciones de identidad son de Verbara.** `POST` y `PUT /api/v1/contacts` rechazan una dirección WebChat que empiece con `webchat-visitor:` o `webchat-external:` con `400 reserved-contact-address`.

### 8.1 Nivel 1: el visitante que vuelve

Está **apagado por default**. Mientras esté apagado, Verbara trata todo `visitorId` como ausente: cada sesión es `Anonymous` y ningún contacto recibe una dirección `webchat-visitor:`. El nivel 2 no depende de esto.

Se prende en el contenedor `api` y se lee una sola vez al arranque:

```yaml
    environment:
      WebChat__VisitorIdentity__Level1Enabled: "true"
```

> ⚠️ **Prendelo sólo con la consola que marca al contacto no verificado ya desplegada.** Un contacto `Unverified` muestra al agente el historial del navegador, y la consola tiene que avisarle que esa identidad no está verificada. El orden está en `CHANGELOG.md`, sección *Upgrading*: desplegar la Platform, desplegar la release Web que renderiza `data-identity-level`, hacer que cada agente recargue la consola, y recién ahí setear la variable y reiniciar el `api`. Apagarla de nuevo corta los enlaces nuevos al instante; los contactos ya enlazados conservan su dirección y siguen leyendo `Unverified`.

Es un dato del navegador, no una prueba de quién es la persona: dos personas que comparten el navegador comparten el contacto. Si tu sitio tiene login, usá el nivel 2.

Dos límites: un contacto `Unverified` con 5 conversaciones WebChat abiertas no recibe más sesiones (cada sesión nueva cae en un contacto `Anonymous` propio, y el `api` registra una advertencia por tenant por hora, `webchat-visitor-key-saturated`), y todo contacto conserva sus 50 direcciones de sesión más nuevas. Una sesión viva sigue funcionando aunque su dirección se haya descartado.

### 8.2 Nivel 2: el token que firma tu sitio

**1. Configurá el secreto** del tenant:

```bash
$ openssl rand -base64 32 | tr '+/' '-_' | tr -d '='   # 43 caracteres

$ curl -sS -X PUT http://{server-ip}/api/v1/admin/channels/WebChat \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: $CUSTOMER_TENANT_ID" \
    -H "Content-Type: application/json" \
    -d '{ "isActive": true, "credentials": { "identityTokenSecret": "<secreto>" } }' | jq
```

Tiene que tener entre 32 y 512 caracteres. La clave HMAC son los bytes UTF-8 de ese texto, tal cual: no lo decodifiques antes de firmar. Verbara nunca lo devuelve (la respuesta sólo dice `isSet`) y la auditoría registra sólo el nombre de las claves, así que guardalo vos en el backend de tu sitio.

**2. Firmá un token en tu backend**, nunca en el navegador: el secreto no puede salir de tu servidor. Es un JWT compacto, `base64url(header).base64url(payload).base64url(firma)`, de hasta 4096 caracteres, con HMAC-SHA256 sobre los bytes ASCII de `base64url(header).base64url(payload)`. Sirve cualquier librería JWT.

El header es `{ "alg": "HS256", "typ": "JWT" }`. `alg` tiene que ser exactamente `HS256`; `typ` es opcional y, si está, tiene que ser `JWT`; `kid` es opcional y se ignora.

| Claim | Tipo | Obligatorio | Regla |
|---|---|---|---|
| `external_id` | string | Sí | 1 a 256 caracteres después de recortar espacios. El id estable de tu cliente en tu sitio. Usá un id opaco, nunca un email ni un documento: se guarda y los agentes lo ven en las direcciones del contacto. |
| `aud` | string o array | Sí | El id del tenant de Verbara. Un string igual al `tenantId` del pedido, o un array no vacío de strings, todos iguales a ese `tenantId`. |
| `iat` | integer | Sí | Segundos Unix: cuándo lo firmaste. |
| `exp` | integer | Sí | Segundos Unix. `exp` > `iat` y `exp` - `iat` <= 3600. |
| `name` | string | No | Hasta 200 caracteres. Completa el nombre del contacto sólo si no tiene. |
| `email` | string | No | Hasta 320 caracteres. Se guarda como campo `webchatEmail`. Nunca es una dirección del canal Email ni une contactos. |

El reloj tolera 300 segundos de diferencia: se rechaza si `iat` > ahora + 300 o si ahora > `exp` + 300. Un token no es de un solo uso (dos pestañas o una recarga lo presentan de nuevo), por eso su vida máxima es de 3600 segundos.

```js
// Node.js, en tu backend.
const { createHmac } = require('node:crypto');
const b64url = (text) => Buffer.from(text).toString('base64url');

function signIdentityToken(customer, tenantId, secret) {
  const iat = Math.floor(Date.now() / 1000);
  const header = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = b64url(JSON.stringify({
    external_id: customer.id,
    aud: tenantId,
    iat,
    exp: iat + 1800,
    name: customer.name,
    email: customer.email,
  }));
  const signature = createHmac('sha256', secret).update(`${header}.${payload}`).digest('base64url');
  return `${header}.${payload}.${signature}`;
}
```

**3. Entregá el token.** Viaja como `identityToken` en `POST /api/v1/webchat/sessions`. Cuando verifica, el `visitorId` del mismo pedido no se usa. La forma de dárselo al widget llega con su release Web. Verbara no guarda el token, no lo registra en logs ni lo devuelve.

**Un token rechazado no rechaza la sesión.** La sesión abre en el nivel que permita el resto del pedido, y `identityTokenRefusal` dice por qué. Se evalúa en este orden, y el `api` registra sólo el tenant y el código:

| `identityTokenRefusal` | Cuándo |
|---|---|
| `identity-token-not-configured` | El tenant no tiene `identityTokenSecret`. |
| `identity-token-malformed` | La estructura, el largo, el base64url, el JSON del header o `alg`. Después de la firma, también un claim obligatorio ausente, mal tipado o fuera de sus límites. |
| `identity-token-signature-invalid` | La firma no verifica con el secreto actual ni con el anterior dentro de su período de gracia. |
| `identity-token-wrong-tenant` | `aud` no es el `tenantId` del pedido, o un array de `aud` incluye otro tenant. |
| `identity-token-lifetime-too-long` | `exp` <= `iat`, o `exp` - `iat` > 3600. |
| `identity-token-not-yet-valid` | `iat` > ahora + 300. |
| `identity-token-expired` | Ahora > `exp` + 300. |

### 8.3 Rotar y revocar el secreto

Todo pasa por el mismo `PUT /api/v1/admin/channels/WebChat`, en `credentials`:

| Qué querés hacer | `credentials` | Qué pasa |
|---|---|---|
| Rotar | `{ "identityTokenSecret": "<nuevo>" }` | El secreto viejo pasa a `identityTokenSecretPrevious` y Verbara estampa `identityTokenSecretReplacedAt`. El anterior sigue verificando **24 horas** desde ese momento, para que despliegues el nuevo en tu sitio. Mandar el mismo valor no rota nada. |
| Revocar el anterior | `{ "identityTokenSecretPrevious": null }` | Se termina esa gracia al instante. Usalo si el secreto viejo quedó expuesto. |
| Apagar el nivel 2 | `{ "identityTokenSecret": null, "identityTokenSecretPrevious": null }` | Todo token siguiente responde `identity-token-not-configured`, y las sesiones caen al nivel 1 o a `Anonymous`. Borrar sólo `identityTokenSecret` tampoco deja gracia. |

`identityTokenSecretReplacedAt` lo escribe sólo el servidor. Los rechazos, con `details` nombrando las claves:

| Código | Cuándo |
|---|---|
| `400 weak-identity-token-secret` | `identityTokenSecret` con menos de 32 o más de 512 caracteres. |
| `400 server-managed-credential-key` | Un valor para `identityTokenSecretPrevious`, o `identityTokenSecretReplacedAt` nombrada de cualquier forma, `null` incluido. |
| `400 unknown-credential-key` | Una clave fuera de `defaultQueueId`, `identityTokenSecret`, `identityTokenSecretPrevious` e `identityTokenSecretReplacedAt`. |

## Próximo paso

→ [05-canal-email.md](05-canal-email.md) — configurar SMTP outbound + la entrada por webhook (Gmail App Password, M365 OAuth, o SMTP genérico), y opcionalmente el poller IMAP de respuestas CSAT.
