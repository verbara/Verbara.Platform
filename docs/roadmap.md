# Roadmap — Verbara.Platform + Verbara.Platform.Web

**Última actualización:** 2026-05-18 · **Baselines actuales:** Platform **`2.3.0`** (SHIPPED 2026-05-18 — commits `5d773e55` + `d592e8d5`, tag `v2.3.0`, pushed; CI triggered for signed image build) · Platform.Web **`3.1.0-web`** (HTTP 402 upgrade-modal UX SHIPPED 2026-05-18) · SDK pin `2.1.2` · Pro pin **`2.4.1-pro`** (SHIPPED 2026-05-18 — commit `8d98022`, tag `v2.4.1-pro`, pushed; 24 nupkgs queued for GH Packages) · **🎉 visibility flip EXECUTED 2026-05-10 19:04 UTC — all 7 ADR-0018 triggers GREEN; Platform + Web repos PUBLIC; first cosign-signed images live at `ghcr.io/verbara/platform/{api,web}`** · **🎯 2026-05-17 PIVOT: Reference Deployment + manuales SMB on-premise (Fase 1) — 6 deliverables shipped (~6,300 líneas)** · **✅ 2026-05-18 PRO 2.4.0-pro TRAIN END-TO-END COMPLETE**: Platform v2.2.0 (HTTP 402 RFC 9457 contract + `GET /management/system/license/status` admin surface + SMB manuales off `LICENSING_MODE`) + Web v3.1.0-web (`<PaymentRequiredDialogHost />` modal renders tiered Trial/Upgrade/ContactSales CTAs from RFC 9457 extension members). Cumulative cross-repo: 3,112 tests passing. · **✅ 2026-05-18 R5.5 K8s Phase D-LK COMPLETE PASS-with-findings** — 24h soak K8s 99.987% success @ p99 10.73 ms (9.3× under SLO); forensics revelaron worker silent-death architectural bug → resuelto en Worker Resilience train. · **✅ 2026-05-18 WORKER RESILIENCE TRAIN COMPLETE**: Pro v2.4.1-pro + Platform v2.3.0 (27 workers hardened cross-repo + `HostOptions.BackgroundServiceExceptionBehavior = StopHost` wired + 48 net new resilience tests; Pro 1,550 tests / Platform 961 tests; 0 warnings; ADRs Pro-0013 + Platform-0021). **D-LK soak rerun bundled con Pro v2.5.0-pro train** (extended protocol scenarios A-E covering hardening + licensing-mode removal) — eligible 2026-06-28+. **Pro v2.5.0-pro pre-conditions #2 + #3 MET; #1 elegible 2026-06-28+.** R5.5 cloud (Phase 0C+) deferred indefinidamente.

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
| **Platform 2.2.0 "Pro v2.4.0-pro consumer migration"** | **2026-05-18** | **Pro v2.4.0-pro cascade + HTTP 402 contract + license-status admin surface.** (a) 21 `Verbara.Sdk.Pro.*` pins `2.3.0-pro` → `2.4.0-pro` (all 24 Pro nupkg pushed manually to GH Packages — Pro CI still 0-run-state since rebrand); (b) **new endpoint** `GET /management/system/license/status` (`PlatformAdminOnly`, sibling of existing `/license`) returns raw Pro `LicenseStatusSnapshot` — no Platform DTO wrapper; (c) **`LicenseGateMiddleware` HTTP contract change**: 403 → **402 Payment Required** + RFC 9457 ProblemDetails with extension members `tier_required` / `trial_url` / `upgrade_url` / `contact_sales_url` populated via `LicenseGuard.Evaluate`; (d) SMB operator docs migrated `LICENSING_MODE` → `LICENSE_PATH` (`.env.reference-smb.example`, `docker-compose.reference-smb.yml`, `manuales/smb/99-troubleshooting.md`, `operations/first-realistic-demo.md`); (e) Helm chart additive `api.licensing.licenseFilePath` value, `enforcementMode` documented as deprecated; (f) dev/demo compose keeps `EnforcementMode=Disabled` back-compat + suppresses Pro event-id 12001 boot log via `Logging:LogLevel:Verbara.Sdk.Pro.Licensing.LicensingDeprecationHostedService=Warning`. **Diverges from Pro spec on 2 points** (intentional Platform-side prerogative): endpoint path = `/management/system/license/status` not `/api/v1/admin/...`; status code = 402 not 501. 32 files / +441 / -49 / 2,048 tests passing cross-package (Api.Tests 932/932). **Unlocks Pro v2.5.0-pro pre-conditions #2 (consumer release) + #3 (manuales updated)**; #1 (≥6 weeks since Pro v2.4.0-pro tag) elegible 2026-06-28+. ADR-0012 cycle progress: transition release (Pro v2.4.0-pro) consumed; removal release (Pro v2.5.0-pro) gates only on calendar. | **SDK 2.1.2 + Pro 2.4.0-pro** |

**Platform.Web** track sigue cadencia independiente desde el rebrand (Web v3.0.x; ROADMAP COMPLETE 2026-05-09 — todas las 7 niveles cerradas).

[Releases GitHub](https://github.com/verbara/Verbara.Platform/releases) · [Releases Web](https://github.com/verbara/Verbara.Platform.Web/releases)

---

## En curso (active plans)

> Las "En planificación" originales (1.9.x sub-releases + 2.0.0 arquitectónico + v2.1.0 image-binding + visibility flip) **YA SHIPPEARON** entre 2026-04-20 y 2026-05-10 — ver tabla "Shipped" arriba. Sección reescrita 2026-05-10 (post-flip) reflejando el estado real.

### SMB Reference Deployment + Manuales — ✅ Fase 1 COMPLETE 2026-05-17

**Contexto.** Tras el visibility flip (2026-05-10), el producto necesitaba pivotar desde validación de producción interna (R5.5) hacia entregables customer-facing. Decisión 2026-05-17: priorizar **reference deployment + manuales paso a paso** para que un cliente final (o equipo de implementación) pueda instalar Verbara desde cero y configurar los 3 canales V1 (Voz/SIP + WebChat + Email) sin acompañamiento del equipo Verbara.

**Fase 1 = Docker SMB on-premise.** Fase 2 (K8s on-prem) deferred — el chart actual asume el lab Talos y necesita refactor (parametrizar Kamailio IP, externalizar secrets, cert-manager) que toma 2-3 semanas adicionales.

**Plan canónico:** `docs/plans/completed/2026-05-17-reference-deployment-smb.md` (archivado al cierre de Fase 1). Decisión + tiers de hardware + 4 escenarios NAT + decisión `network_mode: host` en lugar de bridge — todo argumentado ahí.

**Deliverables shipped (6 commits)**:
1. **Imagen Web publicada** (`ghcr.io/verbara/platform/web:v3.0.3-web`) — workflow GitHub Actions mirror del API, firmada con cosign keypair compartido. Antes solo existía la imagen API públicamente; ahora ambas pueden verificarse con el mismo `cosign.pub`.
2. **`docker/docker-compose.reference-smb.yml`** + **`.env.reference-smb.example`** (~90 vars) + **`docker-compose.coturn.yml`** overlay. Arquitectura: Asterisk `network_mode: host` (todos los puertos SIP/RTP en el host NIC — evita ~3 GB RAM de docker-proxy overhead para 300 calls), Platform.Api + Web + Postgres + Redis en bridge, Postgres bind loopback `127.0.0.1:5432` para que Asterisk-on-host alcance realtime DB. Tier matrix Lite (50 calls) / Standard (150) / Plus (300) configurable via env. Pinned a `api:v2.1.0` + `web:v3.0.3-web`. Renderer/Mail build local (no publicados a ghcr aún). `entrypoint-asterisk.sh` extendido con `PG_REALTIME_HOST/PORT/DB/USER/PASSWORD` env substitution.
3. **`scripts/quickstart-smb.sh`** — 11 pre-flight checks: tooling, recursos RAM/CPU/disk con detección de tier, puertos TCP+UDP libres, rango RTP completo (`ss -uln` scan), firewall hints por distro (UFW/firewalld/nftables) + cloud metadata detection (GCP/AWS/Azure), **detección NAT 4 escenarios** (A IP directa, B/C privada+NAT, D CGNAT), bandwidth informativo, `.env` validation (rechaza placeholders `CHANGE_ME`), pull + up --wait + polling `/health/ready`. Validado en el host dev (detectó correctamente escenario C: `192.168.40.100 LAN → 200.118.42.61 pública`).
4. **12 manuales `docs/manuales/smb/`** (~3,800 líneas, 100% español): `00-vision-general` (arquitectura + tiers + OS), `01-instalacion-docker` (install por distro Debian/Ubuntu/Rocky/Alma/Amazon Linux + firewall por distro + cloud SG + on-prem router commands MikroTik/pfSense/Ubiquiti/TP-Link + DNS + Let's Encrypt + 4 escenarios NAT), `02-arranque-stack`, `03-setup-inicial` (admin + tenant + agente + queue), `04-canal-webchat`, `05-canal-email` (SMTP/IMAP + MS Graph OAuth + Gmail OAuth2), `06-canal-voz-sip` (538 líneas — el más extenso: trunk Twilio Elastic + genérico, inbound dialplan 3 patrones, WebRTC agent provisioning, golden path test, Coturn behind strict NAT, escalado entre tiers), `07-validacion-e2e`, `08-troubleshooting-sip` (10 secciones síntoma→causa→solución), `99-troubleshooting` (no-SIP), `checklist-validacion-cliente` (imprimible con firmas), `capacity-reference` (tier matrix + mediciones reales del D-L 24h soak).
5. **`tests/e2e/tests/reference-deployment.spec.ts`** + **`fixtures/channel.fixture.ts`** en Verbara.Platform.Web — 7 tests con tag `@reference-deployment`: stack healthcheck, setup wizard completo, WebChat demo widget loads, WebChat config persistence, Email config persistence, Voice trunk CRUD + WebRTC provisioning, Asterisk ARI smoke (skip si no hay `VERBARA_ARI_PASSWORD`).
6. **3 helpers de validación**: `tests/manual-validation/webchat-test.html` (página standalone con widget embedded + form configurable + checklist 10 items), `sip-softphone-test.md` (guía Linphone + Zoiper + WebRTC comparativa para test sin trunk), `scripts/capacity-calc.sh` (calc pre-deploy: agents + codec + retention → tier + hardware + .env vars + cloud costs orientativos AWS/Azure/GCP/Hetzner/DO).

**Total:** ~6,300 líneas customer-facing en 1 día. Sin tocar `infra/k8s/helm/` (Fase 2). Sin romper compose anteriores (`smb.yml`/`full.yml`/`production.yml`/`scale.yml`/`demo.yml` siguen funcionando idénticos).

**Fase 2 (deferred sin fecha)** — K8s on-prem reference: refactor Helm chart (externalizar secrets, eliminar `192.168.122.201` hardcoded, parametrizar hostnames, cert-manager integration, Kamailio off-hostNetwork con MetalLB), Docker → K8s migration guide, manuales K8s espejo de los SMB.

### ADR-0018 visibility-flip checklist — ✅ COMPLETE 2026-05-10

7/7 GREEN. Flip ejecutado 2026-05-10 19:04 UTC sobre Platform + Platform.Web. Plan tracking en `docs/plans/completed/2026-05-08-visibility-decision-and-alignment.md`. Apache 2.0 economics ya operando; ADR-0006 funnel ahora viable.

### R5.5 K8s Phase 0LK reabierto (2026-05-16) — chart gap-fix blocking B-LK

Status: **2 gaps reales detectados, 1 mitigado, 1 pendiente de ship.**

Cuando se reanudó B-LK.1 el 2026-05-16, primer `curl -X POST /api/v1/setup` contra el cluster devolvió `HTTP 401 — Access to the path '/app/data' is denied`. Diagnóstico profundo (ver `~/.claude/projects/-media-Data-Source-Verbara-Verbara-Platform/memory/project_r55_k8s_real_state_2026_05_16.md` — incluye análisis de 7 alternativas rechazadas) reveló que Phase 0LK declaró "FULLY COMPLETE" con 28 pods Ready, pero **el path de auth nunca se ejerció contra el cluster K8s**. Hay DOS gaps en el Helm chart `infra/k8s/helm/platform/`:

1. **Orphaned legacy `Ingress`** (`templates/ingress.yaml`) — Cilium tiene `gatewayAPI.enabled: true` pero NO `ingressController.enabled: true`, así que los recursos `networking.k8s.io/v1 Ingress` quedan huérfanos sin controller. El Gateway cluster-level tiene 0 HTTPRoutes. **Mitigado hoy** con `infra/k8s/manifests/httproute-platform.yaml` (HTTPRoutes binding `api.r55.local` → `platform-api:5000` + `r55.local` → `web:80`, cross-ns referencia al `default/platform-gateway`). Sin commitear. Migrar chart de Ingress → HTTPRoute es la solución permanente.

2. **ADR-0012 JWT rotation pool sin wireup** — `Program.cs:540` defaults `Auth:KeyDirectory` a `{ContentRoot}/data` = `/app/data`. Pod corre como UID 1654 sin volumen ahí → 401 en TODA llamada de auth. Aunque se montase un volumen escribible, el problema multi-réplica que ADR-0012 fue escrita para resolver persiste (cada réplica genera su propio RSA → JWT firmado por A falla validación en B). **El chart no setea las 3 env vars que activan el path correcto:**
   ```yaml
   - name: Identity__JwtKeyRotation__UseRotationPool
     value: "true"
   - name: Identity__JwtKeyRotation__RequireRedisStore
     value: "true"
   - name: ConnectionStrings__IdentityRedis
     value: "redis.r55-data.svc.cluster.local:6379"
   ```
   Redis prereqs OK: service `redis.r55-data.svc:6379` Running 12d; NetworkPolicy `allow-redis-from-platform` permite 6379 desde r55-platform; conectividad TCP verificada via `/dev/tcp` desde dentro del pod platform-api.

**Atajos rechazados** (todos crean nuevo anti-pattern en lugar de cerrar el real): emptyDir + fsGroup (claves efímeras → logout en cada redeploy + multi-replica desync), PVC + fsGroup (pods → pets, rompe HPA 2→8), image rebuild con /app/data chowned (single-pod-ok, multi-replica-broken), initContainer chown (idem), runAsRoot (viola PodSecurity baseline). Sólo la opción 1 (Redis-pool wireup) cierra ambos síntomas.

**Plan de ship** (~30 min):
- Editar `infra/k8s/helm/platform/templates/platform-api-deployment.yaml` + `values.yaml` (3 env vars + matching values block).
- `helm upgrade platform infra/k8s/helm/platform/` (rolling, ~60 s).
- Probar `/api/v1/setup` → 201 (o 409); seed-staging.sh; arrancar B-LK.1.
- Commit conjunto: `feat(k8s): R5.5 Phase 0LK gap-fix (HTTPRoute migration + JWT rotation pool wireup) — unblocks B-LK auth`.

**Notas adicionales descubiertas en el camino:**
- `scripts/k8s-up.sh` necesita rama "warm-restart": pasos 5 (apply-config --insecure) y 6 (bootstrap) fallan si los nodos ya están provisionados. Hoy hubo que ejecutar manualmente `talosctl kubeconfig --force` para saltarlos. Parche idempotente a `net-start default` ya aplicado en working tree (no committeado).
- Imagen deployada es `asterisk-platform/api:1.14.6` + `asterisk-platform/web:1.15.5` (pre-rebrand, namespace OCI `asterisk-platform/*`) — esto es **metodológicamente correcto** para Phase B-LK ya que matchea la baseline Docker B-L también v1.14.6 (D-L 24h soak PASS 2026-04-30 fue en esta versión). NO actualizar a v2.1.0 antes de B-LK o el comparativo K8s vs Docker pierde validez. SDK pin transitivo ~1.15.x, Pro pin transitivo 1.16.0-pro.
- **Registry host `192.168.122.1:5050` DOWN** — los pods corren porque las imágenes están cacheadas en `containerd` de cada nodo. `helm upgrade` con `imagePullPolicy: IfNotPresent` (chart default, verificar) funciona; si fuera `Always` fallaría. **Antes de Phase C-LK chaos** (que mata pods intencionalmente) el registry debe estar arriba. Para B-LK.1 baseline es safe diferirlo.
- **Licensing no se necesita para B-LK** — chart hardcodea `licensing.enforcementMode: Disabled`. `LicenseGateMiddleware` pasa todo sin validar. Mantiene paridad con Docker B-L baseline (también sin license). Modos disponibles: `Disabled` (actual), `WarnOnly` (staging), `Enforce` (production — requeriría license key con `AuthorizedImageDigests` si fuera v2.1.0 por ADR-0011, o solo expiry check si es v1.14.6). Medir overhead de `LicenseGateMiddleware` con Enforce + license válido sería un Phase B-LK.6 opcional separado.

### Post-flip follow-ups (no version-gated)

Capturados en el Status update del 2026-05-10 en [ADR-0018](decisions/0018-visibility-decision-3-private-now-public-on-trigger.md). Resumen:
- **ghcr.io package visibility** — los paquetes OCI (`ghcr.io/verbara/platform/{api,web}`) heredan visibility privada por defecto; flip manual UI pendiente (GitHub no expone REST API para esto).
- **Real-customer e2e validation** — primer cliente Tier 1+ que descargue imagen + corra `verbara-verify-image.sh` cerrará la valid-loop completa.
- **Announcement** (HN "Show HN" / r/asterisk / r/devops / Twitter / ProductHunt) — deferred hasta primer customer-driven milestone.
- **Cloudflare git auto-deploy reconnect** — re-attach pipeline (operator: dashboard → Workers & Pages → verbara-website → Settings → Builds & deployments).

### Platform v2.1.x patch train (reactive)

Reservado para parches sobre v2.1.0 (security follow-ups, image-binding bug fixes, cosign workflow drift). Sin plan activo; trigger será findings reales en producción o feedback de operadores tras visibility flip.

### Platform v2.2.0 "Pro v2.4.0-pro consumer migration" — ✅ SHIPPED 2026-05-18

**Status:** **SHIPPED** — commit `0de22761` on `origin/main`, tag `v2.2.0` published, Platform CI `release.yml` running (cosign + GHCR signed image). See Shipped table above for the full row. Detailed scope retained below for historical reference; section moves to historical/archived in next roadmap revision.

**Scope (as shipped):**
- **Pro pin cascade** — 21 `Verbara.Sdk.Pro.*` `PackageVersion` lines en `Directory.Packages.props` bumpean `2.3.0-pro` → `2.4.0-pro`.
- **Nuevo endpoint** `GET /management/system/license/status` (`PlatformAdminOnly`, sibling del existente `GET /management/system/license`) consumiendo el nuevo `ILicenseStatusReader` de Pro 2.4.0-pro. Retorna raw `LicenseStatusSnapshot` (NO Platform DTO wrapper — Pro guarantees el contract vía `LicensingJsonContext`). Tier, ExpiresAt, MaxAgents, MaxNodes, AuthorizedDigestsCount, LastValidationResult, LastValidationAt, RevalidationInterval, Licensee.
- **`LicenseGateMiddleware` contract change** — HTTP status code **403 → 402 Payment Required** (RFC-9110-accurate para subscription gates, no consume SLO error budget, no auto-retry en HTTP clients). Body sigue siendo RFC 9457 ProblemDetails pero gana extension members `tier_required` + `trial_url` + `upgrade_url` + `contact_sales_url` poblados por `LicenseGuard.Evaluate` (Pro 2.4.0-pro's `Enrich` helper).
- **SMB operator docs migration off `LICENSING_MODE`** — 4 files updated (`.env.reference-smb.example`, `docker-compose.reference-smb.yml`, `docs/manuales/smb/99-troubleshooting.md`, `docs/operations/first-realistic-demo.md`). Reemplazo canónico: `LICENSE_PATH=` + referencia al Tier 0.5 issuer en `https://verbara.io/developer-license`. Historical docs (specs/, completed plans, R5.5 active plan, onboarding-feedback) intacto — snapshots-in-time.
- **Dev/demo compose strategy** — `docker-compose.full.yml` + `demo/docker-compose.demo.yml` MANTIENEN `Licensing__EnforcementMode: Disabled` (removing-the-var rompería startup bajo Pro 2.4.0-pro back-compat) PERO añaden `Logging__LogLevel__Verbara.Sdk.Pro.Licensing.LicensingDeprecationHostedService: Warning` para suprimir el event-id 12001 boot log spam hasta lockstep migration en v2.5.0-pro.
- **Helm chart additive update** — `infra/k8s/helm/platform/values.yaml` gana `licenseFilePath: ""` (nuevo, opcional). `enforcementMode` mantenido pero documentado como deprecated. Template emite ambas env vars condicionalmente.
- **CHANGELOG** entry `## [2.2.0] — 2026-05-17` cubriendo Pro pin cascade + endpoint + contract change + deprecation handling + SMB doc updates.
- **Tests** — `LicenseGateTests.cs` flip 403→402 + rename 3 métodos + 4 nuevos tests (ProblemDetails extension members per `LicenseBlockReason`). `ManagementClusterLicenseGateTests.cs` add `.NotBe(PaymentRequired)`. NEW `LicenseStatusEndpointTests.cs` (4 tests: happy-path / unloaded / 401-anon / 403-tenant-admin).

**Decisiones arquitectónicas tomadas en planning** (algunas divergen del spec Pro original — Platform-side prerogative):

| Aspect | Pro spec ejemplo | v2.2.0 decision | Why |
|---|---|---|---|
| Endpoint path | `/api/v1/admin/license/status` | **`/management/system/license/status`** | Platform convention — system-level state pertenece a `/management/system/*` (`PlatformAdminOnly`), no a tenant-admin `/api/v1/admin/*` (`AdminOnly`) |
| HTTP status code | 501 Not Implemented | **402 Payment Required** | RFC-9110-accurate para subscription gates (Stripe-style); 4xx no consume SLO error budget; clients no auto-retry 402 |
| Endpoint return shape | (custom JSON) | **Raw `LicenseStatusSnapshot`** (no Platform DTO) | Pro guarantees contract via `LicensingJsonContext`; wrapping = translation layer con zero value |

**Total: 16 files modified.** ~6h estimated. Target tag `v2.2.0`.

**Pre-flight risk (resolved during ship):** `NuGet.Config` `packageSourceMapping` constraint para `Verbara.Sdk.Pro*` → GH Packages source. Pro CI broken (0 workflow runs en `verbara/Verbara.Sdk.Pro` desde rebrand 2026-05-05). **Resolution:** maintainer's `GITHUB_PACKAGES_PAT` (`~/.verbara/secrets.env`) used to push all 24 v2.4.0-pro `.nupkg` files manually via `dotnet nuget push --source github`. Platform restore resolved cleanly.

**Cross-repo unlock state (post-ship 2026-05-18):** ✅ Pro v2.5.0-pro pre-conditions #2 (consumer release) + #3 (manuales updated) **MET**. ⏳ Pre-condition #1 (≥6 weeks since Pro v2.4.0-pro tag 2026-05-17) elegible **2026-06-28+** — only calendar gate remaining.

### Worker Resilience Pattern Hardening — Pro v2.4.1-pro + Platform v2.3.0 — ✅ SHIPPED 2026-05-18 (commits `8d98022` Pro, `5d773e55` + `d592e8d5` Platform; tags `v2.4.1-pro` + `v2.3.0`)

**Status:** SHIPPED. 27 workers hardened cross-repo (13 Pro + 14 Platform) + `BackgroundServiceExceptionBehavior.StopHost` wired in `Verbara.Platform.Api/Program.cs`. 48 new resilience tests (25 Pro Tier-1 deep + smoke + 23 Platform incl. integration test for HostOptions wiring). Pro 1,550 tests pass; Platform 961 tests pass (was 938 + 23 new). 0 warnings; AOT publish clean.

**D-LK soak repeat — bundled with Pro v2.5.0-pro train, NOT run standalone:**

The post-train D-LK rerun (originally tracked as "validate hardening in real cluster") is consolidated into the Pro v2.5.0-pro release validation when that train executes (≥2026-06-28). Rationale: (a) 48 new tests already lock the discipline at code-level; (b) `StopHost` is well-tested .NET runtime behavior; (c) workers running live in lab K8s during the 6-week observability window IS continuous validation — any silent-death regression surfaces as visible pod restart via the new discipline; (d) bundling saves cluster-hours + improves failure attribution; (e) the v2.5.0-pro behavioral change (Pro features degrade to 402 instead of crashing at startup on invalid license) introduces new scenarios that benefit from sustained soak validation in the same run.

**Extended D-LK protocol** when v2.5.0-pro train runs (5 scenarios):

| ID | Scenario | Validates v2.4.1-pro hardening | Validates v2.5.0-pro removal |
|---|---|---|---|
| A | Boot with valid license + sustained 24h load | ✅ no silent worker death | ✅ Pro features OK |
| B | Boot WITHOUT license (v2.5.0-pro: app starts) | indirect | ✅ Pro features 402 from boot |
| C | Mid-soak license expiration at T+12h | ✅ no worker crash during transition | ✅ in-flight 200→402 graceful |
| D | Chaos: kill worker under stress → pod restart visible | ✅ `StopHost` E2E + liveness reaction | n/a |
| E | Chaos: invalid IMAGE_DIGEST + license válida | n/a | ✅ `UnauthorizedImage` 402 path |

Canonical: Pro spec [`2026-05-17-pro-v250-licensing-enforcement-mode-removal.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-17-pro-v250-licensing-enforcement-mode-removal.md) section "Validation requirements — D-LK protocol extension".

---

### Worker Resilience Pattern Hardening — ORIGINAL planning (kept for archive — superseded by ✅ SHIPPED above)

**Status:** **Spec listo, ejecución pending.** Spec canónico: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](specs/2026-05-18-worker-resilience-pattern-hardening.md).

**Origen:** D-LK 24h soak forensics (2026-05-18) — ver `R5.5 K8s Phase D-LK` arriba. El Finding 1 documentó un pattern bug que afecta cualquier `BackgroundService` long-running en Platform o Pro: cuando una excepción propaga out of `ExecuteAsync`, el default `BackgroundServiceExceptionBehavior.Ignore` la oculta — el worker queda "Running" para el orquestador pero internamente muerto. K8s liveness probe detecta la cascada (health check Unhealthy) y mata el pod 30-45s después; Docker no tiene esa autorrecuperación y el worker queda zombie indefinidamente.

**Scope (cross-repo):**

- **Pattern A (timer-based workers):** outer try-catch wrapping el while loop completo + `LogWorkerCrash` + `throw` (rethrow para que `StopHost` lo capture).
- **Pattern B (Rx event-driven workers):** OnError handler nullifica `_subscription` → `CheckHealthAsync` retorna Unhealthy en lugar de Healthy-stale.
- **Host-level:** `services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost)` en `Program.cs`. **Esto es el cambio crítico** que hace el pattern funcione.

**Workers afectados (audit needed):**

| Repo | Worker | Pattern |
|---|---|---|
| Platform | `QueueDistributionWorker` | A — confirmed vulnerable (el que murió en D-LK) |
| Platform | `ConversationTimeoutWorker` | A — likely same pattern |
| Platform | Otros `*Worker.cs` en `src/Verbara.Platform.Api/Services/` | audit |
| Pro | `PresenceFanoutService` + `PresenceMergeConsumer` | B |
| Pro | `Verbara.Sdk.Pro.Dialer/` workers | audit (A/B) |
| Pro | `Verbara.Sdk.Pro.EventStore/` workers | audit (A/B) |
| Pro | `Verbara.Sdk.Pro.Realtime/` workers | audit (A/B) |

Estimado 8-12 worker files total cross-repo.

**Cadence:**

- **Pro v2.4.1-pro** — Pro half (~10h: PresenceFanout/Merge + Dialer/EventStore/Realtime workers audit + tests). Spec section "Pattern B" + "Pro repo" en el doc.
- **Platform v2.3.0** — Platform half (~10h: QueueDistributionWorker + ConversationTimeoutWorker + HostOptions wiring + integration test + Platform-side `WorkerLog.cs`). Sibling release; consumer migration de Pro v2.4.1-pro.
- **Sequence:** ship Pro v2.4.1-pro primero, luego Platform v2.3.0 que consume.

**Decisión: NO bundlear con Pro v2.4.0-pro Licensing simplification** (que YA shipped 2026-05-17). Razones: clarity > bundle (rollback de un train no afecta el otro; tests aislados; changelog limpio). Confirmado en spec §"Distribution / release pathway".

**Relacionado:** el Phase G-PRE del Pro v2.4.0-pro spec (presence health check semantic fix — `CheckHealthAsync` differentiates pre-start / disposed / active-idle) **NO shipped en v2.4.0-pro** (no estaba en spec original); ahora documentado para bundlear con Pro v2.4.1-pro Worker Resilience train. Total Pro v2.4.1-pro effort: ~10h + 3h G-PRE = ~13h.

### R5.5 K8s Phase D-LK — ✅ COMPLETE 2026-05-18 (PASS-with-findings)

**Status:** ✅ **PASS** del 24h soak K8s. 2,591,667 / 2,592,000 requests OK (**99.987%** success). p99 latency **10.73 ms** (9.3× bajo el SLO budget de 100 ms). Run window: 2026-05-17 04:36:49 → 2026-05-18 04:36:13 local. Reporte canónico: [`docs/operations/soak-test-report-k8s-local.md`](operations/soak-test-report-k8s-local.md).

**El "OOM" hipotético resultó NO ser OOM.** Forensics post-soak ([`chaos-reports/dlk-oom-analysis-20260518.md`](../chaos-reports/dlk-oom-analysis-20260518.md)) reveló que el restart a T+16h36m fue K8s liveness probe failure (NOT kernel OOM killer) gatillado por `QueueDistributionWorker` silent death. 333 fails (0.013%) correspondieron al window de SIGTERM→SIGKILL grace + Cilium endpoint slice update (~30s).

**5 hallazgos catalogados:**
1. 🔴 **Worker silent-death architectural bug** (REAL, customer-facing). `BackgroundService` workers en Platform + Pro pueden morir silenciosamente cuando excepción propaga out of `ExecuteAsync` con default `BackgroundServiceExceptionBehavior.Ignore`. Afecta cualquier deploy long-running (Docker SMB o K8s). Nuevo spec: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](specs/2026-05-18-worker-resilience-pattern-hardening.md). Ships en Worker Resilience track (sección siguiente).
2. 🟡 **`presence-fanout/merge` Degraded 21h FALSE POSITIVE.** Workers son event-driven (Rx subscription); idle heartbeat = stale by design. El bug está en el HealthCheck design, no en los workers. Cosmético. Fix bundleado en Pro v2.4.0-pro Phase G-PRE (ya en spec, ejecución pendiente patch o v2.4.1).
3. 🟡 **metrics-server caído en lab Talos** → HPA non-functional. Mandatory para Fase 2 K8s ref deploy customer-facing. ~2h.
4. 🟢 **Cilium eBPF endpoint slice updates ROBUST** (validation). Production-ready.
5. 🟡 **Chaos Mesh + Cilium kube-proxy-replacement incompat** reconfirmado. Deferred Phase 0C cloud.

**Comparativa con Docker D-L (2026-04-30 PASS):** K8s D-LK p99 10.73 ms vs Docker D-L p99 60.66 ms (K8s ~6× mejor, pero scenario más simple). Restart pattern absent en Docker (no liveness probe mechanism). Methodologically válido — D-L baseline cerró antes de pivot a customer-facing trabajo.

**R5.5 K8s validation completa.** Próximos K8s steps (Fase 2 customer ref deploy, cloud Phase 0C+) deferidos por pivot 2026-05-17 a SMB Docker.

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
