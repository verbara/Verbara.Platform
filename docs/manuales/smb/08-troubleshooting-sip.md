# Manual SMB · 08 — Troubleshooting SIP

> **Audiencia:** operador que ya levantó el canal Voz/SIP (manual [06](06-canal-voz-sip.md)) y se encuentra con problemas.
> **Formato:** referencia rápida — buscás por **síntoma**, leés **causa probable** + **solución**.

> 💡 **Ante cualquier problema SIP**, lo primero es **activar el logger PJSIP** para tener evidencia:
> ```bash
> $ docker exec verbara-asterisk asterisk -rx 'pjsip set logger on'
> $ docker logs -f verbara-asterisk | tee /tmp/pjsip-trace.log
> ```
> Reproducí el problema mientras el logger corre, luego apagalo:
> ```bash
> $ docker exec verbara-asterisk asterisk -rx 'pjsip set logger off'
> ```
> El log captura todos los mensajes SIP (INVITE, 100/180/183/200/BYE, etc.) — invaluable para entender qué pasa.

## Índice rápido

| Síntoma | Sección |
|---|---|
| No hay audio o audio sólo en una dirección | [§1 — No audio](#1--no-audio) |
| Llamada se conecta pero cae a los 30 segundos | [§2 — Stateful UDP timeout](#2--stateful-udp-timeout-llamada-cae-a-los-30s) |
| Agente no recibe ring en el browser | [§3 — WSS handshake failures](#3--wss-handshake-failures) |
| Trunk no se registra / aparece como `Failed` | [§4 — Registration failures](#4--registration-failures) |
| Caller-ID aparece como `Anonymous` o vacío | [§5 — From-header rejection](#5--from-header-rejection) |
| Eco, distorsión, audio entrecortado | [§6 — Codec mismatch + jitter](#6--codec-mismatch--jitter) |
| Calidad pobre con 100+ calls simultáneas | [§7 — CPU + RTP starvation](#7--cpu--rtp-starvation) |
| `pjsip show endpoints` no muestra trunks | [§8 — Realtime DB issues](#8--realtime-db-issues) |
| Llamada rechazada con `403 Forbidden` | [§9 — IP ACL mismatch](#9--ip-acl-mismatch) |
| `488 Not Acceptable Here` | [§10 — Codec offer rejected](#10--488-codec-offer-rejected) |

---

## 1 — No audio

### Síntoma
La llamada conecta (ring + accept funcionan), pero:
- **Ningún lado oye nada** → audio bidireccional roto.
- **Sólo uno oye** → audio asimétrico (típicamente el que está detrás del NAT del server oye al otro pero no a la inversa).

### Causas más comunes

#### 1.1 EXTERNAL_IP mal configurada / no configurada

Asterisk reescribe el SDP de las respuestas con `EXTERNAL_IP`. Si está vacía pero estás detrás de NAT, el peer remoto manda RTP a tu IP privada (`192.168.40.100`), que no es ruteable desde internet → audio se pierde.

```bash
$ grep EXTERNAL_IP /opt/verbara/platform/docker/.env.reference-smb
EXTERNAL_IP=                               # ← vacío, mal!

# Setear:
$ ${EDITOR:-nano} /opt/verbara/platform/docker/.env.reference-smb
# → EXTERNAL_IP=200.118.42.61
$ cd /opt/verbara/platform && dc restart asterisk
```

**Validar después:** activar `pjsip set logger on`, hacer una llamada, ver el SDP en el `200 OK`:
```
v=0
o=- ... ...
c=IN IP4 200.118.42.61    ← debe ser tu IP pública
m=audio 20034 RTP/AVP 0 8
```

Si sigue mostrando IP privada, revisar que el `entrypoint-asterisk.sh` haya hecho la sustitución:
```bash
$ docker exec verbara-asterisk grep external_media_address /etc/asterisk/pjsip.conf
external_media_address = 200.118.42.61
external_signaling_address = 200.118.42.61
```

#### 1.2 Port-forwarding del router NO incluye el RTP range

El router/firewall forwarda 5060 pero no 20000-20200 → SIP funciona, RTP no.

```bash
# Desde otra máquina en internet (no LAN):
$ nc -uvz {tu-IP-pública} 20050 < /dev/null
# Esperado: "Connection succeeded"
# Si dice "timed out" → puerto no llega
```

**Solución:** revisar manual [01](01-instalacion-docker.md) §3 — agregar el rango completo `20000-20200/udp` al port-forward o DMZ.

#### 1.3 EXTERNAL_IP correcta pero `ice_host_candidates` falta

Para WebRTC behind NAT, Asterisk anuncia candidates ICE — si la traducción privada→pública no está, el browser intenta conectar a la privada.

El `entrypoint-asterisk.sh` lo agrega automáticamente al `rtp.conf` cuando `EXTERNAL_IP` está seteada:
```ini
[ice_host_candidates]
{IP_CONTAINER} => {EXTERNAL_IP}
```

Verificar:
```bash
$ docker exec verbara-asterisk grep -A 2 ice_host_candidates /etc/asterisk/rtp.conf
[ice_host_candidates]
172.17.0.5 => 200.118.42.61
```

Si no aparece, el entrypoint no corrió bien — `docker logs verbara-asterisk | head -20` para ver mensajes de entrypoint.

#### 1.4 Carrier hace SRTP pero Asterisk no

Algunos trunks fuerzan SRTP (cifrado de RTP). Si el endpoint Verbara no lo soporta, la llamada conecta SIP pero el RTP se descarta.

Validar en el INVITE entrante del trunk:
```
m=audio 12345 RTP/SAVP 0 8       ← SAVP = SRTP
a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:...
```

**Solución:** habilitar SRTP en el trunk endpoint:
```bash
$ curl -sS -X PATCH .../api/v1/dialer/trunks/{id} \
    -d '{"mediaEncryption": "Sdes", "mediaUseReceivedTransport": true}'
```

---

## 2 — Stateful UDP timeout (llamada cae a los 30s)

### Síntoma
La llamada conecta y funciona bien por exactamente 30 segundos (a veces 60 o 90). Después corta sin razón aparente. CDR muestra `END_CAUSE: NORMAL_CLEARING` pero el agente no colgó.

### Causa
El firewall del router del cliente (o el del server) es **stateful UDP** y caduca la conexión UDP del SIP signalling después de N segundos de inactividad. SIP no manda keep-alives por default → cuando el firewall caduca, los mensajes `BYE` no se entregan, pero los siguientes paquetes RTP tampoco.

### Solución
Habilitar **SIP keepalive** en el trunk + en los endpoints WebRTC:

```bash
# Trunk
$ curl -sS -X PATCH .../api/v1/dialer/trunks/{id} \
    -d '{
      "qualifyFrequencyS": 30,
      "keepAliveIntervalS": 25
    }'
```

Esto hace que Asterisk mande `OPTIONS` SIP cada 30s al trunk + cada 25s a los endpoints WebRTC → mantiene la conexión NAT viva.

**Validar:**
```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts' | head -5
twilio-elastic/sip:...    Available      35.345ms
agente1/sip:...           Available      12.123ms
```

`Available` confirma keepalives funcionando.

---

## 3 — WSS handshake failures

### Síntoma
Agente abre `/agent`, click **Conectar**, indicador queda en rojo (`Desconectado`). En la consola devtools del browser:
```
WebSocket connection to 'wss://verbara.tu-dominio.com:8089/asterisk/ws' failed:
Error during WebSocket handshake: Unexpected response code: 502
```
O:
```
WebSocket connection to 'wss://...' failed:
SSL certificate problem: self signed certificate
```

### Causas

#### 3.1 Asterisk no tiene cert TLS para WSS

Asterisk usa `/etc/asterisk/keys/asterisk.pem` + `.key` para WSS. El image base trae un self-signed que NO sirve para browsers en producción.

**Solución:** montar tu cert Let's Encrypt:

```yaml
# En docker-compose.reference-smb.yml, service asterisk:
    volumes:
      - ./asterisk-config:/etc/asterisk
      - /etc/letsencrypt/live/verbara.tu-dominio.com/fullchain.pem:/etc/asterisk/keys/asterisk.pem:ro
      - /etc/letsencrypt/live/verbara.tu-dominio.com/privkey.pem:/etc/asterisk/keys/asterisk.key:ro
```

```bash
$ dc up -d --force-recreate asterisk
```

Renovación automática: certbot ya renueva, pero hay que `dc restart asterisk` después de cada renovación para que pickee el cert nuevo. Agregar a `/etc/letsencrypt/renewal-hooks/deploy/restart-verbara-asterisk.sh`:
```bash
#!/bin/sh
cd /opt/verbara/platform && \
docker compose -f docker/docker-compose.reference-smb.yml \
               --env-file docker/.env.reference-smb \
               restart asterisk
```

#### 3.2 Mixed-content blocked

Web está bajo `http://` (no `https://`) pero intenta abrir `wss://` → algunos browsers (Firefox estricto) lo bloquean.

**Solución:** servir Web bajo TLS (manual [01](01-instalacion-docker.md) §5). En producción es obligatorio igual porque el browser no da micrófono sin HTTPS.

#### 3.3 Origen no permitido por Asterisk

Asterisk valida el `Origin:` del WSS handshake. Si tu Web está en `https://verbara.tu-dominio.com` pero Asterisk no tiene ese origin, rechaza con 403.

Verificar `/etc/asterisk/http.conf`:
```ini
[general]
enabled = yes
bindaddr = 0.0.0.0
bindport = 8088
tlsenable = yes
tlsbindaddr = 0.0.0.0:8089
tlscertfile = /etc/asterisk/keys/asterisk.pem
tlsprivatekey = /etc/asterisk/keys/asterisk.key
sessionlimit = 200
```

El default permite cualquier origin si no se setea explícitamente. Si tu instalación tiene `allowed_origins`, agregá `https://verbara.tu-dominio.com`.

---

## 4 — Registration failures

### Síntoma
El trunk aparece como `Failed` o `Unreachable` en:
```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show registrations'
twilio-elastic/sip:...    creds    Rejected
```

### Causas

#### 4.1 Credenciales mal

```bash
$ docker logs verbara-asterisk 2>&1 | grep -i 'authentication'
auth_check: Authentication failed for ...
```

Verificar trunk:
```bash
$ curl -sS .../api/v1/dialer/trunks/{id} | jq '{authType, authUser}'
```

#### 4.2 IP no whitelisteada

El trunk usa IP ACL pero el carrier no tiene tu IP pública agregada.

```bash
$ docker logs verbara-asterisk 2>&1 | grep -i '403'
"SIP/2.0 403 Forbidden"
```

**Solución:** loguearte al carrier portal y agregar tu `EXTERNAL_IP` al ACL.

#### 4.3 Reachability del carrier

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show contact twilio-elastic'
Contact:    twilio-elastic/sip:...   Unreachable
```

Validar DNS + reachability:
```bash
$ docker exec verbara-asterisk dig +short verbara-tu-empresa.pstn.twilio.com
$ docker exec verbara-asterisk nc -uvz verbara-tu-empresa.pstn.twilio.com 5060
```

Si DNS no resuelve → el container no tiene resolver. Si resuelve pero nc falla → firewall outbound del host bloquea SIP.

---

## 5 — From-header rejection (caller-ID anonymous)

### Síntoma
Hacés llamada saliente. El móvil destino suena pero:
- El caller-ID aparece como `Anonymous` o `Unknown`.
- O aparece con un número desconocido (default del carrier).

### Causa
El carrier rechazó el `From:` header que mandó Asterisk y lo reemplazó con el default. Pasa cuando:
- Mandás un número que NO está provisionado en tu cuenta del carrier (ej. otro DID que no compraste).
- Mandás el number en formato no-E.164 (ej. `5555550199` cuando el carrier quiere `+15555550199`).

### Solución

```bash
$ curl -sS -X PATCH .../api/v1/dialer/trunks/{id} \
    -d '{
      "callerId": "+15551234567",
      "fromUser": "+15551234567",
      "fromDomain": "verbara-tu-empresa.pstn.twilio.com"
    }'
```

Validar en el INVITE saliente (después de `pjsip set logger on`):
```
INVITE sip:+15555550199@verbara-tu-empresa.pstn.twilio.com SIP/2.0
From: <sip:+15551234567@verbara-tu-empresa.pstn.twilio.com>;tag=...
```

`From:` debe ser tu DID correcto.

---

## 6 — Codec mismatch + jitter

### Síntoma
- Audio con eco metálico.
- Audio entrecortado / glitches periódicos.
- Distorsión en el habla rápida.

### Causa
- Codec mismatch → transcoding agresivo en CPU saturada.
- Jitter alto en la red → RTP buffer no compensa.

### Diagnóstico

```bash
# Stats de RTP en una llamada activa
$ docker exec verbara-asterisk asterisk -rx 'rtcp show stats'
                                      Loss%   Jitter      Round Trip
PJSIP/twilio-...                       0.2%    12 ms       45 ms
PJSIP/agente1-...                      0.5%    25 ms       180 ms      ← jitter alto
```

`Jitter > 30 ms` = perceptible. `> 80 ms` = audio inutilizable.

### Soluciones

#### 6.1 Forzar codec común evitando transcoding

```bash
# Trunk: G.711 ulaw + alaw fallback
$ curl -X PATCH .../api/v1/dialer/trunks/{id} \
    -d '{"codecs": ["g711_ulaw", "g711_alaw"]}'

# Agente WebRTC: G.711 también (perdés Opus wideband, ganás 5× CPU)
$ curl -X PATCH .../api/v1/admin/agents/agente1/webrtc \
    -d '{"preferredCodecs": ["ulaw", "alaw"]}'
```

#### 6.2 Aumentar jitter buffer

Editar `/etc/asterisk/pjsip.conf` del endpoint:
```ini
use_avpf = no
ice_support = yes
rtp_keepalive = 5
```

Y `/etc/asterisk/rtp.conf`:
```ini
[general]
rtpstart = 20000
rtpend = 20200
strictrtp = no            ; default yes — relax si hay NAT raro
rtpchecksums = no
```

`dc restart asterisk` para aplicar.

#### 6.3 Validar que la WAN no está saturada

```bash
$ iperf3 -c speedtest-server.example.com -t 60
# Si bandwidth simétrico < lo que tu tier necesita → upgrade WAN
```

---

## 7 — CPU + RTP starvation

### Síntoma
Con < 50 calls todo OK. A partir de cierto threshold:
- Audio comienza a degradar global (todas las llamadas, no una).
- `top` muestra Asterisk en 90+ %CPU.
- Nuevas llamadas no entran o se cuelgan.

### Causa
CPU del host saturada. Si estás en SMB Lite (4 vCPU) y tenés 100 calls G.711 = 200 % CPU (~ 2 cores). Si encima hay transcoding Opus↔G.711, ya saturás los 4.

### Solución

1. Validar tier hardware vs calls reales:
   ```bash
   $ docker stats --no-stream verbara-asterisk
   CONTAINER          CPU %        MEM USAGE / LIMIT
   verbara-asterisk   195.32%      1.4GiB / 4GiB
   ```
2. Si CPU > 80 % consistente → escalar tier (manual [06](06-canal-voz-sip.md) §10).
3. Si memoria > 80 % → bump `ASTERISK_MEM_LIMIT`.

---

## 8 — Realtime DB issues

### Síntoma
`pjsip show endpoints` muestra solo `transport-*` pero ningún endpoint user. O los endpoints aparecen pero el agente no puede registrarse.

### Causa
Asterisk no puede leer del Postgres realtime. Recordá que en SMB con `network_mode: host`, Asterisk conecta a Postgres por **`127.0.0.1:5432`**, no `postgres:5432`.

### Diagnóstico

```bash
# Asterisk dice qué pasa con la conexión PG
$ docker exec verbara-asterisk asterisk -rx 'realtime show pgsql status'
Connected to verbara@127.0.0.1, port 5432 with username platform

# Si dice "Failed to connect" → revisar:
$ docker exec verbara-asterisk grep dbhost /etc/asterisk/res_config_pgsql.conf
dbhost = 127.0.0.1                      ; debe ser 127.0.0.1, NO postgres

$ docker exec verbara-asterisk env | grep PG_REALTIME
PG_REALTIME_HOST=127.0.0.1
PG_REALTIME_DB=verbara
PG_REALTIME_USER=platform
PG_REALTIME_PASSWORD=...
```

### Solución
Si las env vars no están seteadas: el `.env.reference-smb` está incompleto — re-copiar del `.example` y ajustar.

Si las env vars están bien pero `dbhost` en el conf sigue siendo `postgres` → el entrypoint no rewrote. `docker logs verbara-asterisk | head -20` para ver mensajes de entrypoint. Reiniciar:
```bash
$ dc up -d --force-recreate asterisk
```

---

## 9 — IP ACL mismatch

### Síntoma
INVITE del trunk al server llega pero Asterisk responde `403 Forbidden`.

### Causa
PJSIP usa `identify` para matchear endpoints por IP. Si la IP del trunk no está en el `identify` o no matchea, rechaza.

### Diagnóstico

```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show identifies'
   <Identify/Endpoint..............................>
   twilio-elastic/twilio-elastic
        Match: 54.172.60.0/30
        Match: 54.244.51.0/30
        ...
```

Comparar con la IP origen del INVITE rechazado (logger PJSIP on):
```
<--- Received SIP request (855 bytes) from UDP:18.235.121.45:5060 --->
INVITE sip:+15551234567@200.118.42.61:5060 SIP/2.0
```

`18.235.121.45` no aparece en el identify → 403.

### Solución
Agregar la IP al trunk:
```bash
$ curl -X PATCH .../api/v1/dialer/trunks/{id} \
    -d '{"matchIps": ["18.235.121.45/32", "54.172.60.0/30", ...]}'
```

O pedir al carrier la lista oficial de IPs origen y agregarlas todas.

---

## 10 — 488 Codec offer rejected

### Síntoma
INVITE entrante responde:
```
SIP/2.0 488 Not Acceptable Here
```

### Causa
El codec offered en el SDP del INVITE no es ninguno de los configurados en el endpoint Verbara.

### Diagnóstico

```bash
# SDP del INVITE
INVITE ... SIP/2.0
...
m=audio 12345 RTP/AVP 9 18           ; codec 9 = G.722, 18 = G.729
```

Endpoint Verbara:
```bash
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoint twilio-elastic' | grep -i codec
   Codec: ulaw alaw
```

Mismatch: el peer ofrece G.722/G.729, Verbara solo acepta ulaw/alaw.

### Solución
Agregar codecs aceptados:
```bash
$ curl -X PATCH .../api/v1/dialer/trunks/twilio-elastic \
    -d '{"codecs": ["g711_ulaw", "g711_alaw", "g722"]}'
```

> ⚠️ G.729 requiere licencia comercial. No la agregues sin saber qué implica.

---

## Diagnóstico end-to-end: probar audio paso a paso

Si nada funciona y querés un diagnóstico sistemático, correr este orden:

```bash
# 1. ¿Asterisk corre y bound?
$ docker exec verbara-asterisk asterisk -rx 'pjsip show transports'

# 2. ¿Trunk está registered/reachable?
$ docker exec verbara-asterisk asterisk -rx 'pjsip show registrations'
$ docker exec verbara-asterisk asterisk -rx 'pjsip show contacts'

# 3. ¿Endpoint del agente existe en realtime?
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoints'

# 4. ¿Llamada de prueba LAN → LAN funciona? (loopback)
#    Registrar 2 softphones Linphone como agente1 y agente2, marcar entre sí.

# 5. ¿Llamada de prueba móvil → DID llega al server?
$ sudo tcpdump -i any -n port 5060 -A | head -50
# Llamar al DID → ver el INVITE entrante en el tcpdump

# 6. ¿Llegan paquetes RTP?
$ sudo tcpdump -i any -n 'udp portrange 20000-20200' -c 100

# 7. CPU/memoria del host bajo carga?
$ docker stats verbara-asterisk verbara-platform-api
```

Si en algún punto algo falla → buscar el síntoma en este manual.

## Próximo paso

→ Volver al manual que estabas siguiendo, o ir a [99-troubleshooting.md](99-troubleshooting.md) para problemas no-SIP (DB, API, Web).
