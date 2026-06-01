# Manual SMB · 06 — Canal Voz/SIP

> **Audiencia:** operador con stack arriba + setup inicial completo + firewall/NAT validado (manual [01](01-instalacion-docker.md) §3 OK).
> **Tiempo:** 60–90 minutos (más si es la primera vez que configurás SIP).
> **Pre-requisitos:**
> - Trunk SIP con un carrier o Twilio Elastic SIP — necesitás credenciales (usuario/password) **o** que el carrier permita autenticación por IP.
> - DID (Direct Inward Dialing) — el número telefónico que va a aterrizar las llamadas en tu Verbara.
> - Servidor con `EXTERNAL_IP` configurada (si estás detrás de NAT).
> - Una **cola** ya creada (manual [03](03-setup-inicial.md) §4) y al menos un **agente** con su login.

> ⚠️ **ALCANCE — leé esto antes de empezar.** La **voz entrante llega a la cola** (la llamada del carrier se identifica como tu trunk, se resuelve el tenant, el DID se mapea a una cola y Asterisk la encola) **y** el agente la atiende de dos formas: con un **teléfono SIP externo** (escritorio, Zoiper, Linphone — §6) **o** con el **softphone en el navegador** (responder + audio WebRTC bidireccional + control de llamada + saliente, ya implementado y validado en lab — §9). Lo que **todavía NO está** (roadmap §10): **transferencia atendida/consulta + conferencia**, **supervisor monitor/whisper/barge**, y **TURN/Coturn** (sólo para agentes tras NAT estricto). El softphone del navegador viene en el **build de Fase 3** — ver la nota de release en §9. Cada sección marca claramente qué está **✅ verificado**, qué es **🔜 roadmap** y qué requiere **validación con tu carrier real**.

Este manual cubre el canal de voz entrante end-to-end con los endpoints reales:

1. Pre-requisitos de red (puertos + bandwidth).
2. Capacidad por tier — agentes y llamadas concurrentes.
3. Provisionar un trunk SIP (IP-ACL o registración) — **`POST /admin/trunks`**.
4. Mapear el DID a una cola — **`POST /admin/did-routes`**.
5. Cómo fluye una llamada entrante por dentro (trunk → Stasis → cola).
6. Darle a un agente un endpoint SIP para recibir llamadas.
7. Probar una llamada entrante.
8. Troubleshooting (índice → [08](08-troubleshooting-sip.md)).
9. Softphone en el navegador — atender + audio + control de llamada + saliente.
10. Qué sigue (roadmap: transferencia atendida, supervisor, Coturn).

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

> El puerto `8089/tcp` (WSS WebRTC) hace falta para el **softphone del navegador** (§9) — abrilo si tus agentes van a atender desde la pestaña. Para voz entrante a cola + teléfonos SIP externos NO es necesario.

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

**Cómo responde el agente la llamada encolada — dos vías:**

| Vía | Estado | Cómo |
|---|---|---|
| **Teléfono SIP externo** (escritorio, Zoiper, Linphone) | ✅ disponible hoy | El agente registra su softphone/teléfono con **usuario** `{tenant}-{extension}` (ej. `acme-1001`), **password** `sipPassword`, **dominio/proxy** = tu `EXTERNAL_IP`/dominio, transport UDP/TCP. Cuando entra una llamada a su cola, el teléfono suena. |
| **Softphone en el navegador** (responder desde la pestaña del agente) | ✅ implementado, validado en lab | SIP.js + WebRTC dentro del Web UI: ring + atender + audio bidireccional + control de llamada + saliente, con la llamada rastreada como conversación. Viene en el **build de Fase 3** (ver nota de release en §9). Requiere puerto 8089 + cert WSS + contexto seguro para el mic — **todo el detalle en §9**. |

> ⚠️ **Honestidad sobre el alcance verificado:** la cadena trunk → Stasis → cola está validada end-to-end con una llamada SIP real (la llamada **entra a la cola**). El tramo "el endpoint del agente registra + suena + audio bidireccional" depende de un cliente SIP registrado y de NAT/RTP correctos; validalo con tu primer teléfono/navegador real (§7 / §9). El softphone del navegador está **implementado y validado en lab** (Fase 3) — leé su **nota de release y los pre-requisitos de contexto seguro en §9** antes de prometérselo a un cliente.

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

## 9. Softphone en el navegador (Fase 3) — ✅ implementado, validado en lab

El agente ya puede **atender y hacer llamadas desde la pestaña del navegador**, sin teléfono externo: un softphone SIP.js + WebRTC se registra contra Asterisk por WSS, la llamada encolada **suena en el navegador**, el agente atiende y hay **audio bidireccional**. La llamada se rastrea como una **conversación de voz** (screen-pop con quién llama + su historial, agent-assist en vivo, y wrap-up/disposición al colgar).

> ⚠️ **Estado de release.** Fase 3 (softphone navegador + audio WebRTC + control de llamada + saliente) **ship en Platform `v2.7.0` + Web `v3.3.0-web`** — los defaults del `docker-compose.reference-smb.yml` (`PLATFORM_API_TAG=v2.7.0`, `PLATFORM_WEB_TAG=v3.3.0-web`). Code-completo + validado en lab (Fase 3 cerrada 2026-06-01). Si fijaste tags previos (`v2.6.0` / `v3.2.0-web`) **no vas a ver el softphone** — son anteriores a Fase 3; usá los defaults o tags ≥ esos. El patrón de **teléfono SIP externo** (§6) sigue válido como alternativa y no depende de esto.

### 9.1 Pre-requisitos

- **Puerto `8089/tcp` (WSS) abierto** al `EXTERNAL_IP` — es por donde el navegador hace REGISTER (a diferencia de la voz entrante a cola, que no lo necesita).
- **Certificado TLS del WSS.** El entrypoint de Asterisk **auto-genera un cert self-signed** en el arranque si no hay uno montado (`docker/gen-asterisk-cert.sh` → `/var/lib/asterisk/keys/`). Para producción, montá un cert real (Let's Encrypt) en su lugar.
- **Perfil WebRTC** como default del agente — los agentes nuevos se sincronizan con perfil WebRTC automáticamente. Si tu tenant fue creado antes de Fase 3, ejecutá una vez:
  ```bash
  $ curl -sS -X POST http://localhost:5000/api/v1/admin/realtime/profiles/seed-defaults \
      -H "Authorization: Bearer $TOKEN"
  # GET .../admin/realtime/profiles debe mostrar "WebRTC Agent" con isDefault:true
  ```

### 9.2 Configurar la URL WSS que usa el navegador

El Web UI lee `asteriskWssUrl` de su `config.json` (servido al boot). Seteá la **ruta `/ws`** (es la default de `res_http_websocket` de Asterisk — **no** `/asterisk/ws`):

```jsonc
// config.json servido por el Web
{ "asteriskWssUrl": "wss://TU-EXTERNAL_IP:8089/ws" }
```

Si queda vacío, el softphone no arranca (voz queda como canal de texto).

### 9.3 Aceptar el certificado self-signed (una vez por navegador)

Con cert self-signed, el navegador rechaza el WSS hasta que el operador/agente visita **`https://TU-EXTERNAL_IP:8089/`** una vez y acepta la advertencia. Sin esto, el REGISTER falla en silencio (SIP.js sólo reporta un error de transporte). Con un cert real (Let's Encrypt) este paso no hace falta.

### 9.4 Contexto seguro para el micrófono (CRÍTICO)

`getUserMedia` (el mic) **sólo funciona en un contexto seguro**: `https://…` **o** `http://localhost`. El reference-smb sirve el Web UI por **HTTP en el puerto `80`** → si el agente entra por `http://TU-IP-LAN`, el navegador **bloquea el micrófono** y no hay audio. Opciones:

- **Agente en la misma máquina** → entrá por `http://localhost` (es contexto seguro).
- **Agentes remotos** → **terminá TLS** para el origen del Web (poné un cert en el nginx-gateway / reverse-proxy y serví el Web por `https://`). Es el camino de producción.

### 9.5 Provisionar el agente del navegador

Igual que §6 — el agente necesita `extension` + `sipPassword`. Desde la UI de admin (**Agentes → editar → Extensión + Generar password**), o por API (§6). El secreto se expone **sólo al propio agente** vía `GET /agents/me` (nunca en los listados de admin).

### 9.6 Qué puede hacer el agente desde el navegador — ✅ implementado

| Capacidad | Estado | Nota |
|---|---|---|
| Registrar + recibir ring + **atender** + **colgar** | ✅ | la tarjeta de llamada suena en `/agent`; audio WebRTC bidireccional |
| **Auto-answer** (opt-in) | ✅ | flag por-agente con cascada al default de la cola; zip-tone al auto-atender; gated en mic concedido |
| **Hold / Mute / Teclado DTMF** | ✅ | control client-side (SimpleUser) |
| **Transferencia ciega** a cola / a otro agente / a número externo | ✅ | server-side (AMI Redirect, leader-gated) |
| **Llamada saliente** (click-to-dial) | ✅ | reusa el stack del Dialer Pro: DNC → ruta→trunk → caller-ID del tenant; rastreada como conversación saliente |
| **Screen-pop + agent-assist + wrap-up** | ✅ | la llamada es una conversación de voz rastreada (contacto/historial, transcripción/sentimiento, disposición al colgar) |

> El **caller-ID saliente** se setea por tenant (Configuración → caller-ID saliente). Sin él, se usa el default del trunk.

### 9.7 Verificar el registro del navegador

```bash
# El contacto del agente debe aparecer sobre transport-wss
$ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts' | grep -i agent
  Contact:  acme-agent-...   transport-wss   ...   Avail
```

Pasa = contacto `transport-wss` visible + la llamada encolada hace ring en `/agent` + atender da audio + colgar limpia el canal.

> ⚠️ **Limitación de lab (no del producto):** en el lab el agente WebRTC habla **opus** y el generador de prueba (SIPp) habla **ulaw**; este Asterisk de lab no transcodifica opus↔ulaw para legs sintéticos, así que el audio de prueba se corta a los segundos. Con agentes WebRTC reales + carriers reales (que negocian un codec común) esto no aplica. La señalización, el ruteo, el bridging y el rastreo de conversación quedan validados igual.

## 10. Qué sigue (roadmap de voz — todavía NO implementado)

Estas funciones **no están todavía** y por eso no las documentamos como operativas:

- **Transferencia atendida / consulta** y **conferencia** (3 o más participantes) — hoy la transferencia es **ciega** (§9.6). (Fase 3B.3.)
- **Supervisor: monitor / whisper / barge** de **voz** (escuchar la llamada, susurrarle al agente, entrar a la llamada) — vía ARI Snoop, **no implementado**. (Fase 3B.3.) *(Ojo: el whisper/coaching que existe hoy es de texto sobre conversaciones digitales, no audio de voz.)*
- **Transferir una llamada saliente que ya está activa** — la entrante y la saliente recién originada se transfieren bien; transferir una saliente en curso aún no está cableado.
- **WebRTC tras NAT estricto (Coturn/TURN)** — necesario sólo cuando los agentes trabajan desde casa con CGNAT. Para LAN / mismo host alcanza con `EXTERNAL_IP` + candidatos host.

El patrón base sigue intacto: **voz entrante → cola → (teléfono SIP externo §6 **o** softphone navegador §9)**, con la cadena trunk → Stasis → cola validada end-to-end.

## Próximo paso

→ [07-validacion-e2e.md](07-validacion-e2e.md) — checklist de validación + cómo correr una llamada de prueba con SIPp sin depender del carrier.
