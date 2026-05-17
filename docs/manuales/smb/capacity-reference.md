# Capacity Reference — Verbara Platform SMB

> **Para qué:** tabla de referencia rápida — dado X agentes telefónicos y Y canales activos, qué hardware necesitás y qué tunings aplicar.
> **Audiencia:** equipo de ventas pre-deploy + operador eligiendo tier.

## Tier matrix (resumen)

| Tier | vCPU | RAM | Disco SSD | WAN simétrica | Calls G.711 | Calls Opus↔G.711 | Agentes WebRTC | WebChat sessions concurrentes | Email msg/hora |
|---|---|---|---|---|---|---|---|---|---|
| **SMB Lite** | 4 | 16 GB | 100 GB | 25 Mbps | 50 | 10 | 50 | 200 | 500 |
| **SMB Standard** | 8 | 32 GB | 250 GB | 50 Mbps | 150 | 30 | 150 | 600 | 1500 |
| **SMB Plus** | 16 | 64 GB | 500 GB | 100 Mbps | 300 | 60 | 300 | 1200 | 3000 |
| **Enterprise** (K8s, fuera de scope SMB) | múltiples nodes | — | — | — | 1000+ | — | — | — | — |

> Los números asumen el peor caso (todas las llamadas en simultáneo en codec G.711 passthrough). Para el caso normal mixto (50/50 inbound+outbound + algunas en pausa) la capacidad real es 1.5-2× la declarada.

## Bottlenecks principales

```
┌───────────────────────────────────────────────────────────────┐
│ Bottleneck                  │ Cuándo se manifiesta            │
├─────────────────────────────┼─────────────────────────────────┤
│ RTP ports                   │ Llegás al ceiling = range/2     │
│ CPU Asterisk                │ G.711: 2%/call. Opus tx: 10%    │
│ CPU transcoding             │ Trunk G.711 + agentes Opus      │
│ Postgres pool exhaustion    │ Burst de 100+ logins simultánea │
│ WAN bandwidth               │ Bidir G.711 ≈ 80 kbps/call      │
│ Disco recordings            │ 30 MB/hora/call con G.711       │
└─────────────────────────────────────────────────────────────────┘
```

## Tuning por tier

Defaults en el `.env.reference-smb` están calibrados para **SMB Standard**. Para los otros:

### SMB Lite (downsizing)

```env
RTP_PORT_END=20200                          # 100 calls max
ASTERISK_CPU_LIMIT=4.0
ASTERISK_MEM_LIMIT=4G
PLATFORM_API_CPU_LIMIT=2.0
PLATFORM_API_MEM_LIMIT=1500M
POSTGRES_CPU_LIMIT=2.0
POSTGRES_MEM_LIMIT=2G
PG_MAX_CONNECTIONS=100
PG_SHARED_BUFFERS=512MB
PG_EFFECTIVE_CACHE_SIZE=2GB
PG_POOL_MAX=10
```

### SMB Standard (default — no cambiar)

Ver `.env.reference-smb.example`.

### SMB Plus (upsizing)

```env
RTP_PORT_END=20600                          # 300 calls max
ASTERISK_CPU_LIMIT=12.0
ASTERISK_MEM_LIMIT=16G
PLATFORM_API_CPU_LIMIT=8.0
PLATFORM_API_MEM_LIMIT=6G
POSTGRES_CPU_LIMIT=4.0
POSTGRES_MEM_LIMIT=8G
PG_MAX_CONNECTIONS=300
PG_SHARED_BUFFERS=2GB
PG_EFFECTIVE_CACHE_SIZE=8GB
PG_POOL_MAX=20
```

## Storage growth — recordings

| Codec | Bitrate audio | MB/hora | GB/mes (50 agentes × 4 h/día × 22 días) |
|---|---|---|---|
| G.711 ulaw/alaw mono | 64 kbps | ~30 | **132 GB** |
| G.711 stereo | 128 kbps | ~60 | 264 GB |
| Opus 24 kbps mono | 24 kbps | ~12 | 53 GB |
| Opus 48 kbps stereo | 48 kbps | ~22 | 97 GB |

**Recomendación:** ratio típico SMB:
- Recordings retention: 30-90 días en local + S3 cold para >90 días.
- Si retención legal del cliente exige 3+ años → S3/MinIO `--profile s3` mandatorio.

## Storage growth — DB

| Tabla | Tasa de crecimiento | GB/mes típico SMB |
|---|---|---|
| `audit_log` | ~5 KB/event, miles/día | 5-15 GB |
| `cdr` (call detail records) | ~1 KB/call | 1-5 GB |
| `messages` (cualquier canal) | 2-10 KB/msg | 3-20 GB |
| `conversations` | ~2 KB/conv | < 1 GB |
| `pg_stat_*` / WAL | — | 5-20 GB churn |

Total: **15-60 GB/mes** sin retention sweepers. Con sweepers en `/admin/tenant-settings → Retention`, esto se estabiliza después del primer ciclo.

## Bandwidth WAN — cálculo manual

Formula: `concurrent_calls × bitrate × 2 (bidireccional) × 1.15 (RTP+UDP+IP headers + retransmits)`

| Calls | G.711 | Opus 32 kbps | Opus 48 kbps |
|---|---|---|---|
| 10 | 1.8 Mbps | 0.7 Mbps | 1.1 Mbps |
| 50 | 9 Mbps | 3.7 Mbps | 5.5 Mbps |
| 100 | 18 Mbps | 7.4 Mbps | 11 Mbps |
| 150 | 28 Mbps | 11 Mbps | 17 Mbps |
| 200 | 37 Mbps | 14.7 Mbps | 22 Mbps |
| 300 | 55 Mbps | 22 Mbps | 33 Mbps |

> **Latencia tolerable:** < 150 ms one-way para G.711. WebRTC con Opus tolera hasta 200 ms. Más de 300 ms genera awkward conversations independiente del codec.

## ¿Cuándo migrar de SMB a Enterprise (K8s)?

Indicadores de que ya no entrás en SMB Plus:

- **> 300 calls concurrentes** sostenidas (no pico).
- **Múltiples DCs / regiones** para active-active.
- **Resilencia tier 3+**: zero-downtime upgrades, multi-zone failover.
- **Aislamiento por tenant** crítico (regulatorio): cada tenant en pod separado.
- **Carga predecible de picos** que requiere autoscaling horizontal.

En esos casos → manual K8s (Fase 2, separate doc).

## Costos cloud orientativos (referencia 2026)

Para clientes que prefieren cloud VM en lugar de servidor físico:

| Tier | AWS EC2 | Azure VM | GCP CE | Hetzner | DigitalOcean |
|---|---|---|---|---|---|
| SMB Lite | t3.xlarge | D4s_v5 | n2-standard-4 | CCX13 | s-4vcpu-16gb |
| precio mensual | ~$120 USD | ~$140 | ~$130 | ~$25 EUR | ~$80 |
| SMB Standard | m5.2xlarge | D8s_v5 | n2-standard-8 | CCX23 | g-8vcpu-32gb |
| precio mensual | ~$280 | ~$340 | ~$260 | ~$50 | ~$200 |
| SMB Plus | m5.4xlarge | D16s_v5 | n2-standard-16 | CCX33 | g-16vcpu-64gb |
| precio mensual | ~$560 | ~$680 | ~$520 | ~$100 | ~$400 |

**Costos adicionales NO incluidos arriba:**
- Bandwidth saliente (cloud cobra ~$0.08-0.12/GB out → ~$50-150/mes para 300 calls 24/7).
- Backup storage (~$0.02/GB S3 → $5-20/mes según retención).
- DNS / DDoS protection / etc.

**Hetzner típicamente es 5-10× más barato** que AWS/Azure/GCP para este perfil de carga. Para SMB on-prem-replacement, considerar Hetzner Cloud o DigitalOcean.

## Memorias por componente (mediciones reales del lab Verbara D-L 24h soak)

Mediciones tomadas durante el soak D-L 2026-04-30 (24h × VU=500 × ~11k RPS):

| Container | Memoria sostenida | Memoria pico | CPU sostenido |
|---|---|---|---|
| platform-api (1 replica) | 800 MB | 1.2 GB | 60-80 % de 4 cores |
| asterisk (idle) | 200 MB | — | < 5 % |
| asterisk (100 calls G.711) | 1.5 GB | 1.8 GB | 200 % (2 cores) |
| postgres | 1 GB | 1.4 GB | 30-50 % |
| redis (con identity-redis) | 50 MB | 80 MB | < 5 % |
| web (nginx) | 30 MB | 40 MB | < 1 % |
| renderer | 200 MB | 350 MB | < 10 % salvo cuando hace PDF |
| mail | 150 MB | 250 MB | < 5 % salvo durante IMAP poll |

**Suma de bases (sin calls activas):** ~2.5 GB → SMB Lite (16 GB) tiene 13.5 GB de headroom para 50 calls × 30 MB + buffers.

## Pre-deployment capacity sizing — script

Verbara incluye `scripts/capacity-calc.sh` que toma inputs (#agentes esperados, modo codec, retention recordings) y devuelve el tier recomendado + ajustes específicos al `.env`. Ejecutar antes de comprar/aprovisionar hardware:

```bash
$ bash scripts/capacity-calc.sh \
    --agents 120 \
    --codec g711 \
    --recordings-days 90 \
    --inbound-pct 60 \
    --outbound-pct 40

→ Tier recomendado: SMB Standard
  • 8 vCPU / 32 GB RAM / 250 GB SSD
  • RTP range: 20000-20400 (200 calls peak headroom)
  • WAN simétrica: 50 Mbps
  • Storage anual recordings: ~1.6 TB
    → habilitar S3 profile + retention 30 días en local

Variables .env propuestas:
  RTP_PORT_END=20400
  ASTERISK_CPU_LIMIT=6.0
  ASTERISK_MEM_LIMIT=8G
  PLATFORM_API_CPU_LIMIT=4.0
  PG_SHARED_BUFFERS=1GB
```
