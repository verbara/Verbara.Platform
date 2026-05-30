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
| `Unable to bind to 'http://+:5000'` | Puerto 5000 ya ocupado en el host | `sudo ss -tlnp '( sport = :5000 )'` + matar el ofensor o cambiar `API_PORT` en `.env` |
| `Could not parse JWT_SIGNING_KEY` | Valor del secret no tiene 32+ chars | Regenerar con `openssl rand -base64 64` |
| `Failed to load license` | `LICENSE_PATH` apunta a un archivo inexistente o corrupto | Dejar `LICENSE_PATH=` vacío (modo OSS — Pro endpoints retornan HTTP 402 con upgrade URL); o adquirir Tier 0.5 gratis en https://verbara.io/developer-license y mountear el `.lic` |
| `IMAGE_DIGEST mismatch` | Pro digest binding falla (ADR-0011) | `IMAGE_DIGEST` debe matchear `docker inspect ghcr.io/verbara/platform/api:v2.6.0` `RepoDigests` |

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

#### 3.2 Password rotado
Si rotaste `JWT_SIGNING_KEY` post-arranque, todos los tokens emitidos quedan inválidos pero las passwords siguen funcionando. Re-login debería emitir un token nuevo. Si el login sigue fallando con 401, no es JWT — es la password.

#### 3.3 Cuenta locked por intentos fallidos
Default lockout: 5 fallos en 5 min → cuenta locked por 15 min.

```bash
$ curl -sS -H "X-Api-Key: $MANAGEMENT_API_KEY" \
    http://{server}:5000/api/v1/admin/users/{user-id}/lockout-status | jq

{"isLocked": true, "lockoutEnd": "2026-05-17T15:30:00Z"}
```

Unlock manual:
```bash
$ curl -sS -X POST -H "X-Api-Key: $MANAGEMENT_API_KEY" \
    http://{server}:5000/api/v1/admin/users/{user-id}/unlock
```

#### 3.4 MFA enrollment required
Si el rol del user requiere MFA pero todavía no enrolló:
```bash
$ curl -sS http://{server}:5000/api/v1/auth/login \
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

```bash
$ cd /opt/verbara/platform

# 1. Backup obligatorio antes
$ docker exec verbara-postgres pg_dump -U platform -Fc verbara > backup-pre-upgrade.dump

# 2. Leer release notes (ej. upgrade desde v2.5.4 hacia v2.6.0)
$ git fetch --tags
$ git log v2.5.4..v2.6.0 --oneline -- docs/decisions/ docs/specs/ MIGRATIONS.md

# 3. Checkout
$ git checkout v2.6.0

# 4. Actualizar tag en .env
$ ${EDITOR:-nano} docker/.env.reference-smb
# PLATFORM_API_TAG=v2.6.0          (aplica a api+realtime+renderer+mail; comparten tag)
# PLATFORM_WEB_TAG=v3.2.0-web      (web tiene su propio tren de release)

# 5. Verificar firmas (5 imágenes, ADR-0023) — cosign v3+ con --insecure-ignore-tlog
$ for img in api realtime renderer mail; do
    cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
        ghcr.io/verbara/platform/$img:v2.6.0
  done
$ cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
    ghcr.io/verbara/platform/web:v3.2.0-web

# 6. Pull + up
$ dc pull
$ dc up -d --wait

# 7. Validar (al menos el healthcheck)
$ curl http://localhost:5000/health/ready
```

### Rollback
```bash
$ git checkout v2.5.4                              # tag previo a la upgrade
$ ${EDITOR:-nano} docker/.env.reference-smb        # revert tags
$ dc pull && dc up -d --wait
```

Si una migration de DB rompió la compatibility con la versión vieja, restaurar del backup:
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
