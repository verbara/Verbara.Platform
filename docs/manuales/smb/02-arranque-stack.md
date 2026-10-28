# Manual SMB · 02 — Arranque del stack

> **Audiencia:** operador con un servidor Linux listo (manual [01](01-instalacion-docker.md) terminado).
> **Tiempo:** 15-20 minutos (primera vez puede ser 30 min — pull de imágenes + build de Asterisk).
> **Pre-requisitos:**
> - Docker + Compose plugin instalados, usuario en grupo `docker`.
> - Firewall del host y router/cloud Security Group configurados.
> - IP pública conocida (para `EXTERNAL_IP` si estás en escenario B o C).

## 1. Clonar el repositorio público

```bash
$ sudo mkdir -p /opt/verbara && sudo chown $USER:$USER /opt/verbara
$ cd /opt/verbara
$ git clone https://github.com/verbara/platform.git
$ cd platform
$ git checkout v2.34.0     # el tag de la release a deployar
```

> 💡 Si querés un workdir distinto (ej. `/srv/verbara` o `/home/operator/verbara`), reemplazá `/opt/verbara` por lo que prefieras — el manual asume `/opt/verbara/platform` de aquí en adelante.

Validá que tenés todo:

```bash
$ ls docker/docker-compose.reference-smb.yml docker/.env.reference-smb.example scripts/quickstart-smb.sh
docker/.env.reference-smb.example
docker/docker-compose.reference-smb.yml
scripts/quickstart-smb.sh
```

Si alguno no aparece, estás en un commit/tag viejo — re-checkout a `v2.34.0` o superior.

> ⚠️ **El tag que hacés checkout decide qué corrés**: el compose y el `.env` de ejemplo de ese tag fijan el par de imágenes (`PLATFORM_API_TAG=v2.34.0` para api/realtime/renderer/mail, `PLATFORM_WEB_TAG=v3.27.1-web` para la consola). No mezcles el compose de un tag con imágenes de otro.

## 2. Verificar firmas de las imágenes (opcional pero recomendado)

Las imágenes públicas `ghcr.io/verbara/platform/{api,realtime,renderer,mail,web}` están firmadas con [cosign](https://docs.sigstore.dev/cosign/overview/) (ADR-0023). Validar antes del primer pull asegura que la imagen no fue tampered y viene del workflow oficial.

Instalar cosign una sola vez:

```bash
$ COSIGN_VER=v3.0.6   # cosign v2.x ya no valida firmas del flujo actual
$ curl -L "https://github.com/sigstore/cosign/releases/download/${COSIGN_VER}/cosign-linux-amd64" \
    -o /tmp/cosign && chmod +x /tmp/cosign && sudo mv /tmp/cosign /usr/local/bin/cosign
$ cosign version
```

Verificar las cinco imágenes:

```bash
$ cd /opt/verbara/platform
$ for img in api realtime renderer mail; do
    cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
        ghcr.io/verbara/platform/$img:v2.34.0
  done

$ cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
    ghcr.io/verbara/platform/web:v3.27.1-web
```

Esperado en cada caso:
```
Verification for ghcr.io/verbara/platform/api:v2.34.0 --
The following checks were performed on each of these signatures:
  - The cosign claims were validated
  - The signatures were verified against the specified public key
```

> 🔒 `cosign.pub` está commiteado en el repo (`docker/cosign.pub` y `.github/cosign.pub`). Si querés un canal independiente, descargá la misma clave desde [verbara.io/keys/cosign.pub](https://verbara.io) y comparalos:
> ```bash
> $ curl -sL https://verbara.io/keys/cosign.pub | diff - docker/cosign.pub
> ```

## 3. Copiar y editar el archivo `.env`

```bash
$ cp docker/.env.reference-smb.example docker/.env.reference-smb
$ ${EDITOR:-nano} docker/.env.reference-smb
```

### 3.1 Valores que **debés** cambiar antes de arrancar

| Variable | Cómo generar | Por qué |
|---|---|---|
| `POSTGRES_PASSWORD` | `openssl rand -base64 32` | Contraseña del DB |
| `SERVICE_KEY` | `openssl rand -base64 32` | Auth interna entre Api / Realtime / Renderer / Mail; el servicio mail también la usa para entregarle al Api las respuestas CSAT |
| `CSAT_REPLY_TOKEN_SECRET` | `openssl rand -base64 32` | El Api firma con ella el `Reply-To` `csat+<token>@…` de cada encuesta CSAT por email y el servicio mail verifica las respuestas con la misma. Obligatoria aunque no actives el poller IMAP (el compose no arranca sin ella) |
| `CUSTOMER_TENANT_ID` | el id del tenant Customer que vas a crear en el wizard (slug en minúsculas, ej. `acme`) | El Api le sirve a Asterisk los agentes, colas y trunks de este tenant; también es el tenant por defecto del buzón CSAT. No puede quedar vacía: el compose se niega a arrancar (un valor vacío nombraría un tenant `""`) |
| `AMI_PASSWORD` | `openssl rand -base64 32` | Acceso Asterisk Manager Interface |
| `ARI_PASSWORD` | `openssl rand -base64 32` | Acceso Asterisk REST Interface |
| `EXTERNAL_IP` | tu IP pública (Escenario B/C) | Asterisk reescribe SDP con esto — sin esto no hay audio bidireccional con peers externos |
| `CORS_ORIGINS` | `https://verbara.tu-dominio.com` | Single origin permitido para la SPA |
| `PUBLIC_BASE_URL` | `https://verbara.tu-dominio.com` (o `http://192.168.1.50` si entran por IP de la LAN) | Dirección con la que los usuarios abren la consola: con ella se arman el enlace de restablecer contraseña y el `redirect_uri` de OIDC, nunca con la cabecera `Host`. El ejemplo la trae activa con `http://localhost`: cambiala. Si `CORS_ORIGINS` nombra un solo origen podés comentarla; con el default de dos orígenes es obligatoria |
| `LICENSE_DAY_ZONE` | zona IANA, ej. `America/Bogota` (default `UTC`) | Zona horaria que corta el día de medición de agentes licenciados. **Elegila una sola vez, antes del primer arranque**: el primer arranque la fija en la base, y cambiarla después detiene el cierre diario hasta que se restaure |

**Atajo: generar todos los secrets a la vez** y pegarlos al `.env`:

```bash
$ cat <<EOF
POSTGRES_PASSWORD=$(openssl rand -base64 32 | tr -d '=+/\n')
SERVICE_KEY=$(openssl rand -base64 32 | tr -d '=+/\n')
CSAT_REPLY_TOKEN_SECRET=$(openssl rand -base64 32 | tr -d '=+/\n')
AMI_PASSWORD=$(openssl rand -base64 32 | tr -d '=+/\n')
ARI_PASSWORD=$(openssl rand -base64 32 | tr -d '=+/\n')
EOF
```

> ⚠️ **Guardar los secretos en un gestor de contraseñas (1Password / Bitwarden / Vault).** Si perdés `POSTGRES_PASSWORD`, perdés acceso al DB. Si cambiás `CSAT_REPLY_TOKEN_SECRET`, las respuestas a encuestas CSAT ya enviadas dejan de verificarse.

> 💡 **No hay clave de firma JWT que configurar.** El Api genera sus claves de firma y las guarda en Redis, donde Realtime las lee para validar los tokens de la consola. Por eso Redis es un servicio fijo del stack (ya no un perfil opcional) y recrear el contenedor del Api no cierra las sesiones. `REDIS_PASSWORD` es opcional: si la definís, Redis arranca con `--requirepass` y el compose agrega `,password=…` a cada cadena de conexión.

### 3.2 Tier de capacidad — ajustar según tu hardware

| Tier | Editá estas variables en `.env` |
|---|---|
| **SMB Lite** (4 vCPU / 16 GB) | (defaults ya están — `RTP_PORT_END=20200`) |
| **SMB Standard** (8 vCPU / 32 GB) | `RTP_PORT_END=20400`<br>`ASTERISK_CPU_LIMIT=6.0`<br>`ASTERISK_MEM_LIMIT=8G`<br>`PLATFORM_API_CPU_LIMIT=4.0`<br>`PLATFORM_API_MEM_LIMIT=3G`<br>`POSTGRES_MEM_LIMIT=4G`<br>`PG_SHARED_BUFFERS=1GB`<br>`PG_EFFECTIVE_CACHE_SIZE=4GB` |
| **SMB Plus** (16 vCPU / 64 GB) | `RTP_PORT_END=20600`<br>`ASTERISK_CPU_LIMIT=12.0`<br>`ASTERISK_MEM_LIMIT=16G`<br>`PLATFORM_API_CPU_LIMIT=8.0`<br>`PLATFORM_API_MEM_LIMIT=6G`<br>`POSTGRES_MEM_LIMIT=8G`<br>`PG_SHARED_BUFFERS=2GB`<br>`PG_EFFECTIVE_CACHE_SIZE=8GB`<br>`PG_POOL_MAX=20` |

Detalle completo en [capacity-reference.md](capacity-reference.md).

### 3.3 Email (opcional ahora)

Si todavía no tenés las credenciales SMTP/IMAP, podés dejar las defaults y configurar más tarde en [05-canal-email.md](05-canal-email.md). SMTP es el envío saliente. **IMAP no es la entrada del canal Email**: es el poller de respuestas CSAT del servicio mail — lee el buzón al que llegan las respuestas `csat+<token>@CSAT_REPLY_TO_DOMAIN` y le entrega cada calificación al Api. Arranca apagado (`IMAP_ENABLED=false`). Si ya tenés las credenciales:

```env
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USE_TLS=true
SMTP_USER=tu-cuenta@gmail.com
SMTP_PASSWORD={app-password-de-gmail}
SMTP_FROM=tu-cuenta@gmail.com
SMTP_FROM_NAME=Soporte Verbara

# Poller de respuestas CSAT (no el canal Email)
IMAP_ENABLED=true
CSAT_REPLY_TO_DOMAIN=csat.tu-dominio.com   # csat+<token>@ este dominio debe llegar al buzón de abajo
# IMAP_TENANT_ID=                         # sin definir = CUSTOMER_TENANT_ID
IMAP_HOST=imap.gmail.com
IMAP_PORT=993
IMAP_USE_TLS=true
IMAP_USER=tu-cuenta@gmail.com
IMAP_PASSWORD={mismo-app-password}
```

> Gmail con OAuth2 (no App Password) requiere setup adicional — ver [05](05-canal-email.md).

## 4. Ejecutar el quickstart (pre-flight + arranque)

```bash
$ cd /opt/verbara/platform
$ bash scripts/quickstart-smb.sh
```

El script ejecuta 11 chequeos en orden:

```
▶ 1/11  Verificando tooling instalado
▶ 2/11  Recursos del host
▶ 3/11  Puertos TCP del host
▶ 4/11  Puertos UDP del host (signalling SIP)
▶ 5/11  Rango RTP (20000-20200 UDP)
▶ 6/11  Firewall del host (informativo)
▶ 7/11  NAT detection
▶ 8/11  Bandwidth requirements (informativo)
▶ 9/11  Archivo .env
▶ 10/11 Resumen pre-flight
▶ 11/11 Pull + arranque del stack
```

El paso 11 baja las imágenes publicadas y construye localmente la de Asterisk (`docker compose build asterisk`,
~7 min la primera vez, cacheada después): esa imagen no se publica en ghcr.io.

### 4.1 Si el script aborta con fallos críticos

Cada `✗` viene con un `→ hint` que te dice qué hacer. Casos típicos:

| Síntoma | Causa | Solución |
|---|---|---|
| `TCP/5060 ocupado` | Hay otro PBX en el host (FreeSWITCH, Kamailio, Asterisk standalone) | Apagarlo (`sudo systemctl stop kamailio` / `pkill -f asterisk`) o desinstalarlo |
| `TCP/80 ocupado` | nginx/apache2 corriendo en el host | `sudo systemctl stop nginx apache2` + `sudo systemctl disable nginx apache2`, o mover el gateway a otro puerto con `WEB_HTTP_PORT` en `.env` (el script chequea y prueba ese puerto) |
| `Puertos del rango RTP ocupados` | Otro servicio usa puertos en `20000-20200` | Ver con `sudo ss -uln '( sport >= :20000 and sport <= :20200 )'`; matar el servicio o cambiar `RTP_PORT_START/END` en `.env` a otro rango libre |
| `RAM total X GB — mínimo recomendado 16 GB` | Server muy chico para SMB Lite | Upgrade del server, o re-evaluar |
| `.env: X sigue con el placeholder CHANGE_ME_...` | Olvidaste editar algún secret | Re-editar `docker/.env.reference-smb` y re-ejecutar el script |
| `.env: CUSTOMER_TENANT_ID está vacío` | Falta el id del tenant Customer | Poné el slug que vas a usar en el wizard ([03](03-setup-inicial.md)), ej. `acme` |

### 4.2 Modo check-only (sólo validar sin arrancar)

Si querés sólo correr los pre-checks (sin hacer pull ni `up`):

```bash
$ bash scripts/quickstart-smb.sh --check-only
```

## 5. Verificar que el stack está arriba

Si el quickstart terminó con el banner `✓ STACK VERBARA ARRIBA`, validá manualmente:

```bash
# Todos los servicios deben estar Up (healthy)
$ docker compose -f docker/docker-compose.reference-smb.yml \
                 --env-file docker/.env.reference-smb ps

NAME                    STATUS                  PORTS
verbara-asterisk        Up 2 minutes (healthy)  (network host)
verbara-mail            Up 2 minutes (healthy)
verbara-nginx-gateway   Up 2 minutes            0.0.0.0:80->80/tcp
verbara-platform-api    Up 2 minutes (healthy)  5000/tcp
verbara-postgres        Up 2 minutes (healthy)  127.0.0.1:5432->5432/tcp
verbara-realtime        Up 2 minutes (healthy)  5030/tcp
verbara-redis           Up 2 minutes (healthy)
verbara-renderer        Up 2 minutes (healthy)
verbara-web             Up 2 minutes            80/tcp
```

> El Api (5000) no se publica en el host: todo el tráfico HTTP entra por `nginx-gateway` en `WEB_HTTP_PORT` (80 por defecto), que reenvía `/api/`, `/health`, `/ws/` al Api y `/hubs/` a Realtime. Los ejemplos de abajo usan `http://localhost:${WEB_HTTP_PORT:-80}`; desde otra máquina, la IP de la LAN.

### 5.1 Healthchecks individuales

```bash
# Platform.Api listo para el plano HTTP (login, canales digitales, consola):
# Postgres, retention y los workers digitales. Responde 200 con "Healthy" o
# "Degraded" (un motor Pro sin licencia aparece "Degraded") y 503 solo si una
# entry está "Unhealthy". Asterisk ya no aparece aquí (ver /health/voice abajo).
$ curl -sS http://localhost:${WEB_HTTP_PORT:-80}/health/ready | jq '{status, checks: (.checks | map_values(.status))}'
{
  "status": "Healthy",
  "checks": {
    "services": "Healthy",
    "retention": "Healthy",
    "postgres": "Healthy"
  }
}
# (más una entry por cada motor Pro registrado, p. ej. "dialer-engine")

# Plano de voz: la conexión AMI de este pod. Ningún probe consulta /health/voice.
$ curl -sS http://localhost:${WEB_HTTP_PORT:-80}/health/voice | jq -r '.checks.asterisk.status'
Healthy

# Web sirve la SPA
$ curl -sI http://localhost:${WEB_HTTP_PORT:-80}/ | head -3
HTTP/1.1 200 OK
Server: nginx/1.27.x
Content-Type: text/html

# Asterisk responde al ARI
$ curl -sSu verbara:${ARI_PASSWORD} http://localhost:8088/ari/asterisk/info | jq '.system'
{
  "version": "22.x.x",
  "entity_id": "...",
  ...
}

# WSS WebRTC reachable
$ curl -sIk https://localhost:8089/asterisk/ws | head -3
HTTP/1.1 426 Upgrade Required        # ← correcto, espera handshake WebSocket
```

### 5.2 Puertos SIP escuchando en el host

```bash
$ sudo ss -tlnp '( sport = :5060 or sport = :8088 or sport = :8089 or sport = :5038 )'
LISTEN 0  511  0.0.0.0:5060   ...  users:(("asterisk",pid=1234,fd=15))
LISTEN 0  511  0.0.0.0:5038   ...  users:(("asterisk",pid=1234,fd=20))
LISTEN 0  511  0.0.0.0:8088   ...  users:(("asterisk",pid=1234,fd=12))
LISTEN 0  511  0.0.0.0:8089   ...  users:(("asterisk",pid=1234,fd=14))

$ sudo ss -ulnp '( sport = :5060 )'
UNCONN 0  0  0.0.0.0:5060   users:(("asterisk",pid=1234,fd=16))
```

> ✓ Todos los listeners aparecen como `users:(("asterisk",...))` — confirma que Asterisk está corriendo en host network como esperado.

### 5.3 Probe externo desde otra máquina (recomendado)

Desde un laptop/móvil **fuera del LAN** (4G/otra red):

```bash
# SIP UDP — packet llega al server?
$ nc -uvz {tu-IP-pública} 5060 < /dev/null
Connection to {tu-IP-pública} 5060 port [udp/sip] succeeded!

# Web UI — abre en browser
$ curl -sI http://{tu-IP-pública}/
HTTP/1.1 200 OK
```

Si nc falla pero Web sí responde → el firewall del host bloquea SIP. Si Web también falla → port-forwarding del router/NSG mal configurado.

## 6. URLs accesibles

Después del arranque:

| URL | Para qué | Quién la usa |
|---|---|---|
| `http://{server-ip}/` | Web UI (login + admin + agent) | Operador admin + agentes |
| `http://{server-ip}/health/ready` | Healthcheck API (vía el gateway) | Monitoring + verificación |
| `http://{server-ip}/api/v1/…` | API REST (vía el gateway) | Devs integrando |
| `http://{server-ip}:8088/ari/` | Asterisk REST API | Platform.Api internamente |
| `wss://{server-ip}:8089/asterisk/ws` | WebRTC WSS endpoint | Browser de agentes |
| `tcp://{server-ip}:5038` | Asterisk AMI | Platform.Api (eventos) |

> Si cambiaste `WEB_HTTP_PORT`, agregá `:{puerto}` a las URLs del gateway. Si configuraste DNS + TLS, reemplazá `{server-ip}` por `verbara.tu-dominio.com` y agregá `https://` al Web URL.

## 7. Comandos útiles del operador

```bash
$ cd /opt/verbara/platform

# Definir alias para no repetir flags
$ alias dc='docker compose -f docker/docker-compose.reference-smb.yml --env-file docker/.env.reference-smb'

# Estado de los servicios
$ dc ps

# Logs (tail -f)
$ dc logs -f platform-api
$ dc logs -f asterisk
$ dc logs -f --tail 100      # todos los servicios

# Reiniciar un servicio
$ dc restart platform-api

# Detener el stack (preserva volúmenes — datos persisten)
$ dc down

# Detener + borrar volúmenes (DATA LOSS — sólo para reset completo)
$ dc down -v

# Actualizar a una nueva release — leé antes el plan de upgrade:
# 99-troubleshooting.md §9 (backup, saltos de versión, variables nuevas)
$ git fetch --tags && git checkout vX.Y.Z   # la próxima release publicada
$ dc pull --ignore-buildable   # Asterisk no se publica: se construye localmente (abajo)
$ dc build asterisk            # reconstruirlo con el Dockerfile del tag nuevo
$ dc up -d --wait

# Backup de Postgres
$ docker exec verbara-postgres pg_dump -U platform verbara | gzip > backup-$(date +%F).sql.gz

# Entrar al CLI de Asterisk (útil para debugging)
$ docker exec -it verbara-asterisk asterisk -rvvv
*CLI> pjsip show endpoints
*CLI> core show channels
*CLI> exit
```

## 8. Próximos pasos

El stack está vivo pero **vacío** — no hay admin user, ni tenant, ni agentes, ni canales configurados.

→ [03-setup-inicial.md](03-setup-inicial.md) — completar el wizard `/setup`, que crea los tenants `platform` + tu primer `Customer` (con sus dos admins) + agente + queue.
