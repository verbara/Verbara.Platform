# R5 — Production Readiness + Value Materialization (Release Train)

**Fecha creación:** 2026-04-22 · **Last revised:** 2026-04-25 (D-FORCE-2 split applied)
**Estado:** Approved (envelope D' + S1 expanded product-final) — **R5.1 shipped 2026-04-23** (Pro 1.12.0-pro + Platform 1.10.0 + Web 1.9.0 — pushed + tagged + GH Releases live)
**Duración estimada:** 15.5-16.5 semanas total (~10.5 sem execution + ~6 sem QA gaps entre releases) — **R5.4 added** per post-ship triage D-FORCE-2 to land production-validation work without inflating R5.3 cadence.
**Baseline:** SDK 1.15.0 · Pro 1.11.0-pro · Platform 1.9.3 · Web 1.8.0
**Post-R5.1 triage:** ver [`2026-04-25-r5.1-post-ship-triage.md`](2026-04-25-r5.1-post-ship-triage.md) — reconciles 13 canonical limitations + 5 R5.2 features (Set A) + 7 R5.2 carry-forwards (Set B) + 9 new items + 7 productization categories. R5.2 brainstorm input.

### Breakdown de duración

| Fase | Duración | Notas |
|---|---|---|
| R5.1 execution | 3-3.5 sem | Phase 0 UI primitives (4-6h) + S1 product-final (1.5-2 sem) + S2 Ops bundle paralelizado (~1 sem) — ✅ shipped 2026-04-23 |
| QA gap R5.1→R5.2 | 2 sem | Baking en producción/staging, patches si surgen |
| R5.2 execution | 2 sem | 12 items (5 new features S3.1-S3.5 + 7 carry-forwards B.1/B.2/B.6/B.7/B.9/B.11/B.12) paralelizables con subagents |
| QA gap R5.2→R5.3 | 2 sem | Baking |
| R5.3 execution | 1.5-2 sem | 7 items + R4 closure (Admin Completeness only — production-validation moved to R5.4) |
| QA gap R5.3→R5.4 | 2 sem | Baking; pen-test scoping happens here |
| R5.4 execution | 3 sem | 9 items (load tests + SLOs + alerts + pen-test + Getting Started + OpenAPI + capacity + backup/DR + JWT multi-key); calendar-bounded by external pen-test engagement |
| **TOTAL** | **15.5-16.5 sem** | ~4 meses; con buffers QA reales, no comprimidos |

---

## Principios de diseño (no-atajos)

Estos principios se aplican a **cada item en cada release**. Un item no sale hasta que cumple todos.

1. **Multi-node first.** Cualquier abstraction nueva que mantenga state debe ser cluster-HA desde el diseño. In-memory cache sin abstraction reemplazable es rechazado.
2. **Observability intrínseca.** Cada nuevo `BackgroundService` / `IHostedService` / provider expone `ActivitySource` + `Meter` + health check `tag:"ready"`. Patrón 1.8.0-pro consistente.
3. **AOT sin reflection.** `[JsonSerializable]` actualizado bilateral (Pro/Platform) cuando hay DTO nuevo. `[LoggerMessage]` para logs hot-path.
4. **RBAC tests 401/403** por cada endpoint nuevo, no solo happy path.
5. **Audit entries** por cada mutation (cambio de state, config, membership, license).
6. **i18n keys** en `en-US` + `es-419` + `pt-BR` por cada string UI nuevo.
7. **Playwright E2E** full-stack gated por `E2E_FULL_STACK=true` por cada vertical nuevo.
8. **CHANGELOG** por repo por release con CHANGELOG.md de marketing claro.
9. **Docs** en `docs/architecture.md` / `docs/operations/*.md` actualizados antes de ship.
10. **Zero warnings, CI green** inviolable.

---

## Envelope del Release Train

```
R5.1 ──► QA 2 sem ──► R5.2 ──► QA 2 sem ──► R5.3 ──► QA 2 sem ──► R5.4
"Prod +              "Security              "Admin                "Production
 Ops"                 Admin +                Completeness +        Validation"
                      Compliance"           R4 Closure"
  ▼                     ▼                     ▼                     ▼
Pro 1.12.0-pro       Pro 1.13.0-pro        Platform 1.12.0       Platform 1.13.0
Platform 1.10.0      Platform 1.11.0       Web 1.11.0            Pro 1.13.x-pro (if needed)
Web 1.9.0            Web 1.10.0            (GH Release)          (GH Release)
(GH Release)         (GH Release)
   3-3.5 sem           2 sem                 1.5-2 sem             3 sem

Paralelo oportunista (cross-release):
- R1.5 SDK v1.15.1 "VoiceAi Refresh" (rama paralela, subagent background, ship when ready)
```

Cada release tiene GH Release público con narrativa marketing propia. Patches intermedios dentro de cada release (`1.x.y → 1.x.y+1`) son packs al local feed + tags, sin GH release.

---

## Release 1 — R5.1 "Production Readiness + Ops Toolkit"

**Duración:** 3-3.5 semanas (Phase 0 + Phase 1 + Phase 2)
**Bumps finales:** Pro 1.12.0-pro · Platform 1.10.0 · Web 1.9.0 · **nuevo paquete** `Asterisk.Platform.Identity.Redis`
**Ship target:** GH Release público "Production grade — your ops team can run this without developer assistance"

### Phase 0 — UI primitives consolidation (bloquea paralelización posterior)

Auditoría determinó 5 primitives fragmentados/missing que bloquean paralelización sana de Phase 2. Se consolidan primero.

**Deliverables (Web):**
- `src/core/ui/copy-button.tsx` — `<CopyButton value={...} label="Copy" />` + toast success + tooltip. Reemplaza 8+ impls hardcoded actuales.
- `src/core/ui/status-badge.tsx` — `<StatusBadge status="Healthy" variant="cluster-node" />` con enum mapping global. Reemplaza `STATE_BADGE` inline en cluster-page.tsx + license-card.
- `src/core/ui/drawer-detail.tsx` — wrapper `<DrawerDetail title tabs={[]} actions={[]} onClose />` sobre Sheet existente. Reemplaza patrón ad-hoc en 6+ pages.
- `src/core/ui/code-block.tsx` — `<CodeBlock language="json" code={...} copyable />` con syntax highlight (Prism.js o shiki).
- `src/core/ui/stat-card.tsx` — `<StatCard icon metric trend description />` para KPIs + license card.

**Tests:** Vitest +8 (cobertura básica cada primitive) · Storybook no existe en el repo, skip.

**Duración:** 4-6h con 1 subagent.

### Phase 1 — Sprint 1 producto-final (ya aprobado, 6 items)

Detalle ya presentado y aprobado. Resumen aquí para spec completeness:

**S1.1 · Queue members completo** (Platform + Web)
- Endpoint RESTful anidado `/api/v1/queues/{id}/members` (GET / POST / DELETE / PATCH penalty)
- Audit entries (`queue.members.added`, `queue.members.removed`, `queue.members.penalty_changed`)
- RBAC: `queue.members.read` / `queue.members.write` / `queue.members.delete`
- Web: `useQueueMembers` hook, `queue-detail.tsx` render reemplaza phantom `[]`
- Playwright E2E: asignar/quitar agent + assert Realtime DB sync
- i18n keys EN/ES/PT añadidos

**S1.2 · Queue metrics real vía Postgres materialized** (Pro + Platform + Web)
- **Decisión arquitectónica:** approach B1 (Postgres writer) sobre B5 (in-memory). Razón: cluster-HA + sobrevive restart + consistente con patrón Platform Identity 1.9.1+.
- Nueva tabla `pro_analytics.live_queue_snapshots` (TenantId, QueueName, CallsWaiting, AvgWaitSeconds, AgentsAvailable, UpdatedAt) con índice compuesto `(TenantId, QueueName)` + retention 24h auto-purge
- `LiveQueueSnapshotWriter : IHostedService` suscriber de `LiveQueueStateEvent` → upsert throttled ~5 Hz por queue
- `ILiveQueueMetricsProvider` abstraction + impl Postgres-backed
- Nueva `ActivitySource "Asterisk.Sdk.Pro.Analytics.Live"` + `Meter` (counters published/throttled/duplicate, histogram write duration)
- Health check `live-queue-snapshots-writer` heartbeat 30s
- `Platform/QueueMetricsEndpoints.cs:66-67` → query Postgres (fallback null si unavailable)
- Web render "—" cuando null (no 0 falso)
- **Sub-task crítico:** verificar que `LiveQueueStateEvent` incluya `TenantId`. Si no → **SDK 1.15.1 bump** con event schema fix (sub-proyecto bloqueante)

**S1.3 · AgentAssist runtime feature toggle** (Platform + Web)
- **Decisión arquitectónica:** approach C3 (runtime toggle) sobre C1 (fail-fast) sobre C2 (NullSpeechRecognizer). Razón: enterprise self-service (Genesys/Five9 pattern); credenciales rotables sin redeploy.
- `IAgentAssistFeatureToggle` + `InMemoryAgentAssistFeatureToggle` default + `PostgresAgentAssistFeatureToggle` opcional
- Endpoint `GET/PUT /api/v1/admin/features/agent-assist {enabled, provider, credentials}`
- Credentials cifrados con `IDataProtectionProvider` (patrón JWT key de v1.9.2)
- `AgentAssistEngine` consulta toggle al inicio de cada sesión — live reload sin restart
- Audit entry `agentassist.config.changed` con before/after (credentials redacted)
- RBAC permission `features.agent-assist.manage` (PlatformAdmin only)
- Web: admin feature toggle page `/admin/features/agent-assist` con form (provider dropdown + API key input con copy-on-create)
- `Program.cs:577` reemplaza TODO → `AddProAgentAssist` con `IAgentAssistFeatureToggle` awareness
- Platform E2E: toggle enabled en UI → siguiente call transcribe

**S1.4 · Pro.Realtime cleanup + regression test** (Pro)
- Borrar comment `Phase 2: Queues (UPSERT)` stale en `RealtimeReconciler.cs:108`
- Añadir regression test `PlatformDesiredStateProvider_ShouldReturnQueues_WhenReconcilerRuns`
- Nota CHANGELOG Pro: "confirma Phase 2 queues reconciliation activa"

**S1.5 · MFA + PasswordReset Redis cache** (nuevo paquete Platform.Identity.Redis)
- **Sube de S3 a S1** — es production blocker real para multi-node. Patrón v1.9.2 ya creó las abstractions.
- Nuevo paquete `Asterisk.Platform.Identity.Redis` con `RedisMfaPendingCache : IMfaPendingCache` + `RedisPasswordResetCache : IPasswordResetCache` (StackExchange.Redis)
- DI: `AddAsteriskPlatformIdentityRedis(connString)` — opt-in, fallback al in-memory existente
- docker-compose env var `IDENTITY_REDIS_CONNECTION`
- Tests: contract tests reutilizan los InMemory existentes + Testcontainers Redis IT
- Docs: `docs/operations/identity-redis.md`

**S1.6 · Cross-cutting discipline** (permea S1.1–1.5)
- AOT JSON contexts actualizados bilateral
- i18n keys EN/ES/PT por cada string UI nuevo
- RBAC tests 401/403 por endpoint nuevo
- Audit log entries por cada mutation
- Playwright E2E por cada vertical
- Health checks `tag:ready` por cada `BackgroundService` nuevo

### Phase 2 — Ops Toolkit bundle (paralelizable con 4-5 subagents tras Phase 0)

Post-Phase 0 primitives + post-Phase 1 S1.5 Redis (para CR sana), los 4 items Ops se ejecutan en paralelo con subagents distintos:

**S2.1 · Cluster Node CRUD + drain** (M)
- Backend: endpoints ya existen (`POST/PUT/DELETE /management/cluster/nodes`, `POST /{id}/drain`, `POST /{id}/force-drain`). Verificar RBAC + audit + X-Ops contract.
- Web: `src/admin/cluster/node-list-page.tsx` con DataTable (Name, State, Role, Health, LastSeen, Actions)
- Drawer detail per node: tabs "Overview / Metrics / Drain Status / History"
- Force-drain action: ConfirmDeleteDialog + type-to-confirm
- Grafana deeplink (si `GRAFANA_URL` env var)
- RBAC: `cluster.nodes.read` / `cluster.nodes.write` / `cluster.nodes.drain` / `cluster.nodes.force-drain`
- Playwright E2E: drain healthy node → progress bar → empty → remove

**S2.2 · Webhook DLQ + deliveries + circuit status** (M)
- Backend: endpoints ya existen. Pre-sprint audit: enumerar filters actuales; añadir los faltantes (timeframe, tenant, failure_reason) como sub-task si aplica.
- Web: 3 páginas nuevas bajo `/admin/webhooks/`:
  - `/dead-letter` — DataTable + CodeBlock payload preview + retry individual + bulk retry
  - `/subscriptions/{id}/deliveries` — history con HTTP status + response body preview + timing
  - `/subscriptions/{id}/circuit` — StatusBadge (open/half-open/closed) + manual reset action
- Audit entries: `webhook.dlq.retried`, `webhook.circuit.reset`
- RBAC: `webhook.dlq.read` / `webhook.dlq.retry` / `webhook.circuit.reset`
- Playwright E2E: force webhook failure (test fixture endpoint) → appears in DLQ → retry → success

**S2.3 · License status card + feature matrix** (S)
- Backend: endpoint existe (`GET /management/system/license`). Verificar shape devuelve tier + expiration + grace + feature flags.
- Web: `/admin/license/` page con StatCard (current tier + days remaining) + feature matrix table (feature × enabled/disabled/grace)
- Upload new license key UI con drag-drop (nuevo endpoint: `POST /management/system/license` si no existe)
- Audit entry: `license.key.updated`
- RBAC: `license.read` / `license.update`

**S2.4 · API Key management CRUD + rotate** (M)
- Backend: CRUD completo existe (`/management/api-keys`). Verificar scoped permissions per key + last-used tracking + audit.
- Web: `/admin/api-keys/` list + create (modal con scope checkboxes + expiration date) + rotate (ConfirmDialog) + delete
- **Post-create flow:** show key ONCE con CopyButton + warning prominente "This is the only time you'll see this key"
- Last-used display (relative time)
- Audit entries: `apikey.created` / `apikey.rotated` / `apikey.deleted` (actor + key prefix logged, never full key)
- RBAC: `apikey.read` / `apikey.write` / `apikey.rotate` / `apikey.delete`
- Playwright E2E: create → use key in request → rotate → old fails 401 → new works

### Ship criteria R5.1

- ✅ 0 warnings todos los repos, CI green (SDK + Pro + Platform + Web)
- ✅ Test counts — Pro ~1,260 (+8), Platform ~1,765 (+30), Web Vitest 85 (+15), Platform E2E 275 (+10 Playwright)
- ✅ Full-stack smoke test: queue detail con members + metrics reales + AgentAssist toggleable runtime + cluster node drain visible + webhook DLQ retry funcional + API key lifecycle + license card
- ✅ Grafana dashboards actualizados (nuevo meter `Pro.Analytics.Live` + Platform.Identity.Redis si aplicable)
- ✅ Docs actualizados: `Pro/docs/architecture.md` (Postgres writer pipeline), `Platform/docs/operations/agentassist-setup.md`, `Platform/docs/operations/identity-redis.md`, `Platform/docs/operations/api-keys.md`
- ✅ CHANGELOG cross-repo por release
- ✅ `Asterisk.Platform.Identity.Redis` v1.0.0 en local feed
- ✅ GH Releases públicos para Pro 1.12.0-pro, Platform 1.10.0, Web 1.9.0

### QA gap post-R5.1 (2 semanas)

Periodo de baking en producción / staging. Durante este gap:
- Monitorear Grafana por métricas anómalas (`live_queue_snapshots_writer` duration p99, bridge throttle counters)
- Recolectar feedback de operators
- Patches rápidos si surgen (Pro 1.12.1-pro, Platform 1.10.1, Web 1.9.1) — no acumular bugs a R5.2
- R1.5 SDK VoiceAi Refresh puede shippear aquí oportunísticamente

---

## Release 2 — R5.2 "Security Admin + Compliance Path"

**Duración:** 2 semanas
**Bumps finales:** Pro 1.13.0-pro (si toca; probablemente no) · Platform 1.11.0 · Web 1.10.0
**Ship target:** GH Release público "SOC 2 readiness — audit, MFA, impersonation, retention fully auditable and enforced"

### Scope (5 items, todos paralelizables)

**S3.1 · MFA admin view** (S)
- Backend: endpoint existe (verificar `GET /management/mfa/users` + `POST /management/mfa/users/{id}/reset`). Añadir si falta.
- Web: `/admin/security/mfa/` list con DataTable (User, Tenant, Status, EnrolledAt, LastUsed, Actions)
- Actions: "Reset MFA" (ConfirmDialog) / "Revoke all sessions"
- Filters: status (enrolled/not-enrolled/locked) + tenant
- Audit: `mfa.admin.reset` / `mfa.admin.sessions_revoked`
- RBAC: `security.mfa.admin`

**S3.2 · Audit Log Viewer** (M) — cierra R4 Frente D
- Backend: `GET /audit/events` existe (parcial). Enrich con filters: action-prefix, actor, target, timeframe, tenant. Export CSV/JSON.
- Web: `/admin/security/audit/` con DataTable virtualized (puede tener millones de rows) + filter panel + drawer detail con CodeBlock (before/after diff)
- Export button (backend endpoint si falta: `GET /audit/export?format=csv|json&filter=...`)
- Retention disclosure: "Showing last N days per retention policy" con link a retention page
- RBAC: `audit.read` / `audit.export`

**S3.3 · Impersonation admin session management** (S)
- Backend: impersonation API existe desde v1.2.1. Endpoint para listar sessions activas + revocar.
- Web: `/admin/security/impersonation/` list de sessions activas (Actor → Target, Started, Reason, Status) + revoke action
- History view: sessions pasadas con duration
- Audit: `impersonation.session.revoked`
- RBAC: `security.impersonation.manage`

**S3.4 · Frente E del R4 MFA end-user wizard** (M-L) — **junto con S3.1 por coherencia UX**
- Web: nuevo wizard `/profile/security/mfa/enroll` con:
  - Step 1: QR code + manual code + authenticator app recommendation
  - Step 2: verify TOTP
  - Step 3: recovery codes (generated once, download/copy, requires acknowledgment)
- `/profile/security/sessions` — list de sesiones activas del user (device, IP, location, last activity) + revoke
- `/profile/security/recovery-codes/regenerate` — flow con MFA step-up
- Password policy display (read-only requirements pulled from backend)
- MFA login field integrado en existing auth flow
- i18n completo EN/ES/PT
- Playwright E2E: enroll MFA → verify → login → revoke session

**S3.5 · Retention admin page** (M) — cierra R4 Frente C
- Backend: retention infra ya existe (`Pro.Storage.Common.Retention` desde v1.8.0-pro + per-tenant override si existe).
- Web: `/admin/retention/` con:
  - DryRun toggle (PlatformAdmin)
  - Per-target overview (session_events, completed_sessions, call_attempts, dialer_contacts, analytics_interval_snapshots, agent_assist_sessions, call_analysis_results)
  - Current retention window display + configurable (si backend permite per-target override)
  - Last execution status + rows purged counter (desde meter)
  - Manual trigger action "Run now (dry-run)" + "Run now (purge)"
- Audit: `retention.manual_triggered` / `retention.config_changed` / `retention.dryrun_toggled`
- RBAC: `retention.read` / `retention.manage`

### Ship criteria R5.2

- ✅ Test counts — Platform ~1,810 (+45), Web Vitest 105 (+20), Platform E2E 290 (+15 Playwright)
- ✅ Full-stack smoke: admin ve MFA enrollment status de todos los users + force-reset + audit viewer con filters + impersonation lifecycle + retention dryrun flip + end-user enrolls MFA
- ✅ SOC 2 narrative: demo mostrando "every privileged action is audited with actor + timestamp + target + before/after" acceptable
- ✅ GH Releases Platform 1.11.0 + Web 1.10.0

### QA gap post-R5.2 (2 semanas)

Periodo de baking. Mismo patrón.

---

## Release 3 — R5.3 "Admin Completeness + R4 Closure"

**Duración:** 1.5-2 semanas
**Bumps finales:** Platform 1.12.0 · Web 1.11.0
**Ship target:** GH Release público "Admin workflows complete + R4 Track A fully materialized — zero support tickets for day-2 ops"

### Scope (7 items + R4 closure)

**S4.1 · Tenant Settings editor** (M)
- Backend: `GET/PUT /management/tenants/{tenantId}/settings` existe. Verificar shape + silent-skip fix en `TenantSettingsEndpoints.cs:366`.
- Web: `/admin/tenants/{id}/settings/` con form por sección (General, Security, Features, Billing overrides)
- Audit: `tenant.settings.changed` con diff
- RBAC: `tenant.settings.write`

**S4.2 · Dunning pause toggle** (S)
- Backend: endpoint existe (`POST /management/tenants/{id}/dunning/pause`). Add `POST /dunning/resume` si falta.
- Web: integrar toggle en existing tenant-detail page (no página nueva) con reason field
- Audit: `billing.dunning.paused` / `billing.dunning.resumed`
- RBAC: `billing.dunning.manage`

**S4.3 · Partner Customer suspend/activate** (S)
- Backend: endpoints existen (`POST /{id}/activate`, `POST /{id}/suspend`). Hook ya existe pero sin UI.
- Web: añadir actions a existing partner customer detail page con reason field + ConfirmDialog
- Audit: `partner.customer.suspended` / `partner.customer.activated`
- RBAC: `partner.customer.lifecycle`

**S4.4 · Partner Revenue dashboard** (M)
- Backend: `GET /partner/billing/revenue` + `GET /revenue/details` existen.
- Web: `/partner/revenue/` dashboard con StatCards (MRR, ARR, churn, new customers, at-risk) + chart breakdown por tier/customer
- Export CSV para accounting
- RBAC: `partner.revenue.read`

**S4.5 · Retention Policy per-tenant viewer** (S)
- Backend: `GET /management/tenants/{id}/retention` existe.
- Web: integrar en existing tenant-detail page como tab "Retention" (read-only summary de policies aplicables a ese tenant)
- RBAC: `tenant.retention.read`

**S4.6 · Ω-3 drill-down** (S) — cierra R4 Ω track
- Enriquecer `qa-detail-drawer.tsx` para surface Pro.CallAnalytics summary narrative + compliance violation list + sentiment per-turn timeline + topics
- Verificar si `QaDetail` backend type ya incluye todos los campos; audit primero
- Posibles extensions al endpoint `/api/v1/analytics/qa/{sessionId}` si falta campo
- Vitest +4

**S4.7 · R4 Playwright E2E T27 bridge** (S) — cierra R4 acceptance criterion
- Spec Playwright gated `E2E_FULL_STACK=true`:
  - Login supervisor → abrir `/analytics/speech`
  - Trigger conversation close via API (existing test fixture)
  - Assert `['call-analytics']` query invalidation fires within 500ms (verify re-fetch network request or data change on page)
- Gate patrón como `realtime-presence.spec.ts`

**S4.8 · Sub-B Web Sync residual** (S-M si cabe, sino deferred a R5.4)
- Revisar Frente F del R4 skeleton: cases + canned responses + i18n keys residuales
- Scope guard: si infla R5.3 más allá de 2 sem, punta a R5.4 patch release

### Ship criteria R5.3

- ✅ Test counts — Platform ~1,830 (+20), Web Vitest 125 (+20), Platform E2E 310 (+20 Playwright incluyendo T27 E2E)
- ✅ Full-stack smoke: tenant settings editable + dunning pause funciona + partner customer lifecycle visible + revenue dashboard + retention policy tab + Ω-3 drill-down rich + T27 E2E green
- ✅ **R4 Track A (SDK 1.15 + Pro 1.10 + Platform 1.9 + Web 1.9) declared COMPLETE en memoria y docs**
- ✅ GH Releases Platform 1.12.0 + Web 1.11.0

### QA gap post-R5.3 (2 semanas)

Periodo de baking + scoping del pen-test engagement R5.4 (selección vendor, NDA, scope letter, target environment provisioning).

---

## Release 4 — R5.4 "Production Validation"

**Duración:** 3 semanas (calendar-bounded por engagement externo de pen-test, ~1.5 sem efectivo de dev paralelizado en otros frentes).
**Bumps finales:** Platform 1.13.0 · Pro 1.13.x-pro (si se necesita nuevos meters / dashboards en hardening) · operations docs nuevos.
**Ship target:** GH Release público "Production-validated platform — load-tested SLAs published, security-audited, day-1 operator can deploy from docs alone".
**Origen:** D-FORCE-2 del post-ship triage (2026-04-25). Fragmentar evita inflar R5.3 con scope ortogonal y deja un release autónomo dedicado a credibilidad enterprise.

### Scope (9 items, 4 tracks paralelos)

**Track A · Performance & SLOs** *(parallel, ~1 sem)*

**S5.1 · Load test baseline** (M)
- Suite NBomber (.NET-native, AOT-compatible) cubriendo: JWT issuance/validation throughput · Queue ingestion (1,000 calls/min) · Realtime presence broadcast (3-node cluster, 500 agents) · LiveQueueSnapshotWriter (5 Hz × 100 queues) · AgentAssist session start/STT throughput.
- Output JSON results + Markdown summary en `docs/operations/load-test-baseline.md`.
- Reproducible via `scripts/load-test.sh` con docker-compose target.
- Salidas alimentan **S5.7** (capacity planning) y **S5.2** (SLOs basados en datos reales, no aspirational).

**S5.2 · SLO definitions** (S)
- Documentar en `docs/operations/slos.md`: availability (99.5% baseline / 99.9% enterprise tier), latency p50/p99 por endpoint crítico, error rate ceiling per service class.
- Source attribution: cada SLO referencia el meter/counter que lo mide (de los 17 Pro meters + Platform meters existentes).
- Alert rules de **S5.3** se derivan de estos SLOs.

**S5.3 · Prometheus alert rules baseline** (S)
- Ship `docs/operations/alerts.yml` con 15-20 reglas pre-configuradas cubriendo todos los ActivitySources + Meters críticos.
- Severity classification (P0 page on-call, P1 ticket, P2 review).
- Operator runbook entry corto por cada alert: "what, why, first response".

**Track B · Security & Compliance** *(parallel calendar, ~3 sem por engagement externo)*

**S5.4 · Pen-test engagement + remediation cycle** (L, calendar-bounded)
- Engagement externo 2-3 sem: scope OWASP Top 10 + multi-tenant isolation + JWT/MFA/impersonation + audit log integrity.
- Findings → tickets internos por severidad. P0/P1 son blockers de R5.4 ship; P2/P3 pasan a v1.13.x patches.
- Public-facing security report summary (redacted) — sales asset.
- Remediation cycle ~0.5 sem dev work post-engagement.

**S5.9 · JWT multi-key rotation completion** (M) — cierra C.1 del triage
- Finalizar v1.9.2 partial impl: inject multiple validation keys simultáneamente (old + new valid en rolling window).
- Admin endpoint `POST /management/security/jwt/rotate-key` con audit + observability.
- Integration con `Asterisk.Platform.Identity.Redis` para cache compartido cluster-wide.
- Tests: rotation E2E con 2 nodes simulados, zero downtime claim verified.

**Track C · Operator Onboarding & Docs** *(parallel, ~1 sem)*

**S5.5 · Getting Started guide** (M)
- `docs/getting-started.md` 10-min Docker compose to running tenant.
- `docs/operations/first-deploy.md` 30-min "first call" walkthrough (PJSIP register + queue assign + agent answer).
- `docs/operations/first-realistic-demo.md` 1-hour seed demo (multi-tenant + queues + agents + first analytics + AgentAssist live).
- Smoke verification: nuevo dev clones repo + sigue el guide cold + reporta tiempo real para cada milestone.

**S5.6 · OpenAPI HTML exposure** (S)
- Wire Swashbuckle en Platform.Api (`/swagger/v1/swagger.json` + UI rendered via Scalar o Redoc — modern UX).
- Tagged operations + DTO schemas completos.
- Config: enabled in Development always, opt-in in Production via `Platform__OpenApi__Enabled=true`.
- Linked from Getting Started.

**S5.7 · Capacity planning baseline** (S) — alimentado por S5.1
- `docs/operations/capacity-planning.md`: single-node limits (concurrent calls, agents, queues) · 3-node cluster limits · resource sizing (CPU/RAM/disk/network) per scale tier (small/medium/large/XL).
- Tablas backed by S5.1 load tests, no estimaciones.
- Recomendaciones de Postgres tuning / Redis sizing por tier.

**Track D · Operational Resilience** *(parallel, ~0.5 sem)*

**S5.8 · Backup/DR runbook** (S)
- `docs/operations/backup-disaster-recovery.md`: Postgres backup strategy (pg_dump cron + WAL archive + PITR) · Redis snapshot strategy · recovery procedures (full restore vs PITR).
- Test recovery exercise documentado: chaos test mensual contra staging — restore + verify integrity en <30 min target.
- Sample scripts/cron entries para cada estrategia.

### Ship criteria R5.4

- ✅ Test counts — Platform ~1,850 (+20), `docs/operations/` +5 nuevos guides (load-test-baseline, slos, alerts, capacity-planning, backup-disaster-recovery), Getting Started + first-deploy + first-realistic-demo guides.
- ✅ S5.1 load test baseline reproducible: `scripts/load-test.sh` corre y produce JSON + Markdown.
- ✅ S5.4 pen-test report cerrado (P0/P1 fixed, P2/P3 logged como tickets v1.13.x).
- ✅ S5.6 `/swagger` accesible en dev + opcional en prod con OpenAPI + DTO schemas completos.
- ✅ S5.8 chaos test mensual documentado y al menos un exercise corrido en staging.
- ✅ GH Release Platform 1.13.0 con narrativa marketing "production-validated".

---

## R1.5 SDK v1.15.1 "VoiceAi Refresh" (paralelo oportunista)

**Estado:** rama paralela `r1.5-voiceai-refresh` (pre-creada, pendiente ejecución)
**Scope:** aditivo SDK, zero deps con R5
**Duración:** ~1 semana con subagent background

### Scope
- **ElevenLabs Flash 2.5 TTS** — nuevo modelo, `<150ms TTFA`
- **Deepgram Aura 2 TTS** — refresh modelo
- **Whisper V3 local** — nuevo provider STT air-gapped (usa `Whisper.net` o equivalente)

### Ship strategy
- Ejecutar en background durante QA gaps de R5.1 y/o R5.2
- Ship oportunista cuando esté listo (no atado a cadencia R5)
- GH Release SDK v1.15.1 aparte
- Platform.Api config se actualiza automáticamente cuando AgentAssist toggle detecta nuevos providers disponibles

---

## Branch `feat/calling-permissions` decision gate

**Estado:** 14 commits en rama SDK, huérfanos desde marzo 2026.
**Contenido:** COS (Calling Permissions System) + pattern groups + dial simulator + PbxAdmin integration.
**Scope:** M (reintegration + QA + posible UI consumer).

### Gates de decisión (al menos 1 requerido para merge)

- **Gate R5.1 post-ship:** si customer enterprise pide explícitamente COS, merge a R5.2 con UI add-on
- **Gate R5.2 post-ship:** re-evaluar — si sigue sin demanda, deprecate branch explícito con comentario "deferred indefinitely, rebase against vN if ever needed"
- **Alternativa:** merge a R1.5 SDK como feature SDK-only (sin UI Platform); Platform adopta en v1.12+

**Default si no hay gate trigger:** deprecate post-R5.3 con ADR explícito.

---

## Cross-cutting discipline (aplica a TODAS las releases)

### Observability coverage
- Cada nuevo `BackgroundService` / `IHostedService` / provider:
  - `ActivitySource` propio en `AsteriskProTracing.SourceNames` o equivalente
  - `Meter` propio con counters + histograms + observable gauges según pattern
  - Health check `tag:"ready"` con heartbeat 30s stale threshold
  - Registro en `AddAsteriskProOpenTelemetry()` / `AddAsteriskOpenTelemetry()` one-liner

### Multi-tenant correctness
- Cada abstraction nueva de state cross-tenant: inyecta `ITenantContext` + `TenantId` scoping explícito
- Query predicates siempre incluyen `TenantId`
- Tests incluyen "no leakage cross-tenant" scenario

### AOT compatibility
- `[JsonSerializable]` para todo DTO que cruza HTTP/backplane/disk
- No reflection en hot-path
- `[LoggerMessage]` para structured logging
- Static dispatch preferred

### Security discipline
- RBAC 401/403 tests por endpoint nuevo
- Audit log por mutation (actor + timestamp + before/after + redacted-if-sensitive)
- Secrets en `IDataProtectionProvider` wrap, nunca plaintext ni config.json
- Input validation en endpoint level (FluentValidation o equivalente)

### Frontend discipline
- i18n keys EN/ES/PT por cada string nuevo (revisar con `scripts/check-i18n-coverage.sh` si existe)
- Accessibility: `aria-*` attributes, keyboard nav, color contrast AA
- Responsive: mobile breakpoints validados manualmente
- Error boundary por page-level component
- Loading skeletons (no blank flashes)

### Testing discipline
- Unit tests primero (TDD pattern según superpowers:test-driven-development)
- Integration tests Testcontainers cuando toca Postgres/Redis
- Playwright E2E `E2E_FULL_STACK=true` por vertical crítico
- Sin test "Skip" sin TODO + issue tracker link

---

## Out-of-scope explicitados (NO son "olvidos", son decisiones conscientes)

| Item | Por qué NO en R5 | Destino tentativo |
|---|---|---|
| Encryption at-rest (Postgres TDE/pgcrypto) | Requiere ADR vendor decision + spec dedicada | R6 "Enterprise Compliance" |
| HSM/KMS customer-managed keys | Depende de Encryption at-rest + integration AWS/Azure/GCP KMS | R6 |
| SIEM streaming (Splunk/Datadog push) | Requiere decisión ownership (Pro.Push.Siem vs Platform) + ADR + contrato enterprise | R6 o Pro 1.10.x |
| SCIM 2.0 provisioning | SCIM spec + Okta testing; no demanda concreta | Platform 2.0 |
| IVR Visual Designer | XL scope (>3 meses); requiere UI canvas + backend DSL | Pro 2.0-pro |
| Omnichannel unified queue (chat/email/SMS) | XL cross-repo; requiere SDK base maduro | Pro 2.0-pro |
| WFM (forecasting + scheduling + adherence) | XL; esperando contrato enterprise justificante | Pro 2.0-pro |
| Cluster split-brain / Raft quorum | XL; failover simple actual OK sin demanda concreta | Pro 2.0-pro |
| IPushBackplane decision (ADR-0036) | Pre-R2 / v2.0-preview1 territory | R2 SDK |
| CallWrapUpEvent + CallRingNoAnswerEvent (AHT completeness) | SDK prerequisites, no Platform | R1.5 o R2 |
| PushActivitySource + PushMetrics adoptan SemanticConventions | Observability foundation, pre-R2 | R2 SDK o R1.5 |
| Voice bot framework / Auto-disposition / Next-best-action AI | No en roadmap; requiere product direction | TBD 2.0-pro |

---

## Aceptación final del Release Train R5

R5 se declara **COMPLETE** cuando:

1. ✅ R5.1 + R5.2 + R5.3 + R5.4 todos shippeados con GH Releases públicos
2. ✅ R4 Track A declarada COMPLETE en `docs/roadmap.md` + MEMORY.md (cierra en R5.3)
3. ✅ Full-stack Docker compose demo graba video end-to-end de 10 min cubriendo: multi-tenant login → queue ops → call in progress → supervisor dashboard real metrics → AgentAssist live transcription → admin retention view → audit viewer → MFA enrollment flow → cluster drain demo
4. ✅ CHANGELOGs consolidados entre repos
5. ✅ Grafana dashboard "Asterisk Ecosystem Full" exporta con todos los nuevos meters
6. ✅ Branch `feat/calling-permissions` resuelto (merged o explicitly deprecated via ADR)
7. ✅ R1.5 SDK VoiceAi Refresh shipped como v1.15.1 (o explicitly deferred via ADR)
8. ✅ R5.4 production-validation entregables: load-test baseline reproducible + SLOs publicados + pen-test report cerrado + Getting Started smoke verified + OpenAPI expuesto + capacity planning data-backed + backup/DR runbook con exercise mensual

---

## Principios de ejecución

1. **Subagent-Driven con FCM batching** (per CLAUDE.md global). Phase A (docs batch) → Phase B (component subagents paralelos, hasta 8-10 concurrentes post primitives consolidation) → Phase C (integration + smoke + release coordination).
2. **Spec + ADR antes de código** para cualquier abstraction pública nueva (`ILiveQueueMetricsProvider`, `IAgentAssistFeatureToggle`).
3. **TDD** por feature según superpowers:test-driven-development — tests rojos primero, luego implementación.
4. **Verification before completion** — cada commit pasa CI local antes de push.
5. **Confirmación de push** explícita del user antes de cualquier `git push`.
6. **Zero breaking changes en minors** — R5 entero es aditivo. Breaking changes se acumulan para R2/v2.0-preview1.
7. **Conventional Commits** sin `Co-Authored-By` (per user memory).

---

## Referencias

- Envelope decisión D' + sub-análisis: conversación brainstorming 2026-04-22
- S1 expanded product-final decisión: sección 2 iteración 2 (mismo día)
- Shared UI building blocks audit: Phase 0 research (mismo día)
- R4 skeleton origen: `docs/plans/active/2026-04-21-r4-platform-web-v1.9.0-value-materialization.md`
- ADR relevantes: 0006 (Pro.Resilience sunset), 0007 (Pro v1.10 + SDK 1.15 bump), 0029 (Resilience primitives MIT)
- Pro roadmap histórico: `Asterisk.Sdk.Pro/docs/roadmap.md`
- Competitive research: `Asterisk.Sdk.Pro/docs/research/*.md` (competitive / rbac / auth / realtime)

---

## Próximos pasos inmediatos

1. User review del presente spec → ajustes si necesario
2. Invocar `superpowers:writing-plans` para crear plan de implementación R5.1 Phase 0 + Phase 1 (S1.1–S1.6)
3. Commit del spec a `docs/plans/active/` y mirror en Pro si aplicable
4. Ejecución Phase 0 UI primitives → Phase 1 S1 → Phase 2 Ops bundle → ship R5.1

Plan se ejecuta según `superpowers:executing-plans` con checkpoints per sprint.
