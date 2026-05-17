# Manual SMB · 04 — Canal WebChat

> **Audiencia:** operador con stack arriba + setup inicial completo (manual [03](03-setup-inicial.md)).
> **Tiempo:** 20 minutos.
> **Pre-requisitos:** acceso al HTML/template del sitio web del cliente donde se va a embeber el widget. Si todavía no lo tenés, usá la página de prueba que provee Verbara (paso 4 abajo).

El canal WebChat permite que un visitante anónimo del sitio web del cliente inicie una conversación que aterrice en una queue de Verbara y sea atendida por un agente — sin instalar software, sólo abriendo el sitio.

Arquitectura:

```
   Browser visitante
         │
         │   (1) carga widget JS desde
         │       https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js
         ▼
   Widget burbuja (bottom-right)
         │
         │   (2) click → POST /api/v1/webchat/sessions
         │       (crea sesión + retorna ws path)
         │
         │   (3) abre WebSocket
         │       wss://verbara.tu-dominio.com/ws/webchat/{sessionId}
         ▼
   Platform.Api WebChat connector
         │
         │   (4) crea conversación + enrutado a queue
         ▼
   Agente recibe oferta en /agent/queue
```

## 1. Habilitar el canal WebChat (si no lo hiciste en el wizard)

### Vía Web UI

1. Logueate como admin → `/admin/channels`.
2. Encontrá la row `WebChat` (icono burbuja).
3. Click **Habilitar**.
4. En el modal:
   - **Display name:** `Chat del sitio web`
   - **Allowed origins:** `https://tu-sitio.com` (CSV si son varios)
   - **Default queue:** `Atención General` (la queue del wizard)
5. Click **Guardar**.

### Vía API

```bash
$ curl -sS -X PATCH http://{server-ip}:5000/api/v1/admin/channels/webchat \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "enabled": true,
      "displayName": "Chat del sitio web",
      "allowedOrigins": ["https://tu-sitio.com", "http://localhost"],
      "defaultQueueId": "queue_01HX..."
    }' | jq
```

> ⚠️ **`allowedOrigins` no puede contener `*`.** El backend valida el `Origin:` header de la sesión WebSocket contra esta lista. Permitir `*` desactivaría el control CORS — la documentación de browser webchat **siempre** debe enumerar los hosts exactos.

## 2. Snippet HTML para embeber el widget

En el HTML del sitio web del cliente, antes del cierre de `</body>`:

```html
<script
  src="https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js"
  data-tenant-id="platform"
  data-locale="auto"
  data-position="bottom-right">
</script>
```

| Atributo | Valor | Notas |
|---|---|---|
| `src` | URL del JS del widget servido por el Web nginx | `https://` si tu Web está bajo TLS; `http://{server-ip}/webchat/v1/...` para pruebas locales |
| `data-tenant-id` | el ID del tenant que va a recibir las conversaciones | `platform` si tenés un solo tenant; el slug del tenant si es multi-tenant |
| `data-locale` | `auto` / `es-419` / `pt-BR` / `en-US` | `auto` detecta del navegador del visitante |
| `data-position` | `bottom-right` / `bottom-left` / `top-right` / `top-left` | Posición de la burbuja flotante |

Atributos adicionales opcionales:

| Atributo | Default | Para qué |
|---|---|---|
| `data-queue-id` | el `defaultQueueId` configurado en step 1 | Forzar una queue específica (útil si embebés el widget en distintas páginas del sitio para distintos equipos) |
| `data-primary-color` | `#0d9488` | Color principal del widget (botón + burbuja) — un hex CSS |
| `data-greeting` | el texto del config server-side | Override del mensaje de bienvenida |

## 3. Configurar enrutado a queue (avanzado — opcional)

Por defecto el WebChat va a la `defaultQueueId` configurada en el canal. Si querés enrutar según la URL desde donde viene el visitante (ej. `/soporte/*` → Soporte queue, `/ventas/*` → Ventas queue), creá una **regla de Inbound Routing**:

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/admin/routing/inbound \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "name": "WebChat Soporte",
      "channelType": "WebChat",
      "predicate": {
        "type": "MetadataMatch",
        "key": "page_path",
        "operator": "StartsWith",
        "value": "/soporte/"
      },
      "queueId": "queue_soporte_01HX...",
      "priority": 10
    }' | jq
```

El widget envía `page_path` automáticamente en la metadata de la primera mensaje de la sesión.

## 4. Probar el widget con la página de demo provista

Verbara incluye una página de prueba en `/webchat/demo.html` que ya tiene el snippet embebido y funcional. Útil para validar antes de tocar el sitio del cliente.

Abrí en el browser:

```
http://{server-ip}/webchat/demo.html
```

O con DNS configurado:

```
https://verbara.tu-dominio.com/webchat/demo.html
```

Vas a ver:
- Una página simple explicando el widget.
- En el bottom-right una **burbuja flotante** con icono de chat.
- Click en la burbuja → se abre el panel del widget.

### 4.1 Iniciar conversación de prueba

1. En el panel del widget, escribir nombre + email (opcional).
2. Click **Iniciar conversación**.
3. Escribir un mensaje: `"Hola, esto es una prueba"`.
4. Click **Enviar**.

### 4.2 Validar desde el lado del agente

En otra pestaña del browser:

1. Logueate como `agente1` con la password temporal del wizard (`http://{server-ip}/login`).
2. Aterrizás en `/agent/queue` — ahí debería aparecer la conversación que iniciaste como visitante.
3. Click **Aceptar conversación** → se abre la vista de mensajes.
4. Escribir `"Hola, ¿en qué puedo ayudarte?"` → Enviar.
5. Volver a la pestaña del widget → la respuesta del agente aparece.

✅ Round-trip completo. WebChat funciona.

## 5. Personalizar look & feel (opcional)

El widget acepta CSS variables via `data-*`:

```html
<script
  src="https://verbara.tu-dominio.com/webchat/v1/verbara-webchat.js"
  data-tenant-id="platform"
  data-primary-color="#1d4ed8"
  data-bubble-bg="#1d4ed8"
  data-bubble-icon-color="#ffffff"
  data-greeting="¡Hola! ¿Cómo podemos ayudarte hoy?"
  data-offline-message="Estamos fuera de horario. Dejá tu mensaje y te respondemos pronto.">
</script>
```

Para customización más avanzada (logo, posicionamiento, comportamiento), embed el widget como `iframe`:

```html
<iframe
  src="https://verbara.tu-dominio.com/webchat/embed/?tenant=platform&theme=dark"
  style="position: fixed; bottom: 20px; right: 20px; width: 400px; height: 600px; border: none; z-index: 9999;">
</iframe>
```

> 💡 Branding del tenant (logo, colores, copy) se configura en `/admin/tenant-settings → Branding`. El widget pickea automáticamente esos valores.

## 6. CORS troubleshooting

Si el widget muestra "Connection error" en el browser y la consola devtools dice algo como:

```
Access to fetch at 'https://verbara.tu-dominio.com/api/v1/webchat/sessions'
from origin 'https://tu-sitio.com' has been blocked by CORS policy
```

→ falta agregar `https://tu-sitio.com` a `allowedOrigins`. Re-PATCH el canal con la origen agregada:

```bash
$ curl -sS -X PATCH http://{server-ip}:5000/api/v1/admin/channels/webchat \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{"allowedOrigins": ["https://tu-sitio.com", "https://tu-sitio.com.ar", "http://localhost"]}'
```

> Cambios en `allowedOrigins` se aplican **on-the-fly** sin reiniciar el stack.

## 7. Métricas básicas del canal

```bash
$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    "http://{server-ip}:5000/api/v1/analytics/channels/webchat/summary?from=2026-05-01&to=2026-05-31" | jq

{
  "channelId": "webchat",
  "totalSessions": 142,
  "averageWaitTime": "00:00:28",
  "averageHandleTime": "00:08:12",
  "abandonmentRate": 0.08
}
```

UI: `/admin/analytics/channels` muestra el mismo dato visualmente.

## Próximo paso

→ [05-canal-email.md](05-canal-email.md) — configurar SMTP outbound + IMAP inbound (Gmail App Password, M365 OAuth, o SMTP genérico).
