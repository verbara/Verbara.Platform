#!/usr/bin/env bash
#
# scripts/quickstart-smb.sh — Verbara Platform SMB pre-flight + arranque.
#
# Este script valida que el host Linux esté listo para correr el reference
# deployment de Verbara (docker/docker-compose.reference-smb.yml) y luego
# arranca el stack. Está escrito para que un cliente final (operador
# SysAdmin/DevOps) lo ejecute paso a paso siguiendo el manual
# docs/manuales/smb/02-arranque-stack.md.
#
# Uso:
#   bash scripts/quickstart-smb.sh                # validación + arranque
#   bash scripts/quickstart-smb.sh --check-only   # sólo validar, no arrancar
#   bash scripts/quickstart-smb.sh --skip-edit    # no abrir $EDITOR para .env
#
# Variables de entorno reconocidas (útiles en CI / no-interactivo):
#   RTP_PORT_START / RTP_PORT_END   override del rango RTP a validar
#   EXPOSE_AMI=true/false           ¿validar 5038 también?  (default false)
#   EXPOSE_IAX=true/false           ¿validar 4569/udp?       (default false)
#   NO_COLOR=1                      desactiva color en TTY no-ANSI
#
# Sale con código:
#   0 — todo verde, stack arriba (o --check-only sin issues)
#   1 — fallo crítico (puerto ocupado, recurso insuficiente, etc.)
#   2 — uso/argumento inválido
#
# Compatibilidad: Debian 12+, Ubuntu 22.04+, Rocky/Alma 9, Amazon Linux 2023.
# Requiere bash 4+. NO usa Docker Desktop (Mac/Win) — network_mode: host
# del Asterisk no funciona correctamente bajo la VM intermediaria.

set -euo pipefail

# ─────────────────────────────────────────────────────────────────────────
# Configuración
# ─────────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
COMPOSE_FILE="${REPO_ROOT}/docker/docker-compose.reference-smb.yml"
ENV_FILE_EXAMPLE="${REPO_ROOT}/docker/.env.reference-smb.example"
ENV_FILE="${REPO_ROOT}/docker/.env.reference-smb"

# Recursos mínimos recomendados (SMB Lite — el tier más chico soportado).
MIN_RAM_GB=16
MIN_CPU_CORES=4
MIN_DISK_GB=100

# Puertos TCP requeridos
TCP_PORTS_REQUIRED=(5060 5061 8088 8089 80)
# 5038 (AMI) sólo se valida si EXPOSE_AMI=true (por defecto false = seguro)
# 8180 plain WS es sólo intra-docker → no se valida en host
# 443 sólo si TLS habilitado (warn, no fatal)

# Puertos UDP requeridos (signalling SIP)
UDP_PORTS_REQUIRED=(5060)

# Rango RTP — override por env si el operador necesita más de 100 calls
RTP_PORT_START="${RTP_PORT_START:-20000}"
RTP_PORT_END="${RTP_PORT_END:-20200}"

EXPOSE_AMI="${EXPOSE_AMI:-false}"
EXPOSE_IAX="${EXPOSE_IAX:-false}"

CHECK_ONLY=false
SKIP_EDIT=false
for arg in "$@"; do
    case "$arg" in
        --check-only) CHECK_ONLY=true ;;
        --skip-edit) SKIP_EDIT=true ;;
        --help|-h)
            sed -n '2,/^set -e/p' "$0" | sed 's/^# \{0,1\}//; /^set -e/d'
            exit 0
            ;;
        *)
            echo "Argumento desconocido: $arg (usá --help)" >&2
            exit 2
            ;;
    esac
done

# ─────────────────────────────────────────────────────────────────────────
# Colores
# ─────────────────────────────────────────────────────────────────────────
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RED=$'\033[0;31m'; C_GREEN=$'\033[0;32m'; C_YELLOW=$'\033[0;33m'
    C_BLUE=$'\033[0;34m'; C_CYAN=$'\033[0;36m'; C_BOLD=$'\033[1m'; C_RESET=$'\033[0m'
else
    C_RED=""; C_GREEN=""; C_YELLOW=""; C_BLUE=""; C_CYAN=""; C_BOLD=""; C_RESET=""
fi

step()  { printf "\n${C_BOLD}${C_BLUE}▶ %s${C_RESET}\n" "$1"; }
ok()    { printf "  ${C_GREEN}✓${C_RESET} %s\n" "$1"; }
warn()  { printf "  ${C_YELLOW}⚠${C_RESET}  %s\n" "$1"; }
fail()  { printf "  ${C_RED}✗${C_RESET} %s\n" "$1"; FATAL_COUNT=$((FATAL_COUNT+1)); }
info()  { printf "  ${C_CYAN}ℹ${C_RESET}  %s\n" "$1"; }
hint()  { printf "      ${C_CYAN}→${C_RESET}  %s\n" "$1"; }
fatal() { printf "\n${C_RED}${C_BOLD}FATAL:${C_RESET} %s\n" "$1" >&2; exit 1; }

FATAL_COUNT=0
WARN_COUNT=0

# ─────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────

# Detecta la distro Linux + administrador de firewall recomendado.
detect_distro() {
    if [ -r /etc/os-release ]; then
        # shellcheck disable=SC1091
        . /etc/os-release
        DISTRO_ID="${ID:-unknown}"
        DISTRO_LIKE="${ID_LIKE:-}"
        DISTRO_VERSION="${VERSION_ID:-}"
        DISTRO_NAME="${PRETTY_NAME:-$DISTRO_ID}"
    else
        DISTRO_ID="unknown"; DISTRO_NAME="unknown"
    fi
    # Elegir el firewall esperado
    case "$DISTRO_ID" in
        debian) FW_TOOL="nftables" ;;
        ubuntu) FW_TOOL="ufw" ;;
        rocky|almalinux|rhel|fedora) FW_TOOL="firewalld" ;;
        amzn) FW_TOOL="iptables" ;;
        *) FW_TOOL="iptables" ;;
    esac
}

# ¿está el puerto X/proto libre en el host?
# Devuelve "" si libre, "PID/proc" si ocupado.
port_in_use() {
    local port="$1" proto="$2"
    if command -v ss >/dev/null 2>&1; then
        local flag
        case "$proto" in
            tcp) flag="-tlnp" ;;
            udp) flag="-ulnp" ;;
            *) return 1 ;;
        esac
        # ss -p requiere root para mostrar el proceso pero el listing igual aparece sin root
        ss $flag 2>/dev/null | awk -v p=":$port" '$5 ~ p"$" {print $0; exit}'
    elif command -v netstat >/dev/null 2>&1; then
        local proto_short
        case "$proto" in tcp) proto_short=tcp ;; udp) proto_short=udp ;; esac
        netstat -ln 2>/dev/null | awk -v p=":$port" -v pr="$proto_short" '$1==pr && $4 ~ p"$" {print $0; exit}'
    else
        return 1  # no podemos verificar
    fi
}

# Detecta la versión de docker compose en formato comparable (mayor.menor).
docker_compose_version() {
    docker compose version --short 2>/dev/null | head -1 \
        | grep -oE '^[0-9]+\.[0-9]+' || echo ""
}

version_ge() {
    # version_ge "2.20" "2.20" → true
    printf '%s\n%s\n' "$2" "$1" | sort -V -C
}

# IP local (LAN) — la del NIC de la ruta default
get_local_ip() {
    ip -4 route get 8.8.8.8 2>/dev/null \
        | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") {print $(i+1); exit}}' \
        || true
}

# IP pública (consulta a un servicio externo, con timeout)
get_public_ip() {
    curl -sS --max-time 5 https://api.ipify.org 2>/dev/null \
        || curl -sS --max-time 5 https://ifconfig.me 2>/dev/null \
        || echo ""
}

# Capacidad estimada del host basada en RTP range
estimate_capacity() {
    local range=$((RTP_PORT_END - RTP_PORT_START + 1))
    local max_calls=$((range / 2))
    printf '%d' "$max_calls"
}

print_banner() {
    cat <<'EOF'
╔══════════════════════════════════════════════════════════════════════╗
║          Verbara Platform — Quickstart SMB on-premise                ║
║                                                                      ║
║   Pre-flight checks + arranque del reference deployment.             ║
║   Cubre: tooling, RAM/CPU/disk, puertos SIP+RTP, NAT, firewall.      ║
╚══════════════════════════════════════════════════════════════════════╝
EOF
}

# ─────────────────────────────────────────────────────────────────────────
# Step 0 — Banner + detección de host
# ─────────────────────────────────────────────────────────────────────────
print_banner
detect_distro
echo
echo "  Repositorio:    ${REPO_ROOT}"
echo "  Compose file:   ${COMPOSE_FILE}"
echo "  Env file:       ${ENV_FILE}"
echo "  Distro:         ${DISTRO_NAME}"
echo "  Firewall esperado: ${FW_TOOL}"
echo "  RTP range:      ${RTP_PORT_START}-${RTP_PORT_END} ($(estimate_capacity) calls concurrent)"
echo "  Modo:           $($CHECK_ONLY && echo 'check-only (no arrancará el stack)' || echo 'check + up')"

# ─────────────────────────────────────────────────────────────────────────
# Step 1 — Tooling
# ─────────────────────────────────────────────────────────────────────────
step "1/11  Verificando tooling instalado"

require_cmd() {
    local cmd="$1" pkg_hint="${2:-$1}"
    if command -v "$cmd" >/dev/null 2>&1; then
        ok "$cmd disponible ($(command -v "$cmd"))"
    else
        fail "$cmd no encontrado"
        hint "Instalá con tu package manager. Ej: 'sudo apt install $pkg_hint' (Debian/Ubuntu) o 'sudo dnf install $pkg_hint' (Rocky/Alma)."
    fi
}

require_cmd docker docker.io
require_cmd curl
require_cmd jq
require_cmd openssl
require_cmd awk gawk
if ! command -v ss >/dev/null 2>&1 && ! command -v netstat >/dev/null 2>&1; then
    fail "ss y netstat no encontrados — instalá iproute2 (ss) o net-tools (netstat)"
fi

# Docker daemon corre
if docker info >/dev/null 2>&1; then
    ok "Docker daemon respondiendo"
else
    fail "Docker daemon no responde. ¿Está corriendo? ¿Tu usuario está en el grupo 'docker'?"
    hint "sudo systemctl start docker  &&  sudo usermod -aG docker \$USER  &&  (re-loguear)"
fi

# Docker Compose v2.20+
COMPOSE_VER=$(docker_compose_version)
if [ -z "$COMPOSE_VER" ]; then
    fail "docker compose v2 no encontrado (el plugin no está instalado)"
    hint "Debian/Ubuntu: sudo apt install docker-compose-plugin"
    hint "Rocky/Alma:    sudo dnf install docker-compose-plugin"
elif ! version_ge "$COMPOSE_VER" "2.20"; then
    fail "docker compose v$COMPOSE_VER — se requiere v2.20+. Actualizá el plugin."
else
    ok "docker compose v$COMPOSE_VER (≥ 2.20)"
fi

# Aviso si está corriendo bajo Docker Desktop (Mac/Win) → no compatible
if docker info 2>/dev/null | grep -qi 'Docker Desktop'; then
    fail "Docker Desktop detectado. El reference SMB requiere Docker nativo Linux."
    hint "network_mode: host de Asterisk no funciona correctamente bajo la VM intermediaria"
    hint "de Docker Desktop. Usá un host Linux (Debian/Ubuntu/Rocky/Alma)."
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 2 — Recursos del host (RAM / CPU / disco)
# ─────────────────────────────────────────────────────────────────────────
step "2/11  Recursos del host"

# RAM disponible
TOTAL_RAM_KB=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo 2>/dev/null || echo 0)
TOTAL_RAM_GB=$(( TOTAL_RAM_KB / 1024 / 1024 ))
if [ "$TOTAL_RAM_GB" -lt "$MIN_RAM_GB" ]; then
    fail "RAM total ${TOTAL_RAM_GB} GB — mínimo recomendado ${MIN_RAM_GB} GB (SMB Lite)"
    hint "150 calls (SMB Standard) requiere 32 GB; 300 calls (SMB Plus) requiere 64 GB"
else
    ok "RAM total ${TOTAL_RAM_GB} GB"
    if [ "$TOTAL_RAM_GB" -lt 32 ]; then
        info "Tier detectado: SMB Lite (~50 calls)"
    elif [ "$TOTAL_RAM_GB" -lt 64 ]; then
        info "Tier detectado: SMB Standard (~150 calls)"
    else
        info "Tier detectado: SMB Plus (~300 calls)"
    fi
fi

# vCPUs
CPU_COUNT=$(nproc 2>/dev/null || echo 1)
if [ "$CPU_COUNT" -lt "$MIN_CPU_CORES" ]; then
    fail "vCPUs ${CPU_COUNT} — mínimo recomendado ${MIN_CPU_CORES}"
else
    ok "vCPUs ${CPU_COUNT}"
fi

# Disco libre en /var/lib/docker (o /, si no existe)
DOCKER_ROOT="$(docker info 2>/dev/null | awk '/Docker Root Dir/ {print $4}' || true)"
DOCKER_ROOT="${DOCKER_ROOT:-/var/lib/docker}"
DISK_TARGET="$DOCKER_ROOT"
[ -d "$DISK_TARGET" ] || DISK_TARGET="/"
DISK_FREE_GB=$(df -BG --output=avail "$DISK_TARGET" 2>/dev/null | tail -1 | tr -d ' G' || echo 0)
if [ "$DISK_FREE_GB" -lt "$MIN_DISK_GB" ]; then
    fail "Disco libre en ${DISK_TARGET}: ${DISK_FREE_GB} GB — mínimo ${MIN_DISK_GB} GB"
    hint "Recordings G.711 = ~30 MB/hora/call. 50 agents × 4h × 22 días ≈ 132 GB/mes."
else
    ok "Disco libre en ${DISK_TARGET}: ${DISK_FREE_GB} GB"
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 3 — Puertos TCP libres
# ─────────────────────────────────────────────────────────────────────────
step "3/11  Puertos TCP del host"

TCP_TO_CHECK=("${TCP_PORTS_REQUIRED[@]}")
if [ "$EXPOSE_AMI" = "true" ]; then TCP_TO_CHECK+=(5038); fi

for port in "${TCP_TO_CHECK[@]}"; do
    USAGE=$(port_in_use "$port" tcp)
    if [ -n "$USAGE" ]; then
        fail "TCP/$port ocupado"
        hint "$USAGE"
        case "$port" in
            5060) hint "Otro PBX (FreeSWITCH/Kamailio/PBXact) probablemente corriendo. Apagalo o desinstalalo." ;;
            80) hint "Otro web server (apache2/nginx) en el host. Apagalo o cambiá WEB_HTTP_PORT en .env" ;;
            8088|8089) hint "Otro servicio HTTP. Cambiá ARI_HTTP_PORT/ARI_WSS_PORT en .env si no podés liberar el puerto." ;;
        esac
    else
        ok "TCP/$port libre"
    fi
done

# 443 sólo warn (TLS opt-in)
if [ -n "$(port_in_use 443 tcp)" ]; then
    warn "TCP/443 ocupado — sin importancia si NO planeás terminar TLS en este host. Si sí, liberalo."
    WARN_COUNT=$((WARN_COUNT+1))
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 4 — Puertos UDP libres (signalling)
# ─────────────────────────────────────────────────────────────────────────
step "4/11  Puertos UDP del host (signalling SIP)"

UDP_TO_CHECK=("${UDP_PORTS_REQUIRED[@]}")
if [ "$EXPOSE_IAX" = "true" ]; then UDP_TO_CHECK+=(4569); fi

for port in "${UDP_TO_CHECK[@]}"; do
    USAGE=$(port_in_use "$port" udp)
    if [ -n "$USAGE" ]; then
        fail "UDP/$port ocupado"
        hint "$USAGE"
    else
        ok "UDP/$port libre"
    fi
done

# ─────────────────────────────────────────────────────────────────────────
# Step 5 — Rango RTP libre (CRÍTICO — UDP)
# ─────────────────────────────────────────────────────────────────────────
step "5/11  Rango RTP (${RTP_PORT_START}-${RTP_PORT_END} UDP)"

# Sólo escanear si tenemos ss disponible.
# Pipeline tolerante (pipefail OFF localmente) — grep retorna 1 cuando no hay
# matches y eso es señal de éxito en este chequeo, no de error.
if command -v ss >/dev/null 2>&1; then
    set +o pipefail
    BUSY_RTP=$(ss -uln 2>/dev/null \
        | awk -v lo="$RTP_PORT_START" -v hi="$RTP_PORT_END" '
              NR>1 { match($5, /:([0-9]+)$/, m); if (m[1]+0 >= lo && m[1]+0 <= hi) print m[1] }
          ' \
        | sort -un \
        | head -5)
    set -o pipefail
    if [ -n "$BUSY_RTP" ]; then
        fail "Puertos del rango RTP ocupados:"
        echo "$BUSY_RTP" | sed 's/^/      /'
        hint "Libera estos puertos o ajustá RTP_PORT_START/END en .env (y re-ejecutá el script)."
    else
        ok "Los ${RTP_PORT_END} - ${RTP_PORT_START} + 1 = $((RTP_PORT_END - RTP_PORT_START + 1)) puertos UDP del rango RTP están libres"
        info "Capacidad estimada: $(estimate_capacity) llamadas concurrentes simultáneas"
    fi
else
    warn "ss no disponible — no se pudo verificar el rango RTP. Validá manualmente después."
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 6 — Firewall del host (hints, no fatal)
# ─────────────────────────────────────────────────────────────────────────
step "6/11  Firewall del host (informativo)"

FW_DETECTED=""

# UFW (Ubuntu)
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
    FW_DETECTED="ufw"
    info "UFW activo. Asegurate de abrir los siguientes puertos:"
    cat <<EOF
      sudo ufw allow ${SIP_UDP_PORT:-5060}/udp comment 'Verbara SIP UDP'
      sudo ufw allow ${SIP_TCP_PORT:-5060}/tcp comment 'Verbara SIP TCP'
      sudo ufw allow ${SIP_TLS_PORT:-5061}/tcp comment 'Verbara SIP TLS'
      sudo ufw allow ${ARI_HTTP_PORT:-8088}/tcp comment 'Verbara ARI HTTP'
      sudo ufw allow ${ARI_WSS_PORT:-8089}/tcp comment 'Verbara ARI WSS WebRTC'
      sudo ufw allow ${RTP_PORT_START}:${RTP_PORT_END}/udp comment 'Verbara RTP'
      sudo ufw allow 80/tcp
      # Opt — TLS at nginx:
      # sudo ufw allow 443/tcp
EOF

# firewalld (Rocky/Alma/Fedora/RHEL)
elif command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state 2>/dev/null | grep -q running; then
    FW_DETECTED="firewalld"
    info "firewalld activo. Comandos para abrir los puertos:"
    cat <<EOF
      sudo firewall-cmd --permanent --add-port=${SIP_UDP_PORT:-5060}/udp
      sudo firewall-cmd --permanent --add-port=${SIP_TCP_PORT:-5060}/tcp
      sudo firewall-cmd --permanent --add-port=${SIP_TLS_PORT:-5061}/tcp
      sudo firewall-cmd --permanent --add-port=${ARI_HTTP_PORT:-8088}/tcp
      sudo firewall-cmd --permanent --add-port=${ARI_WSS_PORT:-8089}/tcp
      sudo firewall-cmd --permanent --add-port=${RTP_PORT_START}-${RTP_PORT_END}/udp
      sudo firewall-cmd --permanent --add-service=http
      # Opt: sudo firewall-cmd --permanent --add-service=https
      sudo firewall-cmd --reload
EOF

# nftables (Debian 12 default)
elif command -v nft >/dev/null 2>&1 && nft list ruleset 2>/dev/null | grep -q 'table inet'; then
    FW_DETECTED="nftables"
    info "nftables detectado (default en Debian 12). Reglas necesarias:"
    cat <<EOF
      # Editar /etc/nftables.conf, agregar dentro de tu chain 'input':
      udp dport ${SIP_UDP_PORT:-5060}                accept   comment "Verbara SIP UDP"
      tcp dport ${SIP_TCP_PORT:-5060}                accept   comment "Verbara SIP TCP"
      tcp dport ${SIP_TLS_PORT:-5061}                accept   comment "Verbara SIP TLS"
      tcp dport ${ARI_HTTP_PORT:-8088}               accept   comment "Verbara ARI"
      tcp dport ${ARI_WSS_PORT:-8089}                accept   comment "Verbara WSS"
      udp dport ${RTP_PORT_START}-${RTP_PORT_END}    accept   comment "Verbara RTP"
      tcp dport 80                                   accept
      # Opt: tcp dport 443                                  accept
      # Recargar:
      sudo nft -f /etc/nftables.conf
EOF
else
    info "No detecté ufw/firewalld/nftables activo."
    hint "Si tu host tiene iptables crudo, abrí manualmente: 5060/udp+tcp, 5061/tcp, 8088/tcp, 8089/tcp, 80/tcp, ${RTP_PORT_START}-${RTP_PORT_END}/udp"
fi

# Cloud providers — añadir hint cuando detectamos cloud metadata
if curl -sS --max-time 1 -H 'Metadata-Flavor: Google' http://169.254.169.254/computeMetadata/v1/instance/id >/dev/null 2>&1; then
    info "Detecté metadata GCP. Adicionalmente, asegurate que la firewall GCP tenga reglas:"
    hint "gcloud compute firewall-rules create verbara-sip --allow=udp:${SIP_UDP_PORT:-5060},tcp:${SIP_TCP_PORT:-5060},tcp:${ARI_WSS_PORT:-8089},udp:${RTP_PORT_START}-${RTP_PORT_END}"
elif curl -sS --max-time 1 http://169.254.169.254/latest/meta-data/instance-id 2>/dev/null | grep -qE '^i-'; then
    info "Detecté metadata AWS (EC2). Asegurate que el Security Group tenga ingress permitido para:"
    hint "5060/udp+tcp, 5061/tcp, 8088/tcp, 8089/tcp, 80/tcp, ${RTP_PORT_START}-${RTP_PORT_END}/udp"
elif curl -sS --max-time 1 -H 'Metadata:true' 'http://169.254.169.254/metadata/instance?api-version=2021-02-01' >/dev/null 2>&1; then
    info "Detecté metadata Azure. Asegurate que el NSG permita ingress en los mismos puertos."
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 7 — NAT detection (4 escenarios)
# ─────────────────────────────────────────────────────────────────────────
step "7/11  NAT detection"

LOCAL_IP="$(get_local_ip)"
PUBLIC_IP="$(get_public_ip)"

if [ -z "$LOCAL_IP" ]; then
    warn "No pude detectar la IP LAN del host. ¿Está conectado a una red?"
    WARN_COUNT=$((WARN_COUNT+1))
    LOCAL_IP="(desconocida)"
fi

if [ -z "$PUBLIC_IP" ]; then
    warn "No pude consultar tu IP pública (sin internet outbound?). Asumiendo on-prem aislado."
    WARN_COUNT=$((WARN_COUNT+1))
    PUBLIC_IP="(desconocida)"
fi

echo "      IP LAN:     $LOCAL_IP"
echo "      IP pública: $PUBLIC_IP"

if [ "$LOCAL_IP" = "$PUBLIC_IP" ] && [ "$LOCAL_IP" != "(desconocida)" ]; then
    info "Escenario A — IP pública directa en el NIC (cloud VM o bare-metal expuesto)."
    info "EXTERNAL_IP NO es necesario (dejá vacío en .env)."
elif echo "$PUBLIC_IP" | grep -qE '^100\.(6[4-9]|[789][0-9]|1[01][0-9]|12[0-7])\.'; then
    fail "Escenario D — CGNAT detectado (rango 100.64.0.0/10)"
    hint "Tu ISP no te da IP pública dedicada. SIP inbound NO funcionará desde internet."
    hint "Opciones: pedir IP pública estática al ISP, usar VPS-proxy SIP intermedio, o usar sólo trunk outbound."
elif echo "$LOCAL_IP" | grep -qE '^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.)'; then
    info "Escenario B/C — NAT detectado (LAN privada → IP pública via router/cloud LB)."
    info "EXTERNAL_IP=$PUBLIC_IP REQUERIDO en .env (Asterisk reescribe SDP con esto)."
    hint "Si es on-prem (router doméstico/corporate): asegurate que el router haga port-forward o DMZ a $LOCAL_IP para:"
    hint "    5060/udp, 5060/tcp, 5061/tcp, ${RTP_PORT_START}-${RTP_PORT_END}/udp"
    hint "    (Si tenés DMZ activo apuntando a $LOCAL_IP, ya está cubierto)"
    hint "Si es cloud VM con private NIC + cloud-LB: configurá Security Group/NSG para los mismos puertos."
else
    warn "No pude clasificar el escenario NAT con certeza. Asumiendo NAT (es lo seguro)."
    WARN_COUNT=$((WARN_COUNT+1))
    hint "Setear EXTERNAL_IP=$PUBLIC_IP en .env si los peers externos no oyen audio."
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 8 — Bandwidth requirements (informativo)
# ─────────────────────────────────────────────────────────────────────────
step "8/11  Bandwidth requirements (informativo)"

MAX_CALLS=$(estimate_capacity)
# G.711 = 80 kbps por dirección * 2 (full-duplex) = 160 kbps por call
# Pero típicamente se mide simétrico = 80 kbps cada lado
BW_MBPS=$(( MAX_CALLS * 80 / 1000 ))

cat <<EOF
      Con RTP range ${RTP_PORT_START}-${RTP_PORT_END} podés sostener hasta ${MAX_CALLS} llamadas concurrentes G.711.
      Eso requiere ~${BW_MBPS} Mbps SIMÉTRICOS (bidireccional, p99 < 150 ms one-way).

      Si tu WAN es asimétrica (ej. 100/20 Mbps) el límite real lo marca el lado más chico.
      Si tu trunk SIP soporta Opus, podés bajar el bitrate (24-64 kbps adaptive) — pero
      sólo aplica para llamadas WebRTC ↔ trunk Opus negociadas directamente (sin
      transcoding al backend). Trunks PSTN típicos sólo aceptan G.711 — no se gana nada.

EOF
info "Validá tu WAN con: speedtest-cli  o  iperf3 -c <speedtest-host>"

# ─────────────────────────────────────────────────────────────────────────
# Step 9 — .env file existe + completo
# ─────────────────────────────────────────────────────────────────────────
step "9/11  Archivo .env"

if [ ! -f "$ENV_FILE" ]; then
    if [ ! -f "$ENV_FILE_EXAMPLE" ]; then
        fail ".env.example no encontrado en $ENV_FILE_EXAMPLE"
        fatal "Reposorio inconsistente — no debería pasar. Re-cloná el repo."
    fi
    info ".env.reference-smb no existe. Copiando desde .example..."
    cp "$ENV_FILE_EXAMPLE" "$ENV_FILE"
    ok ".env.reference-smb creado en $ENV_FILE"

    if [ "$SKIP_EDIT" = "false" ] && [ "$CHECK_ONLY" = "false" ]; then
        echo
        info "DEBÉS editarlo AHORA. Cambiá al menos:"
        hint "POSTGRES_PASSWORD, JWT_SIGNING_KEY, SERVICE_KEY, AMI_PASSWORD, ARI_PASSWORD, EXTERNAL_IP"
        echo
        read -rp "  Presioná ENTER para abrirlo en ${EDITOR:-nano}..." _
        "${EDITOR:-nano}" "$ENV_FILE"
    else
        info "Edítá manualmente: $ENV_FILE  (--skip-edit o --check-only activos)"
    fi
else
    ok ".env.reference-smb existe"
fi

# Validar que las vars críticas no sean los placeholders
REQUIRED_VARS=(POSTGRES_PASSWORD JWT_SIGNING_KEY SERVICE_KEY AMI_PASSWORD ARI_PASSWORD)
PLACEHOLDER_REGEX='^CHANGE_ME'

for var in "${REQUIRED_VARS[@]}"; do
    VAL=$(grep -E "^${var}=" "$ENV_FILE" 2>/dev/null | head -1 | cut -d'=' -f2- || echo "")
    if [ -z "$VAL" ]; then
        fail ".env: $var está vacío"
    elif echo "$VAL" | grep -qE "$PLACEHOLDER_REGEX"; then
        fail ".env: $var sigue con el placeholder CHANGE_ME_..."
        hint "Edítalo con: openssl rand -base64 32  (o 64 para JWT_SIGNING_KEY)"
    else
        ok ".env: $var configurada"
    fi
done

# ─────────────────────────────────────────────────────────────────────────
# Step 10 — Resumen pre-flight + abort si hay fatales
# ─────────────────────────────────────────────────────────────────────────
step "10/11 Resumen pre-flight"

if [ "$FATAL_COUNT" -gt 0 ]; then
    fail "$FATAL_COUNT fallos críticos detectados. Resolvelos antes de continuar."
    exit 1
elif [ "$WARN_COUNT" -gt 0 ]; then
    warn "$WARN_COUNT warnings — revisalos pero no son bloqueantes."
fi
ok "Pre-flight verde. Host listo para correr el stack."

if [ "$CHECK_ONLY" = "true" ]; then
    echo
    info "Modo --check-only — no arrancando el stack. Cuando estés listo:"
    hint "  bash scripts/quickstart-smb.sh"
    exit 0
fi

# ─────────────────────────────────────────────────────────────────────────
# Step 11 — Pull + up + healthcheck
# ─────────────────────────────────────────────────────────────────────────
step "11/11 Pull + arranque del stack"

cd "$REPO_ROOT"
info "Pulling images (puede tomar 2-5 min en la primera vez)..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" pull 2>&1 | tail -20

info "Levantando servicios..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --wait --wait-timeout 300 2>&1 | tail -30

# Esperar a que platform-api responda en /health/ready
info "Esperando que Platform.Api responda en /health/ready (timeout 5 min)..."
API_PORT_VAL=$(grep -E '^API_PORT=' "$ENV_FILE" | cut -d'=' -f2 | head -1)
API_PORT_VAL="${API_PORT_VAL:-5000}"
DEADLINE=$(($(date +%s) + 300))
while [ "$(date +%s)" -lt "$DEADLINE" ]; do
    if curl -sf "http://localhost:${API_PORT_VAL}/health/ready" >/dev/null 2>&1; then
        ok "Platform.Api responde en :${API_PORT_VAL}/health/ready"
        break
    fi
    sleep 5
done

if ! curl -sf "http://localhost:${API_PORT_VAL}/health/ready" >/dev/null 2>&1; then
    fail "Platform.Api no respondió tras 5 min. Revisá logs:"
    hint "docker compose -f docker/docker-compose.reference-smb.yml --env-file docker/.env.reference-smb logs platform-api"
    exit 1
fi

# ─────────────────────────────────────────────────────────────────────────
# Resumen final
# ─────────────────────────────────────────────────────────────────────────
echo
echo "${C_BOLD}${C_GREEN}╔══════════════════════════════════════════════════════════════════════╗${C_RESET}"
echo "${C_BOLD}${C_GREEN}║                  ✓  STACK VERBARA ARRIBA                             ║${C_RESET}"
echo "${C_BOLD}${C_GREEN}╚══════════════════════════════════════════════════════════════════════╝${C_RESET}"
echo
echo "  ${C_BOLD}URLs:${C_RESET}"
echo "    Web UI:         http://${LOCAL_IP}:${WEB_HTTP_PORT:-80}/"
echo "    API health:     http://${LOCAL_IP}:${API_PORT_VAL}/health/ready"
echo "    API swagger:    http://${LOCAL_IP}:${API_PORT_VAL}/scalar"
echo "    Asterisk ARI:   http://${LOCAL_IP}:${ARI_HTTP_PORT:-8088}/ari"
echo "    WebRTC WSS:     wss://${LOCAL_IP}:${ARI_WSS_PORT:-8089}/asterisk/ws"
echo
echo "  ${C_BOLD}Siguiente paso — setup inicial:${C_RESET}"
echo "    Abrí http://${LOCAL_IP}:${WEB_HTTP_PORT:-80}/ en un browser y completá el wizard:"
echo "      1. Crear admin platform"
echo "      2. Crear primer tenant"
echo "      3. Crear primer agente"
echo "      4. Crear primera queue"
echo "    Manual: docs/manuales/smb/03-setup-inicial.md"
echo
echo "  ${C_BOLD}Capacidad declarada con esta config:${C_RESET}"
echo "    • $(estimate_capacity) llamadas concurrentes simultáneas"
echo "    • Tier hardware detectado: ~${TOTAL_RAM_GB} GB RAM, ${CPU_COUNT} vCPU"
echo
echo "  ${C_BOLD}Detener stack:${C_RESET}"
echo "    docker compose -f docker/docker-compose.reference-smb.yml \\"
echo "                   --env-file docker/.env.reference-smb down"
echo
