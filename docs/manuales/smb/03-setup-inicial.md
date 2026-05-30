# Manual SMB · 03 — Setup inicial (admin + tenant + agente + queue)

> **Audiencia:** operador con el stack arriba y `healthy`.
> **Tiempo:** 15 minutos.
> **Pre-requisitos:** [02-arranque-stack.md](02-arranque-stack.md) terminado, Web accesible en `http://{server-ip}/`.

Verbara arranca **sin ningún usuario** — para evitar credenciales por defecto. El primer paso es el **setup inicial** vía un endpoint público (`POST /api/v1/setup`) que sólo funciona cuando no existe ningún tenant host. Después de eso el endpoint queda bloqueado (HTTP 409).

> **🏢 Por qué el setup crea DOS tenants.** Verbara distingue 3 tipos de tenant: `Platform` (host administrativo único de la instancia), `Partner` (reseller / white-label, opcional) y `Customer` (tenant **operativo** — es el único donde viven agentes, colas y conversaciones). El tenant `platform` es administrativo por diseño: **no puede manejar agentes ni colas** (ADR-0027 rechaza endpoints operacionales con HTTP 409 desde Platform/Partner). Por eso el setup crea **obligatoriamente** un primer `Customer` además del `platform`, con su propio administrador. Así, apenas termina el setup, ya tenés un tenant operativo listo para configurar colas/agentes/canales — sin pasos manuales extra.

El setup crea entonces, en una sola operación:
- el tenant **`platform`** + su **Platform Admin** (administra tenants, licensing, cluster);
- el primer tenant **`Customer`** (tu empresa) + su **Customer Admin** (administra agentes, colas, conversaciones).

El resto de la configuración (agente + queue + canales) se hace desde la Web UI con el setup wizard, **logueado como el Customer Admin** (o impersonando el Customer desde el Platform Admin).

## 1. Setup inicial (Platform + Customer + 2 admins)

### Opción A — vía curl (más directo)

Desde el host o cualquier máquina con acceso al server:

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/setup \
    -H "Content-Type: application/json" \
    -d '{
      "email": "admin@tu-empresa.com",
      "password": "TU-PASSWORD-FUERTE-12+CHARS",
      "displayName": "Admin Verbara",
      "platformName": "Verbara - Tu Empresa",
      "customerTenantId": "mi-empresa",
      "customerName": "Mi Empresa",
      "customerAdminEmail": "ops@mi-empresa.com",
      "customerAdminPassword": "OTRA-PASSWORD-FUERTE-12+CHARS",
      "customerAdminDisplayName": "Admin Operativo"
    }' | jq
```

**Campos del Customer (todos obligatorios salvo el display name):**

| Campo | Regla |
|---|---|
| `customerTenantId` | slug en minúsculas (letras, dígitos, guiones), **no puede ser `platform`** — ej. `mi-empresa`, `acme` |
| `customerName` | nombre visible de tu empresa — ej. `Mi Empresa` |
| `customerAdminEmail` | email del admin operativo — **debe ser distinto** del `email` del Platform Admin |
| `customerAdminPassword` | mínimo 12 caracteres, con al menos una mayúscula y un número (misma política que el Platform Admin) |
| `customerAdminDisplayName` | opcional |

Respuesta esperada (HTTP 201):

```json
{
  "tenantId": "platform",
  "userId": "user_01HX...",
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "managementApiKey": "vrb_mgmt_KAaSjL...",
  "customerTenantId": "mi-empresa",
  "customerUserId": "user_01HY..."
}
```

> 🔒 **Guardá el `managementApiKey` en tu gestor de contraseñas.** Te da acceso administrativo total al tenant `platform` vía el header `X-Api-Key` — útil para automatizaciones (scripts de provisioning, monitoring, etc.) sin tener que loguearte con email/password.

> ⚠️ **Para operar (crear colas/agentes/canales) usá el Customer Admin**, no el Platform Admin. El `accessToken` que devuelve el setup es del Platform Admin (administrativo). Logueate con `customerAdminEmail` / `customerAdminPassword` contra el tenant `mi-empresa` (paso 2), o impersoná el Customer desde el Platform Admin.

Si el endpoint responde **HTTP 409 Conflict**, significa que el setup ya se completó y quedó bloqueado. En ese caso saltá al paso 2 y logueate con las credenciales ya existentes. Si responde **HTTP 400**, revisá que enviaste todos los campos del Customer y que las contraseñas cumplen la política (≥12, mayúscula, número) y los emails son distintos.

### Opción B — vía Web UI (browser)

Abrí `http://{server-ip}/` en el browser. Verbara detecta que no hay tenant host y redirige automáticamente a `/setup`. El formulario tiene tres secciones:

**Cuenta de Administrador de Plataforma:**

| Campo | Valor |
|---|---|
| **Email** | `admin@tu-empresa.com` |
| **Password** | `TU-PASSWORD-FUERTE` (mínimo 12, con mayúscula y número) |
| **Display name** | `Admin Verbara` |

**Plataforma:**

| Campo | Valor |
|---|---|
| **Platform name** | `Verbara - Tu Empresa` |

**Tu Empresa (Customer):**

| Campo | Valor |
|---|---|
| **Company Name** | `Mi Empresa` |
| **Tenant Id** | `mi-empresa` (minúsculas/dígitos/guiones, no `platform`) |
| **Company Admin Email** | `ops@mi-empresa.com` (distinto al de plataforma) |
| **Company Admin Password** | `OTRA-PASSWORD-FUERTE` (mínimo 12, con mayúscula y número) |

Click **Initialize Platform** → se crean Platform + Customer + ambos admins y aterrizás logueado como Platform Admin. Para operar, logueate como el Customer Admin (paso 2).

## 2. Login al Web UI

Abrí `http://{server-ip}/` en el browser → te lleva a `/login`. Hay dos cuentas según lo que quieras hacer:

**Para operar (colas, agentes, canales) — el caso normal en SMB:** logueate como el **Customer Admin**.

| Campo | Valor |
|---|---|
| **Email** | `ops@mi-empresa.com` (el `customerAdminEmail` del paso 1) |
| **Password** | el `customerAdminPassword` del paso 1 |
| **Tenant ID** | `mi-empresa` (el `customerTenantId` del paso 1) |

**Para tareas administrativas (gestionar tenants, licensing, cluster):** logueate como el **Platform Admin** (`admin@tu-empresa.com` / Tenant ID `platform`). Recordá que desde `platform` no podés crear colas/agentes — tenés que impersonar el Customer.

Click **Iniciar sesión** → aterrizás en `/admin` (dashboard).

## 3. Wizard de setup inicial

Verbara detecta que es la primera vez y te muestra un **banner** en la parte superior del admin:

```
┌──────────────────────────────────────────────────────────────────┐
│ 🚀  Completá la configuración inicial de tu workspace            │
│     • 1 queue • 1 agente • 1 canal • probar un mensaje           │
│                                       [Comenzar wizard]  [Cerrar]│
└──────────────────────────────────────────────────────────────────┘
```

Click **Comenzar wizard** o navegá manualmente a `/admin/setup`.

El wizard tiene **5 pasos**:

### Step 1 — Welcome

Pantalla introductoria. Solo click **Siguiente**.

### Step 2 — Crear primera Queue

Las queues son las **colas de atención** donde los agentes reciben las conversaciones (de cualquier canal).

| Campo | Ejemplo |
|---|---|
| **Nombre** | `Atención General` |

> 💡 Más adelante (post-wizard) podés crear queues por línea de negocio (`Soporte Técnico`, `Ventas`, `Cobros`) con estrategias distintas (longest-idle, skill-based, round-robin). Por ahora una sola alcanza.

Click **Crear queue y continuar**.

### Step 3 — Crear primer Agente

El agente es un usuario que va a atender conversaciones. En este wizard se crea con permisos básicos de agente; podés promoverlo después.

| Campo | Ejemplo |
|---|---|
| **User ID (login)** | `agente1` |
| **Display name** | `María González` |
| **Email** | `maria@tu-empresa.com` |

> Nota: el wizard genera una contraseña temporal y la muestra en pantalla. Anotala — la vas a necesitar para loguearte como agente en el step 7 (validación E2E).

Click **Crear agente y continuar**.

### Step 4 — Configurar primer Canal

Elegir el canal que querés probar primero. **Recomendado para validar rápido: WebChat** (no requiere credenciales externas).

| Canal | Por qué probar primero |
|---|---|
| **WebChat** | Funciona out-of-the-box, mensaje round-trip en < 5 min |
| **Email** | Requiere credenciales SMTP/IMAP — saltarlo si no las tenés a mano |
| **Voz/SIP** | Requiere trunk SIP provisionado + firewall NAT bien — saltarlo hasta haber leído [06](06-canal-voz-sip.md) |

Para WebChat:

| Campo | Valor |
|---|---|
| **Channel ID** | `webchat` (default) |
| **Display name** | `Chat del sitio web` |
| **Allowed origins** | `https://tu-sitio.com,http://localhost` |

Click **Habilitar canal y continuar**.

> Los detalles avanzados de cada canal (widget snippet, OAuth flows, trunk config) se cubren en los manuales 04, 05, 06.

### Step 5 — Probar un mensaje (test)

El wizard genera un widget WebChat embebido en la misma pantalla. Escribí un mensaje de prueba y validá que:

- ✅ Aparece en `/agent/queue` (otra pestaña o ventana — logueate como `agente1` con la password temporal)
- ✅ El agente puede asignárselo
- ✅ El agente responde y la respuesta llega de vuelta al widget

Si todos los ✓ pasan, click **Finalizar wizard** → quedás en `/admin`.

## 4. Verificación post-wizard

Validar desde la API. **Importante:** las colas/agentes/canales viven en el **Customer**, no en `platform`. Usá el token del **Customer Admin** (login del paso 2) y `X-Tenant-Id: mi-empresa`. (Si usás el token del Platform Admin contra `X-Tenant-Id: platform`, recibís **HTTP 409** — el tenant `platform` no maneja recursos operacionales por diseño, ADR-0027.)

```bash
$ TOKEN={el-accessToken-del-Customer-Admin}     # del login del paso 2
$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: mi-empresa" \
    http://{server-ip}:5000/api/v1/admin/queues | jq

[
  {
    "id": "queue_01HX...",
    "name": "Atención General",
    "isActive": true,
    "agentCount": 1
  }
]

$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: mi-empresa" \
    http://{server-ip}:5000/api/v1/admin/agents | jq

[
  {
    "userId": "agente1",
    "displayName": "María González",
    "email": "maria@tu-empresa.com",
    "queues": [{ "id": "queue_01HX...", "name": "Atención General" }],
    "status": "Offline"
  }
]

$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: mi-empresa" \
    http://{server-ip}:5000/api/v1/admin/channels | jq

[
  {
    "id": "webchat",
    "displayName": "Chat del sitio web",
    "enabled": true,
    "tenantId": "mi-empresa"
  }
]
```

Todos los counts/IDs no-vacíos → setup OK.

## 4b. Routing ejecutivo: membership = quién recibe conversaciones

Desde la versión `2.6.0-pro` del SDK (Phase B de ADR-0026), la tabla `queue_memberships` es la **fuente de verdad ejecutiva** para el routing de TODOS los canales (digital + voz). Antes de Phase B era ejecutiva sólo para voz; ahora también lo es para digital.

**Implicación práctica para el operador SMB:**

- Un agente sin row en `queue_memberships` para una queue **NO recibe conversaciones de esa queue**, sin importar qué skills tenga su perfil.
- Un agente con `AllowedChannels=['WebChat']` recibe sólo chats. Las llamadas no le timbran (Asterisk no lo sincroniza en `queue_members`) **y** los chats de otros canales (Email, WhatsApp) no le llegan (Verbara filtra el pool).
- Un agente con `AllowedChannels=NULL` recibe todos los canales que la queue acepta — comportamiento "all-channels", equivalente a la membership implícita pre-Phase B.

Donde se administra: `/admin/agents/{agentId}/queues` (editor visual con chips por canal + badge "Voice → Asterisk" / "Digital only"). El wizard del paso 3 ya creó la membership default-all-channels para `agente1` en `Atención General`, así que la verificación E2E del paso 5 funciona out-of-the-box.

> 💡 Si estás trabajando como **Platform Admin** (el rol que creaste en el manual [02-arranque-stack.md](02-arranque-stack.md)) y querés validar el routing, tenés que **impersonar un Customer tenant** primero. El tenant `platform` no tiene queues ni agentes operacionales por diseño (ADR-0027 enforcer rechaza endpoints operacionales con HTTP 409 desde Platform / Partner tenants).

### Backfill para upgrades de instalaciones legacy

Si estás migrando desde una instalación **anterior a Phase A.6** (pre-2026-05-28) donde el routing digital se hacía implícitamente por skill-match sin requerir `queue_memberships`, ejecutá una sola vez:

```bash
$ bash scripts/infer-memberships-from-skills.sh --dry-run    # preview
$ bash scripts/infer-memberships-from-skills.sh              # backfill
```

El script inserta `(tenant_id, queue_id, agent_id)` para cada par donde `agents.skills` interseca `queue_configs.required_skills` y todavía no hay row. Es **idempotente** — re-ejecutar no duplica filas (`ON CONFLICT DO NOTHING`). Para instalaciones nuevas hechas con el wizard del paso 3, el output esperado es `0 memberships inferred` porque el wizard ya creó la membership explícita.

## 5. (Opcional) Crear más tenants Customer

El setup del paso 1 ya creó tu primer Customer (`mi-empresa`). Si vas a usar Verbara para **múltiples clientes** (modelo multi-tenant tipo agencia o reseller), creá un tenant Customer adicional por cliente. El tenant `platform` queda como administrativo.

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/management/tenants \
    -H "X-Api-Key: $MANAGEMENT_API_KEY" \
    -H "Content-Type: application/json" \
    -d '{
      "id": "cliente-acme",
      "displayName": "ACME Corp",
      "ownerEmail": "owner@acme.com"
    }' | jq
```

Para cada tenant nuevo, repetir el wizard de setup interno (`/admin/setup` después de loguearse al tenant).

## 6. Crear más usuarios admin

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/admin/users \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "email": "admin2@tu-empresa.com",
      "displayName": "Segundo Admin",
      "password": "OTRA-PASSWORD-FUERTE",
      "roleTemplates": ["Admin"]
    }' | jq
```

Templates de rol disponibles: `Agent`, `Supervisor`, `QualityAnalyst`, `Manager`, `Admin`, `SystemAdmin`, `Api`, `PlatformAdmin`. Detalle de los 64 permisos cubiertos por cada template en `docs/decisions/0003-rbac-design.md` (en este mismo repo).

## 7. Próximos pasos — configurar canales

Ahora que el platform está funcionando, configurá los canales que tu cliente va a usar:

| Canal | Manual | Tiempo | Pre-requisitos del cliente |
|---|---|---|---|
| **WebChat** | [04-canal-webchat.md](04-canal-webchat.md) | 20 min | Acceso al HTML/template de su sitio web |
| **Email** | [05-canal-email.md](05-canal-email.md) | 30 min | Credenciales SMTP + IMAP (o cuenta M365/Gmail con OAuth) |
| **Voz/SIP** | [06-canal-voz-sip.md](06-canal-voz-sip.md) | 60-90 min | Trunk SIP provisionado (Twilio Elastic / carrier PSTN / etc.) + Manual 01 §3 firewall NAT validado |

> Aunque el wizard del paso 3 te permitía elegir un solo canal, **no estás limitado a uno**. Después de cerrar el wizard, podés configurar los 3 simultáneamente desde `/admin/channels`.
