#!/usr/bin/env bash
#
# scripts/capacity-calc.sh — calculadora pre-deploy de Verbara SMB.
#
# Dado un perfil de uso esperado, recomienda el tier hardware + tunings
# específicos del .env.reference-smb. Usar ANTES de comprar/aprovisionar
# el servidor para tu cliente.
#
# Uso:
#   bash scripts/capacity-calc.sh \
#     --agents 120 \
#     --codec g711 \
#     --recordings-days 90 \
#     --inbound-pct 60 \
#     --outbound-pct 40
#
# Salida:
#   • Tier recomendado (Lite / Standard / Plus)
#   • Especificaciones hardware (vCPU, RAM, disco, WAN)
#   • RTP range a configurar
#   • Storage anual estimado de recordings
#   • Variables .env propuestas (con override exacto)
#   • Costo cloud orientativo por provider
#
# El cálculo asume el peor caso (todos los agentes en llamada simultánea).
# Para SMB el factor de uso típico es 0.6 (60 % en llamada simultánea),
# pero la planificación SIEMPRE usa el peor caso.

set -euo pipefail

# Defaults
AGENTS=50
CODEC="g711"
RECORDINGS_DAYS=30
INBOUND_PCT=50
OUTBOUND_PCT=50
HOURS_PER_DAY=4
DAYS_PER_MONTH=22
WEBCHAT_AGENTS=0
EMAIL_MSG_HOUR=0

usage() {
    cat <<EOF
Usage: $0 [options]

Options:
  --agents N                Número de agentes simultáneos esperados (default 50)
  --codec g711|opus|mixed   Codec dominante (default g711)
  --recordings-days N       Días de retención local de grabaciones (default 30)
  --inbound-pct N           % de llamadas inbound (default 50)
  --outbound-pct N          % de llamadas outbound (default 50)
  --hours-per-day N         Horas promedio en llamada por agente/día (default 4)
  --days-per-month N        Días laborales/mes (default 22)
  --webchat-agents N        Agentes adicionales sólo WebChat (default 0)
  --email-msg-hour N        Emails entrantes/hora promedio (default 0)
  --help                    Esta ayuda

Ejemplo:
  $0 --agents 120 --codec g711 --recordings-days 90 \\
     --inbound-pct 60 --outbound-pct 40
EOF
    exit 0
}

while [ $# -gt 0 ]; do
    case "$1" in
        --agents) AGENTS="$2"; shift 2 ;;
        --codec) CODEC="$2"; shift 2 ;;
        --recordings-days) RECORDINGS_DAYS="$2"; shift 2 ;;
        --inbound-pct) INBOUND_PCT="$2"; shift 2 ;;
        --outbound-pct) OUTBOUND_PCT="$2"; shift 2 ;;
        --hours-per-day) HOURS_PER_DAY="$2"; shift 2 ;;
        --days-per-month) DAYS_PER_MONTH="$2"; shift 2 ;;
        --webchat-agents) WEBCHAT_AGENTS="$2"; shift 2 ;;
        --email-msg-hour) EMAIL_MSG_HOUR="$2"; shift 2 ;;
        --help|-h) usage ;;
        *) echo "Argumento desconocido: $1" >&2; exit 2 ;;
    esac
done

# Colores
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_GREEN=$'\033[0;32m'; C_BLUE=$'\033[0;34m'; C_YELLOW=$'\033[0;33m'
    C_CYAN=$'\033[0;36m'; C_BOLD=$'\033[1m'; C_RESET=$'\033[0m'
else
    C_GREEN=""; C_BLUE=""; C_YELLOW=""; C_CYAN=""; C_BOLD=""; C_RESET=""
fi

# ─────────────────────────────────────────────────────────────────────
# Cálculos
# ─────────────────────────────────────────────────────────────────────

# Caso peor: todos los agentes en llamada simultánea = peak calls
PEAK_CALLS=$AGENTS

# CPU por call según codec (porcentaje de UN core)
case "$CODEC" in
    g711)    CPU_PER_CALL=2;  CPU_NOTE="G.711 passthrough (caso típico PSTN)" ;;
    opus)    CPU_PER_CALL=5;  CPU_NOTE="Opus end-to-end (WebRTC + trunk Opus)" ;;
    mixed)   CPU_PER_CALL=10; CPU_NOTE="Opus↔G.711 transcoding (5× CPU vs G.711 puro)" ;;
    *) echo "Codec desconocido: $CODEC (válidos: g711/opus/mixed)" >&2; exit 2 ;;
esac

# Bitrate por dirección
case "$CODEC" in
    g711)  BITRATE_KBPS=80 ;;
    opus)  BITRATE_KBPS=40 ;;
    mixed) BITRATE_KBPS=80 ;;       # G.711 lado trunk + Opus lado WebRTC = G.711 manda
esac

# Tier matrix — elegir el tier que aguanta peak_calls según el bottleneck dominante
# 4 cores → 4*50%=200% disponible para Asterisk → 200/CPU_PER_CALL calls
TIER_LITE_CALLS=$(( 4 * 50 / CPU_PER_CALL ))
TIER_STD_CALLS=$(( 8 * 50 / CPU_PER_CALL ))
TIER_PLUS_CALLS=$(( 16 * 50 / CPU_PER_CALL ))

if [ "$PEAK_CALLS" -le "$TIER_LITE_CALLS" ] && [ "$PEAK_CALLS" -le 50 ]; then
    TIER="SMB Lite"
    VCPU=4; RAM_GB=16; DISK_GB=100
    WAN_MBPS=25; RTP_END=20200
    ASTERISK_CPU="4.0"; ASTERISK_MEM="4G"
    API_CPU="2.0"; API_MEM="1500M"
    PG_MEM="2G"; PG_SHARED_BUF="512MB"; PG_EFFECTIVE_CACHE="2GB"
    PG_POOL_MAX="10"
elif [ "$PEAK_CALLS" -le "$TIER_STD_CALLS" ] && [ "$PEAK_CALLS" -le 150 ]; then
    TIER="SMB Standard"
    VCPU=8; RAM_GB=32; DISK_GB=250
    WAN_MBPS=50; RTP_END=20400
    ASTERISK_CPU="6.0"; ASTERISK_MEM="8G"
    API_CPU="4.0"; API_MEM="3G"
    PG_MEM="4G"; PG_SHARED_BUF="1GB"; PG_EFFECTIVE_CACHE="4GB"
    PG_POOL_MAX="15"
elif [ "$PEAK_CALLS" -le "$TIER_PLUS_CALLS" ] && [ "$PEAK_CALLS" -le 300 ]; then
    TIER="SMB Plus"
    VCPU=16; RAM_GB=64; DISK_GB=500
    WAN_MBPS=100; RTP_END=20600
    ASTERISK_CPU="12.0"; ASTERISK_MEM="16G"
    API_CPU="8.0"; API_MEM="6G"
    PG_MEM="8G"; PG_SHARED_BUF="2GB"; PG_EFFECTIVE_CACHE="8GB"
    PG_POOL_MAX="20"
else
    TIER="ENTERPRISE (fuera de SMB)"
    VCPU="?"; RAM_GB="?"; DISK_GB="?"
fi

# Bandwidth requerido (bidireccional + 15 % overhead headers)
BW_MBPS=$(( PEAK_CALLS * BITRATE_KBPS * 2 * 115 / 1000 / 100 ))

# Storage recordings — sólo audio (recordings de WebChat/Email son negligibles)
# Asume todas las llamadas se graban (caso conservador)
TOTAL_CALL_HOURS_PER_MONTH=$(( AGENTS * HOURS_PER_DAY * DAYS_PER_MONTH ))
# G.711 mono = 480 KB/min = ~30 MB/hora. Opus ~12 MB/hora.
MB_PER_HOUR=30
[ "$CODEC" = "opus" ] && MB_PER_HOUR=12
STORAGE_MB_MONTH=$(( TOTAL_CALL_HOURS_PER_MONTH * MB_PER_HOUR ))
STORAGE_GB_MONTH=$(( STORAGE_MB_MONTH / 1024 ))
STORAGE_GB_RETENTION=$(( STORAGE_GB_MONTH * RECORDINGS_DAYS / 30 ))

# Postgres storage (rough estimate)
# Audit ~5 KB/event, conv ~2 KB, msg ~3 KB
# Conservador: 100 events/agent/hr × hours × days × 5KB
PG_GB_MONTH=$(( AGENTS * HOURS_PER_DAY * DAYS_PER_MONTH * 100 * 5 / 1024 / 1024 + 5 ))

# ─────────────────────────────────────────────────────────────────────
# Output
# ─────────────────────────────────────────────────────────────────────

cat <<EOF

${C_BOLD}${C_BLUE}╔══════════════════════════════════════════════════════════════════════╗
║          Verbara Platform — Capacity Calculator                      ║
║                                                                      ║
║   Sizing pre-deploy para SMB on-premise.                             ║
╚══════════════════════════════════════════════════════════════════════╝${C_RESET}

${C_BOLD}Inputs:${C_RESET}
  • Agentes simultáneos:    $AGENTS
  • Codec dominante:        $CODEC ($CPU_NOTE)
  • Inbound/Outbound:       ${INBOUND_PCT}% / ${OUTBOUND_PCT}%
  • Hours/día/agente:       $HOURS_PER_DAY
  • Días/mes:               $DAYS_PER_MONTH
  • Retention recordings:   $RECORDINGS_DAYS días local

${C_BOLD}${C_GREEN}→ Tier recomendado: $TIER${C_RESET}

EOF

if [ "$TIER" = "ENTERPRISE (fuera de SMB)" ]; then
    cat <<EOF
${C_YELLOW}${C_BOLD}⚠️  Tu carga excede el tier SMB Plus (300 calls max).${C_RESET}
   Necesitás migrar a Kubernetes con horizontal scaling.
   El manual SMB no cubre tu caso. Contactar al equipo Verbara para
   el manual K8s on-prem (Fase 2).
EOF
    exit 0
fi

cat <<EOF
${C_BOLD}Hardware mínimo:${C_RESET}
  ┌─────────────────────────────────────────────────────┐
  │  ${C_CYAN}vCPU:        ${C_RESET}$VCPU
  │  ${C_CYAN}RAM:         ${C_RESET}$RAM_GB GB
  │  ${C_CYAN}Disco SSD:   ${C_RESET}$DISK_GB GB (con $RECORDINGS_DAYS días retention)
  │  ${C_CYAN}WAN:         ${C_RESET}$WAN_MBPS Mbps simétrica
  │  ${C_CYAN}Bandwidth:   ${C_RESET}$BW_MBPS Mbps sostenido (peak calls)
  └─────────────────────────────────────────────────────┘

${C_BOLD}Storage estimado:${C_RESET}
  • Recordings/mes (todo grabado):     $STORAGE_GB_MONTH GB
  • Retention en disco ($RECORDINGS_DAYS días):  $STORAGE_GB_RETENTION GB
  • Postgres growth/mes:               $PG_GB_MONTH GB
  • Total disco para 1 año operación:  ~$(( STORAGE_GB_RETENTION + PG_GB_MONTH * 12 + 30 )) GB

${C_BOLD}Variables .env propuestas:${C_RESET}
${C_CYAN}# Editar docker/.env.reference-smb con estos valores:${C_RESET}
RTP_PORT_END=$RTP_END

ASTERISK_CPU_LIMIT=$ASTERISK_CPU
ASTERISK_MEM_LIMIT=$ASTERISK_MEM

PLATFORM_API_CPU_LIMIT=$API_CPU
PLATFORM_API_MEM_LIMIT=$API_MEM

POSTGRES_MEM_LIMIT=$PG_MEM
PG_SHARED_BUFFERS=$PG_SHARED_BUF
PG_EFFECTIVE_CACHE_SIZE=$PG_EFFECTIVE_CACHE
PG_POOL_MAX=$PG_POOL_MAX

EOF

# Cloud costs orientativos
case "$TIER" in
    "SMB Lite")
        AWS="~\$120 USD/mes (t3.xlarge)"
        AZURE="~\$140 (D4s_v5)"
        GCP="~\$130 (n2-standard-4)"
        HETZNER="~\$25 EUR (CCX13)"
        DO="~\$80 (s-4vcpu-16gb)"
        ;;
    "SMB Standard")
        AWS="~\$280 (m5.2xlarge)"
        AZURE="~\$340 (D8s_v5)"
        GCP="~\$260 (n2-standard-8)"
        HETZNER="~\$50 EUR (CCX23)"
        DO="~\$200 (g-8vcpu-32gb)"
        ;;
    "SMB Plus")
        AWS="~\$560 (m5.4xlarge)"
        AZURE="~\$680 (D16s_v5)"
        GCP="~\$520 (n2-standard-16)"
        HETZNER="~\$100 EUR (CCX33)"
        DO="~\$400 (g-16vcpu-64gb)"
        ;;
esac

# Bandwidth egress cost estimate (~$0.10/GB AWS)
GB_PER_MONTH=$(( BW_MBPS * 86400 * DAYS_PER_MONTH / 8 / 1024 ))
EGRESS_COST=$(( GB_PER_MONTH * 10 / 100 ))

cat <<EOF
${C_BOLD}Costo cloud orientativo (sólo compute, sin bandwidth):${C_RESET}
  • AWS EC2:      $AWS
  • Azure VM:     $AZURE
  • GCP CE:       $GCP
  • Hetzner Cloud:$HETZNER  ${C_GREEN}← típicamente más barato${C_RESET}
  • DigitalOcean: $DO

${C_BOLD}Bandwidth egress (mensual):${C_RESET}
  • ~$GB_PER_MONTH GB/mes saliendo del DC
  • AWS/Azure/GCP cobran ~\$0.08-0.12/GB → adicionar ~\$$EGRESS_COST USD
  • Hetzner: 20 TB incluidos en el tier base → \$0 adicional
  • DigitalOcean: 4-8 TB incluidos → \$0 adicional usualmente

${C_BOLD}Para producción on-prem (servidor propio):${C_RESET}
  Refurbished Dell R640 / HPE DL380 con specs equivalentes:
    • SMB Lite-equivalente:  ~\$1,500 USD una vez
    • SMB Standard:           ~\$2,500 USD
    • SMB Plus:               ~\$4,500 USD
  ROI vs cloud típicamente < 1 año si la carga es 24/7.

${C_BOLD}Siguiente paso:${C_RESET}
  1. Aprovisionar el server con las specs arriba.
  2. Seguir docs/manuales/smb/01-instalacion-docker.md.
  3. Aplicar las variables .env arriba antes del primer 'compose up'.

EOF
