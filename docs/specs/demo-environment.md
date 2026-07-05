# Demo Environment — Verbara Platform

> **Last updated:** 2026-07-05
> **IMPORTANT:** This document MUST be updated whenever any file under `docker/demo/` is modified.
>
> **2026-07-05:** Fixed a latent bug (found by `openspec/changes/released-image-smoke`'s first
> real run against a released image, not just a signature check): `platform-api`'s
> `ConnectionStrings__*` were hard-coded to `Database=verbara`, while `.env.demo` sets
> `POSTGRES_DB=platform` (the value this doc's own Environment Variables table already
> documented) and `demo-reset.sh` seeds via `psql -d platform`. A truly fresh `demo-reset.sh`
> run (which always does `down -v` first) would create a `platform` database that platform-api
> never connected to, crashing on `DatabaseMigrationService` with `3D000: database "verbara"
> does not exist`. Connection strings now read `Database=${POSTGRES_DB:-platform}` so the two
> can never drift again.

## Overview

Self-contained Docker environment (10 services) that demonstrates a fully operational omnichannel contact center with Asterisk 22, multi-tenancy, RBAC, IVR in Spanish, PSTN simulation, and real-time analytics. Zero external dependencies — everything runs locally with a single script.

## Quick Start

```sh
cd docker/demo
chmod +x demo-reset.sh
./demo-reset.sh
```

**URLs after startup:**

| Service | URL | Notes |
|---------|-----|-------|
| Platform Web | http://localhost | Served by nginx-gateway → web container |
| Platform API | http://localhost:5000 | Direct (also proxied at `http://localhost/api/*`) |
| Realtime Hub | http://localhost/hubs/* | Proxied by nginx-gateway (no direct host port) |
| Grafana | http://localhost:3000 | |
| Prometheus | http://localhost:9090 | |
| Asterisk AMI | localhost:5038 | |
| Asterisk ARI | http://localhost:8088 | |
| Asterisk SIP/WSS | localhost:5060/8089 | UDP + TLS |

---

## Architecture

```
                ┌───────────────────────────────────────────┐
                │       nginx-gateway (:80)                 │
                │   /        → web                          │
                │   /api/*   → platform-api                 │
                │   /hubs/*  → realtime  (SignalR / WS)     │
                └────┬──────────────┬────────────┬──────────┘
                     │              │            │
              ┌──────▼───┐   ┌──────▼─────┐  ┌───▼─────────┐
              │   web    │   │platform-api│  │  realtime   │
              │ React 19 │   │  (:5000)   │  │  (:5030)    │
              │  Vite    │   │ .NET 10 AOT│  │ SignalR Hub │
              └──────────┘   └─────┬──────┘  └─────┬───────┘
                                   │ AMI/ARI       │
                                   │               │ backplane
                            ┌──────▼──────┐  ┌─────▼──────┐
                            │  asterisk   │  │   redis    │
                            │ :5038/5060  │  │   :6379    │
                            │ :8088/8089  │  └────────────┘
                            └──────┬──────┘
                                   │ Realtime tables
                            ┌──────▼──────┐
                            │  postgres   │  Platform + Pro + Realtime DB
                            │   :5432     │
                            └─────────────┘
                                   ▲
                                   │ SIP trunk
                            ┌──────┴──────┐
                            │pstn-emulator│
                            │ Asterisk 22 │
                            └─────────────┘

┌──────────────┐     ┌──────────────┐
│  prometheus  │────>│   grafana    │
│  (:9090)     │     │  (:3000)     │
└──────────────┘     └──────────────┘
```

> **ADR-0022 Phase A topology**: `nginx-gateway` is the single ingress (mirrors K8s Cilium HTTPRoute layout). `web`, `platform-api` and `realtime` are internal-only; the gateway routes `/`, `/api/*` and `/hubs/*` respectively. Multi-pod scale-out of `realtime` requires the Redis backplane (already wired here) + Pro.Cluster leader-election (Phase A.5, scaffolded in Pro v2.5.1-pro).

## Services (10)

| Service | Image | Ports | Purpose |
|---------|-------|-------|---------|
| **postgres** | postgres:18-alpine | 5432 (internal) | Single DB: Platform migrations + Realtime tables + Pro schemas + demo seed |
| **redis** | redis:8-alpine | 6379 (internal) | Session/cache store + SignalR backplane for realtime + IdentityRedis (JWT rotation, MFA throttle) |
| **asterisk** | Custom (Dockerfile.asterisk) | 5038, 5060/udp, 8088, 8089, 8180, 20000-20050/udp | Main PBX — AMI, ARI, SIP, WSS, RTP |
| **pstn-emulator** | Custom (Dockerfile.demo-pstn) | Internal only | Simulated PSTN gateway with 10 test scenarios |
| **platform-api** | Custom (Dockerfile, Native AOT) | 5000 | .NET 10 API — 34 src packages + 20 Pro packages, 77 endpoint groups |
| **realtime** | Custom (Dockerfile.realtime) | Internal only (5030) | SignalR Hub microservice (ADR-0022 Phase A); presence + Hub broadcast over Redis backplane |
| **web** | Custom (Platform.Web Dockerfile) | Internal only (80) | React 19 frontend, `VITE_DEFAULT_TENANT_ID=demo` |
| **nginx-gateway** | nginx:1.27-alpine | 80 | Single ingress fronting web + platform-api + realtime (mirrors K8s Cilium HTTPRoute) |
| **prometheus** | prom/prometheus:latest | 9090 | Metrics scraping (API + self, every 15s) |
| **grafana** | grafana/grafana:latest | 3000 | Dashboard "Contact Center" (8 panels, Spanish) |
| (volume) | pgdata | - | Persistent PostgreSQL data |

### Database Initialization Order

1. PostgreSQL starts, runs Platform migrations from `/docker-entrypoint-initdb.d/`
2. platform-api starts, Pro packages run `EnsureSchemaAsync` (creates `completed_sessions`, `interval_snapshots`, Dialer tables, etc.)
3. demo-reset.sh seeds Realtime + historical data via `psql` after all services are healthy

---

## Credentials

### Platform Admin (host tenant: `platform`)

| Field | Value |
|-------|-------|
| Email | `platform@admin.local` |
| Password | `PlatformAdmin2026!` |
| Role | Platform Admin |
| Scope | `/api/v1/management/*`, `/api/v1/setup` |
| Management API Key | Generated at setup (printed in console) |

### Demo Tenant (customer, child of `platform`)

| User | Email | Password | Role | Extension |
|------|-------|----------|------|-----------|
| Demo Admin | `admin@demo.local` | `DemoAdmin2026!` | Admin | — |
| Demo Supervisor | `supervisor@demo.local` | `DemoSupervisor2026!` | Supervisor | — |
| Maria Garcia | `maria.garcia@demo.local` | `DemoAgent2026!` | Agent | 2001 (sales) |
| Carlos Lopez | `carlos.lopez@demo.local` | `DemoAgent2026!` | Agent | 2002 (sales) |
| Ana Martinez | `ana.martinez@demo.local` | `DemoAgent2026!` | Agent | 2003 (sales) |
| Pedro Ruiz | `pedro.ruiz@demo.local` | `DemoAgent2026!` | Agent | 3001 (support) |
| Lucia Fernandez | `lucia.fernandez@demo.local` | `DemoAgent2026!` | Agent | 3002 (support) |
| Demo Agent | `demo.agent@demo.local` | `DemoAgent2026!` | Agent | 3003 (support) |

### SIP/WebRTC Credentials

| Extension | Username | Password | Transport |
|-----------|----------|----------|-----------|
| 2001 | 2001 | demo2001 | WSS (WebRTC) |
| 2002 | 2002 | demo2002 | WSS (WebRTC) |
| 2003 | 2003 | demo2003 | WSS (WebRTC) |
| 3001 | 3001 | demo3001 | WSS (WebRTC) |
| 3002 | 3002 | demo3002 | WSS (WebRTC) |
| 3003 | 3003 | demo3003 | WSS (WebRTC) |

### Infrastructure

| Service | User | Password |
|---------|------|----------|
| PostgreSQL | platform | platform_demo |
| Grafana | admin | demo |
| Grafana (anonymous) | — | — (Viewer access) |
| AMI | platform | platform_demo |
| ARI | verbara-platform | platform_demo |

---

## `demo-reset.sh` — Step by Step

| Step | Action | Details |
|------|--------|---------|
| 1 | Clean up | `docker compose down -v --remove-orphans` |
| 2 | Copy NuGet feed | Pro packages from the sibling `local-nuget-feed/` (resolved relative to the repo root; fails loud if missing) |
| 3 | Build images | `docker compose build --quiet` |
| 4 | Start Postgres | Wait for `pg_isready` |
| 5 | Start all services | `docker compose up -d` (Pro tables created by `EnsureSchemaAsync` during API DI) |
| 6 | Wait for health | 120s timeout per service (asterisk, pstn-emulator, platform-api, web, grafana) |
| 7 | Setup wizard | `POST /api/v1/setup` — since v2.6.0 creates host tenant "platform" (admin + mgmt API key) **AND** the first Customer tenant "demo" (admin `admin@demo.local`) in one call (Platform is admin-only per ADR-0027, so a Customer is mandatory) |
| 7.5 | Role assignment | Ensure platform admin user has the `platform_admin` role assigned |
| 8 | Ensure demo tenant | `POST /api/v1/management/tenants` — idempotent no-op safety net; "demo" already created in step 7 (409 swallowed on re-run) |
| 8.5 | Set tenant plans | platform=Enterprise, demo=Pro (enables feature-gated endpoints) |
| 9 | Seed via API | 1 admin + 1 supervisor + 6 agents + 2 queues + 2 bots + WebChat channel activation + team |
| 9.5 | Seed billing | Billing data via Management API (requires mgmt key, not JWT) |
| 10 | SQL seed | Asterisk Realtime (7 endpoints, 8 queues, PSTN trunk) + 50 CDRs + 48 interval snapshots |
| 11 | Warmup + Summary | Login test as demo admin + API health check + print summary |

---

## Seeded Data

### Platform Data (via API, step 9)

- **1 admin user** + **1 supervisor** + **6 agent users** (with agent profiles, extensions, skills)
- **2 queues**: Sales (ringall), Support (leastrecent)
- **WebChat channel**: Activated (no external widget connected)

### Asterisk Realtime Data (via SQL, step 10)

**File:** `sql/010_demo_asterisk_seed.sql`

| Table | Records | Details |
|-------|---------|---------|
| `ps_endpoints` | 7 | 6 WebRTC agents (opus/ulaw/alaw, WSS, RFC4733 DTMF) + 1 PSTN trunk |
| `ps_auths` | 6 | userpass auth for each agent |
| `ps_aors` | 7 | max_contacts=1, qualify_frequency=60s |
| `queues` | 8 | sales, support + 6 IVR queues (ventas-nuevos, ventas-existentes, soporte-urgente, soporte-general, facturacion, rrhh) |
| `queue_members` | 18 | 6 PJSIP agent members + 12 virtual agent members (Local/name@virtual-agent) |
| `ps_registrations` | 1 | PSTN trunk registration to pstn-emulator |

### Historical Data (via SQL, step 10)

**File:** `sql/020_demo_historical_data.sql`

| Table | Records | Distribution |
|-------|---------|-------------|
| `completed_sessions` | 50 CDRs | 60% answered, 20% abandoned, 10% busy, 10% no-answer |
| `interval_snapshots` | 48 snapshots | 30-min intervals over 24h, 2 queues (sales + support) |

**CDR distribution:** Peak hours 10-12h and 15-17h. 60% support queue, 40% sales. Talk times 20-200s for answered calls.

---

## IVR Structure (Spanish)

```
200 (Main Menu)
├── 1 → Ventas
│   ├── 1 → ventas-nuevos (queue)
│   ├── 2 → ventas-existentes (queue)
│   └── 9 → Back to main
├── 2 → Soporte
│   ├── 1 → soporte-urgente (queue)
│   ├── 2 → soporte-general (queue)
│   └── 9 → Back to main
├── 3 → Facturación
│   ├── 1 → facturacion (queue)
│   ├── 2 → facturacion (queue)
│   └── 9 → Back to main
├── 4 → RRHH (queue)
└── 9 → Repeat menu
```

Max 3 retries on invalid/timeout, then hangup.

### Virtual Agents

IVR queues use `Local/name@virtual-agent` members that play pre-recorded audio:
- Answer → agent greeting (e.g., `agent-maria`) → 8s pause → `agent-farewell` → Hangup

### Feature Codes

| Code | Function |
|------|----------|
| `*72` | Call Forward (set) |
| `*73` | Call Forward (cancel) |
| `*67` | Caller ID block |
| `*69` | Last caller redial |
| `*78` | DND on |
| `*79` | DND off |

### Other Extensions

| Extension | Function |
|-----------|----------|
| 100 | Queue shortcut: Sales |
| 101 | Queue shortcut: Support |
| 200 | IVR entry point (Spanish) |
| 700 | Parking |
| 800 | Conference bridge |
| 100X | PSTN outbound (routes to pstn-trunk) |
| 2XXX/3XXX | Stasis → Platform ARI |

---

## PSTN Emulator (10 Test Scenarios)

Dial from any registered extension to test outbound call handling:

| Extension | Scenario | Duration |
|-----------|----------|----------|
| 1001 | Normal answer + hello-world + goodbye | ~10s |
| 1002 | Busy signal | 10s |
| 1003 | Ring then hangup (no-answer) | 30s |
| 1004 | Network congestion | 10s |
| 1005 | Voicemail simulation | ~15s |
| 1006 | DTMF echo (read 4 digits, say back) | Interactive |
| 1007 | Simple IVR (press 1 or 2) | Interactive |
| 1008 | Long call (MusicOnHold) | 5 min |
| 1009 | Echo test | Indefinite |
| 1010 | Quick beep + hangup | ~1s |

---

## Grafana Dashboard

**Name:** Contact Center (Spanish)
**Access:** http://localhost:3000 (anonymous Viewer, or admin/demo)
**Theme:** Dark

| Panel | Type | Query Source |
|-------|------|-------------|
| Total Llamadas (24h) | Stat | `COUNT(*)` from `completed_sessions` |
| Contestadas | Stat | `COUNT(*)` where `final_state=1` |
| Tiempo Promedio (seg) | Stat | `AVG(talk_time_ms)/1000` |
| SLA % | Gauge | Answered within SLA / Total (thresholds: red <70%, yellow <85%, green >=85%) |
| Llamadas por Hora | Bar chart | Grouped by hour + queue_name |
| Distribución por Resultado | Pie chart | Contestada / Sin respuesta / Ocupado / Abandonada |
| Métricas por Cola | Table | Ofrecidas, Contestadas, Abandonadas, SLA%, Espera Promedio |
| Llamadas Recientes | Table | Last 20 calls (time, caller, queue, agent, duration, state) |

**Datasources:** PostgreSQL (default, direct SQL) + Prometheus (platform-api:5000 metrics)

---

## Environment Variables

**File:** `.env.demo`

| Variable | Default | Purpose |
|----------|---------|---------|
| `POSTGRES_DB` | platform | Database name |
| `POSTGRES_USER` | platform | Database user |
| `POSTGRES_PASSWORD` | platform_demo | Database password |
| `AMI_PASSWORD` | platform_demo | Asterisk Manager Interface password |
| `ARI_PASSWORD` | platform_demo | Asterisk REST Interface password |
| `EXTERNAL_IP` | (empty) | NAT traversal — set to public IP for remote WebRTC |
| `CORS_ORIGINS` | http://localhost | Allowed CORS origins for API |

### platform-api Environment (docker-compose)

| Variable | Value | Purpose |
|----------|-------|---------|
| `ASPNETCORE_ENVIRONMENT` | Production | .NET environment |
| `ConnectionStrings__Postgres` | Host=postgres;... | Main storage (activates Postgres mode) |
| `ConnectionStrings__Analytics` | Host=postgres;... | EventStore + Analytics + CallAnalytics |
| `ConnectionStrings__Dialer` | Host=postgres;... | Dialer campaign storage |
| `Asterisk__Ami__Hostname` | asterisk | AMI connection |
| `Asterisk__Ari__BaseUrl` | http://asterisk:8088 | ARI connection |
| `Asterisk__Ari__Application` | verbara-platform | ARI Stasis app name |
| `Redis__ConnectionString` | redis:6379 | Session/cache |

---

## Audio Files

### Music on Hold (`docker/demo/moh/`)

| File | Genre |
|------|-------|
| jazz/bossa-nova.wav | Jazz/Bossa Nova |
| jazz/smooth-jazz-01.wav | Smooth Jazz |
| jazz/smooth-jazz-02.wav | Smooth Jazz |
| macroform-cold_day.wav + .ulaw | Ambient |
| macroform-robot_dity.wav + .ulaw | Electronic |
| macroform-the_simplicity.wav + .ulaw | Ambient |
| manolo_camp-morning_coffee.wav + .ulaw | Chill |
| reno_project-system.wav + .ulaw | System |

### Spanish Custom Sounds (`docker/demo/sounds/es-custom/`)

12 audio files: IVR prompts (main greeting, ventas, soporte, facturacion menus) + agent greetings (maria, carlos, ana, pedro, lucia) + farewell.

---

## What WORKS

| Feature | Status | Notes |
|---------|--------|-------|
| Multi-tenancy | OK | Platform (host) + Demo (customer), hierarchy verified |
| Login + JWT + Refresh tokens | OK | Email/password, dual-scheme (JWT + API key) |
| RBAC (60 permissions, 8 templates) | OK | Admin/Supervisor/Agent roles enforced |
| Setup wizard | OK | First-boot platform initialization |
| Management API | OK | Tenant CRUD, system info, API keys |
| Admin API | OK | Users, agents, queues, channels CRUD |
| Asterisk Realtime | OK | SIP endpoints in PostgreSQL, dynamic registration |
| IVR in Spanish | OK | 3-level menu with 8 queue destinations |
| PSTN emulator | OK | 10 test scenarios (1001-1010) |
| Feature codes | OK | Call forward, DND, caller ID, last redial |
| Conference/Parking | OK | Extension 800 (conference), 700 (parking) |
| Grafana + Prometheus | OK | 8-panel dashboard with pre-seeded data |
| Historical analytics | OK | 50 CDRs + 48 interval snapshots |
| Agent state transitions | OK | Available/OnCall/OnBreak/Offline (API) |
| API versioning | OK | All endpoints under `/api/v1/`, backward-compat redirect for `/api/` |
| SSE real-time events | OK | `/api/v1/events/sse` (active when events occur) |
| Audit trail | OK | Login events, permission changes logged |
| Health/Metrics endpoints | OK | `/health` + `/metrics` |
| WebRTC endpoints | OK | 6 agents with WSS transport + Opus codec |

## What DOES NOT Work / Is Not Seeded

| Feature | Reason |
|---------|--------|
| Real PSTN calls | Only local emulator, no external SIP trunk |
| WhatsApp/SMS/Telegram/Email/Instagram/Messenger/Twitter/RCS | Require real provider credentials (Meta, Twilio, etc.) |
| WebChat widget in frontend | Channel activated in API but no embedded widget in web app |
| Dialer / Outbound campaigns | Endpoints exist, infrastructure wired, but no campaigns seeded |
| Bot / Virtual Agent (AI) | Virtual agents are pre-recorded audio, no LLM/NLU connected |
| Knowledge Base | API exists but empty |
| Agent Assist (AI coaching) | API exists but no engine configured |
| Call Analytics (sentiment/transcription) | Requires external AI engine |
| MFA / TOTP | Endpoints exist, no demo user has MFA enabled |
| OIDC SSO | Requires external IdP (Okta, Azure AD, etc.) |
| Recordings | Volume mounted but no pre-existing recordings |
| S3 / MinIO storage | Not in demo compose (only in production) |
| Surveys | API exists but no surveys created |
| Flows (DAG workflows) | Engine exists but no flows defined |
| Scheduled Reports | API exists but no reports configured |
| Clustering | Single-node, no peers |
| Skills (beyond seed) | Only "sales" and "support" skill labels on agents |

---

## Use Cases

### Good For

1. **Commercial demo** — Show login, roles, agents, queues, Grafana dashboard, IVR
2. **Frontend development** — Real API with seeded data, no mocks needed
3. **SIP/WebRTC integration testing** — Real calls between asterisk and pstn-emulator
4. **Realtime validation** — SIP endpoints created in PostgreSQL, Asterisk reads dynamically
5. **RBAC testing** — 3 distinct roles with different permission sets
6. **Multi-tenancy verification** — Platform vs Demo tenant isolation
7. **API exploration** — Fully functional REST API with auth

### Not For

1. **Production** — Hardcoded credentials, no TLS, licensing disabled
2. **Load testing** — Single-node, no tuning
3. **Omnichannel demo** — Only voice + WebChat (without widget), other channels need real providers
4. **AI/Bot demo** — No LLM connected, virtual agents are audio playback
5. **Advanced analytics** — Only mock CDRs, no real sentiment/transcription
6. **Outbound dialer demo** — Infrastructure present but no campaigns seeded

---

## File Inventory

```
docker/demo/
├── docker-compose.demo.yml          # Main orchestration (9 services)
├── demo-reset.sh                     # Full reset + seed script (11 steps)
├── .env.demo                         # Environment variables
├── Dockerfile.demo-pstn              # PSTN emulator image (Asterisk 22 + Opus)
├── entrypoint-asterisk.sh            # NAT/EXTERNAL_IP handler for main Asterisk
│
├── demo-overrides/
│   ├── extensions.conf               # IVR, feature codes, queue shortcuts, Stasis
│   ├── manager.conf                  # AMI credentials
│   ├── ari.conf                      # ARI app config
│   └── res_config_pgsql.conf         # Realtime PostgreSQL connection
│
├── asterisk-config-pstn/
│   ├── extensions.conf               # 10 PSTN test scenarios (1001-1010)
│   ├── pjsip.conf                    # SIP trunks (pbx-trunk, trunk-main)
│   ├── modules.conf                  # Module loading config
│   └── manager.conf                  # AMI credentials
│
├── certs/
│   ├── asterisk.pem                  # Self-signed SSL cert (WSS)
│   └── asterisk.key                  # SSL private key
│
├── sql/
│   ├── 010_demo_asterisk_seed.sql    # Realtime endpoints, queues, trunk
│   └── 020_demo_historical_data.sql  # 50 CDRs + 48 interval snapshots
│
├── prometheus/
│   └── prometheus.yml                # Scrape config (self + platform-api)
│
├── grafana/
│   └── provisioning/
│       ├── datasources/
│       │   └── datasources.yml       # PostgreSQL + Prometheus datasources
│       └── dashboards/
│           ├── dashboards.yml        # Dashboard provisioning config
│           └── contact-center.json   # 8-panel Spanish dashboard
│
├── moh/                              # Music on Hold (wav + ulaw)
│   ├── jazz/                         # 3 tracks
│   ├── macroform-*.wav/.ulaw         # 3 tracks (6 files)
│   ├── manolo_camp-*.wav/.ulaw       # 1 track (2 files)
│   └── reno_project-*.wav/.ulaw      # 1 track (2 files)
│
└── sounds/
    └── es-custom/                    # 12 Spanish audio files
        ├── ivr-main-greeting.*       # IVR welcome
        ├── ivr-ventas.*              # Sales submenu
        ├── ivr-soporte.*             # Support submenu
        ├── ivr-facturacion.*         # Billing submenu
        ├── agent-maria.*             # Agent greetings
        ├── agent-carlos.*
        ├── agent-ana.*
        ├── agent-pedro.*
        ├── agent-lucia.*
        └── agent-farewell.*          # Agent goodbye
```

---

## Comparison with Other Compose Files

| Aspect | demo | full | production |
|--------|------|------|------------|
| Services | 9 | 2 (asterisk + api) | 5 (postgres + redis + api + minio + metrics) |
| Monitoring | Prometheus + Grafana | None | Minimal |
| PSTN emulator | Yes | No | No |
| Web UI | Yes (port 80) | No | No |
| Seed data | Full (users, agents, queues, CDRs) | None | None |
| Recording storage | Volume mount | /recordings mount | S3 (MinIO) |
| Auth credentials | In .env.demo | In .env | From .env.production |
| Multi-tenant seed | Platform + Demo tenants | No | No |
| Purpose | Development, demos, testing | Minimal dev | Production deployment |

---

## Related: post-release functional smoke

`docker/verbara-smoke-released.sh` (openspec/changes/released-image-smoke) reuses this compose
file as its substrate, re-pinned to a released image's cosign-verified digest via
`docker/smoke/docker-compose.smoke-override.yml` — a minimal `postgres` + `redis` + `platform-api`
subset (no Asterisk/pstn-emulator/realtime/web/nginx-gateway/prometheus/grafana; the
`AsteriskAmiHealthCheck` reports Healthy in "digital-only" mode when `Asterisk:Ami:Hostname` is
unset). It is NOT part of the demo environment itself — it verifies a **released** GHCR image
actually boots and completes one real journey (`POST /api/v1/setup` → `POST /api/v1/auth/login`),
as a functional follow-up to `docker/verbara-verify-image.sh`'s signature-only check. See that
script's own header comment for usage.
