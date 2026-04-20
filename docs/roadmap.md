# Roadmap — Asterisk.Platform + Asterisk.Platform.Web

**Última actualización:** 2026-04-19 · **Baselines actuales:** Platform `1.8.1` · Platform.Web `1.8.0`

> **Authoritative source** — por decisión 2026-04-19, este repo es el workstream autoritativo para todo lo que cruza API + Web. Plans, specs, ADRs y research viven aquí. `Asterisk.Platform.Web` sigue siendo repo separado para código frontend, pero su planning se origina en este árbol `docs/`.

Para el roadmap **downstream** (SDK y SDK.Pro) que alimenta este stack: `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/roadmap.md`.

---

## Shipped (histórico condensado)

| Versión | Fecha | Tema | Deps |
|---|---|---|---|
| Platform 1.0.x | 2026-03 | Foundation, auth, RBAC, 11 channels | SDK ≤1.4 / Pro ≤1.0-pro |
| Platform 1.1.0 "Enterprise Ready" | 2026-03-26 | JWT+MFA+OIDC, 64 perms, 8 role templates | — |
| Platform 1.2.0 "Monetization Ready" | 2026-03-31 | Billing (metering+quotas+rate cards+invoices), Platform Admin, E2E Playwright | — |
| Platform 1.2.1 "Operations" | 2026-03-31 | DTO hardening, Server Mgmt API, Impersonation, Cluster UI | Pro 1.1.1-pro |
| Platform 1.3.0 "Integration & Compliance" | 2026-04-03 | License enforcement, OIDC SSO, GDPR, outbound webhooks | — |
| Platform 1.3.1 "Operational Maturity" | 2026-04-04 | API versioning /api/v1/, rate limiting, audit (phase 1), license gates, scheduled reports, webhook circuit breaker | — |
| Platform 1.4.0 | 2026-04-08 | Plan 31 core operations (canned responses, cases, dispositions, surveys) | — |
| Platform 1.5.0 "Production Ready" | 2026-04-09 | Plan 32 critical fixes, Web sync | — |
| Platform 1.6.0 "Production Polish" | 2026-04-11 | Subs A/C/D/E (Sub B deferred) | — |
| Platform 1.7.0 "Reseller Enablement" | 2026-04-13 | Partner portal, white-label, onboarding, impersonation | SDK 1.8.0 + Pro 1.2.0-pro |
| **Platform 1.8.0** | **2026-04-19** | **Plan 32C "Real-Time Presence" end-to-end** — PlatformHub wired, SignalR supervisor actions, frontend @microsoft/signalr 10, realtime store + hooks + E2E | **SDK 1.11.1 + Pro 1.7.2-pro** |
| **Platform 1.8.1** | **2026-04-19** | **Pro 1.8.x "Enterprise Ready" consumer bump** — `AddProResilience` + `AddProLicenseGuard` + `AddProRetention` (DryRun default) + 5 retention targets wired. 0 breaking, 0 UI change, 1,644 unit tests green. | **SDK 1.11.1 + Pro 1.8.1-pro** |

**Platform.Web** sigue la misma numeración; los hitos principales coinciden 1:1 con Platform.

[Releases GitHub](https://github.com/Harol-Reina/Asterisk.Platform/releases) · [Releases Web](https://github.com/Harol-Reina/Asterisk.Platform.Web/releases)

---

## En planificación

### Platform 1.9.x "Feature Expansion" (post-hardening)

**Precondición:** Platform 1.8.x ya consume Pro 1.8.0-pro hardened.

Sub-releases independientes alineados con los tiers de Pro 1.9.x. Cada uno ~1-3 semanas con backend API + frontend UI coordinados:

| Sub-release | Pro dep | Platform backend | Platform.Web frontend | Tamaño |
|---|---|---|---|---|
| **1.9.0 Post-Call Survey** | Pro 1.9.0-pro (CSAT runner) | `SurveyEndpoints` expanded, CSAT dashboard query | CSAT dashboard page + agent scorecard widget | S (~1 sem) |
| **1.9.1 Speech Analytics Dashboard** | Pro 1.9.x (topic trends+alerts) | `AnalyticsEndpoints` topic trends API + alert configurator | Topic trends page + supervisor alert setup UI | M (~1.5 sem) |
| **1.9.2 Callback / Virtual Queue** | Pro 1.9.x (`Pro.CallbackQueue`) | `CallbackEndpoints` + Routing integration | Queue management page (extend) + agent callback list | M (~2 sem) |
| **1.9.3 Quality Management** | Pro 1.9.x (QM workflow) | `CoachingEndpoints`, `ScorecardEndpoints` | Coaching module (net-new UI area: plans, assignments, ack flow) | M (~2 sem) |
| **1.9.4 Integration Marketplace** | Pro 1.9.x (CRM connectors) *o* Platform-owned | `IntegrationEndpoints` + connector config store | Marketplace listing + per-connector settings page | M × 3 = 3 sem |

**Open question:** ¿CRM connectors (Salesforce/HubSpot/Zendesk) los ownea Pro o Platform? Decisión pendiente. Argumentos: Pro → reutilizable en deployments sin Platform. Platform → tenant-aware + UI tightly-coupled.

### Platform 2.0.0 (arquitectónico/breaking)

**Trigger:** Pro 2.0.0-pro shippea (Cluster split-brain/Raft + WFM). Platform aprovecha para:
- **Custom domains + white-label + SaaS** — Let's Encrypt, reverse proxy config UI, custom CSS injection.
- **Multi-region data residency** — tenant-region binding, cross-region read replicas.
- **WebAuthn / Passkeys** — supplemento/reemplazo de TOTP MFA.
- **Slack/Teams notification integrations** + admin-configurable notification rules.
- **Consolidated Partner→Customer invoicing** + automated scheduled billing.
- **Self-service plan upgrade by Partners** + Partner DELETE customer endpoint (hoy suspend-only).

Sin fecha — lanza cuando Pro 2.0 esté shippeado.

---

## Deferred / backlog (sin versión asignada)

### Security & Auth
- **Trusted devices** — device fingerprinting + MFA bypass 30d (diferido de Sub C).
- **Session LastActivityAt** tracking (diferido de Sub C) — middleware hook + storage write cost.
- **Tenant session-timeout UI display** (diferido de Sub C) — fields existen backend, no expuestos en frontend.
- **Self-service admin-lockdown policies** — `DisableSelfServiceMfaChanges`, `DisableSelfServicePasswordChange` tenant flags.

### Ops & Infra
- SLA breach alerting + idle agent detection (diferido de Sprint 4).
- TenantBranding font + login bg image (diferido de Sprint 4).
- Payment gateway integration (Stripe) — fuera de v1.x.
- Notification escalation chains + AI-driven alerts + auto-resolve.
- Push notifications (mobile/PWA) + SMS notification channel.

### Platform.Web E2E testing
- Sprint 4 — Agent Workspace (~40 tests).
- Sprint 5 — Advanced Flows (~50 tests).
- Sprint 6 — Cross-Cutting (~30 tests).

---

## Principios de planificación

1. **Platform sigue a Pro.** Platform no adelanta features que requieran capacidades Pro no-shippeadas. El versionado Platform es downstream del Pro.
2. **Web y API mismo número.** Platform 1.8.0 ↔ Platform.Web 1.8.0. Desincronizar genera confusión en deploy coordinados.
3. **0 breaking changes en minors.** Same rule que Pro. Major 2.0 cuando haya breaking acumulado.
4. **Plans cross-repo aquí.** Cualquier plan que toque API + Web se origina en este repo's `docs/plans/active/`. Platform.Web repo's `docs/` es secundario.
5. **AOT-first en API, React 19 + Zustand en Web.** Sin deviations.

---

## Cadencia histórica y proyectada

- **Platform 1.0→1.8.0:** 8 minors en ~4 semanas (feature-landing agresivo, coordinado con SDK/Pro).
- **Platform 1.8.0 → 1.8.x:** esperando Pro 1.8.0-pro, estimado Q2 2026.
- **Platform 1.8.x → 1.9.x:** tras Pro 1.9.x landings, cadencia ~1-3 semanas por sub-release.
- **Platform 2.0.0:** sin fecha — detonado por Pro 2.0 + contrato enterprise que justifique SaaS push.
