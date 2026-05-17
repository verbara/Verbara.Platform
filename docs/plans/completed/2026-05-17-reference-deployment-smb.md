# Pivot del roadmap: reference deployments + manuales para cliente final

## Context

Hasta hoy el roadmap se enfocó en **validación de producción interna** (R5.5: NBomber + SIPp + chaos engineering + soak 24h + cloud replication). Ese trabajo entregó datos de capacidad y resilencia, pero **nada de eso es entregable a un cliente final** — los charts/composes son específicos del lab Talos, los secrets están hardcodeados, los hostnames son `r55.local`, y no existe ningún manual paso a paso de cómo configurar la aplicación + sus canales después de un deploy fresco.

El producto necesita pivotar: **construir un entorno limpio + reference deployment + manuales que un cliente (o el equipo de implementación) pueda seguir paso a paso para instalar Verbara y configurar sus canales de comunicación**. El mismo trabajo de configuración genera el contenido del manual ("documentation-by-doing").

**Decisión del 2026-05-17 (vía clarificación):**
- D-LK soak en flight **NO se cancela** — corre 22h más, watcher alerta, reporte final guardado mañana como bonus track.
- Canales en V1: **Voz/SIP, WebChat, Email** (sin WhatsApp por dependencia Meta lenta de obtener).
- Target: **SMB on-premise** (1 servidor o 1 cluster K8s on-prem; sin cloud managed EKS/GKE).
- Idioma: **español**.
- Asterisk debe exponer **todos los puertos SIP** (5060 UDP/TCP + RTP 10000-20200 + ARI 8088/8089 + AMI 5038) para que un cliente conecte cualquier trunk externo.

**Fase 1 = Docker SMB (priority).** **Fase 2 = K8s on-prem (después).** Este plan especifica Fase 1 en detalle y deja Fase 2 como esqueleto a expandir cuando Fase 1 ship.

---

## Estado actual (de la exploración)

### Lo que YA está bien (reutilizar)
- `Verbara.Platform.Web/Dockerfile` — multi-stage Node 22 → nginx, production-ready.
- `docker/docker-compose.smb.yml` — canónico SMB, ADR-0015 Phase 2 compliant, Asterisk con puertos `5060 udp+tcp, 8088, 8089, 5038, 20000-20200/udp` expuestos.
- `docker/docker-compose.production.yml` — alias SMB + `.env.production` injection.
- `docker/docker-compose.verified.yml` — digest-pinned + cosign verification.
- API endpoints: `POST /api/v1/setup` (admin inicial idempotente) + `/api/v1/admin/channels/{ch}` (CRUD + test por canal).
- React UI: `/admin/channels` página + setup wizard `/src/admin/setup/steps/channel-step.tsx`.
- i18n: 3 locales completos (en-US, es-419, pt-BR) con `npm run i18n:check` CI gate.
- `docs/getting-started.md` (10 min walkthrough) + `docs/operations/first-deploy.md` (30 min, voice/SIP baseline). Base reutilizable para el manual nuevo.
- `ghcr.io/verbara/platform/api` imagen pública firmada con cosign (visibility flip 2026-05-10).
- Imagen pública Asterisk: `andrius/asterisk:22` (community); Kamailio: `ghcr.io/kamailio/kamailio:5.8.8-bookworm`; rtpengine: `fonoster/rtpengine:0.3.17`. Todos public, sin auth.

### Gaps confirmados que cierra Fase 1
| Gap | Solución en Fase 1 |
|---|---|
| **No existe imagen pública de Web** | Crear workflow `.github/workflows/release.yml` en `Verbara.Platform.Web/` que publique a `ghcr.io/verbara/platform/web` en tag `v*` (mismo patrón que API). Opcional: cosign sign. |
| **No existe manual per-channel** | Crear `docs/manuales/0X-canal-*.md` (español) por cada canal de V1. |
| **No existe compose "reference" para SMB** | Crear `docker/docker-compose.reference-smb.yml` (basado en `smb.yml` con secrets externalizados a `.env.reference-smb.example` + comentarios explicativos para el operador). |
| **No existe quickstart script** | `scripts/quickstart-smb.sh` que valida pre-requisitos, baja imágenes pinned, levanta stack, hace probe inicial. |
| **E2E suite mínima** (solo 1 test bridge visible) | Expandir `Verbara.Platform.Web/tests/e2e/` con scenarios: setup wizard → 3 canales config → mensaje round-trip. |

### Gaps reconocidos pero deferred a Fase 2
- Helm charts hardcodean `192.168.122.201` (Kamailio dispatcher), secrets en `stringData` plaintext, hostnames `r55.local`, no integración con External Secrets / cert-manager. Refactor completo en Fase 2.
- `scripts/k8s-up.sh` asume KVM + Talos — Fase 2 documenta requisitos para cluster cliente (no automatiza el bootstrap).

---

## Fase 1 — Docker SMB on-premise (priority)

### Deliverable 1: Imagen pública de Platform.Web
- **Archivo nuevo:** `Verbara.Platform.Web/.github/workflows/release.yml`
- **Patrón fuente:** `Verbara.Platform/.github/workflows/release.yml` (mismo trigger en tag `v*`, build → push a `ghcr.io/verbara/platform/web:{version}` + `:latest` + opcional cosign keyless).
- **Imagen output:** `ghcr.io/verbara/platform/web:{semver}` accesible público (post-visibility flip).
- **Validación:** push tag `v3.0.2` → workflow corre → `docker pull ghcr.io/verbara/platform/web:3.0.2` funciona desde una máquina sin auth.

### Deliverable 2: Reference docker-compose SMB
- **Archivo nuevo:** `docker/docker-compose.reference-smb.yml` — basado en `docker/docker-compose.smb.yml` (que actualmente expone 5060 UDP+TCP, 8088, 8089, 8180, 5038, 9092, 20000-20200/udp), con estos cambios delta:
  - **Imágenes pinned por tag semver** (`ghcr.io/verbara/platform/api:2.1.0`, `ghcr.io/verbara/platform/web:3.0.2`) — no `latest`. Opcional: digest pinning (`@sha256:...`) para air-gap / cosign-verified deploys (template comentado).
  - **Puertos SIP completos expuestos** (lista total — superset del smb.yml actual):
    ```yaml
    ports:
      - "${SIP_UDP_PORT:-5060}:5060/udp"        # SIP signalling UDP — REQUERIDO
      - "${SIP_TCP_PORT:-5060}:5060/tcp"        # SIP signalling TCP — REQUERIDO
      - "${SIP_TLS_PORT:-5061}:5061/tcp"        # SIP TLS opt — comentado por default
      - "${ARI_HTTP_PORT:-8088}:8088/tcp"       # ARI HTTP — REQUERIDO
      - "${ARI_WSS_PORT:-8089}:8089/tcp"        # ARI HTTPS + WebRTC WSS — REQUERIDO
      # 8180 WS plain — NO expuesto host (sólo intra-docker para Web→Asterisk)
      - "${AMI_PORT:-5038}:5038/tcp"            # AMI — opt, recomendado limitar a IP allowlist en firewall
      # - "4569:4569/udp"                       # IAX2 — opt, comentado default (poco uso moderno)
      - "${RTP_PORT_START:-20000}-${RTP_PORT_END:-20200}:20000-20200/udp"  # RTP (200 puertos = 100 calls)
    ```
  - **Variable `EXTERNAL_IP`** en environment block del Asterisk (ya existe en current `full.yml`). Asterisk la usa para reescribir SDP en respuestas (NAT traversal). El `.env.example` la comenta explicando cuándo es necesaria.
  - **Variables documentadas** en comentarios inline + en `.env.example`: `SIP_DOMAIN`, `SMTP_HOST`, `SMTP_USER`, `SMTP_PASS`, `IMAP_HOST`, `TLS_CERT_PATH`, `TLS_KEY_PATH`, `RTP_PORT_START`, `RTP_PORT_END`, `EXTERNAL_IP`, `AMI_PASSWORD`, `ARI_PASSWORD`, `JWT_SIGNING_KEY` (32+ chars), `POSTGRES_PASSWORD`, `REDIS_PASSWORD` (opt), `LICENSE_KEY` (Pro opt).
  - **Volúmenes named** (no anonymous):
    - `verbara_postgres_data` (~5-50 GB según retention)
    - `verbara_redis_data` (~50 MB)
    - `verbara_asterisk_recordings` (~30 MB/hora × concurrent calls × hours/day)
    - `verbara_asterisk_voicemail` (~5 MB/mensaje)
    - `verbara_media` (uploads + branding assets)
  - **Healthchecks** explícitos para cada servicio (ya muchos están en `full.yml`; replicar + endurecer timeouts).
  - **Resource limits**: `mem_limit` + `cpus` para cada servicio según tier SMB (Asterisk 2 GB, Platform.Api 1.5 GB, Postgres 2 GB, Web 256 MB, Redis 256 MB).
  - **Sin servicios opt-in por default**: Prometheus, Loki, MinIO, blackbox-exporter, Kamailio (no necesario en SMB single-server), RTPEngine externa (Asterisk hace su propio RTP), Renderer/Mail microservices (opt para PDF/email avanzado).
  - **Profile-based** (`docker compose --profile observability up`) para los servicios opcionales arriba.
- **Archivo nuevo:** `docker/.env.reference-smb.example` — template comentado **exhaustivo** (~80 vars) con cada var explicada en 1-2 líneas + valores razonables por default + **ejemplos reales** (no "changeme") como `SMTP_HOST=smtp.gmail.com` con link a la doc Gmail OAuth.

### Deliverable 3: Quickstart script con validación profunda de puertos SIP
- **Archivo nuevo:** `scripts/quickstart-smb.sh`
- Pre-flight (todo abortable):
  1. **Versiones de tooling**: `docker compose` ≥ 2.20, `curl`, `jq`, `ss`/`netstat`, `openssl`.
  2. **Recursos host mínimos**: 16 GB RAM disponible, 4 vCPU, 100 GB disco libre. WARN si menos.
  3. **Puertos TCP host libres** (no en uso por otro proceso) — chequear cada uno con `ss -tln`:
     - `5060/tcp` (SIP TCP)
     - `5061/tcp` (SIP TLS — opt pero reservado)
     - `5038/tcp` (AMI — solo expuesto si `EXPOSE_AMI=true`, default no)
     - `8088/tcp` (ARI HTTP)
     - `8089/tcp` (ARI HTTPS + WebRTC WSS)
     - `8180/tcp` (WS plain WebRTC — solo internal docker network)
     - `80/tcp` + `443/tcp` (HTTP/HTTPS Web/API ingress)
     - `5432/tcp` (Postgres — solo si EXPOSE_DB=true, default no)
  4. **Puertos UDP host libres** — `ss -uln`:
     - `5060/udp` (SIP UDP) — el crítico
     - `4569/udp` (IAX2 — opt, solo si EXPOSE_IAX=true)
     - `9092/udp` (rtpengine NG protocol — solo si rtpengine en host)
  5. **Range RTP libre** (CRÍTICO — cada llamada usa 2 puertos UDP RTP/RTCP):
     - Default range: `20000-20200/udp` (200 puertos = max 100 llamadas concurrentes simultáneas).
     - Script chequea que NINGÚN puerto del range esté en uso:
       ```bash
       BUSY=$(ss -uln | awk '{print $5}' | grep -oE ':[0-9]+$' | tr -d ':' | sort -un | awk -v lo=20000 -v hi=20200 '$1>=lo && $1<=hi')
       [ -n "$BUSY" ] && fatal "Puertos RTP ocupados: $BUSY — libera o ajusta RTP_PORT_START/END en .env"
       ```
     - Permite override: `RTP_PORT_START=30000 RTP_PORT_END=30500 ./quickstart-smb.sh` para expandir a 250 calls concurrentes.
  6. **Firewall del host** (informativo + auto-suggest):
     - Si `ufw` activo → imprime las reglas exactas a aplicar:
       ```
       sudo ufw allow 5060/udp comment "Verbara SIP signalling"
       sudo ufw allow 5060/tcp
       sudo ufw allow 5061/tcp
       sudo ufw allow 8088/tcp comment "Verbara ARI HTTP"
       sudo ufw allow 8089/tcp comment "Verbara ARI/WSS WebRTC"
       sudo ufw allow 20000:20200/udp comment "Verbara RTP"
       sudo ufw allow 80/tcp; sudo ufw allow 443/tcp
       ```
     - Idem para `firewalld` (Fedora/RHEL) e `iptables` raw.
     - **Detección NAT**:
       ```bash
       LOCAL_IP=$(ip route get 8.8.8.8 | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") print $(i+1)}')
       PUBLIC_IP=$(curl -sS https://api.ipify.org 2>/dev/null)
       ```
       Si `LOCAL_IP != PUBLIC_IP` → WARN extenso:
       ```
       ⚠️  Estás detrás de NAT.
       LOCAL_IP = 192.168.1.50 (este server)
       PUBLIC_IP = 203.0.113.42 (tu IP pública)
       
       Para que tu trunk SIP externo (Twilio, carrier) pueda alcanzar este server, 
       configura port-forwarding en tu router:
         - 5060/udp → 192.168.1.50:5060
         - 5060/tcp → 192.168.1.50:5060
         - 20000-20200/udp → 192.168.1.50:20000-20200
       
       Y en tu .env.reference-smb setea:
         EXTERNAL_IP=203.0.113.42
       
       (Asterisk usa EXTERNAL_IP para reescribir SDP en respuestas, sino el RTP 
       desde el peer remoto se pierde.)
       ```
  7. **Bandwidth check** (informativo): valida con `iperf3 -c {speedtest-host}` opcional, sino solo imprime el cálculo:
     ```
     Capacity de este server con .env actual:
       - RTP range 20000-20200 = max 100 llamadas concurrentes
       - WAN requerido (G.711 PCMU): ~16 Mbps simétrico para 100 calls
       - Storage para recordings: ~30 MB/hora × calls/hora promedio
     ```
  8. **Si no existe `.env.reference-smb`**: copia desde `.example`, le pide al operador editarlo (`$EDITOR ${EDITOR:-nano}`), pausa hasta confirmar.
  9. **Pull + up**: `docker compose pull && docker compose up -d --wait`.
  10. **Polling**: `/health/ready` hasta 200 OK (timeout 5 min).
  11. **Print final**:
      - URLs (Web UI, API, ARI, Grafana opcional).
      - Comando del setup wizard.
      - Tabla resumen "Tu cluster soporta: N agentes telefónicos / N llamadas concurrentes / N agentes WebChat" (computed según .env).

### Deliverable 3.5: Capacity planning + network requirements + decisión arquitectural Asterisk-host-network

#### Decisión arquitectural — Asterisk usa `network_mode: host`

**Cambio crítico vs el `docker-compose.smb.yml` actual**: el reference compose monta Asterisk con `network_mode: host` (no docker bridge con port-mapping). Justificación:

| Aspecto | Bridge (smb.yml actual — bottleneck 100 calls) | `network_mode: host` (reference SMB) |
|---|---|---|
| iptables rules por puerto RTP | 1 cada uno (200 rules para 100 calls; 600 para 300 calls = inviable) | 0 |
| `docker-proxy` userland process per port | 1 cada uno (~5 MB RAM + scheduling overhead) | 0 |
| Latencia UDP por paquete | +0.5-2 ms (DNAT + proxy) | nativa kernel |
| RAM/CPU overhead para 300 calls | 600 procesos docker-proxy = **~3 GB RAM solo overhead** | 0 |
| Conflictos con otros servicios SIP en host | Aislados | Asterisk reclama 5060/5061/8088/8089/5038/8180/9092 del host (no debe haber otro PBX) |
| Portabilidad Linux on-prem + cloud VM (Azure/AWS/GCP) | Idéntica | Idéntica |
| Portabilidad Docker Desktop (Mac/Win dev) | OK | **NO** funciona realmente (Docker Desktop = VM intermediaria) |
| Comunicación Platform.Api → Asterisk AMI/ARI | DNS `asterisk:5038` (docker network) | `host.docker.internal:5038` via `extra_hosts: [host.docker.internal:host-gateway]` |

**Implementación**:
- `docker-compose.reference-smb.yml` declara `network_mode: host` solo en el servicio `asterisk`.
- Platform.Api + Web + Postgres + Redis siguen en `default` bridge network.
- Platform.Api environment: `Asterisk__Ami__Host=host.docker.internal`, `Asterisk__Ari__BaseUrl=http://host.docker.internal:8088`.
- Platform.Api service declara `extra_hosts: ["host.docker.internal:host-gateway"]` (Docker resuelve a la IP del gateway docker0 = host).
- Para **Docker Desktop dev** (Mac/Win): el manual `01-instalacion-docker.md` aclara que Mac/Win usa una VM Linux internamente — el setup funciona pero la "IP del host" desde el peer SIP externo es la IP de la VM Docker Desktop, no la del laptop. Para dev local usar `network_mode: host` con `extra_hosts` igual; para producción usar Linux Docker nativo (Ubuntu / Debian / RHEL).
- **WebRTC clients** (browser de agente) conectan directo a `wss://{host-public-or-lan-ip}:8089/asterisk/ws` — sin cambio.

#### Cuántos agentes telefónicos puede conectar un SMB (revisado con host-network)

Constraints encadenados (el menor de los 5 limita):

| Constraint | Fórmula | SMB Lite (4 vCPU / 16 GB) | SMB Standard (8 vCPU / 32 GB) | SMB Plus (16 vCPU / 64 GB) |
|---|---|---|---|---|
| **RTP ports** (2/call) — con host network no hay overhead de mapping | range/2 | 20000-20200 (200 puertos) → **100 calls** | 20000-20400 → **200 calls** | 20000-20600 → **300 calls** |
| **Asterisk RAM** (~10 MB/call) | RAM/10 MB | ~150 calls ceiling (1.5 GB) | ~400 calls | ~800 calls |
| **CPU Asterisk G.711 passthrough** (~2 %/call) | cores × 0.5 / 0.02 | 4 × 0.5 / 0.02 = **100 calls** | 8 × 0.5 / 0.02 = **200 calls** | 16 × 0.5 / 0.02 = **400 calls** |
| **CPU Asterisk con transcoding Opus↔G.711** (~10 %/call) | cores × 0.5 / 0.10 | 20 calls | 40 calls | 80 calls |
| **Postgres pool** (CDR + queue events; ADR-0015 Phase 2 shared NpgsqlDataSource, pool size tunable) | maxPoolSize | default 100 → bump a 200 si necesario | 200 | 300 |
| **WAN bandwidth** (G.711, bidir) | calls × 80 kbps × 2 | 16 Mbps simétrico | 32 Mbps | 48 Mbps |

**Capacidad declarada para cada tier** (asumiendo G.711 passthrough — el caso típico de trunks SIP PSTN):

| Tier | Hardware | Calls concurrentes | Agentes WebRTC simultáneos | RTP range a configurar |
|---|---|---|---|---|
| **SMB Lite** | 4 vCPU / 16 GB / 100 GB SSD | **50 calls / 50 agents nominal** (pico 100) | 50 | 20000-20200 (default) |
| **SMB Standard** | 8 vCPU / 32 GB / 250 GB SSD | **150 calls / 150 agents** | 150 | 20000-20400 |
| **SMB Plus** | 16 vCPU / 64 GB / 500 GB SSD | **300 calls / 300 agents** ← el target del usuario | 300 | 20000-20600 |

**Reglas para el cliente final**:
- Reference compose default → SMB Standard (target sweet-spot del producto).
- `.env` permite ajustar `RTP_PORT_START` + `RTP_PORT_END` + `ASTERISK_CPU_LIMIT` + `ASTERISK_MEM_LIMIT` para mover entre tiers sin cambiar compose.
- Si el cliente declara hardware bajo el tier que pide → quickstart-smb.sh imprime WARN: "Tu hardware = SMB Lite pero `.env` declara RTP range para 300 calls. Recomendado bumpear a 16 vCPU / 64 GB o reducir `RTP_PORT_END`."

#### Codec impact en capacity (replicado con números actualizados)

| Codec | Bitrate | CPU impact / call | Recomendado para |
|---|---|---|---|
| G.711 PCMU/PCMA (μ-law / a-law) | 80 kbps con headers | ~2 % core (passthrough) | Trunks PSTN; mayoría carriers |
| G.722 wideband | 64 kbps | ~3 % | SIP HD |
| Opus 48 kHz | 24-64 kbps adaptive | ~5 % | WebRTC negotiated end-to-end (sin transcoding al backend si trunk soporta Opus, e.g. Twilio Elastic) |
| **Opus ↔ G.711 transcoding** | mixed | **~10 % core** | **EVITAR** — reduce capacity 5× |

**Recomendación reference SMB**: G.711 passthrough + WebRTC en Opus negotiated. Si trunk NO acepta Opus, el manual documenta cómo forzar G.711 también en el WebRTC offer (perdiendo wideband audio pero ganando capacity).

#### Bandwidth requirements (revisado con tiers reales)

| Calls concurrent | Bitrate sostenido (G.711 bidir) | WAN minimum recomendado | Latencia tolerable one-way |
|---|---|---|---|
| 50 (SMB Lite) | 8 Mbps | 25 Mbps simétrico | < 150 ms |
| 150 (SMB Standard) | 24 Mbps | 50 Mbps simétrico | < 150 ms |
| 300 (SMB Plus) | 48 Mbps | 100 Mbps simétrico | < 100 ms |

#### NAT detection — 4 escenarios

El `quickstart-smb.sh` ejecuta esta lógica:

```bash
LOCAL_IP=$(ip route get 8.8.8.8 | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") print $(i+1)}')
PUBLIC_IP=$(curl -sS --max-time 5 https://api.ipify.org 2>/dev/null || \
            curl -sS --max-time 5 https://ifconfig.me 2>/dev/null)

# Escenario A: cloud VM con IP pública directa asignada a NIC
if [ "$LOCAL_IP" = "$PUBLIC_IP" ]; then
    echo "✓ Escenario A — IP pública directa (cloud VM o bare-metal expuesto)"
    echo "  EXTERNAL_IP no requerido (Asterisk auto-detecta)"
    echo "  Verificar Security Group / firewall del cloud permita 5060+RTP inbound"
fi

# Escenario B: cloud con NIC privada + IP pública via cloud (AWS ENI, Azure, GCP)
if [ "$LOCAL_IP" != "$PUBLIC_IP" ] && \
   echo "$LOCAL_IP" | grep -qE '^(172\.(1[6-9]|2[0-9]|3[01])|10\.|192\.168\.0\.)'; then
    # Probe externo: usar un servicio test que verifica si nuestro puerto público es alcanzable
    PROBE=$(curl -sS --max-time 5 "https://www.canyouseeme.org/proxy.php?port=5060&ip=$PUBLIC_IP" 2>/dev/null)
    # Heurística: si el servicio responde con "Success" o el port está abierto, asumimos NAT funcional
    # (sin servicio third-party confiable, asumimos que está OK si LOCAL es cloud subnet)
    echo "✓ Escenario B/C — NAT detectado (privada=$LOCAL_IP, pública=$PUBLIC_IP)"
    echo "  EXTERNAL_IP=$PUBLIC_IP REQUERIDO en .env"
    echo "  Si es cloud (AWS/Azure/GCP): Security Group debe permitir 5060+RTP a la NIC"
    echo "  Si es on-prem: router debe hacer port-forward o DMZ a $LOCAL_IP"
fi

# Escenario D: CGNAT detection (ISP residencial con NAT propio)
if echo "$PUBLIC_IP" | grep -qE '^100\.(6[4-9]|[789][0-9]|1[01][0-9]|12[0-7])\.'; then
    echo "❌ Escenario D — CGNAT detectado (rango 100.64.0.0/10)"
    echo "  Tu ISP no te da una IP pública dedicada. SIP inbound NO funcionará."
    echo "  Opciones: pedir IP pública estática al ISP, usar VPS proxy SIP, o trunk solo outbound."
fi
```

**Tu setup específico** (192.168.40.100 + 200.118.42.61 + DMZ): caer en Escenario C (on-prem DMZ). Script detecta NAT, pide `EXTERNAL_IP=200.118.42.61` en `.env`, instruye verificar que el DMZ del router efectivamente forwarda 5060/udp+tcp + 20000-20200/udp al 192.168.40.100. Con DMZ activo eso ya está cubierto pero el quickstart imprime el comando de verificación: `nc -uvz 200.118.42.61 5060` desde otra máquina internet → debe llegar al Asterisk.

**Cloud VM (Azure/AWS/GCP) con IP pública directa**: cae en Escenario A. Sin NAT en el camino. Cloud Security Group reemplaza al firewall on-prem. El manual incluye snippets de Terraform / CLI para abrir 5060+RTP en cada cloud provider (Azure NSG, AWS Security Group, GCP firewall rule).

#### Codec impact en capacity
| Codec | Bitrate | CPU impact | DSP transcoding? |
|---|---|---|---|
| G.711 PCMU/PCMA (μ-law / a-law) | 64 kbps + RTP headers ≈ 80 kbps | ~2 % core/call (passthrough) | No |
| G.722 wideband | 64 kbps | ~3 % | No (interop con SIP HD) |
| Opus 48 kHz | 24-64 kbps | ~5 % | No (negociated WebRTC ↔ SIP) |
| **Opus ↔ G.711 transcoding** | mixed | **~10 % core/call** | **SÍ — half capacity** |

**El reference SMB se documenta con G.711 passthrough recomendado** para trunks PSTN. WebRTC agents negocian Opus internamente entre Asterisk y el browser via WSS sin transcoding al backend SIP siempre que el trunk acepte Opus (algunos sí, ej. Twilio Elastic con opus offering).

#### Bandwidth requirements
| Calls concurrent | Bitrate sostenido (G.711, bidireccional) | WAN minimum recomendado |
|---|---|---|
| 10 | 1.6 Mbps | 5 Mbps simétrico |
| 50 | 8 Mbps | 25 Mbps simétrico |
| 100 (full SMB) | 16 Mbps | 50 Mbps simétrico |

**Latency**: SIP/RTP tolera hasta ~150 ms one-way para llamadas aceptables; >300 ms es notable. Documentar en manual: cliente debe verificar `ping {trunk-provider-edge}` antes de comprometer SLA.

#### NAT + STUN/TURN
- Si server **detrás de NAT** (caso común SMB on-prem): `EXTERNAL_IP=<public-ip>` en `.env` es OBLIGATORIO. Sino el SDP en respuestas SIP tiene la IP privada y el peer remoto manda RTP a `192.168.x.x` → se pierde.
- **Port forwarding del router** REQUERIDO: 5060/udp + 5060/tcp + range RTP completo. El script imprime las reglas exactas.
- **STUN/TURN** (para agentes WebRTC remote behind corporate NAT): documentado en `06-canal-voz-sip.md` § "WebRTC agents behind strict NAT" — el cliente puede usar Coturn público (`stun.l.google.com:19302`) gratis o levantar su propio Coturn como container adicional (incluido en compose como `coturn` opt-in).

#### Storage growth
- **Recordings** (si on): G.711 mono WAV = ~480 KB/min = ~30 MB/hora/call. Cliente con 50 agentes × 4h llamada/agente/día × 22 días = **132 GB/mes** mínimo. Manual documenta política de retención + rotación.
- **CDR + CEL** (Postgres): ~1 KB/call. 50 agentes × 100 calls/agente/día × 22 días = 110 MB/mes. Trivial.
- **Audit log**: ~5 KB/event con compresión Postgres. Documentado en `docs/operations/capacity-planning.md` ya existente — referenciar.


Carpeta: `docs/manuales/smb/`

| Archivo | Contenido |
|---|---|
| `00-vision-general.md` | Qué es Verbara, qué cubre el manual, **3 tiers de hardware** (SMB Lite 4 vCPU/16 GB → 50 calls; Standard 8 vCPU/32 GB → 150 calls; Plus 16 vCPU/64 GB → 300 calls), arquitectura visual de los componentes (qué container corre qué, network bridge vs Asterisk-host-network), **decisión OS**: Debian 12 primary recomendado (alineamiento con base image .NET de Microsoft + ecosistema SIP/telco) — Ubuntu LTS / Rocky / Alma / Amazon Linux 2023 también soportados; Docker Desktop NO. Tabla rápida de imágenes Docker que la plataforma usa internamente (Platform.Api = Debian, Web = Alpine, Asterisk = Debian, Postgres/Redis = Alpine) — informativo para entender los stacks layereados. |
| `01-instalacion-docker.md` | **Distro host recomendado: Debian 12 (bookworm)** (racional: alineamiento con `mcr.microsoft.com/dotnet/aspnet:10.0` que usa Debian bookworm-slim; Asterisk se desarrolla primero en Debian; mínima superficie de ataque sin Snap overhead). **Alternativas igualmente soportadas**: Ubuntu 22.04 LTS / 24.04 LTS, Rocky Linux 9, AlmaLinux 9, Amazon Linux 2023. **NO soportados**: Docker Desktop Mac/Win (incompat `network_mode: host`), Alpine como host (musl libc edge cases), Windows Server, macOS. **Tabla de tiers de hardware (SMB Lite/Standard/Plus)** con calls/agents target. Comandos de install **por distro** (apt Debian/Ubuntu, dnf Rocky/Alma, yum Amazon Linux). **Firewall rules** exactas por distro (nftables Debian 12 — el default; UFW Ubuntu; firewalld Rocky/Alma; iptables fallback). **Cloud snippets** (Azure NSG via `az network nsg rule`, AWS Security Group via `aws ec2 authorize-security-group-ingress`, GCP firewall via `gcloud compute firewall-rules create`) para Escenarios A/B. **On-prem NAT/DMZ guide** Escenario C: Mikrotik (`/ip firewall nat add chain=dstnat ...`), pfSense (UI screenshots), OPNsense, Ubiquiti EdgeOS, ASUS/TP-Link routers consumer. **Detección CGNAT** Escenario D + opciones (IP pública estática, VPS proxy SIP). DNS records sugeridos. TLS Let's Encrypt con `certbot` standalone. |
| `02-arranque-stack.md` | `git clone` + `cp .env.reference-smb.example .env` + qué editar + `scripts/quickstart-smb.sh` + verificación cada servicio healthy. |
| `03-setup-inicial.md` | First-run wizard via Web UI: crear platform admin + primer tenant + primer usuario agente + primera queue. Screenshots paso a paso. |
| `04-canal-webchat.md` | Configurar widget WebChat: ir a `/admin/channels` → habilitar WebChat → copiar snippet HTML → embed en página de prueba (`tests/manual-validation/webchat-test.html` provista) → validar mensaje round-trip. |
| `05-canal-email.md` | Configurar canal Email: credenciales SMTP outbound (Gmail OAuth, M365, o SMTP genérico) + IMAP inbound + dirección catch-all + reglas de threading. Probar envío + recepción. |
| `06-canal-voz-sip.md` | **El manual más extenso**. Estructura: (a) Requisitos de red detallados (todos los puertos SIP + RTP + WSS, NAT, firewall, IP pública, bandwidth) — replica las reglas que imprime `quickstart-smb.sh`. (b) **Tier de capacidad** con tablas: SMB 16GB → 50 agentes / 100 calls pico; Mid 32GB → 150 agentes / 250 calls; Large 64GB → 350 agentes / 500 calls. (c) Cómo expandir el RTP range si necesitas más. (d) Configurar trunk SIP externo: Twilio Elastic SIP step-by-step (ejemplo realista de credenciales + IP whitelisting + caller-id) **+ sección genérica** "tu carrier SIP" (host, port, registration vs IP-auth, SRTP, codec negotiation). (e) Provisionar trunk via `POST /api/v1/dialer/trunks` (curl example) o UI `/admin/trunks` (screenshots). (f) Configurar dialplan inbound: caller-id translation → queue routing → estrategia (round-robin / longest-idle / skill-based). (g) Registrar primer agente WebRTC: WSS endpoint (`wss://api.r55.local:8089/asterisk/ws` o equivalent), credenciales pjsip auto-provisioned al crear el agente, mic permissions en browser. (h) Probar llamada entrante: número trunk → IVR → queue → WebRTC ring → agente atiende → conversación → hang up. (i) Probar llamada saliente desde WebRTC. (j) **Sección Escalado**: cómo subir a Mid tier (cambios .env + compose ports + hardware). (k) **Sección WebRTC behind strict NAT**: cuándo se necesita TURN, cómo levantar Coturn opt-in en el compose. (l) **Troubleshooting SIP**: "no oigo audio" (NAT/SDP/EXTERNAL_IP), "llamada cae a los 30s" (firewall stateful UDP), "agente no recibe ring" (WSS cert, websocket close 1006). |
| `07-validacion-e2e.md` | Checklist E2E + comando para ejecutar `npx playwright test --grep @reference-deployment` que automatiza la verificación. |
| `99-troubleshooting.md` | Errores comunes (puerto 5060 bloqueado, Postgres no arranca, login HTTP 401, llamada no entra) + cómo diagnosticar. Reutiliza patrones de `docs/operations/alerts-runbook.md`. |

### Deliverable 5: Suite E2E "reference-deployment"
- **Archivo nuevo:** `Verbara.Platform.Web/tests/e2e/tests/reference-deployment.spec.ts`
- Tag: `@reference-deployment`
- Tests:
  1. Setup wizard completa (admin + tenant + agente + queue).
  2. Configurar canal WebChat → enviar mensaje desde widget mockeado → asignar a agente → agente responde → mensaje llega al widget.
  3. Configurar canal Email → mock SMTP recibe outbound → mock IMAP inyecta inbound → conversación se crea → agente responde → outbound delivered.
  4. Configurar canal SIP → registrar softphone test (PJSIP-style) → marcar extensión queue → llamada en cola → asignar agente → WebRTC client de agente recibe `INVITE`.
- **Fixtures reutilizar:** `auth.fixture.ts`, `api.fixture.ts` (ya existen). Añadir `channel.fixture.ts` con helpers por canal.
- **Run target:** contra el stack levantado por `quickstart-smb.sh` en un host limpio (Docker Desktop o Linux).

### Deliverable 6: Validación humana documentada
- **Archivo nuevo:** `tests/manual-validation/webchat-test.html` (página simple HTML con el widget embed para probar visualmente).
- **Archivo nuevo:** `docs/manuales/smb/checklist-validacion-cliente.md` (checklist imprimible para el implementador del cliente: "marcá cada caja"). Cubre los mismos puntos que el E2E suite pero por humano, en español.

---

## Fase 2 — K8s on-premise (después de Fase 1)

> **Esqueleto** — se expande a plan detallado cuando Fase 1 ship. El esfuerzo es ~2-3 semanas; Fase 1 es ~1 semana.

### Deliverables principales (Fase 2)
1. **Helm chart customer-portable** — refactor `infra/k8s/helm/platform/` + `asterisk/`:
   - Externalizar TODOS los secrets (eliminar `stringData`, integrar External Secrets Operator + Sealed Secrets como opciones).
   - Eliminar `192.168.122.201` hardcoded en Kamailio dispatcher; parametrizar via values `asterisk.sip.externalIp` (operador setea el LB del cluster cliente).
   - Hostnames parametrizados (no `r55.local`).
   - TLS por default con cert-manager (chart instala `Issuer` ACME si operador opta).
   - Kamailio off-hostNetwork con LoadBalancer Service (compatible con MetalLB on-prem).
   - Documentar requisitos cluster: K8s 1.30+, CNI con NetworkPolicy support, ingress controller (NGINX / Traefik / Istio), MetalLB o equivalente, cert-manager, External DNS.

2. **Manuales K8s** en `docs/manuales/k8s/` (mismo flujo Fase 1, contexto K8s):
   - `01-prerequisitos-cluster.md`, `02-instalacion-helm.md`, `03-setup-inicial.md`, `04-08-canales` (idem Fase 1), `09-troubleshooting.md`.

3. **Suite E2E** — reutilizar la misma `reference-deployment.spec.ts` apuntando al ingress K8s en lugar de localhost.

---

## Files to modify / create (Fase 1)

### Nuevos
- `Verbara.Platform.Web/.github/workflows/release.yml` — workflow publish a `ghcr.io/verbara/platform/web`
- `docker/docker-compose.reference-smb.yml` — compose customer-ready con puertos SIP completos
- `docker/docker-compose.coturn.yml` — Coturn opt-in para WebRTC behind NAT
- `docker/.env.reference-smb.example` — ~80 vars comentadas
- `docker/asterisk-config-reference/rtp.conf.example` — template con RTP_PORT_START/END expandible
- `scripts/quickstart-smb.sh` — validación profunda de puertos SIP + RTP + NAT + bandwidth
- `scripts/capacity-calc.sh` — calculadora informativa: input agentes esperados → output recursos necesarios + warning si .env actual no aguanta
- `docs/manuales/smb/00-vision-general.md` — arquitectura + tiers de capacidad (SMB/Mid/Large) + matriz hardware → calls/agents
- `docs/manuales/smb/01-instalacion-docker.md` — pre-requisitos OS + Docker + **firewall rules exactas** + DNS + Let's Encrypt
- `docs/manuales/smb/02-arranque-stack.md` — clone + .env + quickstart + healthchecks
- `docs/manuales/smb/03-setup-inicial.md` — first-run wizard (admin/tenant/agente/queue)
- `docs/manuales/smb/04-canal-webchat.md` — widget config + snippet + página HTML de prueba
- `docs/manuales/smb/05-canal-email.md` — SMTP/IMAP (Gmail OAuth + M365 + SMTP genérico)
- `docs/manuales/smb/06-canal-voz-sip.md` — **manual más extenso**: red SIP + capacity + trunk + dialplan + WebRTC + escalado + Coturn + troubleshooting
- `docs/manuales/smb/07-validacion-e2e.md` — checklist + comando E2E suite
- `docs/manuales/smb/08-troubleshooting-sip.md` — SUBDOCUMENTO dedicado a problemas SIP (NAT/SDP/firewall/codec/registration/audio/WSS) con tabla síntoma → causa probable → solución
- `docs/manuales/smb/99-troubleshooting.md` — troubleshooting general (no SIP)
- `docs/manuales/smb/checklist-validacion-cliente.md` — checklist humano imprimible
- `docs/manuales/smb/capacity-reference.md` — referencia rápida: con N agentes telefónicos necesitas X RAM, Y CPU, Z bandwidth, W storage/mes
- `tests/manual-validation/webchat-test.html` — página de prueba con widget embebido
- `tests/manual-validation/sip-softphone-test.md` — guía corta: cómo configurar Linphone/Zoiper para probar SIP manual
- `Verbara.Platform.Web/tests/e2e/tests/reference-deployment.spec.ts` — E2E setup + 3 canales
- `Verbara.Platform.Web/tests/e2e/fixtures/channel.fixture.ts` — helpers por canal

### Modificados (mínimo, sin romper lo existente)
- `Verbara.Platform/docs/roadmap.md` — entrada nueva "Reference Deployments + Manuales SMB" + marcar R5.5 como "research / capacity envelope" (no cancelado, queda como histórico).
- `Verbara.Platform/CLAUDE.md` — referencia a `docs/manuales/smb/` en sección Documentation Layout.

### NO se toca
- `infra/k8s/helm/` (Fase 2)
- `docker/docker-compose.smb.yml` (queda como base canónica, no se rompe back-compat)
- Cualquier cosa relacionada con R5.5 / D-LK / E-LK / chaos.

---

## Verificación (Fase 1)

Pasos en orden, cada uno debe ser verde antes de pasar al siguiente:

1. **Build de imagen Web pública.** En `Verbara.Platform.Web/`: `git tag v3.0.2 && git push origin v3.0.2`. Verificar en GitHub Actions que el workflow corrió + en `ghcr.io/verbara/platform/web` que la imagen está. Desde una máquina sin auth: `docker pull ghcr.io/verbara/platform/web:3.0.2` debe funcionar.

2. **Reference compose smoke test.** En una VM Linux limpia (Ubuntu 22.04 sin Verbara previo): `git clone Verbara.Platform && cd Verbara.Platform && cp docker/.env.reference-smb.example docker/.env.reference-smb && nano docker/.env.reference-smb` (operador edita) → `bash scripts/quickstart-smb.sh`. **Verificar que el quickstart**:
   - Imprime claramente cada puerto SIP/RTP requerido + libre/ocupado
   - Detecta correctamente NAT (correr con server detrás de router doméstico) e imprime las reglas de port-forwarding
   - Imprime la matriz de capacidad estimada con los .env actuales
   - Aborta limpio si un puerto crítico (5060/udp o cualquier puerto del RTP range) está ocupado
   - Todos los servicios `healthy` al final + URLs imprime
   - `ss -uln | grep 20000` → asterisk listening en el range RTP
   - `ss -tln | grep -E '5060|8088|8089|5038'` → todos los puertos SIP listening
   - **Probe externo desde otra máquina del LAN**: `nc -u {server-ip} 5060 < /dev/null && echo "SIP UDP reachable"` debe devolver "reachable" antes de hacer 100 ms.

3. **Setup wizard end-to-end.** Seguir manuales 03 + 04 + 05 + 06 paso a paso. Al final del 06: 1 admin user + 1 tenant + 1 agente registrado + 3 canales configurados + 1 trunk SIP probado. Tomar screenshots para versión final de los manuales (intercalar visuales).

4. **Mensajes round-trip por canal.** Desde un browser segundo (no donde corre el admin):
   - **WebChat**: abrir `webchat-test.html` → enviar "hola" → ver en la cola → agente toma → responde → mensaje llega de vuelta al widget.
   - **Email**: enviar correo a la dirección catch-all desde una cuenta externa → ver conversación creada → agente responde → outbound recibido en la cuenta externa.
   - **SIP — pruebas escalonadas** (cada una debe pasar antes de la siguiente):
     a. `kamcmd` o `asterisk -rx "pjsip show transports"` muestra UDP+TCP+WSS bound en los puertos esperados.
     b. Desde otra máquina del LAN: registrar **Linphone** con credenciales auto-generadas del primer agente → estado `Registered` confirma SIP TCP/UDP path.
     c. **Test loopback** (agente call agente): 2 agentes registrados, agente A marca extensión de agente B → ring + conversación + hangup → CDR en Postgres muestra la llamada.
     d. **Test trunk SIP entrante**: llamar desde un teléfono móvil al DID provisto por Twilio Elastic SIP (o tu carrier) → llamada entra al server vía 5060/udp → routea a queue → ring en agente WebRTC del Web UI → agente atiende → **audio bidireccional confirmado** (esto valida RTP en el range 20000-20200 + EXTERNAL_IP correcto si está detrás de NAT).
     e. **Test trunk SIP saliente**: agente WebRTC marca número móvil → llamada sale por trunk → móvil ring → conversación → hangup limpio (BYE en log).
     f. **Test concurrencia mínima**: 5 llamadas simultáneas (5 softphones de LAN llamando uno tras otro al DID) → todas conectan → 5 agentes contestan → 5 audios independientes. Esto valida que el RTP port range NO se solapa.
     g. **Test capacity declarada**: con SIPp (tooling existente del lab) lanzar 50 llamadas concurrentes → 50/50 OK + audio OK + 50 CDRs grabados → confirma tier SMB.

5. **Suite E2E automatizada.** En el host con el stack vivo: `cd Verbara.Platform.Web && npx playwright test --grep @reference-deployment`. Esperado: 4 tests verdes (setup + 3 canales).

6. **Validación de manual por revisor externo.** Pedirle a alguien que NO participó del setup que siga los manuales en una segunda VM limpia. Que escriba qué pasos le confunden o fallan. Iterar manuales hasta cero objeciones.

7. **Commit + tag de release.** `feat(smb): reference deployment v1 + manuales canal Voz/WebChat/Email` + tag `manuales-smb-v1.0`.

---

## R5.5 actual — no se cancela

- **D-LK soak v2** corre en background hasta mañana 04:36 local. Reporte final guardado como bonus en `tests/Verbara.Platform.LoadTests/soak-reports/k8s-dlk/`. No invertir más tiempo en R5.5 más allá de capturar el reporte automático.
- **E-LK induce-failure validation** queda pending hasta despues de D-LK termine (scope reducido: solo verificar que las alertas Prometheus disparan; no escala-ola post-fix).
- **Round 4 #1 (Cilium L2 hairpin)** + **#3 (T1 rebuild)** quedan deferred. El refactor de Helm en Fase 2 (Kamailio off-hostNetwork + parametrizar LB) los aborda naturally — no hay que arreglarlos en el lab obsoleto.
- **Phase 0C/B-C/C-C/D-C/E-C cloud**: deferred indefinidamente. La métricas existentes B-LK + capacity envelope del Docker B-L bastan para sales/marketing. Cloud comparison se hace cuando un cliente real lo pida.
- **Phase F closure**: se cierra con el R5.5 actual + se abre nueva entrada de roadmap "Reference Deployments + Manuales SMB v1".
