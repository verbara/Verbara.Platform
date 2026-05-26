# Manual SMB · 00 — Visión general

> **Audiencia:** SysAdmin / DevOps del cliente que va a instalar Verbara Platform en su propio servidor.
> **Tiempo de lectura:** 10 minutos.
> **Pre-requisitos:** ninguno — este es el punto de entrada del manual.

## ¿Qué es Verbara Platform?

Verbara es una plataforma omnicanal de contact center que un cliente instala en **su propia infraestructura** (on-premise o cloud VM). Soporta:

- **Voz/SIP** — agentes WebRTC en el navegador + trunks SIP externos (Twilio Elastic, carriers PSTN, etc.).
- **WebChat** — widget embebido en una página web del cliente, conversaciones asignadas a agentes.
- **Email** — canal SMTP/IMAP, threading automático, agentes responden desde la misma UI que voz.
- *(Próximos canales — WhatsApp, SMS, Telegram, Messenger, Instagram, RCS, Video, Twitter — están construidos en la plataforma pero no se documentan en esta versión del manual.)*

Todo corre en **un solo servidor** (Docker) para SMB, o en **un cluster Kubernetes on-prem** (Phase 2 — manual separado).

## Componentes que vas a instalar

```
┌──────────────────────────────────────────────────────────────────────┐
│                        Host Linux (tu servidor)                      │
│                                                                      │
│  ┌──────────────────────────────────┐   ┌────────────────────────┐   │
│  │   Asterisk PBX (network=host)    │   │   Docker bridge net    │   │
│  │   • 5060/udp+tcp SIP signalling  │   │                        │   │
│  │   • 5061/tcp     SIP TLS (opt)   │   │  ┌──────────────────┐  │   │
│  │   • 8088/tcp     ARI HTTP        │◄──┼──┤ Platform.Api     │  │   │
│  │   • 8089/tcp     WSS WebRTC      │   │  │ (composition     │  │   │
│  │   • 5038/tcp     AMI (opt)       │   │  │  root .NET 10)   │  │   │
│  │   • 20000-20200  RTP UDP         │   │  └────────┬─────────┘  │   │
│  └──────────────────────────────────┘   │           │            │   │
│                                          │  ┌────────▼─────────┐ │   │
│                                          │  │ Web (React + nx) │ │   │
│                                          │  │ :80/:443         │ │   │
│                                          │  └──────────────────┘ │   │
│                                          │                       │   │
│                                          │  ┌──────────────────┐ │   │
│                                          │  │ Postgres 18      │ │   │
│                                          │  │ (loopback 5432)  │ │   │
│                                          │  └──────────────────┘ │   │
│                                          │  ┌──────────────────┐ │   │
│                                          │  │ Redis 8 (opt)    │ │   │
│                                          │  └──────────────────┘ │   │
│                                          │  ┌──────────────────┐ │   │
│                                          │  │ Renderer (PDF)   │ │   │
│                                          │  │ Mail (SMTP/IMAP) │ │   │
│                                          │  └──────────────────┘ │   │
│                                          └────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
```

- **Asterisk PBX** corre en `network_mode: host` y reclama los puertos SIP del host. Esto evita el overhead enorme de `docker-proxy` con port-mapping para el rango RTP (200+ puertos UDP). Sin esta optimización, 300 llamadas concurrentes serían inviables.
- **Platform.Api** + **Web** + **Postgres** + **Redis** + microservicios corren en una red `bridge` de Docker.
- **Postgres** está expuesto sólo en `127.0.0.1:5432` (loopback) — invisible desde la LAN pero accesible para Asterisk (host network) y herramientas locales (psql, backups).
- **Web** se expone en el puerto 80 (HTTP) y opcionalmente 443 (HTTPS si terminás TLS en su nginx).

Para los detalles arquitecturales completos ver [docker/docker-compose.reference-smb.yml](../../../docker/docker-compose.reference-smb.yml) — el archivo está comentado in-line con cada decisión.

## Tiers de hardware

Verbara SMB se distribuye en **3 tiers** que comparten el mismo binario. Pasás de un tier a otro editando solamente `.env.reference-smb` (resource limits + RTP range):

| Tier | vCPU / RAM | Disco | Llamadas concurrentes | Agentes WebRTC | RTP range a configurar |
|---|---|---|---|---|---|
| **SMB Lite** | 4 / 16 GB | 100 GB SSD | **50** | 50 | `20000-20200` (default) |
| **SMB Standard** | 8 / 32 GB | 250 GB SSD | **150** | 150 | `20000-20400` |
| **SMB Plus** | 16 / 64 GB | 500 GB SSD | **300** | 300 | `20000-20600` |

> **Métrica de capacidad** asume codec **G.711 passthrough** (caso típico de trunks PSTN). Si tu trunk hace transcoding Opus↔G.711, la capacidad de CPU baja 5× — usá el tier inmediatamente superior. Ver [06-canal-voz-sip.md](06-canal-voz-sip.md) §Codecs.

> Si el cliente necesita >300 llamadas concurrentes, ya no es SMB — está en el tier **Enterprise** y debe migrar a Kubernetes con replicas horizontales. Manual K8s (Phase 2) cubre eso.

## Sistema operativo del host

| OS | Versión mínima | Soportado | Notas |
|---|---|---|---|
| **Debian** | 12 (bookworm) | ✅ **Primario** | Recomendado — alinea con la imagen base `.NET 10` (Microsoft usa `bookworm-slim` para `mcr.microsoft.com/dotnet/aspnet:10.0`); Asterisk se desarrolla primero en Debian |
| **Ubuntu** | 22.04 LTS / 24.04 LTS | ✅ | Equivalente a Debian, ecosistema más popular para servidores |
| **Rocky Linux** | 9 | ✅ | Para clientes que requieren RHEL-compatible |
| **AlmaLinux** | 9 | ✅ | Idem Rocky |
| **Amazon Linux** | 2023 | ✅ | Para clientes en AWS EC2 que prefieren la AMI nativa de Amazon |
| **Docker Desktop** (Mac/Win) | — | ❌ **NO** | `network_mode: host` de Asterisk no funciona correctamente — Docker Desktop usa una VM intermediaria y la "IP del host" desde un peer SIP externo es la IP de la VM, no la del laptop |
| **Windows Server** | — | ❌ | Sin soporte para `network_mode: host` correctamente para Docker Linux containers |
| **macOS server** | — | ❌ | Idem Windows Server |
| **Alpine como HOST** (no como base image) | — | ❌ | `musl libc` tiene edge cases con la ejecución de los binarios .NET 10 |

**Resumen recomendado por escenario:**
- **On-prem datacenter / oficina con servidor físico** → Debian 12 (estable, pocas actualizaciones disruptivas).
- **VM en Azure** → Ubuntu 24.04 LTS (mejor soporte de la galería Azure) o Debian 12.
- **VM en AWS** → Amazon Linux 2023 (integración nativa con CloudWatch + AWS CLI) o Ubuntu 24.04.
- **VM en GCP** → Debian 12 (default de GCP Compute Engine).
- **Cluster on-prem corporativo (RHEL-shop)** → Rocky 9 o AlmaLinux 9.

## Recorrido del manual

| # | Manual | Tiempo estimado | Pre-requisitos |
|---|---|---|---|
| 00 | [Visión general](00-vision-general.md) | 10 min | — |
| 01 | [Instalación de Docker + firewall + DNS](01-instalacion-docker.md) | 30-45 min | Servidor Linux fresco |
| 02 | [Arranque del stack](02-arranque-stack.md) | 15-20 min | Manual 01 terminado |
| 03 | [Setup inicial — admin + tenant + agente + queue](03-setup-inicial.md) | 15 min | Stack arriba |
| 04 | [Canal WebChat](04-canal-webchat.md) | 20 min | Setup inicial terminado |
| 05 | [Canal Email (SMTP + IMAP)](05-canal-email.md) | 30 min | Credenciales email del cliente |
| 06 | [Canal Voz/SIP — el más extenso](06-canal-voz-sip.md) | 60-90 min | Trunk SIP del cliente provisionado |
| 07 | [Validación end-to-end](07-validacion-e2e.md) | 30 min | Los 3 canales configurados |
| 08 | [Troubleshooting SIP — síntoma → causa → solución](08-troubleshooting-sip.md) | referencia | — |
| 99 | [Troubleshooting general (no SIP)](99-troubleshooting.md) | referencia | — |
| — | [Checklist de validación cliente (imprimible)](checklist-validacion-cliente.md) | — | — |
| — | [Capacity reference](capacity-reference.md) | — | — |

**Tiempo total esperado** para un install fresco hasta validación E2E: **3-4 horas** (asumiendo trunk SIP ya provisionado y credenciales email a mano). Si es la primera vez que el operador hace el deploy, contar 5-6 horas.

## Convenciones del manual

- **Bloques de código** que empiezan con `$` son comandos para correr en el host:
  ```bash
  $ docker compose ps
  ```
- **Bloques de código** sin `$` son archivos de configuración o salidas esperadas.
- **`{placeholders}`** entre llaves se reemplazan con tus valores reales (ej. `{tu-ip-publica}` → `200.118.42.61`).
- **Cajas con `⚠️`** son advertencias importantes — leerlas siempre.
- **Cajas con `💡`** son tips opcionales para optimización u operación avanzada.
- **Cajas con `🔒`** son consideraciones de seguridad que no son negociables en producción.

## Versionado y soporte

Esta guía aplica a:
- **Verbara.Platform.Api** `v2.5.4` (imagen `ghcr.io/verbara/platform/api:v2.5.4`, Native AOT)
- **Verbara.Platform.Realtime** `v2.5.4` (imagen `ghcr.io/verbara/platform/realtime:v2.5.4`) — microservicio SignalR Hub (ADR-0022 Phase A)
- **Verbara.Platform.Renderer** `v2.5.4` (imagen `ghcr.io/verbara/platform/renderer:v2.5.4`, Native AOT)
- **Verbara.Platform.Mail** `v2.5.4` (imagen `ghcr.io/verbara/platform/mail:v2.5.4`, Native AOT)
- **Verbara.Platform.Web** `v3.1.4-web` (imagen `ghcr.io/verbara/platform/web:v3.1.4-web`)
- **nginx** `1.27-alpine` — actúa de gateway frente a Web + Api + Realtime, sirviendo el host port 80 (refleja la topología K8s Cilium HTTPRoute)
- **Asterisk** 22 (build local desde `docker/Dockerfile.asterisk`)
- **Postgres** 18-alpine
- **Redis** 8-alpine (opt — solo necesario para multi-pod Realtime + JWT rotation pool)

Las cinco imágenes Verbara están **firmadas con cosign** (ADR-0023) — antes del primer pull, validá con cosign v3+:
```bash
$ for img in api realtime renderer mail; do \
    cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
        ghcr.io/verbara/platform/$img:v2.5.4; \
  done
$ cosign verify --key docker/cosign.pub --insecure-ignore-tlog \
    ghcr.io/verbara/platform/web:v3.1.4-web
```
> `--insecure-ignore-tlog` es **obligatorio**: el workflow de release firma con `--signing-config` + `tlog-upload=false`, así que la firma es válida offline pero no está en la transparency log de Sigstore. La clave pública `docker/cosign.pub` sigue siendo el root of trust.

## Próximo paso

→ [01-instalacion-docker.md](01-instalacion-docker.md) — instalar Docker + firewall + DNS + (opt) TLS Let's Encrypt en tu servidor.
