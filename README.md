# Verbara Platform

> Backend for the **Verbara** open-core contact-center platform.
> Formerly `Verbara.Platform` — rebranded to **Verbara** per [ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md).
>
> **Visibility status (2026-05-08):** This repository is currently **private**. The Apache 2.0 license has been chosen (see [ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md)) with a planned transition to public when all triggers in [ADR-0018](docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) are met. Tier 0 (Community) self-host becomes available at that time.

Composition-root host + REST API for an omnichannel contact center built on the
Asterisk PBX (Sangoma/Digium) + .NET 10 Native AOT. Consumes [Verbara Sdk](https://github.com/verbara/Verbara.Sdk)
(MIT) and [Verbara Sdk Pro](https://github.com/verbara/Verbara.Sdk.Pro)
(commercial) via NuGet. Pairs with
[Verbara Web](https://github.com/verbara/Verbara.Platform.Web) (React 19
SPA) for the operator UI.

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

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full contribution guide.

Quick version: branch from `main`, [Conventional Commits](https://www.conventionalcommits.org/), sign commits with DCO (`git commit -s`), and ensure `dotnet build -c Release` (with `TreatWarningsAsErrors=true`) and `dotnet test` pass before opening a PR. **Do not mock the database** in integration tests — use Testcontainers.

For security disclosure use `security@verbara.io`. For commercial licensing inquiries use `licensing@verbara.io`.

## License

This repository is licensed under the **[Apache License 2.0](LICENSE)**. See [`NOTICE`](NOTICE) for attributions.

This is the open-source backend of the **Verbara** open-core contact-center stack:

| Repository | License | Role |
|---|---|---|
| **Verbara Sdk** | MIT | Telephony primitives (AMI / AGI / ARI / Live API / Sessions / Voice AI) — community attractor |
| **Verbara Web** | Apache 2.0 | Frontend UI (admin / agent / analytics / operations) |
| **Verbara Platform** (this repository) | **Apache 2.0** | Backend application — full contact-center engine |
| **Verbara Sdk Pro** | Commercial | Enterprise overlays (multi-tenant, analytics, cluster, licensing) |

**Why Apache 2.0 + commercial Pro:** the engineering moat is the runtime ECDSA license-key validation in `Pro.Licensing` (`LicenseGateMiddleware`), not source-license restrictions. Apache maximizes adoption and trial-to-Pro conversion. See [ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md) for the full rationale (license decision + rebrand to Verbara).

**Pro is runtime-required for production:** without a valid Pro license key, the `LicenseGateMiddleware` rejects requests requiring multi-tenant, advanced analytics, cluster mode, and licensed plan features. Self-host single-tenant evaluation is supported under Apache 2.0 alone.

**Trademark note:** "Asterisk" is a registered trademark of **Sangoma Technologies / Digium** and refers to the Asterisk PBX product. This project builds *on top of* Asterisk PBX as a runtime dependency. The **Verbara** name and branding are distinct from the Asterisk PBX trademark.
