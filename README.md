# Asterisk.Platform

Composition-root host + REST API for an omnichannel contact center built on
Asterisk + .NET 10 Native AOT. Consumes [Asterisk.Sdk](https://github.com/Harol-Reina/Asterisk.Sdk)
(MIT) and [Asterisk.Sdk.Pro](https://github.com/Harol-Reina/Asterisk.Sdk.Pro)
(commercial) via NuGet. Pairs with [Asterisk.Platform.Web](https://github.com/Harol-Reina/Asterisk.Platform.Web)
(React 19 SPA) for the operator UI.

## Quick start

New here? Follow [Getting Started](docs/getting-started.md) (10 minutes from
clone to running tenant). Then [first-deploy.md](docs/operations/first-deploy.md)
makes your first call (30 min), and [first-realistic-demo.md](docs/operations/first-realistic-demo.md)
walks the full multi-tenant + R4/R5 feature set (~1 hour).

## What's in the box

- **`/api/v1/`** — 59 endpoint groups (RBAC + JWT + API key + OIDC SSO, MFA, multi-tenant).
- **11 omnichannel connectors** — WhatsApp, SMS, WebChat, Email, Telegram, Messenger, Instagram, Video, Twitter, RCS, Voice.
- **Pro pipelines** — Dialer, EventStore, Analytics (real-time wallboard), CallAnalytics, AgentAssist, Cluster, Realtime config provisioning.
- **Native AOT** — fast cold start, no reflection, `[JsonSerializable]` everywhere.
- **Operations** — `/health` + `/health/ready` + `/metrics` (Prometheus), distributed tracing (OpenTelemetry, 21 ActivitySources across SDK + Pro), audit log + retention admin + cluster drain UI.

## Repository layout

| Path | What's there |
|---|---|
| `src/` | Platform packages (Api, Identity, Conversations, Queues, Channels.*, Bot, Flows, Audit, Billing, Renderer, Mail, Storage.Postgres, etc.) |
| `tests/` | Unit + integration test assemblies |
| `docs/` | Specs, ADRs, plans, operations runbooks (incl. the [getting-started guide](docs/getting-started.md)) |
| `docker/` | `docker-compose.full.yml` (one-command stack) + Asterisk config + demo seed |
| `scripts/` | Operational helpers (load test, ZAP scan, RBAC reseed) |

## License

Open-core. The base SDK is MIT. Pro features (commercial) require a
license key validated by `Pro.Licensing` (ECDSA). See
[docs/](docs/) for ADRs and architecture notes.
