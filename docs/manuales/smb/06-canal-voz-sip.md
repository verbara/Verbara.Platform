# Manual SMB · 06 — Canal Voz/SIP

> **Audiencia:** operador con stack arriba + setup inicial completo + firewall/NAT validado (manual [01](01-instalacion-docker.md) §3 OK).
> **Tiempo:** 60–90 minutos (más si es la primera vez que configurás SIP).
> **Pre-requisitos:**
> - Trunk SIP con un carrier o Twilio Elastic SIP — necesitás credenciales (usuario/password) **o** que el carrier permita autenticación por IP.
> - DID (Direct Inward Dialing) — el número telefónico que va a aterrizar las llamadas en tu Verbara.
> - Servidor con `EXTERNAL_IP` configurada (si estás detrás de NAT).
> - Una **cola** ya creada (manual [03](03-setup-inicial.md) §4) y al menos un **agente** con su login.

> ⚠️ **ALCANCE — leé esto antes de empezar.** Hoy la **voz entrante llega hasta la cola**: la llamada del carrier se identifica como tu trunk, se resuelve el tenant, el DID se mapea a una cola y Asterisk la encola y hace ring al endpoint SIP del agente. Lo que **todavía NO está**: el **softphone en el browser** (responder la llamada y hablar desde la pestaña del agente), el **audio WebRTC bidireccional**, la **marcación saliente desde el browser** y el **TURN/Coturn**. Eso es la **Fase 3** del roadmap de voz. Mientras tanto, un agente recibe las llamadas encoladas registrando un **teléfono SIP externo** (teléfono de escritorio, Zoiper, Linphone) con su extensión — ver §6. Cada sección marca claramente qué está **✅ verificado**, qué es **🔜 Fase 3** y qué requiere **validación con tu carrier real**.

Este manual cubre el canal de voz entrante end-to-end con los endpoints reales:

1. Pre-requisitos de red (puertos + bandwidth).
2. Capacidad por tier — agentes y llamadas concurrentes.
3. Provisionar un trunk SIP (IP-ACL o registración) — **`POST /admin/trunks`**.
4. Mapear el DID a una cola — **`POST /admin/did-routes`**.
5. Cómo fluye una llamada entrante por dentro (trunk → Stasis → cola).
6. Darle a un agente un endpoint SIP para recibir llamadas.
7. Probar una llamada entrante.
8. Troubleshooting (índice → [08](08-troubleshooting-sip.md)).
9. Qué viene en Fase 3 (softphone browser, saliente, Coturn).

---

## 0. Verificar que el Realtime de Asterisk está conectado (CRÍTICO)

Verbara escribe la config PJSIP (trunks, agentes, colas) en Postgres y Asterisk la lee por **Realtime** (`res_config_pgsql`). Si ese driver no está conectado, **nada de lo que sigue funciona** (Asterisk no ve el trunk ni la cola). Validalo primero:

```bash
$ docker exec verbara-asterisk asterisk -rx 'module show like res_config_pgsql'

Module                         Description                              Use Count  Status
res_config_pgsql.so            PostgreSQL RealTime Configuration Driver 1          Running
```

✓ **`Running`** = realtime conectado. Si dice **`Not Running`**, el archivo `res_pgsql.conf` no se generó o apunta a la DB equivocada:

```bash
# Ver la config efectiva dentro del contenedor
$ docker exec verbara-asterisk cat /etc/asterisk/res_pgsql.conf
[general]
dbhost = 127.0.0.1        # host-network: loopback donde está publicada la DB
dbport = 5432             # el puerto publicado de tu Postgres
dbname = verbara
dbuser = platform
dbpass = ********
```

> 🛠️ El entrypoint del contenedor genera `res_pgsql.conf` desde las variables `PG_REALTIME_*` del `.env.reference-smb`. Si tu Postgres está publicado en un puerto distinto de `5432` (típico cuando el host ya tiene otro Postgres en 5432), seteá `PG_REALTIME_PORT` en el `.env` al puerto publicado. Tras cambiarlo: `dc restart asterisk`.

Después de cualquier cambio en trunks/colas/agentes, Asterisk los toma on-demand desde Realtime, pero los **identifies por IP** (IP-ACL de trunks) se cargan a memoria — si agregaste un trunk IP-ACL y no aparece, recargá:

```bash
$ docker exec verbara-asterisk asterisk -rx 'module reload res_pjsip_endpoint_identifier_ip.so'
```

## 1. Pre-requisitos de red — checklist final

Si esto NO está verde, **revisá manual 01 §2 y §3** antes de seguir.

| Puerto | Estado esperado | Validación (desde internet) |
|---|---|---|
| `5060/udp` SIP UDP | ABIERTO al `EXTERNAL_IP` | `nc -uvz {tu-IP-pública} 5060 < /dev/null` |
| `5060/tcp` SIP TCP | ABIERTO | `nc -vz {tu-IP-pública} 5060` |
| `20000-20200/udp` RTP | ABIERTO al `EXTERNAL_IP` | se valida con una llamada real |
| `EXTERNAL_IP` en `.env` | = `curl https://api.ipify.org` desde el server | `grep EXTERNAL_IP docker/.env.reference-smb` |
| ARI responde | OK | `curl -u verbara:$ARI_PASSWORD http://localhost:8088/ari/asterisk/info` |

> El puerto `8089/tcp` (WSS WebRTC) sólo hace falta para el **softphone del browser (Fase 3)**. Para voz entrante a cola + teléfonos SIP no es necesario.

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show transports'
  Transport:  transport-udp   udp   0   0   0.0.0.0:5060
  Transport:  transport-tcp   tcp   0   0   0.0.0.0:5060
  Transport:  transport-ws    ws    0   0   0.0.0.0:8180
  Transport:  transport-wss   wss   0   0   0.0.0.0:8089
```

## 2. Capacidad — llamadas + agentes por server

| Tier | vCPU/RAM | Calls G.711 passthrough | Calls Opus↔G.711 transcoding | WAN simétrica recomendada |
|---|---|---|---|---|
| **SMB Lite** | 4 / 16 GB | **50** | 10 | 25 Mbps |
| **SMB Standard** | 8 / 32 GB | **150** | 30 | 50 Mbps |
| **SMB Plus** | 16 / 64 GB | **300** | 60 | 100 Mbps |

- **G.711 passthrough** (mayoría de trunks PSTN): ~2 % CPU/call; límite real = CPU + rango RTP.
- **Opus↔G.711 transcoding**: ~5× más CPU/call. Si el trunk no acepta Opus, forzá G.711 en los codecs del trunk para evitarlo.
- **WAN**: ~80 kbps/call simétrico para G.711. SMB Plus a 300 calls = ~48 Mbps sostenidos — la WAN del cliente DEBE ser simétrica.

## 3. Provisionar un trunk SIP — `POST /admin/trunks`

> **Tenant:** los endpoints de trunk son **operativos** → tenés que autenticarte como admin de tu tenant **Customer** (ej. `acme`), **no** como Platform admin. (Un trunk en el tenant `platform` se rechaza con HTTP 409.)

Conseguí un token de tu admin Customer:

```bash
$ TOKEN=$(curl -sS -X POST http://localhost:5000/api/v1/auth/login \
    -H 'Content-Type: application/json' \
    -d '{"tenantId":"acme","email":"admin@acme.local","password":"TU-PASSWORD"}' \
    | jq -r .accessToken)
```

El modelo de trunk real tiene estos campos (no hay `host`/`port`/`fromUser`/`authType` — eso era de un diseño viejo):

| Campo | Tipo | Para qué |
|---|---|---|
| `name` | string (req) | id lógico del trunk |
| `displayName` | string? | nombre visible |
| `type` | `"pjsip"` \| `"sip"` \| `"iax2"` \| `"dahdi"` | tecnología (usá `pjsip`) |
| `isActive` | bool | habilitado |
| `maxChannels` | int | tope de canales simultáneos |
| `transport` | string? | `transport-udp` (default) / `transport-tcp` / `transport-tls` |
| `codecs` | string | CSV, ej. `"ulaw,alaw"` o `"opus,ulaw,alaw"` |
| `authUsername` / `authPassword` | string? | **auth por digest** (registración) |
| `registrationUri` / `clientUri` | string? | si el trunk **se registra** contra el carrier |
| `context` | string? | contexto de dialplan entrante (default `from-trunk`) |
| `matchHost` | string? | **IP-ACL**: IP/CIDR origen del carrier — identifica INVITEs entrantes sin digest |

Hay **dos modos de identificación** del trunk entrante. Elegí según tu carrier:

### 3.1 IP-ACL (recomendado — Twilio Elastic, Bandwidth, Skyetel) — ✅ verificado

El carrier manda llamadas desde IPs fijas; Verbara identifica el INVITE por la IP origen, sin digest.

```bash
$ curl -sS -X POST http://localhost:5000/api/v1/admin/trunks \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{
      "name": "twilio-elastic",
      "displayName": "Twilio Elastic SIP",
      "type": "pjsip",
      "isActive": true,
      "maxChannels": 50,
      "codecs": "ulaw,alaw",
      "context": "from-trunk",
      "matchHost": "54.172.60.0/30"
    }' | jq
```

> `matchHost` acepta IP (`203.0.113.10`) o CIDR (`203.0.113.0/24`). **Poné el rango de origination de tu carrier** (Twilio lo lista en *Elastic SIP Trunking → Origination*). Un valor mal formado se rechaza con HTTP 400; un rango demasiado amplio (`0.0.0.0/0`) dejaría que cualquiera entre como tu trunk — usá el rango exacto del carrier.

### 3.2 Registración / digest (VoIP.ms, Telnyx user/pass, carriers que piden registro)

El trunk se registra contra el carrier con usuario/password.

```bash
$ curl -sS -X POST http://localhost:5000/api/v1/admin/trunks \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{
      "name": "voipms",
      "displayName": "VoIP.ms",
      "type": "pjsip",
      "isActive": true,
      "maxChannels": 20,
      "transport": "transport-udp",
      "codecs": "ulaw",
      "authUsername": "TU-USUARIO-SIP",
      "authPassword": "TU-PASSWORD-SIP",
      "registrationUri": "sip:chicago.voip.ms",
      "clientUri": "sip:TU-USUARIO-SIP@chicago.voip.ms",
      "context": "from-trunk"
    }' | jq
```

### 3.3 Verificar que Asterisk ve el trunk

Cada trunk crea (vía Realtime) un endpoint `t-{id}` con tu tenant en `set_var=TENANT_ID={tenant}` (así la llamada entrante sabe a qué tenant pertenece — el nombre del canal del trunk **no** lleva el tenant). Si pusiste `matchHost`, además se crea un identify `ipauth-t-{id}`.

```bash
# El endpoint del trunk (reemplazá 1 por el id que devolvió el POST)
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoint t-1' | grep -E 'Endpoint:|context'
 Endpoint:  t-1   Unavailable   0 of inf
 context : from-trunk

# El identify IP-ACL (sólo si usaste matchHost)
$ docker exec verbara-asterisk asterisk -rx 'pjsip show identifies'
 Identify:  ipauth-t-1/t-1
      Match: 54.172.60.0/30

# El registro (sólo si usaste registración)
$ docker exec verbara-asterisk asterisk -rx 'pjsip show registrations'
 reg-t-2/sip:chicago.voip.ms   ...   Registered
```

> Si el identify no aparece, recargá: `asterisk -rx 'module reload res_pjsip_endpoint_identifier_ip.so'`.

### 3.4 Trunks por carrier (referencia)

| Carrier | Host típico | Auth | Codecs |
|---|---|---|---|
| Twilio Elastic SIP | `*.pstn.twilio.com` | IP-ACL (`matchHost`) | ulaw, opus |
| Bandwidth.com | `sip.bandwidth.com` | IP-ACL | ulaw |
| Skyetel | `sip.skyetel.com` | IP-ACL | ulaw, opus |
| VoIP.ms | `*.voip.ms` | registración (user/pass) | ulaw, g729 |
| Telnyx | `sip.telnyx.com` | registración **o** IP-ACL | ulaw, opus |
| Carrier local (Movistar, Claro, Telmex, Vivo, Oi) | varía | varía | ulaw |

Pedile SIEMPRE a tu carrier: hostname, modo de auth (IP vs user/pass), **rango de IPs de origination** (para `matchHost`), codecs, DTMF mode (RFC 2833 es lo universal) y formato de Caller ID (E.164 vs nacional).

## 4. Mapear el DID a una cola — `POST /admin/did-routes`

Cuando entra una llamada, el `StasisInboundConsumer` resuelve el **DID marcado → cola** usando la tabla `did_routes` de tu tenant. Es un mapeo directo número → cola (1 a 1).

```bash
# El id de la cola lo sacás de GET /admin/queues
$ QUEUE_ID=$(curl -sS http://localhost:5000/api/v1/admin/queues \
    -H "Authorization: Bearer $TOKEN" | jq -r '.[0].id')

$ curl -sS -X POST http://localhost:5000/api/v1/admin/did-routes \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"did\":\"18005551234\",\"queueId\":\"$QUEUE_ID\",\"isActive\":true}" | jq
```

| Campo | Para qué |
|---|---|
| `did` | el número marcado, **exactamente** como llega en el INVITE (típicamente E.164 sin `+`, ej. `18005551234`) |
| `queueId` | id de la cola destino (`GET /admin/queues`) |
| `isActive` | habilitado |

> El DID es **único por tenant** (un segundo route con el mismo `did` devuelve HTTP 409). Para listar/editar: `GET /admin/did-routes`, `GET /admin/did-routes/by-did/{did}`, `PUT /admin/did-routes/{id}`, `DELETE /admin/did-routes/{id}`.

> **¿IVR / horario de atención / overflow?** El `did_route` es DID→cola directo. Los menús IVR, business-hours y overflow se modelan con **Flows** (`/admin/flows`) y **Automation** — son canales/funciones aparte y **no** forman parte del contrato de `did_routes`. Para V1 de voz, DID→cola directo es el patrón soportado y verificado.

## 5. Cómo fluye una llamada entrante (qué pasa por dentro) — ✅ verificado

```
Carrier ──INVITE(DID)──▶ Asterisk
   │  (identificado como trunk t-{id} por IP-ACL o registración)
   │  el canal hereda set_var TENANT_ID={tenant}
   ▼
[from-trunk]  exten => _X.,1,Stasis(verbara,inbound,${EXTEN})
   ▼
StasisInboundConsumer (en Platform.Api, gated por leader-election)
   1. lee TENANT_ID del canal               → tenant
   2. busca did_routes(tenant, DID)          → queue_id
   3. busca la cola                          → nombre realtime "{tenant}-{cola}"
   4. AnswerAsync + set QUEUE_NAME + Continue(stasis-queue, s)
   ▼
[stasis-queue]  exten => s,1,Queue(${QUEUE_NAME})
   ▼
app_queue ── rinde a los miembros de la cola (endpoints SIP de los agentes)
```

Si **cualquier** paso no resuelve (sin TENANT_ID, DID sin route, cola inexistente), el consumer **cuelga** la llamada (fail-closed) en vez de dejarla colgada en Stasis. Todo queda logueado con prefijo `[STASIS]` en los logs de `platform-api`.

> **Requisito de plataforma:** el consumer corre **sólo en el pod líder** (leader-election sobre `voice:stasis:inbound:leader`, respaldado por Postgres). En single-host SMB hay un solo pod → siempre es líder. En multi-pod, sólo uno abre el WebSocket ARI (Asterisk entrega una app Stasis a un único socket).

## 6. Darle a un agente un endpoint SIP para recibir llamadas

Para que la cola le haga **ring** a un agente, ese agente necesita un **endpoint PJSIP** registrado. Se lo das seteándole `extension` + `sipPassword`:

```bash
# AGENT_ID = GET /admin/agents → .[].id
$ curl -sS -X PUT http://localhost:5000/api/v1/admin/agents/$AGENT_ID \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"extension":"1001","sipPassword":"UNA-PASSWORD-FUERTE"}' | jq
```

Esto sincroniza (vía Realtime) el endpoint del agente a `ps_endpoints` (`{tenant}-agent-{agentId}`, transport WSS) + su `ps_auths` (username `{tenant}-{extension}`). El agente ya es "marcable" desde la cola.

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoint acme-agent-...' | grep -E 'Endpoint:|Aor:'
```

**Cómo responde el agente la llamada encolada — hoy vs Fase 3:**

| Vía | Estado | Cómo |
|---|---|---|
| **Teléfono SIP externo** (escritorio, Zoiper, Linphone) | ✅ disponible hoy | El agente registra su softphone/teléfono con **usuario** `{tenant}-{extension}` (ej. `acme-1001`), **password** `sipPassword`, **dominio/proxy** = tu `EXTERNAL_IP`/dominio, transport UDP/TCP. Cuando entra una llamada a su cola, el teléfono suena. |
| **Softphone en el browser** (responder desde la pestaña del agente) | 🔜 **Fase 3** | SIP.js + WebRTC dentro del Web UI — todavía no implementado. |

> ⚠️ **Honestidad sobre el alcance verificado:** la cadena trunk → Stasis → cola está validada end-to-end con una llamada SIP real (la llamada **entra a la cola**). El tramo "el endpoint del agente registra + suena + audio bidireccional" depende de un cliente SIP registrado y de NAT/RTP correctos; validalo con tu primer teléfono real (§7). El softphone del browser + audio WebRTC es Fase 3.

## 7. Probar una llamada entrante

**Estado inicial:**
- Realtime `Running` (§0). Trunk creado + visible en Asterisk (§3.3). `did_route` DID→cola creado (§4).
- Un agente con `extension`+`sipPassword` (§6) y su teléfono SIP **registrado**:
  ```bash
  $ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts' | grep agent
  ```

**Llamar al DID** desde un teléfono (o, para una prueba de humo sin carrier, con SIPp — ver [07-validacion-e2e.md](07-validacion-e2e.md)):

1. Marcá tu DID (ej. `+1 800 555 1234`).
2. En el server, mirá que la llamada entra y se encola:
   ```bash
   # El consumer loguea el ruteo:
   $ docker logs verbara-platform-api --since 30s | grep STASIS
   [STASIS] Channel 1780....0 (tenant acme, DID 18005551234) → queue 'acme-Cola Atención'.

   # La cola la recibe:
   $ docker exec verbara-asterisk asterisk -rx 'queue show'
   acme-Cola Atención has 1 calls (max unlimited) ...
   ```
3. El teléfono SIP del agente suena → atiende → audio.

### 7.1 La llamada entra pero NO suena ningún agente
- ¿La cola tiene miembros? El agente tiene que estar en la cola **y** `Disponible`, y su teléfono **registrado** (`pjsip show contacts`).
- ¿`A:` (abandoned) sube pero `Completed` no? La llamada llegó a la cola pero ningún endpoint estaba reachable — revisá registración del teléfono del agente.

### 7.2 Suena pero NO hay audio (o en una sola dirección)
- Causa típica: `EXTERNAL_IP` mal o RTP sin port-forwarding. En el `200 OK` del INVITE, el SDP debe anunciar tu **IP pública**, no la LAN:
  ```bash
  $ docker exec verbara-asterisk asterisk -rx 'pjsip set logger on'
  # hacé una llamada y revisá el SDP:  c=IN IP4 {TU-IP-PÚBLICA}
  ```
- Detalle en [08-troubleshooting-sip.md](08-troubleshooting-sip.md) §"No audio".

### 7.3 La llamada se rechaza antes de entrar (`Couldn't find auth` / `No matching endpoint`)
- `Couldn't find auth 'auth-t-{id}'`: trunk IP-ACL mal provisionado (versión vieja). Verificá que el trunk **no** referencia un auth inexistente: `pjsip show endpoint t-{id}` no debe listar `auth` si es IP-ACL.
- `No matching endpoint`: la IP origen del carrier no matchea tu `matchHost`. Confirmá el rango real del carrier y recargá `res_pjsip_endpoint_identifier_ip.so`.

## 8. Troubleshooting — síntoma → solución (índice)

| Síntoma | Detalle en |
|---|---|
| Realtime `Not Running` / Asterisk no ve trunk ni cola | §0 de este manual + [08](08-troubleshooting-sip.md) §"Realtime" |
| "No hay audio" / "Audio en una sola dirección" | [08](08-troubleshooting-sip.md) §"No audio" |
| "Llamada cae a los 30s" | [08](08-troubleshooting-sip.md) §"Stateful UDP timeout" |
| "Trunk no se registra" | [08](08-troubleshooting-sip.md) §"Registration failures" |
| "Caller-ID anonymous" | [08](08-troubleshooting-sip.md) §"From-header rejection" |
| "Eco / distorsión" | [08](08-troubleshooting-sip.md) §"Codec mismatch + jitter" |

## 9. Qué viene en Fase 3 (roadmap de voz)

Estas funciones **no están todavía** y por eso no las documentamos como operativas:

- **Softphone en el browser** — responder/marcar llamadas desde la pestaña del agente (SIP.js + WebRTC contra `wss://…:8089/asterisk/ws`), con control de llamada (hold/transfer/mute), CLID y timer.
- **Llamada saliente desde el browser**.
- **WebRTC behind strict NAT (Coturn/TURN)** — relevante sólo cuando exista el softphone browser y los agentes trabajen desde casa con CGNAT.
- **AMI bridge** que sincroniza el estado de la llamada SIP con la tarjeta de conversación del agente en el Web UI.

Hasta entonces, el patrón soportado es: **voz entrante → cola → teléfono SIP del agente** (§6), con la cadena trunk → Stasis → cola validada end-to-end.

## Próximo paso

→ [07-validacion-e2e.md](07-validacion-e2e.md) — checklist de validación + cómo correr una llamada de prueba con SIPp sin depender del carrier.
