# Manual SMB · 99 — Troubleshooting general (no SIP)

> **Audiencia:** operador con problemas no-SIP. Para problemas SIP → [08-troubleshooting-sip.md](08-troubleshooting-sip.md).
> **Formato:** referencia rápida, síntoma → causa → solución.

## Índice

| Síntoma | Sección |
|---|---|
| El stack no arranca / un servicio queda `unhealthy` | [§1](#1--servicios-unhealthy-al-arranque) |
| Web UI muestra "Network Error" / "Failed to fetch" | [§2](#2--web-ui-network-error) |
| Login devuelve 401 incluso con credenciales correctas | [§3](#3--login-devuelve-401) |
| Postgres no arranca / falla healthcheck | [§4](#4--postgres-no-arranca) |
| Disco lleno → containers crashean | [§5](#5--disco-lleno) |
| Memoria/swap saturada | [§6](#6--memoria-saturada) |
| Logs gigantes / no rotan | [§7](#7--logs-no-rotan) |
| Backup / restore de Postgres | [§8](#8--backup--restore) |
| Upgrade de versión | [§9](#9--upgrade-de-versión) |
| Reset completo del stack | [§10](#10--reset-completo) |

---

## 1 — Servicios `unhealthy` al arranque

### Diagnóstico
```bash
$ dc ps
NAME                    STATUS
verbara-platform-api    Up 30 seconds (unhealthy)
```

```bash
$ dc logs --tail 50 platform-api
```

### Causas más comunes

| Mensaje en log | Causa | Solución |
|---|---|---|
| `Connection refused (localhost:5432)` | Postgres no levantó todavía / mal config | Esperar 30s; si persiste, revisar §4 |
| `Bind for 0.0.0.0:80 failed: port is already allocated` (al levantar `nginx-gateway`) | El puerto del gateway ya está ocupado en el host (el Api no publica puerto propio: todo entra por el gateway) | `sudo ss -tlnp '( sport = :80 )'` + matar el ofensor, o cambiar `WEB_HTTP_PORT` en `.env` |
| `required variable CSAT_REPLY_TOKEN_SECRET is missing a value` / `CUSTOMER_TENANT_ID required` (lo imprime `docker compose`, antes de arrancar nada) | Falta una variable obligatoria en `.env` | Completarla (ver [02](02-arranque-stack.md) §3.1): `openssl rand -base64 32` para el secreto; el id del tenant Customer para `CUSTOMER_TENANT_ID` |
| `Failed to load license` | El `.lic` montado en `/etc/verbara/license.lic` (desde `LICENSE_PATH`) no existe o está corrupto | Volver a comentar el bind-mount del `.lic` en el compose (modo OSS — Pro endpoints retornan HTTP 402 con upgrade URL); o adquirir Tier 0.5 gratis en https://verbara.io/developer-license y montar ese `.lic` |
| `Asterisk:Ari:NodeApplication ('…') must differ from Asterisk:Ari:Application` | La app Stasis del cliente ARI de nodo (`Asterisk__Ari__NodeApplication`, por defecto `verbara-node`) es igual a la del dialplan (`Asterisk__Ari__Application`, `verbara`): sólo el consumer del líder puede registrar la del dialplan (Platform/ADR-0044) | Dejar `Asterisk__Ari__NodeApplication: verbara-node` como viene en `docker-compose.reference-smb.yml`, o borrarla para usar el valor por defecto |
| `IMAGE_DIGEST mismatch` | Pro digest binding falla (Pro/ADR-0011) | `IMAGE_DIGEST` debe matchear `docker inspect ghcr.io/verbara/platform/api:v2.33.1` `RepoDigests`, y ese digest tiene que estar autorizado en tu `.lic` (cada release nueva del api trae un digest nuevo) |

### Reiniciar uno solo
```bash
$ dc restart platform-api
$ dc logs -f platform-api      # tail-follow
```

---

## 2 — Web UI Network Error

### Síntoma
Abrís `http://{server}/` → carga la SPA pero todas las llamadas a `/api/v1/...` devuelven Network Error en devtools.

### Causa típica
Web nginx no encuentra el API o el CORS bloquea.

```bash
$ docker exec verbara-web cat /etc/nginx/conf.d/default.conf | grep proxy_pass
proxy_pass http://platform-api:5000/;        # ← debe ser el service DNS docker
```

Si el proxy_pass está mal o el container `platform-api` no responde, todo el `/api/v1/*` rompe.

```bash
$ docker exec verbara-web wget -qO- http://platform-api:5000/health/ready
{"status":"Healthy",...}                      # ← debe responder
```

Si no responde: `dc restart platform-api`.

### Causa secundaria: CORS

Si la consola dice `blocked by CORS policy`:
```bash
$ grep CORS_ORIGINS /opt/verbara/platform/docker/.env.reference-smb
CORS_ORIGINS=http://localhost                  # ← agregar el dominio real
```

```env
CORS_ORIGINS=http://localhost,https://verbara.tu-dominio.com,https://app.tu-dominio.com
```

Con más de un origen en `CORS_ORIGINS`, definí también `PUBLIC_BASE_URL` con la dirección con la
que los usuarios abren la consola (por ejemplo `PUBLIC_BASE_URL=https://verbara.tu-dominio.com`):
sin ella la API no envía el correo de restablecer contraseña y rechaza el inicio de sesión OIDC
con un 500, y deja en el log una advertencia que nombra la variable.

```bash
$ dc up -d           # aplica el cambio
```

---

## 3 — Login devuelve 401

### Síntoma
Credenciales que sabés que están bien → `401 Unauthorized`.

### Causas

#### 3.1 Tenant mismatch
El user existe en tenant `acme` pero estás logueando contra `platform`. El campo "Tenant ID" del login form debe matchear.

#### 3.2 Token inválido vs. password
Las claves de firma de los tokens viven en Redis (el Api las genera y rota solo). Si se borró el volumen de Redis, los tokens emitidos quedan inválidos pero las passwords siguen funcionando: re-login emite un token nuevo. Si el login sigue fallando con 401, no es el token — es la password.

#### 3.3 Cuenta locked por intentos fallidos
Default lockout: 5 fallos en 5 min → cuenta locked por 15 min.

```bash
$ curl -sS -H "X-Api-Key: $MANAGEMENT_API_KEY" \
    http://{server}/api/v1/admin/users/{user-id}/lockout-status | jq

{"isLocked": true, "lockoutEnd": "2026-05-17T15:30:00Z"}
```

Unlock manual:
```bash
$ curl -sS -X POST -H "X-Api-Key: $MANAGEMENT_API_KEY" \
    http://{server}/api/v1/admin/users/{user-id}/unlock
```

#### 3.4 MFA enrollment required
Si el rol del user requiere MFA pero todavía no enrolló:
```bash
$ curl -sS http://{server}/api/v1/auth/login \
    -d '{"email": "...", "password": "..."}'
{"state": "mfa_enrollment_required", "userId": "..."}
```

La UI debería redirigir a `/auth/mfa-enroll` automáticamente.

---

## 4 — Postgres no arranca

### Síntoma
```bash
$ dc ps
NAME                STATUS
verbara-postgres    Restarting (1) 5 seconds ago
```

### Diagnóstico
```bash
$ dc logs --tail 30 postgres
PostgreSQL Database directory appears to contain a database; Skipping initialization
...
FATAL: database files are incompatible with server
```

### Causas

#### 4.1 Postgres version mismatch
El volumen `verbara_postgres_data` fue creado por una versión vieja (ej. 16) y el container nuevo es 18. Postgres no auto-upgrades.

**Solución:**
```bash
# Backup primero!
$ docker exec verbara-postgres pg_dump -U platform verbara > /tmp/verbara-backup-$(date +%F).sql

# Recrear el volumen
$ dc down
$ docker volume rm docker_verbara_postgres_data
$ dc up -d postgres
$ sleep 30   # esperar init

# Restaurar
$ docker exec -i verbara-postgres psql -U platform -d verbara < /tmp/verbara-backup-*.sql
$ dc up -d
```

#### 4.2 Volumen corrupto (apagado abrupto)

```bash
$ dc logs postgres | grep -i 'recovery'
LOG: redo done at 0/1234567

# Si entra a loop de recovery → puede haber corrupción
$ docker exec -it verbara-postgres pg_resetwal --dry-run /var/lib/postgresql/data
```

**Solución de último recurso** (¡con backup primero!):
```bash
$ docker exec -it verbara-postgres pg_resetwal /var/lib/postgresql/data
$ dc restart postgres
```

#### 4.3 Out of disk

```bash
$ df -h /var/lib/docker/volumes/docker_verbara_postgres_data
# Si está al 100% → liberar espacio (§5) y restart
```

---

## 5 — Disco lleno

### Síntoma
```bash
$ docker stats
NAME                  CPU %     MEM USAGE / LIMIT     ...
verbara-platform-api  98.43%    1.4GiB / 1.5GiB       ...

# Logs de containers con "no space left on device"
```

### Diagnóstico

```bash
$ df -h
Filesystem      Size  Used Avail Use%
/dev/sda1       100G   99G  1.0G  99%

# ¿Qué ocupa /var/lib/docker?
$ sudo du -sh /var/lib/docker/{volumes,overlay2,containers}/*  2>/dev/null | sort -h | tail -20
```

### Soluciones

#### 5.1 Recordings ocupando espacio

Si grabaciones llenan el volumen `verbara_asterisk_recordings`:

```bash
$ docker exec verbara-asterisk du -sh /var/spool/asterisk/recording
35G    /var/spool/asterisk/recording

# Rotación manual: borrar > 90 días
$ docker exec verbara-asterisk find /var/spool/asterisk/recording -type f -mtime +90 -delete
```

> 💡 **Habilitar rotación automática** en Verbara: `/admin/tenant-settings → Retention → Call recordings = 90 days`. La plataforma corre el sweeper diariamente.

> 💡 **Mover recordings a S3/MinIO** evita este problema — perfil `s3` en el compose.

#### 5.2 Logs de Docker grandes

```bash
$ sudo du -sh /var/lib/docker/containers/*/   | sort -h | tail
```

Los compose files ya configuran `max-size=100m, max-file=5` por container → max 500 MB por container. Si superás eso, validá que `logging:` aparezca en cada servicio (debería).

```bash
$ docker inspect verbara-platform-api | jq '.[0].HostConfig.LogConfig'
{"Type": "json-file", "Config": {"max-size": "100m", "max-file": "5"}}
```

#### 5.3 Imágenes viejas / dangling

```bash
$ docker system prune -a --volumes      # ⚠️ borra TODO no usado, cuidado
```

Más conservador:
```bash
$ docker image prune -a       # solo imágenes sin tag
$ docker container prune      # solo containers stopped
```

#### 5.4 Postgres DB grande

```bash
$ docker exec verbara-postgres psql -U platform -d verbara -c "
  SELECT
    relname, pg_size_pretty(pg_total_relation_size(relid))
  FROM pg_catalog.pg_statio_user_tables
  ORDER BY pg_total_relation_size(relid) DESC
  LIMIT 10;
"
```

Tablas que crecen rápido: `audit_log`, `cdr`, `messages`. Verbara incluye sweepers para retención — configurar en `/admin/tenant-settings → Retention`.

---

## 6 — Memoria saturada

### Síntoma
- OOM kill de containers (`docker logs` muestra `OOMKilled: true`).
- Swap del host al 100 %.
- `dmesg | grep -i killed`.

### Diagnóstico
```bash
$ docker stats --no-stream
NAME                  MEM USAGE / LIMIT     MEM %
verbara-platform-api  1.4GiB / 1.5GiB       93.33%
verbara-asterisk      3.8GiB / 4.0GiB       95.00%      ← cerca del limit
```

### Solución
- Bumpear `MEM_LIMIT` para el container apretado en `.env`.
- Si el host está físicamente saturado → escalar hardware o reducir tier.

---

## 7 — Logs no rotan

### Síntoma
```bash
$ sudo du -sh /var/lib/docker/containers/*/*.log
500M    /var/lib/docker/containers/abc.../*-json.log    ← creció
```

Esperado: con rotación, cada container tiene `5 × 100m = 500m` max. Si pasa de eso, la rotación falló.

### Causa
El `logging:` block no está aplicado (compose viejo, o re-creaste el container sin pickear el cambio).

```bash
$ docker inspect verbara-platform-api | jq '.[0].HostConfig.LogConfig'
{"Type": "json-file", "Config": {}}              ← vacío, mal
```

### Solución
```bash
$ dc up -d --force-recreate     # recrea con la config actual
```

---

## 8 — Backup / restore

### Backup completo

```bash
$ alias dc='docker compose -f docker/docker-compose.reference-smb.yml --env-file docker/.env.reference-smb'

# Postgres
$ docker exec verbara-postgres pg_dump -U platform -Fc verbara > backup-pg-$(date +%F).dump

# Asterisk realtime config (pjsip endpoints, queues, etc. — viene del PG dump arriba ya)

# Recordings (puede ser muchos GB)
$ docker run --rm -v docker_verbara_asterisk_recordings:/src -v $PWD:/backup alpine \
    tar czf /backup/recordings-$(date +%F).tar.gz -C /src .

# .env (CRÍTICO — contiene los secretos)
$ cp docker/.env.reference-smb backup-env-$(date +%F)

# Asterisk config files (si modificaste)
$ tar czf asterisk-config-$(date +%F).tar.gz docker/asterisk-config/
```

> 🔒 **Cifrar el bundle antes de movelo:** `tar czf - ... | gpg -c > backup.tar.gz.gpg`.

### Restore

```bash
$ dc down                                # detener stack

# Recrear postgres limpio
$ docker volume rm docker_verbara_postgres_data
$ dc up -d postgres
$ sleep 30

# Restaurar dump
$ docker exec -i verbara-postgres pg_restore -U platform -d verbara < backup-pg-2026-05-17.dump

# Restaurar recordings
$ docker run --rm -v docker_verbara_asterisk_recordings:/dst -v $PWD:/backup alpine \
    tar xzf /backup/recordings-2026-05-17.tar.gz -C /dst

# Restaurar .env
$ cp backup-env-2026-05-17 docker/.env.reference-smb

# Arrancar el resto
$ dc up -d
```

### Backup automatizado (cron)

```bash
$ sudo crontab -e
# Cada día a las 3 AM
0 3 * * * cd /opt/verbara/platform && bash scripts/backup-pg.sh /var/backups/verbara/
```

El script ya existe en `scripts/backup-pg.sh` con rotación a 30 días.

---

## 9 — Upgrade de versión

> ⚠️ **Documentado, no probado.** Este plan de upgrade se armó leyendo los changelogs; el camino desde una base existente no se ejecutó de punta a punta. Seguilo en orden y **empezá siempre por el backup**.

### 9.1 Antes de tocar nada

1. **Backup obligatorio.** Es el único rollback posible una vez que la versión nueva migró la base:
   ```bash
   $ cd /opt/verbara/platform
   $ docker exec verbara-postgres pg_dump -U platform -Fc verbara > backup-pre-upgrade.dump
   ```
2. **¿Desde qué versión venís?** `git describe --tags` en `/opt/verbara/platform`, o el tag de `PLATFORM_API_TAG` en tu `.env`.
   - **Base creada con una versión anterior a v2.10.0:** v2.10.0 reagrupó las migraciones bajo nombres nuevos. Esas bases tienen que pasar **primero** por `v2.9.1` (checkout de ese tag, `PLATFORM_API_TAG=v2.9.1`, arrancar, esperar `/health/ready` y detener) y recién después saltar a la release nueva.
3. **Si venís de una versión anterior a v2.27.0 — reporte de agentes duplicados.** La migración de v2.27.0 se niega a correr si un usuario tiene más de un agente en el mismo tenant (el Api no arranca y el log nombra los pares). Corré el reporte de sólo lectura **antes** del primer arranque de la versión nueva, y repetilo tras cada corrección hasta que su sección 1 no devuelva filas:
   ```bash
   $ dc exec -T postgres psql -U platform -d verbara < scripts/ops/agents-identity-report.sql
   ```
   El procedimiento de corrección está en `docs/operations/licensed-agent-metering-upgrade.md`.
4. **Si venís de una versión anterior a v2.27.0 — zona del día de medición.** Elegí `LICENSE_DAY_ZONE` (zona IANA, ej. `America/Bogota`; default `UTC`) **antes** del primer arranque: ese arranque la fija en la base y cambiarla después detiene el cierre diario de agentes licenciados hasta que se restaure.
5. **Si venís de una versión anterior a v2.28.0 — sin vuelta atrás.** El primer arranque de v2.28.0 o posterior cifra los secretos de los canales en la base. Es de un solo sentido: una versión anterior ya no los puede leer. Desde ahí, volver atrás = restaurar el backup del paso 1.

### 9.2 Cambios del `.env` (comparalo con el `.env.reference-smb.example` del tag nuevo)

```bash
$ git fetch --tags
$ git checkout v2.33.1
$ diff <(command grep -oE '^[A-Z_]+=' docker/.env.reference-smb.example | sort) \
       <(command grep -oE '^[A-Z_]+=' docker/.env.reference-smb | sort)
```

- **Nuevas obligatorias:** `CSAT_REPLY_TOKEN_SECRET` (`openssl rand -base64 32`) y `CUSTOMER_TENANT_ID` (el id de tu tenant Customer — el que ya existe en tu base). Sin ellas `docker compose` no arranca.
- **Nuevas opcionales:** `LICENSE_DAY_ZONE` (ver 9.1 paso 4), `IMAP_ENABLED` + `CSAT_REPLY_TO_DOMAIN` + `IMAP_TENANT_ID` (el poller de respuestas CSAT; ver [05](05-canal-email.md)).
- **`PUBLIC_BASE_URL`** viene activa en el ejemplo: con el default de dos orígenes en `CORS_ORIGINS` es obligatoria.
- **`JWT_SIGNING_KEY`, `JWT_ISSUER`, `JWT_AUDIENCE`: la API ya no los usa.** El Api genera sus claves de firma y las guarda en Redis; el compose ya no se los pasa a `platform-api` y `scripts/quickstart-smb.sh` ya no los pide. Podés borrarlos del `.env`. Al pasar a las claves en Redis, contá con que las sesiones abiertas se invaliden una vez (los usuarios vuelven a iniciar sesión).
- **Redis ya no es un perfil** (`--profile identity-redis` desapareció): arranca siempre, y `platform-api` y `realtime` dependen de él.
- **IMAP** ya no es la entrada del canal Email: ahora alimenta el poller de respuestas CSAT y queda apagado hasta `IMAP_ENABLED=true`.
- **Licencia:** el Api lee el `.lic` en `/etc/verbara/license.lic`; si usás licencia, descomentá el bind-mount de `LICENSE_PATH` en el compose.

Actualizá los tags:
```bash
$ ${EDITOR:-nano} docker/.env.reference-smb
# PLATFORM_API_TAG=v2.33.1         (aplica a api+realtime+renderer+mail; comparten tag)
# PLATFORM_WEB_TAG=v3.27.1-web     (web tiene su propio tren de release)
```

### 9.3 Instalaciones con licencia Pro

Cada release del api tiene un digest nuevo. Si tu `.lic` autoriza digests (Pro/ADR-0011), pedí la autorización del digest de `ghcr.io/verbara/platform/api:v2.33.1` **antes** de arrancar, y poné ese digest en `IMAGE_DIGEST`. Sin eso falla el binding de digest (`IMAGE_DIGEST mismatch`, ver §1).

### 9.4 Verificar, construir y arrancar

```bash
# Firmas (5 imágenes, ADR-0023) — cosign v3+ con --insecure-ignore-tlog
$ for img in api realtime renderer mail; do
    cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
        ghcr.io/verbara/platform/$img:v2.33.1
  done
$ cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
    ghcr.io/verbara/platform/web:v3.27.1-web

# Asterisk se construye localmente: reconstruirlo con el Dockerfile del tag nuevo
$ dc build asterisk

# Pull + up; recrear el gateway para que tome su configuración nueva
$ dc pull
$ dc up -d --wait
$ dc up -d --force-recreate nginx-gateway

# Validar (al menos el healthcheck, a través del gateway)
$ curl http://localhost:${WEB_HTTP_PORT:-80}/health/ready
```

### Rollback
Antes del primer arranque de la versión nueva, volver es un checkout:
```bash
$ git checkout {tag-previo}                         # el tag desde el que venías
$ ${EDITOR:-nano} docker/.env.reference-smb        # revert tags
$ dc pull && dc up -d --wait
```

Si la versión nueva ya arrancó y migró la base (siempre, si cruzaste v2.28.0), restaurar del backup:
```bash
$ dc down
$ docker volume rm docker_verbara_postgres_data
$ dc up -d postgres
$ docker exec -i verbara-postgres pg_restore -U platform -d verbara < backup-pre-upgrade.dump
$ dc up -d
```

---

## 10 — Reset completo

> ⚠️ **DESTRUCTIVO.** Esto borra todos los datos (users, conversations, recordings, configs).

```bash
$ cd /opt/verbara/platform
$ dc down -v        # -v = borra volúmenes
$ rm docker/.env.reference-smb       # opcional: borrar config

# Volver a empezar desde cero
$ cp docker/.env.reference-smb.example docker/.env.reference-smb
$ ${EDITOR:-nano} docker/.env.reference-smb        # nuevos secrets
$ bash scripts/quickstart-smb.sh
```

---

## Cuando nada más funciona

Recolectar info para soporte:

```bash
$ cd /opt/verbara/platform

# Bundle de diagnóstico
$ mkdir -p /tmp/verbara-debug
$ dc ps > /tmp/verbara-debug/services.txt
$ dc logs --tail 1000 > /tmp/verbara-debug/all-logs.txt
$ dc images > /tmp/verbara-debug/images.txt
$ docker version > /tmp/verbara-debug/docker-version.txt
$ docker info > /tmp/verbara-debug/docker-info.txt
$ uname -a > /tmp/verbara-debug/host.txt
$ cat /etc/os-release >> /tmp/verbara-debug/host.txt
$ free -h >> /tmp/verbara-debug/host.txt
$ df -h >> /tmp/verbara-debug/host.txt
$ docker stats --no-stream > /tmp/verbara-debug/stats.txt
$ docker exec verbara-asterisk asterisk -rx 'pjsip show transports' > /tmp/verbara-debug/pjsip.txt 2>&1
$ docker exec verbara-asterisk asterisk -rx 'core show version' >> /tmp/verbara-debug/pjsip.txt
$ tar czf /tmp/verbara-debug-$(date +%F).tar.gz /tmp/verbara-debug/

# Sanitizar — NO mandar secretos
$ grep -v PASSWORD /tmp/verbara-debug-*.tar.gz   # validar no hay secrets
```

Compartir el `.tar.gz` con el equipo Verbara (canal de soporte que tu cliente tenga).
