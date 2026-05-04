# Verbara Platform

> Backend for the **Verbara** open-core contact-center platform.
> Repository name (`Asterisk.Platform`) is transitional — the project is rebranding to **Verbara** ([ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md)). Repo rename to `verbara-platform` will happen as part of the coordinated technical rebrand track.

Composition-root host + REST API for an omnichannel contact center built on the
Asterisk PBX (Sangoma/Digium) + .NET 10 Native AOT. Consumes [Verbara Sdk](https://github.com/Harol-Reina/Asterisk.Sdk)
(MIT, currently `Asterisk.Sdk`) and [Verbara Sdk Pro](https://github.com/Harol-Reina/Asterisk.Sdk.Pro)
(commercial, currently `Asterisk.Sdk.Pro`) via NuGet. Pairs with
[Verbara Web](https://github.com/Harol-Reina/Asterisk.Platform.Web) (React 19
SPA, currently `Asterisk.Platform.Web`) for the operator UI.

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
| **Verbara Sdk** (currently `Asterisk.Sdk`) | MIT | Telephony primitives (AMI/ARI/SIP wrappers) — community attractor |
| **Verbara Web** (currently `Asterisk.Platform.Web`) | Apache 2.0 | Frontend UI (admin / agent / analytics / operations) |
| **Verbara Platform** (this repository) | **Apache 2.0** | Backend application — full contact-center engine |
| **Verbara Sdk Pro** (currently `Asterisk.Sdk.Pro`) | Commercial | Enterprise overlays (multi-tenant, analytics, cluster, licensing) |

**Why Apache 2.0 + commercial Pro:** the engineering moat is the runtime ECDSA license-key validation in `Pro.Licensing` (`LicenseGateMiddleware`), not source-license restrictions. Apache maximizes adoption and trial-to-Pro conversion. See [ADR-0016](docs/decisions/0016-license-and-rebrand-to-verbara.md) for the full rationale (license decision + rebrand to Verbara).

**Pro is runtime-required for production:** without a valid Pro license key, the `LicenseGateMiddleware` rejects requests requiring multi-tenant, advanced analytics, cluster mode, and licensed plan features. Self-host single-tenant evaluation is supported under Apache 2.0 alone.

**Trademark note:** "Asterisk" is a registered trademark of **Sangoma Technologies / Digium** and refers to the Asterisk PBX product. This project (which builds *on top of* Asterisk PBX as a runtime dependency) is rebranding to **Verbara** to avoid trademark conflict. Repository names beginning with `Asterisk.` are transitional and will be renamed to `verbara-*` as part of the coordinated rebrand track.
