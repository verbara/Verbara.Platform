# Manual SMB · 06 — Canal Voz/SIP

> **Audiencia:** operador con stack arriba + setup inicial completo + firewall/NAT validado (manual [01](01-instalacion-docker.md) §3 OK).
> **Tiempo:** 60-90 minutos (más si es la primera vez que el operador configura SIP).
> **Pre-requisitos:**
> - Trunk SIP provisionado con un carrier o Twilio Elastic SIP — necesitás credenciales + IP whitelist.
> - DID (Direct Inward Dialing) — el número telefónico que va a aterrizar las llamadas en tu Verbara.
> - Servidor con `EXTERNAL_IP` configurada correctamente (si estás detrás de NAT).

Este es **el manual más extenso y crítico** del kit SMB. Cubre el canal de voz end-to-end:

1. Pre-requisitos de red (re-lectura compacta de los puertos + bandwidth).
2. Capacidad por tier — cuántos agentes y llamadas concurrentes podés sostener.
3. Configurar un trunk SIP (ejemplo paso a paso con Twilio Elastic + sección genérica).
4. Configurar el dialplan inbound (DID → IVR → queue).
5. Provisionar agentes WebRTC.
6. Probar llamada entrante.
7. Probar llamada saliente.
8. Escalado a tier superior.
9. WebRTC behind strict NAT (Coturn).
10. Troubleshooting SIP (índice — el detalle está en [08-troubleshooting-sip.md](08-troubleshooting-sip.md)).

## 1. Pre-requisitos de red — checklist final

Si todo esto NO está verde, **detenete y revisá manual 01 §2 y §3** antes de continuar. Cada problema acá multiplica el tiempo de troubleshooting 10×.

| Puerto | Estado esperado | Comando de validación (desde otra máquina en internet) |
|---|---|---|
| `5060/udp` SIP UDP | ABIERTO al `EXTERNAL_IP` | `nc -uvz {tu-IP-pública} 5060 < /dev/null` |
| `5060/tcp` SIP TCP | ABIERTO | `nc -vz {tu-IP-pública} 5060` |
| `8089/tcp` WSS WebRTC | ABIERTO | `curl -sIk https://{tu-IP-pública}:8089/asterisk/ws` → HTTP 426 |
| `20000-20200/udp` RTP | ABIERTO al `EXTERNAL_IP` | Difícil de probar standalone — se valida con una llamada real |
| `EXTERNAL_IP` en `.env` | matchea `curl https://api.ipify.org` desde el server | `grep EXTERNAL_IP docker/.env.reference-smb` |
| Asterisk responde al ARI | OK | `curl -u verbara:$ARI_PASSWORD http://localhost:8088/ari/asterisk/info` |

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show transports'

  Transport:        <TransportId........>  <Type>  <cos>  <tos>     <BindAddress...>
==========================================================================================
  Transport:           transport-udp          udp      0      0           0.0.0.0:5060
  Transport:           transport-tcp          tcp      0      0           0.0.0.0:5060
  Transport:            transport-ws          ws       0      0           0.0.0.0:8180
  Transport:           transport-wss          wss      0      0           0.0.0.0:8089
```

✓ Los 4 transports `pjsip` bound a `0.0.0.0` — Asterisk está escuchando.

## 2. Capacidad — cuántas llamadas + agentes soporta tu server

Recapitulando el tier matrix de [00-vision-general.md](00-vision-general.md), con números afilados específicos para Voz:

| Tier | vCPU/RAM | Calls G.711 passthrough | Calls Opus↔G.711 transcoding | Agentes WebRTC concurrent | WAN simétrica recomendada |
|---|---|---|---|---|---|
| **SMB Lite** | 4 / 16 GB | **50** | 10 | 50 | 25 Mbps |
| **SMB Standard** | 8 / 32 GB | **150** | 30 | 150 | 50 Mbps |
| **SMB Plus** | 16 / 64 GB | **300** | 60 | 300 | 100 Mbps |

**Bottleneck dominante** según escenario:

- **G.711 passthrough** (mayoría de trunks PSTN): CPU Asterisk (~2 %/call). El límite real es CPU + RTP range.
- **Opus↔G.711 transcoding**: 5× más CPU/call. Si tu trunk no acepta Opus pero los agentes son WebRTC (que negocia Opus por default), Asterisk transcodifica → capacidad cae 5×.
- **WAN bandwidth**: 80 kbps/call simétrico para G.711. SMB Plus a 300 calls = 48 Mbps sostenidos — la WAN del cliente DEBE ser simétrica (no `100/20 Mbps`).

> 💡 **Para evitar transcoding**: si tu trunk acepta Opus (ej. Twilio Elastic SIP con `Codecs: opus` configurado), forzá Opus end-to-end. Si NO, forzá G.711 también en el WebRTC offer (perdés wideband audio pero recuperás 5× capacity). Configuración en [§6 Provisionar agente WebRTC](#6-provisionar-primer-agente-webrtc).

## 3. Configurar trunk SIP

### 3.1 Ejemplo: Twilio Elastic SIP Trunking

Twilio Elastic SIP es el caso más documentado por nosotros — funciona con IP authentication (sin password, mejor) y soporta Opus + G.711.

**En la consola de Twilio:**

1. **Elastic SIP Trunking → Trunks → Create new SIP Trunk**:
   - Friendly name: `Verbara - {tu-empresa}`.
2. **Termination URI**:
   - Domain name: `verbara-{tu-empresa}.pstn.twilio.com` (te lo asigna Twilio).
   - **Recording**: `Disabled` (Verbara graba localmente — evitar doble grabación).
3. **Termination → Authentication → IP ACL**:
   - Click **Add an IP Access Control List**.
   - **Friendly name**: `Verbara server`.
   - **CIDR network address**: `{tu-IP-pública}/32` (tu single public IP).
   - Save.
4. **Origination URIs** (Twilio → tu Asterisk):
   - Add origination URI: `sip:{tu-IP-pública}:5060`.
   - Priority: 10, Weight: 10.
5. **Numbers**: comprá un número (Phone Numbers → Buy a Number). Una vez comprado, en sus settings:
   - **Configure with**: `SIP Trunk`.
   - **Trunk**: `Verbara - {tu-empresa}`.

Tus credenciales finales:
- **Trunk hostname**: `verbara-{tu-empresa}.pstn.twilio.com`
- **Auth**: IP whitelist (no usuario/password).
- **Tu DID**: `+15551234567` (el número que compraste).

### 3.2 Provisionar el trunk en Verbara

Vía API:

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/dialer/trunks \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "name": "twilio-elastic",
      "displayName": "Twilio Elastic SIP",
      "host": "verbara-tu-empresa.pstn.twilio.com",
      "port": 5060,
      "transport": "udp",
      "authType": "IpAuth",
      "fromUser": "+15551234567",
      "fromDomain": "verbara-tu-empresa.pstn.twilio.com",
      "callerId": "+15551234567",
      "codecs": ["g711_ulaw", "g711_alaw"],
      "isActive": true
    }' | jq
```

> Para trunks con auth user/password (no IP ACL):
> ```json
> "authType": "BasicAuth",
> "authUser": "tu-usuario",
> "authPassword": "tu-password"
> ```

### 3.3 Vía UI (alternativa)

`/admin/trunks → Crear trunk`. Misma información que el JSON pero en form fields. Screenshots en el manual completo en `docs/manuales/smb/screenshots/` (anexo).

### 3.4 Validar registro / conectividad del trunk

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoints' | head -10

  Endpoint:              twilio-elastic                            Not in use    0 of inf
        Aor:             twilio-elastic                                                  0
        Identify:        twilio-elastic                            verbara-tu-empresa....
```

Si el trunk usa registration (no IP-auth):
```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show registrations'

  <Registration/ServerURI..............................>  <Auth..........>  <Status.......>
==========================================================================================
  twilio-elastic/sip:verbara-tu-empresa.pstn.twilio.com    twilio-creds      Registered
```

✓ `Registered` = trunk conectado a Twilio.

### 3.5 Trunks genéricos (otros carriers PSTN)

Patrón común:

| Carrier | Host típico | Auth | Codecs |
|---|---|---|---|
| Twilio Elastic SIP | `*.pstn.twilio.com` | IP ACL | g711, opus |
| Vonage Business / Nexmo | `sip.nexmo.com` | username + IP | g711 |
| Bandwidth.com | `sip.bandwidth.com` | IP ACL | g711 |
| VoIP.ms | `chicago.voip.ms` | username/password | g711, g729 |
| Skyetel | `sip.skyetel.com` | IP ACL | g711, opus |
| Telnyx | `sip.telnyx.com` | username/password OR IP ACL | g711, opus |
| Carrier local (Movistar, Claro, Telmex, Vivo, Oi) | varía — pedir al carrier | varía | g711 |

Datos que pedirle SIEMPRE a tu carrier:
1. **SIP hostname** del trunk (registration + termination).
2. **Authentication mode**: usuario/password vs IP ACL.
3. **Codecs soportados** (para alinear con `.codecs`).
4. **DTMF mode** (RFC 2833 / INFO / inband) — RFC 2833 es lo más universal.
5. **Caller ID format** que esperan (E.164 vs national).

## 4. Configurar dialplan inbound (DID → IVR → queue)

Cuando una llamada entrante llega al trunk, Asterisk la enruta según el dialplan. Verbara ofrece 3 patrones built-in:

### 4.1 Patrón A — DID directo a queue (más simple)

Llamada al `+15551234567` → directa a queue `Atención General`.

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/dialer/inbound-routes \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "name": "Main DID",
      "trunkName": "twilio-elastic",
      "didNumber": "+15551234567",
      "destination": {
        "type": "Queue",
        "queueId": "queue_01HX..."
      },
      "isActive": true
    }' | jq
```

### 4.2 Patrón B — IVR + opciones de menu

Llamada al DID → mensaje grabado "Presione 1 para soporte, 2 para ventas" → routed a queue según selección.

Crear un **Flow** (dialplan visual) en `/admin/flows → Crear → Voice IVR`:

1. Drag node `PlayPrompt` → texto: `"Gracias por llamar. Presione 1 para soporte, 2 para ventas."`
2. Drag node `GetDigit` → max digits 1, timeout 5s.
3. Branch por digit:
   - `1` → node `TransferToQueue` → `queue_soporte_id`
   - `2` → node `TransferToQueue` → `queue_ventas_id`
   - timeout → node `TransferToQueue` → `queue_atencion_general` (fallback)
4. Save flow → copiar el `flowId`.

Luego provisionar el DID apuntando al flow:

```bash
$ curl -sS -X POST .../api/v1/dialer/inbound-routes \
    -d '{
      "name": "Main DID with IVR",
      "didNumber": "+15551234567",
      "destination": {"type": "Flow", "flowId": "flow_01HX..."},
      "isActive": true
    }'
```

### 4.3 Patrón C — Business hours + after-hours

Wrap del patrón A/B con horario de atención. Fuera de horario → voicemail o mensaje grabado.

```bash
$ curl -sS -X POST .../api/v1/dialer/business-hours \
    -d '{
      "name": "Horario soporte",
      "timezone": "America/Bogota",
      "schedule": [
        { "day": "mon", "open": "08:00", "close": "18:00" },
        { "day": "tue", "open": "08:00", "close": "18:00" },
        { "day": "wed", "open": "08:00", "close": "18:00" },
        { "day": "thu", "open": "08:00", "close": "18:00" },
        { "day": "fri", "open": "08:00", "close": "18:00" }
      ]
    }'
```

Wrap el inbound route con la condición:
```json
{
  "destination": {
    "type": "BusinessHoursWrapper",
    "businessHoursId": "bh_01HX...",
    "onOpen": {"type": "Queue", "queueId": "queue_01HX..."},
    "onClosed": {"type": "Voicemail", "mailbox": "soporte"}
  }
}
```

## 5. Estrategia de routing en la queue

Editá la queue para definir cómo se asignan las llamadas a los agentes:

```bash
$ curl -sS -X PATCH http://{server-ip}:5000/api/v1/admin/queues/queue_01HX... \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "strategy": "LongestIdle",
      "ringTimeoutSeconds": 30,
      "wrapUpTimeSeconds": 15,
      "maxQueueWaitSeconds": 300,
      "overflowDestination": {"type": "Voicemail", "mailbox": "soporte"}
    }'
```

| Estrategia | Cómo funciona | Cuándo usar |
|---|---|---|
| `LongestIdle` | Asigna al agente que más tiempo lleva sin llamada | **Default — recomendado** para fairness |
| `RoundRobin` | Rota por la lista | Equipos pequeños homogéneos |
| `LeastCalls` | Asigna al agente con menos llamadas atendidas el día | Equilibrar carga acumulada |
| `SkillBased` | Match por skills del contacto vs skills del agente (Pro) | Equipos especializados (idioma/producto) |
| `Random` | Aleatorio | Tests/development |

## 6. Provisionar primer agente WebRTC

El agente que creaste en el wizard ya existe como usuario. Falta darle un **PJSIP endpoint** (su softphone virtual en Asterisk) para que pueda recibir llamadas desde el browser.

### 6.1 Auto-provisioning (el camino fácil)

```bash
$ curl -sS -X POST http://{server-ip}:5000/api/v1/admin/agents/agente1/provision-webrtc \
    -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: platform" \
    -H "Content-Type: application/json" \
    -d '{
      "extension": "1001",
      "preferredCodecs": ["opus", "ulaw"]
    }' | jq

{
  "extension": "1001",
  "pjsipUsername": "agente1",
  "pjsipPassword": "{generated-strong-password}",
  "wssUri": "wss://verbara.tu-dominio.com:8089/asterisk/ws",
  "iceServers": [
    {"urls": "stun:stun.l.google.com:19302"}
  ]
}
```

Esto crea automáticamente:
- `ps_endpoints` row con `endpoint=agente1`, transport WSS, codec orden Opus → ulaw.
- `ps_auths` row con username `agente1`, password generado.
- `ps_aors` row con max_contacts=1.

> ⚠️ **Codec order matters**: si querés evitar transcoding cuando el trunk es G.711, ponelo PRIMERO: `"preferredCodecs": ["ulaw", "opus"]`. WebRTC negocia el primer codec común — el browser de tu agente aceptará ulaw también.

### 6.2 Validar el endpoint

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoint agente1'

  Endpoint:           agente1                              Not in use      0 of 1
       InAuth:        agente1                              agente1
          Aor:        agente1                                                  1
      Contact:        agente1/sip:....                     <no-status>      ...
    Transport:        transport-wss                       wss
   Identifier:        agente1                              ip
```

### 6.3 Login del agente en el Web UI

El agente abre `https://verbara.tu-dominio.com/login`:

| Campo | Valor |
|---|---|
| Email | `maria@tu-empresa.com` |
| Password | la del wizard (la que anotaste en step 03) |
| Tenant ID | `platform` |

Aterriza en `/agent`. Click **Conectar** (botón con icono de teléfono en la barra). El browser:

1. Pide permiso del micrófono → **Permitir**.
2. El widget WebRTC abre WSS contra `wss://verbara.tu-dominio.com:8089/asterisk/ws`.
3. Indicador en el header cambia a verde: `Disponible`.

> 🔒 **Browsers requieren HTTPS para acceder al micrófono** (excepto en `localhost`). Si tu Web UI no está bajo TLS, el agente NO podrá usar WebRTC en producción. Volver a manual [01](01-instalacion-docker.md) §5 para configurar Let's Encrypt.

## 7. Probar llamada entrante (golden path)

1. **Estado inicial:**
   - Trunk `twilio-elastic` registered + endpoint inbound configurado para DID `+15551234567`.
   - Agente `agente1` logueado, en estado `Disponible`.

2. **Llamar al DID** desde un teléfono móvil:
   - Marcar `+15551234567`.
3. **Verificar en el server:**
   ```bash
   $ docker exec verbara-asterisk asterisk -rx 'core show channels'

   Channel              Location             State   Application(Data)
   PJSIP/twilio-...     default@verbara:1    Up      Dial(PJSIP/agente1,30)
   PJSIP/agente1-...    queue@verbara:1      Ring    Dial(PJSIP/agente1,30)
   ```
4. **En el browser del agente:** suena ringtone + popup "Llamada entrante de +15554567890".
5. Click **Aceptar** → audio bidireccional.
6. **Validar audio:**
   - Hablar desde el móvil → escuchar en headset del agente.
   - Hablar desde el headset del agente → escuchar en móvil.
7. Click **Colgar** → conversación queda en estado `Wrap-up` 15s para que el agente disponga.

### 7.1 ¿No hay audio en una dirección o en ambas?

→ Causa típica: NAT/EXTERNAL_IP mal o port-forwarding RTP roto.

```bash
# Validar que Asterisk anuncia tu EXTERNAL_IP en el SDP
$ docker exec verbara-asterisk asterisk -rx 'pjsip set logger on'
# Hacer una llamada → ver los logs
$ docker logs verbara-asterisk | grep -i 'connection:' | head -5
```

En la respuesta `200 OK` del INVITE, el SDP debe tener:
```
c=IN IP4 200.118.42.61            ← tu EXTERNAL_IP
m=audio 20034 RTP/AVP 0 8 ...     ← un puerto del rango RTP
```

Si dice `c=IN IP4 192.168.40.100` (LAN privada), el peer remoto manda RTP a la privada → audio se pierde. Setear/corregir `EXTERNAL_IP` en `.env`, `dc restart asterisk`.

Detalles en [08-troubleshooting-sip.md](08-troubleshooting-sip.md) §"No audio".

## 8. Probar llamada saliente

1. En el Web del agente: click icono **Marcar** en la barra superior.
2. Ingresar número destino en E.164: `+15555550199`.
3. Click **Llamar**.
4. El móvil destino ring → atender → conversación.
5. Validar:
   - El caller-id que ve el móvil destino = el DID configurado en el trunk (`+15551234567`).
   - Audio bidireccional OK.
   - Hangup limpio (Asterisk envía `BYE`, sale del CDR).

> Si el caller-id que aparece es **"Anonymous"** o un número raro: el trunk no acepta el `From` que mandás — pedile al carrier qué From-header esperan. Algunos quieren E.164, otros nacional sin `+`, otros un username específico.

## 9. Test de concurrencia mínima (5 llamadas)

Antes de declarar el canal "productivo", validá que el server aguanta más de una llamada a la vez:

1. Configurar 5 softphones de prueba (Linphone/Zoiper) en LAN — todos llamando al DID al mismo tiempo.
2. Tener 5 agentes logueados (puede ser el mismo navegador en 5 modos incógnito distintos).
3. Las 5 llamadas deben:
   - Entrar simultáneamente al server.
   - Conectarse a 5 agentes distintos (no a uno con cola de 4).
   - Tener audio independiente (5 streams RTP en puertos distintos del rango).
4. Validar:
   ```bash
   $ docker exec verbara-asterisk asterisk -rx 'core show channels' | grep PJSIP | wc -l
   10            # ← 5 trunk channels + 5 agent channels
   ```

Si las 5 funcionan sin distorsión, ya validaste:
- ✅ El RTP range tiene capacidad suficiente.
- ✅ El dialplan + queue routing funcionan multi-call.
- ✅ Asterisk no satura CPU a 5 calls.

Para test de capacidad declarada del tier (50/150/300), corré SIPp — ver [07-validacion-e2e.md](07-validacion-e2e.md).

## 10. Escalado entre tiers

Para subir de SMB Lite (50) a Standard (150) sin reinstalar:

```bash
$ ${EDITOR:-nano} docker/.env.reference-smb
```

Cambiar:
```diff
- RTP_PORT_END=20200
+ RTP_PORT_END=20400

- ASTERISK_CPU_LIMIT=4.0
+ ASTERISK_CPU_LIMIT=6.0

- ASTERISK_MEM_LIMIT=4G
+ ASTERISK_MEM_LIMIT=8G

- PG_SHARED_BUFFERS=512MB
+ PG_SHARED_BUFFERS=1GB
```

Asegurate de que el host físico tiene la RAM/CPU necesarios (si no, primero upgrade del host). También actualizá el firewall:

```bash
$ sudo ufw allow 20201:20400/udp comment 'Verbara RTP expansion'    # los nuevos
```

Y el port-forwarding del router (mismo rango ampliado).

Reiniciar el stack:
```bash
$ dc up -d --wait
```

> Cambios de resource limits requieren `up -d` (no `restart`) para que se apliquen.

## 11. WebRTC behind strict NAT — cuando necesitás Coturn

Si tus agentes laburan **desde sus casas** y sus ISPs tienen **NAT simétrica** (común en Latam con CGNAT residencial — ej. Movistar fibra residencial), el WebRTC NO va a poder negociar candidatos directos. Síntoma: la llamada conecta (SIP signalling vía WSS OK) pero **audio en silencio**.

Solución: levantar Coturn como TURN relay.

### 11.1 Setup rápido

```bash
$ cd /opt/verbara/platform
$ ${EDITOR:-nano} docker/.env.reference-smb
```

Descomentar y setear:
```env
COTURN_USER=verbara-turn
COTURN_PASSWORD={32-char random — openssl rand -base64 32}
```

Abrir puertos en el firewall:
```bash
$ sudo ufw allow 3478/udp
$ sudo ufw allow 3478/tcp
$ sudo ufw allow 5349/tcp                  # TLS opt
$ sudo ufw allow 49152:65535/udp           # relay range
```

> ⚠️ El relay range Coturn es **enorme** (49152-65535/udp = 16k puertos). Si no querés abrir tantos, podés reducirlo via `COTURN_RELAY_MIN=49152 COTURN_RELAY_MAX=50000` (1k puertos = 500 simultaneous relays).

Levantar:
```bash
$ dc -f docker/docker-compose.reference-smb.yml \
     -f docker/docker-compose.coturn.yml \
     --profile coturn up -d --wait coturn
```

Configurar el agente WebRTC para usar el TURN:
```bash
$ curl -sS -X PATCH .../api/v1/admin/tenant-settings/webrtc \
    -d '{
      "iceServers": [
        {"urls": "stun:stun.l.google.com:19302"},
        {
          "urls": "turn:verbara.tu-dominio.com:3478?transport=udp",
          "username": "verbara-turn",
          "credential": "{COTURN_PASSWORD}"
        }
      ]
    }'
```

Los agentes deben **re-loguearse** para pickear la nueva config.

### 11.2 Validar que el TURN funciona

[Web del agente] → **Configuración → Diagnóstico WebRTC**:
- ICE candidates gathered:
  - `host` (LAN del agente)
  - `srflx` (server-reflexive vía STUN)
  - **`relay`** (servidor TURN) ← este es el que importa

Si `relay` no aparece → el agente no puede llegar al Coturn. Validar firewall del router del agente.

## 12. Troubleshooting — síntoma → solución (índice)

| Síntoma | Detalle en |
|---|---|
| "No hay audio" / "Audio sólo en una dirección" | [08-troubleshooting-sip.md](08-troubleshooting-sip.md) §"No audio" |
| "Llamada cae a los 30s" | [08](08-troubleshooting-sip.md) §"Stateful UDP timeout" |
| "Agente no recibe ring" | [08](08-troubleshooting-sip.md) §"WSS handshake failures" |
| "Trunk no se registra" | [08](08-troubleshooting-sip.md) §"Registration failures" |
| "Caller-ID anonymous" | [08](08-troubleshooting-sip.md) §"From-header rejection" |
| "Eco / distorsión" | [08](08-troubleshooting-sip.md) §"Codec mismatch + jitter" |
| "Calidad pobre a 100+ calls" | [08](08-troubleshooting-sip.md) §"CPU + RTP starvation" |

## Próximo paso

→ [07-validacion-e2e.md](07-validacion-e2e.md) — checklist completo de validación + comando para correr la suite E2E automatizada.
