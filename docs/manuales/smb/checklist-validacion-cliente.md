# Checklist de validación — Verbara Platform SMB

> **Para qué:** documento imprimible/checkable que el implementador marca paso a paso durante la entrega al cliente.
> **Uso:** imprimí o copiá a un doc colaborativo. Marcá cada ☐ → ☑ a medida que validás.

**Cliente:** _________________________________________________

**Server:** _____________________________________ (IP pública: _______________________)

**Fecha de instalación:** _____________________   **Operador:** _____________________

---

## 1. Pre-requisitos del host

- ☐ OS: ___________ (Debian 12 / Ubuntu 22.04+ / Rocky 9 / Alma 9 / Amazon Linux 2023)
- ☐ Hardware: _____ vCPU, _____ GB RAM, _____ GB disco
- ☐ Tier elegido: ☐ SMB Lite (50 calls) / ☐ Standard (150) / ☐ Plus (300)
- ☐ Docker Engine 24+ instalado
- ☐ Docker Compose v2.20+ instalado
- ☐ Usuario operador en grupo `docker` (puede correr `docker ps` sin sudo)
- ☐ `curl`, `jq`, `openssl`, `ss` disponibles

## 2. Red y firewall

- ☐ IP pública conocida: _______________________
- ☐ IP LAN del server: _______________________
- ☐ Escenario NAT identificado: ☐ A (directa) / ☐ B (cloud NAT) / ☐ C (on-prem DMZ) / ☐ D (CGNAT)
- ☐ EXTERNAL_IP en `.env`: ☐ vacío (escenario A) / ☐ seteado a __________________

Firewall del host (`ufw status` / `firewall-cmd --list-all` / `nft list ruleset`):
- ☐ 5060/udp ABIERTO
- ☐ 5060/tcp ABIERTO
- ☐ 5061/tcp ABIERTO (TLS opt)
- ☐ 8088/tcp ABIERTO (ARI HTTP)
- ☐ 8089/tcp ABIERTO (WSS WebRTC)
- ☐ 80/tcp ABIERTO
- ☐ 443/tcp ABIERTO (TLS)
- ☐ 20000-20200/udp (o el rango ajustado al tier) ABIERTO

Router / Security Group / NSG (si aplica):
- ☐ Mismas reglas que el firewall del host
- ☐ Probe externo OK: `nc -uvz {IP-pública} 5060 < /dev/null` desde otra red devuelve "succeeded"

DNS:
- ☐ Records configurados: verbara.____________________, pbx.____________________
- ☐ Resuelven a la IP pública correcta

TLS (recomendado para producción):
- ☐ Certificado Let's Encrypt obtenido
- ☐ Cert válido (no expirado): _______________ (fecha)
- ☐ Auto-renovación configurada (`certbot.timer` activo + post-renew hook para `restart asterisk`)

## 3. Stack arriba

```bash
$ dc ps
```

Servicios `Up (healthy)`:
- ☐ `verbara-asterisk`
- ☐ `verbara-platform-api`
- ☐ `verbara-web`
- ☐ `verbara-postgres`
- ☐ `verbara-renderer`
- ☐ `verbara-mail`

Healthchecks:
- ☐ `curl http://localhost:5000/health/ready` → 200 OK con todas las entries `Healthy`
- ☐ `curl http://localhost/` → 200 OK
- ☐ `docker exec verbara-asterisk asterisk -rx 'pjsip show transports'` → UDP+TCP+WS+WSS bound

Verificación de firmas (ADR-0023, 5 imágenes signed):
- ☐ `cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/api:v2.4.1` → OK
- ☐ `cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/realtime:v2.4.1` → OK
- ☐ `cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/renderer:v2.4.1` → OK
- ☐ `cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/mail:v2.4.1` → OK
- ☐ `cosign verify --key docker/cosign.pub ghcr.io/verbara/platform/web:v3.0.3-web` → OK

## 4. Setup inicial (Admin + Tenant + Agente + Queue)

- ☐ Platform admin creado vía `POST /api/v1/setup` o wizard
- ☐ Email: _______________________ Password guardada en password manager
- ☐ `managementApiKey` guardada en password manager
- ☐ Login al Web UI exitoso, llega a `/admin`
- ☐ Primer tenant creado: ID = _______________________
- ☐ Primera queue creada: nombre = _______________________
- ☐ Primer agente creado: userId = ____________ email = ____________
- ☐ Agente puede loguearse en `/agent`

## 5. Canal WebChat

- ☐ Canal habilitado en `/admin/channels`
- ☐ `allowedOrigins` incluye el sitio del cliente: ___________________________________
- ☐ Snippet HTML embebido en sitio de prueba (URL: ____________________________________)
- ☐ Burbuja aparece bottom-right
- ☐ Round-trip: visitante → mensaje → queue → agente acepta → agente responde → visitante recibe ✓

## 6. Canal Email

- ☐ Camino elegido: ☐ SMTP+IMAP / ☐ MS Graph OAuth / ☐ Gmail OAuth2
- ☐ Credenciales guardadas en password manager
- ☐ `dc logs mail` muestra `SMTP client initialized` + `IMAP poller started`
- ☐ Canal habilitado en `/admin/channels`
- ☐ Inbound round-trip: mail externo → conversación en `/agent/queue` (60s polling) ✓
- ☐ Outbound round-trip: agente responde → llega al inbox externo con `Re:` + threading correcto ✓

## 7. Canal Voz/SIP

Trunk:
- ☐ Proveedor: ☐ Twilio Elastic / ☐ ____________________
- ☐ DID provisionado: _______________________
- ☐ Modo auth: ☐ IP ACL / ☐ usuario+password
- ☐ Trunk creado en `POST /api/v1/dialer/trunks`
- ☐ Estado: `Registered` o `Reachable` (según mode)
- ☐ `pjsip show endpoints | grep twilio` muestra el trunk como `Available`

Dialplan inbound:
- ☐ DID enrutado a: ☐ queue directo / ☐ IVR / ☐ business-hours wrap
- ☐ `pjsip show transports` lista 4 transports

Agente WebRTC:
- ☐ Endpoint provisionado: `POST /api/v1/admin/agents/{id}/provision-webrtc`
- ☐ Codec order matches policy de transcoding: ____________________
- ☐ Agente logueado en Web UI bajo HTTPS, indicador verde "Disponible"
- ☐ Browser preguntó permiso de micrófono y operador permitió

Test inbound (golden path):
- ☐ Móvil llama al DID → server recibe
- ☐ `core show channels` muestra 2 channels (trunk + agent)
- ☐ Browser del agente ringtone + popup
- ☐ Agente acepta → audio bidireccional (validar voz hablada en ambas direcciones)
- ☐ Hangup limpio
- ☐ CDR aparece en `/admin/analytics/calls`

Test outbound:
- ☐ Agente marca número externo → móvil destino ring
- ☐ Caller-ID en móvil destino = el DID configurado
- ☐ Audio bidireccional
- ☐ Hangup limpio

Test concurrencia mínima (5 calls):
- ☐ 5 softphones LAN llaman simultáneamente al DID
- ☐ 5 agentes distintos atienden
- ☐ 5 audios independientes (sin solapamiento)
- ☐ Todos los CDRs en `/admin/analytics/calls`

## 8. Performance basal

- ☐ `docker stats --no-stream` → ningún container > 80 % CPU sostenido en idle
- ☐ Memoria de cada container < 80 % del limit
- ☐ Login al Web < 1s
- ☐ Carga inicial de `/agent/queue` < 2s
- ☐ Postgres conexiones < 50 en idle (`select count(*) from pg_stat_activity`)

## 9. Operación

- ☐ Backup automático configurado (cron a `scripts/backup-pg.sh`)
- ☐ Carpeta de backups con espacio suficiente (90+ días retention)
- ☐ Plan de monitoring: ☐ Uptime Robot externo / ☐ Prometheus interno / ☐ ____________________
- ☐ Logs rotación validada (`logging:` aplicado a cada container)

## 10. Documentación entregada al cliente

- ☐ URLs del Web (admin + agent)
- ☐ Credenciales del admin user (en gestor de password compartido)
- ☐ Lista de usuarios creados + sus roles
- ☐ Trunks SIP + DIDs provisionados
- ☐ Plan de upgrade / changelog
- ☐ Canal de soporte / SLA acordado

---

## Firma del operador

**Operador:** _________________________________________

**Firma:** ____________________________________________

**Fecha:** ___________________________________________

## Firma del cliente (recepción del deploy)

**Persona responsable:** _________________________________________

**Cargo:** ____________________________________________

**Firma:** ____________________________________________

**Fecha:** ___________________________________________

---

> Este documento debe quedar archivado por **ambas partes** durante al menos la vida del contrato + el periodo de retención legal aplicable.
