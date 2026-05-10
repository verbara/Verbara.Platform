# Roadmap — Verbara.Platform + Verbara.Platform.Web

**Última actualización:** 2026-05-10 · **Baselines actuales:** Platform **`2.1.0`** · Platform.Web `3.0.1` · SDK pin `2.1.2` · Pro pin **`2.3.0-pro`** · **🎉 visibility flip EXECUTED 2026-05-10 19:04 UTC — all 7 ADR-0018 triggers GREEN; Platform + Web repos PUBLIC; first cosign-signed image live at `ghcr.io/verbara/platform/api`**

> **Authoritative source** — por decisión 2026-04-19, este repo es el workstream autoritativo para todo lo que cruza API + Web. Plans, specs, ADRs y research viven aquí. `Verbara.Platform.Web` sigue siendo repo separado para código frontend, pero su planning se origina en este árbol `docs/`.

Para el roadmap **downstream** (SDK y SDK.Pro) que alimenta este stack: `/media/Data/Source/Verbara/Verbara.Sdk.Pro/docs/roadmap.md`.

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
| Platform 1.9.0 "Secure + Current" (R3) | 2026-04-20 | Security + dependency-current foundation; opens R3 release train | SDK 1.13.x + Pro 1.9.0-pro |
| Platform 1.9.1 "Resilience Coverage" (R3b) | 2026-04-21 | Resilience policy expansion | SDK 1.13.x + Pro 1.9.x-pro |
| Platform 1.9.2 "Hardening Follow-Through" (R3c) | 2026-04-21 | JWT hardening (`jti`, DataProtection wrap, fingerprint kid, kill `?token=`, `IJtiRevocationCache`) | SDK 1.13.x + Pro 1.9.x-pro |
| Platform 1.9.3 | 2026-04-21 | Speech Analytics dashboard + Compliance Aggregations API | SDK 1.13.x + Pro 1.9.3-pro |
| Platform 1.10.0 "Production Readiness + Ops Toolkit" (R5.1) | 2026-04-22 | First R5 release; live queue metrics pipeline + AgentAssist runtime feature toggle | SDK 1.15.0 + Pro 1.12.0-pro |
| Platform 1.11.0 "Security Admin + Compliance Path" (R5.2) | 2026-04-26 | Canonical multi-tenant tenant-stamping pipeline (ADR-0002); audit endpoints; webhook DLQ admin | SDK 1.15.0 + Pro 1.13.0-pro |
| Platform 1.12.0 "Admin Completeness + R4 Closure" (R5.3) | 2026-04-26 | Strict-mode SignalR + cross-tenant validation (ADR-0005); R4 Track A declared COMPLETE | SDK 1.15.0 + Pro 1.14.0-pro |
| Platform 1.13.0 "Production Validation" (R5.4) | 2026-04-26 | Internal security audit baseline (ADR-0008) + SLO baseline + alert severity model (ADR-0009); **R5 Production Readiness Release Train DECLARED COMPLETE** | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.0 "Auth Hotpath Hardening" (AHH train opens) | 2026-04-27 | Auth perf + concurrency hardening; opens v1.14.x patch train | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.1 | 2026-04-28 | AHH empirical follow-up + multi-replica scaffold | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.2 | 2026-04-28 | AHH multi-replica unblocked + Argon2id retune + Postgres pool sizing | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.3 | 2026-04-28 | PLATFORMAPI patches: 500→409 on duplicate + `?email=` filter | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.4 | 2026-04-28 | Known-debt patches — closes [AUTH-002](security/internal-audit-2026-04.md), CFG-003 partial, MFA-007 partial | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.5 | 2026-04-28 | [ADR-0015](decisions/0015-npgsql-datasource-sharing-strategy.md) Phase 1 — Postgres connection-pool sprawl mitigation | SDK 1.15.1 + Pro 1.15.0-pro |
| Platform 1.14.6 | 2026-04-28 | [ADR-0015](decisions/0015-npgsql-datasource-sharing-strategy.md) Phase 2 — shared `NpgsqlDataSource` adoption across composition root | SDK 1.15.1 + Pro 1.16.0-pro |
| Platform 1.15.0 "Pre-v2 Foundation" | 2026-05-02 | Final pre-rebrand baseline; alignment with SDK 2.x preparation | SDK 1.15.x + Pro 1.16.0-pro |
| **Platform 2.0.0 "Verbara"** | **2026-05-05** | **Verbara rebrand** — full namespace + package rename per [ADR-0016 license + rebrand](decisions/0016-license-and-rebrand-to-verbara.md) and [ADR-0017 rebrand execution](decisions/0017-verbara-rebrand-execution.md); **Apache 2.0** license adopted; pre-rebrand artefacts archived under `pre-rebrand` tag | **SDK 2.1.0 + Pro 2.0.0-pro** |
| **Platform 2.0.1 "Trigger 3 closure"** | **2026-05-10** | **Security**: closes 2 P0 + 4 P1 findings raised in the 2026-05-09 pre-public security review (`docs/security/2026-05-09-pre-public-security-review.md`); new `TenantBoundaryValidationMiddleware`, OIDC client-secret encryption, scope-aware management API keys ([ADR-0019](decisions/0019-scope-aware-management-api-keys.md)); 35 new regression tests; threat model published; unblocks [ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) Trigger 3 (visibility-flip 6/7 GREEN) | **SDK 2.1.2 + Pro 2.2.0-pro** |
| **Platform 2.1.0 "Image-binding + visibility flip"** | **2026-05-10** | **Trigger 5 closure + repo PUBLIC**. (a) Pro v2.3.0-pro cascade — consumer adopts image-binding API surface (`LicenseGenerator.Generate(..., imageDigest)`, runtime `IMAGE_DIGEST` env-var check on startup per [Pro ADR-0011](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md)); (b) GitHub Actions `release.yml` builds + pushes to `ghcr.io/verbara/platform/api`, signs with cosign v2.5.2 (Sigstore keyless OIDC), publishes manifest digest to job summary; (c) Helm chart `infra/k8s/helm/platform` defaults `image.repository=ghcr.io/verbara/platform/{api,web}` + injects `IMAGE_DIGEST` env-var on Deployment when `api.image.digest` set; Kyverno admission policy template enforces cosign verification cluster-wide; (d) docker-compose verification toolkit (`docker-compose.verified.yml` + `verbara-verify-image.sh` cosign pre-flight script) ships paridad single-host; (e) [ADR-0019](decisions/0019-scope-aware-management-api-keys.md) deprecation warning emitted on wildcard `platform:*` API key use (back-compat through v3.0.0); (f) **first signed image published** `ghcr.io/verbara/platform/api@sha256:f82a9041dc7f26018f6b6b11addf3ddbda6a7833827434f6b8d5ca2486349902` registered in `verbara-website/data/authorized-digests.json` (commit `2e41314`); (g) **🎉 visibility flip executed 19:04 UTC** — `gh api -X PATCH repos/verbara/Verbara.Platform -f visibility=public` succeeded (coordinated with Web); secret scanning + push protection enabled (free tier post-flip); Apache 2.0 declared in repo metadata; ADR-0006 economics now operating | **SDK 2.1.2 + Pro 2.3.0-pro** |

**Platform.Web** track sigue cadencia independiente desde el rebrand (Web v3.0.x; ROADMAP COMPLETE 2026-05-09 — todas las 7 niveles cerradas).

[Releases GitHub](https://github.com/verbara/Verbara.Platform/releases) · [Releases Web](https://github.com/verbara/Verbara.Platform.Web/releases)

---

## En curso (active plans)

> Las "En planificación" originales (1.9.x sub-releases + 2.0.0 arquitectónico + v2.1.0 image-binding + visibility flip) **YA SHIPPEARON** entre 2026-04-20 y 2026-05-10 — ver tabla "Shipped" arriba. Sección reescrita 2026-05-10 (post-flip) reflejando el estado real.

### ADR-0018 visibility-flip checklist — ✅ COMPLETE 2026-05-10

7/7 GREEN. Flip ejecutado 2026-05-10 19:04 UTC sobre Platform + Platform.Web. Plan tracking en `docs/plans/completed/2026-05-08-visibility-decision-and-alignment.md`. Apache 2.0 economics ya operando; ADR-0006 funnel ahora viable.

### Post-flip follow-ups (no version-gated)

Capturados en el Status update del 2026-05-10 en [ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md). Resumen:
- **ghcr.io package visibility** — los paquetes OCI (`ghcr.io/verbara/platform/{api,web}`) heredan visibility privada por defecto; flip manual UI pendiente (GitHub no expone REST API para esto).
- **Real-customer e2e validation** — primer cliente Tier 1+ que descargue imagen + corra `verbara-verify-image.sh` cerrará la valid-loop completa.
- **Announcement** (HN "Show HN" / r/asterisk / r/devops / Twitter / ProductHunt) — deferred hasta primer customer-driven milestone.
- **Cloudflare git auto-deploy reconnect** — re-attach pipeline (operator: dashboard → Workers & Pages → verbara-website → Settings → Builds & deployments).

### Platform v2.1.x patch train (reactive)

Reservado para parches sobre v2.1.0 (security follow-ups, image-binding bug fixes, cosign workflow drift). Sin plan activo; trigger será findings reales en producción o feedback de operadores tras visibility flip.

### Platform v3.0.0 "ADR-0019 wildcard removal" (planned, breaking)

Próximo major. Removes:
- Legacy `platform:*` wildcard en management API keys (operadores deben rotar a scope-whitelists explícitos).
- Posiblemente otras deprecations acumuladas en v2.x.

Sin fecha — gated por adopción del scope-aware pattern por consumidores existentes (none today post-flip; deprecation warning ya en v2.1.0).

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

- **Platform 1.0→1.8.0** (foundation, 2026-03 → 2026-04-19): 8 minors en ~4 semanas (feature-landing agresivo, coordinado con SDK/Pro).
- **Platform 1.9.0→1.13.0** (R3 + R5 release trains, 2026-04-20 → 2026-04-26): 5 minors en ~6 días (hardening + production-validation cadence).
- **Platform 1.14.0→1.14.6** (AHH + ADR-0015 patch train, 2026-04-27 → 2026-04-28): 7 patches en 2 días (hot fixes + pool sprawl mitigation).
- **Platform 1.15.0→2.0.0** (rebrand window, 2026-05-02 → 2026-05-05): pre-rebrand foundation + Verbara cutover.
- **Platform 2.0.0→2.0.1** (post-rebrand security patch, 2026-05-05 → 2026-05-10): 1 patch closing ADR-0018 Trigger 3 P0+P1 findings.
- **Platform 2.1.0** (2026-05-10, same-day with 2.0.1): image-binding + cosign-signed image + Helm/docker-compose verification toolkit + visibility-flip event. Final trigger (#5) cerrado y flip ejecutado en single coordinated push.
- **Platform 3.0.0** (planned, breaking): ADR-0019 wildcard removal + accumulated v2.x deprecations.
