# Manual SMB · 07 — Validación end-to-end

> **Audiencia:** operador que terminó los manuales 03 + 04 + 05 + 06 (al menos los canales que el cliente va a usar).
> **Tiempo:** 30 minutos (validación manual) + opcional 15 min (suite automatizada).

Antes de declarar el deploy "productivo", hacé esta validación E2E. **Mejor descubrir un bug acá** que después de que el cliente esté en producción con tickets pendientes.

## 1. Checklist manual (validación humana)

Imprimí o seguí en pantalla [checklist-validacion-cliente.md](checklist-validacion-cliente.md) y marcá cada caja.

Resumen — debés validar:

### Infra (manuales 01-02)
- ☐ `docker compose ps` → todos los servicios `Up (healthy)`.
- ☐ Firewall abierto: 5060/udp, 5060/tcp, 5061/tcp, 8088, 8089, 80, 443, 20000-20200/udp.
- ☐ Port-forwarding del router/Security Group OK.
- ☐ `EXTERNAL_IP` matchea `curl https://api.ipify.org`.
- ☐ DNS resuelve (si configuraste records).
- ☐ TLS certificado válido + cargado en Web nginx + Asterisk WSS.

### Setup inicial (manual 03)
- ☐ El setup creó **exactamente dos tenants**: el root `platform` (administrativo) + tu primer Customer (`mi-empresa`), y **dos admins** (Platform Admin + Customer Admin) — esto pasa siempre, en una sola llamada a `POST /api/v1/setup` (ADR-0028).
- ☐ Podés loguearte como **Customer Admin** y entrar a `/admin`.
- ☐ La config del día a día (colas/agentes/canales/trunks) vive en el tenant **Customer** (`mi-empresa`), **no** en `platform`. El root `platform` es sólo administrativo: un curl operacional contra `X-Tenant-Id: platform` devuelve **HTTP 409** por diseño (ADR-0027).
- ☐ 1 queue creada (en el Customer).
- ☐ 1 agente provisionado (en el Customer), podés loguearte como ese agente en `/agent`.

### Canal WebChat (manual 04)
- ☐ Canal habilitado en `/admin/channels`.
- ☐ `allowedOrigins` incluye el dominio del sitio del cliente.
- ☐ Snippet HTML embebido en una página de prueba (puede ser `/webchat/demo.html`).
- ☐ Burbuja aparece bottom-right al cargar la página.
- ☐ **Round-trip**: visitante manda mensaje → aparece en `/agent/queue` → agente acepta → agente responde → visitante recibe.

### Canal Email (manual 05)
- ☐ SMTP / IMAP configurado (o OAuth Graph).
- ☐ `dc logs mail` muestra `SMTP client initialized` + `IMAP poller started`.
- ☐ **Round-trip inbound**: enviar mail externo a la cuenta IMAP → en 60s aparece como conversación en `/agent/queue`.
- ☐ **Round-trip outbound**: agente responde → llega al inbox externo con threading correcto (`Re:` + In-Reply-To).

### Canal Voz/SIP (manual 06)
- ☐ Trunk SIP en estado `Registered` o `Reachable` según el modo de auth.
- ☐ `pjsip show transports` lista UDP + TCP + WS + WSS bound a `0.0.0.0`.
- ☐ Agente WebRTC logueado, conectado, en estado `Disponible` con indicador verde.
- ☐ **Test inbound**: llamada desde móvil al DID → entra al server → routea a queue → agente recibe ring → atiende → audio bidireccional → hangup limpio.
- ☐ **Test outbound**: agente marca número externo → móvil ring → atiende → audio bidireccional → hangup limpio.
- ☐ **Test concurrencia 5 calls**: 5 llamadas simultáneas, 5 agentes distintos, audio independiente sin distorsión.
- ☐ CDR refleja todas las llamadas en `/admin/analytics/calls`.

> **Por qué un round-trip puede fallar aunque el canal esté OK (ADR-0026):** bajo el routing ejecutivo, un agente sólo recibe conversaciones de un canal si tiene una row en `queue_memberships` cuyo `AllowedChannels` **permite ese canal** (lo contiene — `"Voice"`, `"WebChat"`, `"Email"`, `"WhatsApp"`, `"Sms"`, etc., en PascalCase, match case-insensitive) **o es `NULL`** (= todos los canales). Si un round-trip llega a la queue pero **no le timbra/aparece a ningún agente**, verificá la membership del agente en `/admin/agents/{agentId}/queues` (o `GET /api/v1/admin/agents/{id}/queue-memberships`) antes de sospechar del canal. El wizard del manual 03 ya creó la membership default-all-channels (`AllowedChannels=NULL`) para el agente de prueba, así que esta validación funciona out-of-the-box.

### Performance (al menos un test rápido)
- ☐ `docker stats` no muestra ningún container > 80 % CPU sostenido.
- ☐ `docker exec verbara-postgres psql -U platform -d verbara -c "SELECT count(*) FROM pg_stat_activity"` < 50 conexiones.
- ☐ Tiempo de login en Web < 1s.

## 2. Suite E2E automatizada (Playwright)

Verbara incluye una suite de tests E2E que valida el setup wizard + los 3 canales contra el stack vivo. Está en `Verbara.Platform.Web/tests/e2e/tests/reference-deployment.spec.ts` con tag `@reference-deployment`.

### 2.1 Pre-requisitos

```bash
# Desde una máquina con acceso al server (puede ser el mismo host)
$ git clone https://github.com/verbara/platform-web.git
$ cd platform-web
$ git checkout v3.2.0-web

$ npm install
$ npx playwright install --with-deps chromium
```

### 2.2 Configurar el target

```bash
$ cp tests/e2e/.env.example tests/e2e/.env
$ ${EDITOR:-nano} tests/e2e/.env
```

```env
VERBARA_API_BASE_URL=http://{server-ip}:5000
VERBARA_WEB_BASE_URL=http://{server-ip}
VERBARA_TENANT_ID=mi-empresa                 # el Customer creado por el setup — NO 'platform' (operacional)
VERBARA_ADMIN_EMAIL=admin@tu-empresa.com     # el Customer Admin (login del paso 2 del manual 03)
VERBARA_ADMIN_PASSWORD=TU-PASSWORD-FUERTE
VERBARA_TEST_AGENT_EMAIL=maria@tu-empresa.com
VERBARA_TEST_AGENT_PASSWORD={password-temp-del-wizard}
VERBARA_SIP_DID=+15551234567
VERBARA_SIP_TEST_NUMBER=+15555550199        # un número de prueba para llamada outbound
```

> **Tenant correcto:** los tests E2E ejercitan recursos **operacionales** (queues/agentes/canales), así que apuntan al tenant **Customer** (`mi-empresa`) con el token del **Customer Admin**. El root `platform` es administrativo y rechaza endpoints operacionales con HTTP 409 (ADR-0027). Usá `platform` sólo si vas a validar gestión de tenants / licensing / cluster.

### 2.3 Correr la suite

```bash
$ npx playwright test --grep @reference-deployment

Running 4 tests using 1 worker

  ✓  setup wizard creates admin + tenant + agent + queue (8.2s)
  ✓  webchat round-trip: visitor → queue → agent → reply (12.4s)
  ✓  email round-trip: inbound → conversation → reply → outbound (45.8s)
  ✓  voice round-trip: DID inbound → agent answer → audio bidir (28.1s)

  4 passed (94.5s)
```

Si **alguno falla**, abrirá un report HTML en `playwright-report/index.html` con screenshots + traces + network logs.

### 2.4 Ejecutar selectivamente

```bash
# Solo el test de webchat
$ npx playwright test --grep @reference-deployment --grep 'webchat'

# Con UI interactiva (para debugging)
$ npx playwright test --grep @reference-deployment --ui

# Generar report después de un run
$ npx playwright show-report
```

## 3. Test de capacidad declarada (SIPp)

> Sólo si tu cliente requiere SLA de capacidad o tenés dudas que el hardware aguante. **Opcional** para SMB Lite (50 calls).

Verbara incluye scenarios SIPp en `tests/sipp-scenarios/` para validar la capacidad declarada de cada tier.

### 3.1 Quickstart SIPp en otro host

```bash
# En una segunda máquina (NO el server Verbara — para no contender CPU)
$ docker run --rm -it --net=host ctaloi/sipp:latest \
    -sf /tests/sipp-scenarios/01-uac-call.xml \
    -s +15551234567 \
    -r 5 -m 50 \
    -d 30000 \
    {tu-IP-pública}:5060
```

Parámetros:
- `-r 5` → 5 calls por segundo.
- `-m 50` → 50 calls total.
- `-d 30000` → cada call dura 30 segundos.

### 3.2 Validar durante el test

En el server Verbara, **en paralelo**:

```bash
# Calls activas en Asterisk
$ watch -n 1 'docker exec verbara-asterisk asterisk -rx "core show channels" | tail -1'
50 active channels, 25 active calls

# CPU del host
$ docker stats --no-stream verbara-asterisk verbara-platform-api verbara-postgres
```

### 3.3 Criterios de PASS

| Tier | Calls target | Aceptable |
|---|---|---|
| SMB Lite | 50 | 50/50 conectadas, audio OK, CPU < 80 % sostenido |
| SMB Standard | 150 | 150/150 conectadas, audio OK, CPU < 80 %, p99 ring < 2s |
| SMB Plus | 300 | 300/300 conectadas, audio OK, CPU < 75 %, p99 ring < 2s |

Si pasás esos thresholds → tu deploy está validado para el tier declarado.

## 4. Test de resiliencia (chaos opcional)

Validá que el stack se recupera de fallos parciales:

```bash
$ alias dc='docker compose -f docker/docker-compose.reference-smb.yml --env-file docker/.env.reference-smb'

# Test 1 — postgres restart
$ dc restart postgres
# Esperar 30s
$ curl http://localhost:5000/health/ready    # debe volver a 200 OK

# Test 2 — platform-api restart
$ dc restart platform-api
$ sleep 30
$ curl http://localhost:5000/health/ready

# Test 3 — asterisk restart (cuidado, corta llamadas activas)
$ dc restart asterisk
$ sleep 30
$ docker exec verbara-asterisk asterisk -rx 'pjsip show transports'
```

Después de cada restart, validar:
- Servicios vuelven a `healthy` en < 60s.
- Login al Web sigue funcionando.
- Agentes pueden reconectarse sin re-login (token JWT sobrevive).

## 5. Generar evidencia para el cliente

Si el cliente requiere reporte de instalación + validación, generar:

```bash
# Snapshot del estado
$ dc ps > /tmp/verbara-status.txt
$ dc images > /tmp/verbara-images.txt
$ docker exec verbara-asterisk asterisk -rx 'core show version' >> /tmp/verbara-status.txt
$ docker exec verbara-asterisk asterisk -rx 'pjsip show endpoints' >> /tmp/verbara-status.txt
$ docker exec verbara-postgres psql -U platform -d verbara -c "\dt" >> /tmp/verbara-status.txt

# Output del E2E test
$ npx playwright test --grep @reference-deployment --reporter=html
# → playwright-report/index.html

# Sign de las 5 imágenes (proof de imagen no-tampered, ADR-0023) — cosign v3+
$ for img in api realtime renderer mail; do
    cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
        ghcr.io/verbara/platform/$img:v2.6.0 > /tmp/sig-$img.txt 2>&1
  done
$ cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
    ghcr.io/verbara/platform/web:v3.2.0-web > /tmp/sig-web.txt 2>&1

# Bundle todo
$ tar czf /tmp/verbara-install-report-$(date +%F).tar.gz \
    /tmp/verbara-*.txt \
    /tmp/sig-*.txt \
    playwright-report/
```

Entregar el `.tar.gz` al cliente como evidencia.

## Próximo paso

Si todo lo anterior está verde → tu deploy está listo para producción. Recomendaciones:

1. **Configurar backup automático** de Postgres + asterisk_recordings.
2. **Monitoring** — al menos un Uptime Robot / cron de cURL contra `/health/ready`.
3. **Plan de upgrade** — leer release notes antes de bumpear tags en `.env`.

→ Si encontrás bugs durante producción, revisar [99-troubleshooting.md](99-troubleshooting.md) (general) o [08-troubleshooting-sip.md](08-troubleshooting-sip.md) (SIP).
