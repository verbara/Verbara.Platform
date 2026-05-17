# Manual SMB · 05 — Canal Email (SMTP outbound + IMAP inbound)

> **Audiencia:** operador con stack arriba + setup inicial completo.
> **Tiempo:** 30 minutos.
> **Pre-requisitos:**
> - Credenciales SMTP (server + puerto + usuario + password).
> - Credenciales IMAP (server + puerto + usuario + password) — para recibir mail.
> - O cuenta de Microsoft 365 / Gmail con OAuth si preferís ese flujo (más seguro pero requiere config en el provider).

El canal Email convierte cada correo entrante en una **conversación** en Verbara. El agente responde desde la misma UI que usa para WebChat/Voz y la respuesta sale como reply al mismo thread vía SMTP outbound. El threading se preserva con headers `In-Reply-To` y `References`.

Arquitectura:

```
   Cuenta IMAP                   Cuenta SMTP
   soporte@empresa.com           noreply@empresa.com
        │                              ▲
        │  (1) poll cada 60s            │  (4) reply
        ▼                              │
   Mail microservice ◄───── Platform.Api Channels.Email
        │                              ▲
        │  (2) parse + create msg       │  (3) agent responde en UI
        ▼                              │
   Inbound pipeline ───► Queue ───► Agente
```

## 1. Estrategia: 3 caminos posibles

| Provider | Recomendado para | Complejidad |
|---|---|---|
| **SMTP genérico + IMAP genérico** | La mayoría de los casos — funciona con cualquier proveedor (Gmail, Zoho, Yandex, server propio Postfix/Dovecot) | Baja |
| **Microsoft 365 OAuth (Graph API)** | Empresas que usan M365 corporativo y no quieren App Passwords | Media (registro de app en Entra ID) |
| **Gmail OAuth2** | Empresas en Google Workspace | Media (registro de app en GCP Console + service account o consent flow) |

> Para arrancar rápido, recomiendo **SMTP + IMAP** con una cuenta dedicada (`soporte@tu-empresa.com`) y App Password. Migrar a OAuth después es un cambio de `.env` sin downtime.

## 2. Camino A — SMTP + IMAP genérico (recomendado para start)

### 2.1 Crear cuenta dedicada

> 🔒 **No uses el inbox personal del operador.** Creá una cuenta exclusiva para Verbara — facilita rotación de password, auditoría, y mantiene el inbox del operador limpio.

Ejemplos:
- **Gmail:** crear cuenta nueva `soporte.verbara@gmail.com`, habilitar 2FA, generar **App Password** (Cuenta Google → Seguridad → 2FA → App Passwords → "Verbara"). El App Password es lo que ponés como `SMTP_PASSWORD`/`IMAP_PASSWORD`.
- **Microsoft 365:** crear buzón `soporte@tu-empresa.com`, generar App Password en `https://mysignins.microsoft.com/security-info` (requiere 2FA habilitado para la cuenta).
- **Zoho Mail:** crear cuenta + generar App Password en `Account → Security → App Passwords`.
- **Server propio (Postfix/Dovecot):** crear el usuario + setear su password.

### 2.2 Editar `.env.reference-smb`

```env
# SMTP outbound (envío de respuestas + notificaciones)
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USE_TLS=true
SMTP_USER=soporte.verbara@gmail.com
SMTP_PASSWORD={app-password-de-gmail}
SMTP_FROM=soporte.verbara@gmail.com
SMTP_FROM_NAME=Soporte ACME Corp

# IMAP inbound (lectura del inbox para crear conversaciones)
IMAP_HOST=imap.gmail.com
IMAP_PORT=993
IMAP_USE_TLS=true
IMAP_USER=soporte.verbara@gmail.com
IMAP_PASSWORD={mismo-app-password}
```

Tabla de servers comunes:

| Provider | SMTP host | SMTP port | IMAP host | IMAP port |
|---|---|---|---|---|
| Gmail / Google Workspace | `smtp.gmail.com` | 587 (STARTTLS) | `imap.gmail.com` | 993 (TLS) |
| Microsoft 365 / Outlook.com | `smtp.office365.com` | 587 (STARTTLS) | `outlook.office365.com` | 993 (TLS) |
| Zoho Mail | `smtp.zoho.com` | 587 (STARTTLS) | `imap.zoho.com` | 993 (TLS) |
| Yandex | `smtp.yandex.com` | 465 (SSL) | `imap.yandex.com` | 993 (TLS) |
| Postfix/Dovecot propio | `mail.tu-dominio.com` | 587 | `mail.tu-dominio.com` | 993 |

### 2.3 Reiniciar el servicio mail para aplicar

```bash
$ cd /opt/verbara/platform
$ alias dc='docker compose -f docker/docker-compose.reference-smb.yml --env-file docker/.env.reference-smb'
$ dc restart mail
$ dc logs -f mail
```

Buscar en los logs:
```
info: Verbara.Platform.Mail.SmtpClient[0]
      SMTP client initialized for smtp.gmail.com:587 (TLS=true)
info: Verbara.Platform.Mail.ImapPoller[0]
      IMAP poller started for soporte.verbara@gmail.com (polling every 60s)
```

### 2.4 Habilitar el canal Email en el admin UI

1. `/admin/channels` → row `Email` → **Habilitar**.
2. Modal:
   - **Display name:** `Email Soporte`
   - **From address:** `soporte.verbara@gmail.com` (debe matchear `SMTP_FROM`)
   - **From name:** `Soporte ACME Corp`
   - **Inbox account:** `soporte.verbara@gmail.com`
   - **Default queue:** `Atención General`
   - **Threading mode:** `In-Reply-To` (default — recomendado)
3. **Guardar**.

### 2.5 Probar round-trip

**Enviar correo entrante** desde una cuenta personal:

```
To:      soporte.verbara@gmail.com
From:    tu-cuenta-personal@gmail.com
Subject: Prueba de canal email Verbara
Body:    Hola, esto es un mensaje de prueba para validar el canal.
```

Esperar 60-120 segundos (poll interval). Validar:

```bash
$ curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    "http://{server-ip}:5000/api/v1/conversations?channelType=Email&limit=5" | jq '.[0]'

{
  "id": "conv_01HX...",
  "channelType": "Email",
  "state": "Queued",
  "subject": "Prueba de canal email Verbara",
  "contact": { "email": "tu-cuenta-personal@gmail.com" }
}
```

En la UI: como `agente1` → `/agent/queue` → la conversación aparece. **Aceptarla** → escribir respuesta `"Hola, esto es la respuesta del agente"` → **Enviar**.

Revisar el inbox de `tu-cuenta-personal@gmail.com`:
```
From:    soporte.verbara@gmail.com (Soporte ACME Corp)
Subject: Re: Prueba de canal email Verbara
Body:    Hola, esto es la respuesta del agente
```

✅ Round-trip OK.

## 3. Camino B — Microsoft 365 OAuth (Graph API)

Si tu cliente usa M365 corporativo y prefiere OAuth sobre App Password (más seguro, auditable, revocable individualmente):

### 3.1 Registrar app en Microsoft Entra ID

1. Login a `https://entra.microsoft.com/` con cuenta admin del tenant.
2. **App registrations → New registration**:
   - Name: `Verbara Platform`
   - Supported account types: `Accounts in this organizational directory only`
   - Redirect URI: dejar vacío por ahora
3. Copiar:
   - **Application (client) ID** → `MS_CLIENT_ID` en `.env`
   - **Directory (tenant) ID** → `MS_TENANT_ID` en `.env`
4. **Certificates & secrets → New client secret** → copiar el value → guardarlo en password manager (sólo se muestra una vez).
5. **API permissions → Add → Microsoft Graph → Application permissions**:
   - `Mail.Send` (enviar mail como app)
   - `Mail.ReadWrite.All` (leer y marcar como leído los mails del buzón configurado)
6. **Grant admin consent** para el tenant (botón verde arriba de la lista de permisos).

### 3.2 Editar `.env.reference-smb`

```env
# Microsoft Graph OAuth
MS_CLIENT_ID={tu-application-id}
MS_TENANT_ID={tu-directory-tenant-id}
MS_CLIENT_SECRET={el-secret-del-step-3.1.4}
MS_MAIL_USER=soporte@tu-empresa.com    # buzón M365 desde el cual mandar/leer

# Dejar SMTP/IMAP vacíos — Graph reemplaza ambos
# SMTP_HOST=
# IMAP_HOST=
```

> El mail microservice detecta automáticamente que `MS_CLIENT_ID` está seteado y prefiere Graph sobre SMTP/IMAP. No es necesario un flag explícito.

### 3.3 Reiniciar mail

```bash
$ dc restart mail
$ dc logs -f mail | grep -i graph
info: Verbara.Platform.Mail.GraphMailClient[0]
      Microsoft Graph client initialized for tenant {tenantId}, mail user soporte@tu-empresa.com
```

Resto del flujo es idéntico al Camino A (habilitar canal + probar round-trip).

## 4. Camino C — Gmail OAuth2

Más complejo que SMTP+App Password porque Google requiere:
1. Crear proyecto en GCP Console.
2. Habilitar Gmail API.
3. Crear Service Account con domain-wide delegation (sólo Google Workspace, NO funciona con cuentas gratis @gmail.com).
4. Autorizar los scopes `https://mail.google.com/` en Admin Console del workspace.
5. Descargar el JSON de la service account y mountarlo en el container `mail`.

Para el detalle paso a paso ver `docs/specs/email-oauth-google.md` (en este repo). Recomendamos **NO usar este flujo en SMB** — el camino A con App Password es ~20× más simple y la seguridad es comparable.

## 5. Configurar threading + auto-acknowledge (avanzado)

### 5.1 Modos de threading

| Modo | Cómo funciona | Cuándo usar |
|---|---|---|
| `In-Reply-To` | Match por header `In-Reply-To` o `References` del correo entrante con el `Message-ID` de la respuesta saliente | **Default — recomendado** |
| `SubjectMatch` | Match por subject normalizado (sin `Re:`/`Fwd:`/etc.) | Cuando el cliente rompe headers (ej. clientes web viejos) |
| `Per-Email` | Cada email entrante = nueva conversación (sin agrupar) | Casos donde cada email es un ticket nuevo |

Cambiar:
```bash
$ curl -sS -X PATCH http://{server-ip}:5000/api/v1/admin/channels/email \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{"threadingMode": "SubjectMatch"}'
```

### 5.2 Auto-acknowledge (responder automático "recibimos tu mensaje")

```bash
$ curl -sS -X PATCH http://{server-ip}:5000/api/v1/admin/channels/email \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "autoAck": true,
      "autoAckSubject": "Recibimos tu mensaje",
      "autoAckBody": "Hola, recibimos tu mensaje y te responderemos pronto. Ticket #{conversationId}."
    }'
```

El placeholder `{conversationId}` se sustituye con el ID corto de la conversación.

## 6. Catch-all y direcciones múltiples

Si tu cliente usa varias direcciones (`soporte@`, `ventas@`, `cobros@` — todos al mismo buzón con regla MX catch-all), creá una **regla de Inbound Routing** por To-address:

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/admin/routing/inbound \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "name": "Email ventas",
      "channelType": "Email",
      "predicate": {
        "type": "ToMatch",
        "value": "ventas@tu-empresa.com"
      },
      "queueId": "queue_ventas_01HX...",
      "priority": 10
    }'
```

Repetir por cada dirección que necesite routing distinto.

## 7. Troubleshooting

### Síntoma: el mail entrante no aparece en la queue

```bash
# 1. Verificar que IMAP esté pulleando
$ dc logs --tail 100 mail | grep -i imap
info: Verbara.Platform.Mail.ImapPoller[0]
      Polled 3 new messages from soporte.verbara@gmail.com

# Si dice "Authentication failed" → password wrong / App Password caducado
# Si dice "Connection refused" → puerto/host mal en .env, o firewall del provider bloquea

# 2. Verificar que llegan al pipeline
$ dc logs --tail 100 platform-api | grep -i email
info: Verbara.Platform.Channels.Email[0]
      Created conversation conv_01HX... from email msg-id <abc@gmail.com>

# 3. Si llegan al pipeline pero no a la queue → revisar routing rules
$ curl ... /api/v1/admin/routing/inbound | jq
```

### Síntoma: el mail de respuesta no sale

```bash
$ dc logs --tail 50 mail | grep -i smtp
info: Verbara.Platform.Mail.SmtpClient[0]
      Sent message to recipient@gmail.com (msg-id <def@verbara>)

# Si dice "SMTP timeout" → puerto SMTP bloqueado por firewall del HOSTER (residential ISPs
# bloquean 25 outbound — pero 587 STARTTLS suele estar abierto)
# Si dice "Authentication required" → SMTP_PASSWORD wrong

# Forzar un test envío:
$ docker exec verbara-mail dotnet Verbara.Platform.Mail.Cli.dll \
    send-test --to recipient@gmail.com
```

### Síntoma: threading roto (cada reply abre conversación nueva)

Causa típica: el cliente web del usuario remoto reescribe `In-Reply-To`. Cambiar a `SubjectMatch` (ver §5.1) suele resolverlo.

## Próximo paso

→ [06-canal-voz-sip.md](06-canal-voz-sip.md) — el manual más extenso. Configurar el canal de voz con trunk SIP, WebRTC para agentes, y validación de audio bidireccional.
