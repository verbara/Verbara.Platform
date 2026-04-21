# Plan — Platform v1.9.1 "Resilience Coverage"

**Fecha:** 2026-04-20 · **Target:** v1.9.1 · **Tipo:** Patch — horizontal resilience wrapping, no API surface changes
**Depende de:** v1.9.0 shipped (SDK 1.15.0 + Pro 1.10.0-pro pins + `Asterisk.Sdk.Resilience` registrado en DI) · **Paralelo a:** R4 Platform.Web v1.7.0 "Value Materialization"
**Track:** A (Pre-v2 continuation) · **Estimación:** 1-2 semanas · **Repos:** `Asterisk.Platform`

---

## Context

Auditoría profunda 2026-04-20 de Platform detectó **29 call-sites** que ejecutan operaciones externas/retriables sin wrap de resilience:
- 9 channel connectors (HttpClient directos)
- 3 servicios flow/report/mail con HttpClient sin policy
- 16 BackgroundServices con DB/external calls sin retry coherente
- 3 HealthChecks sin circuit-state awareness
- 1 storage wrapper (S3/MinIO) usando AWS SDK defaults

v1.9.0 "Secure + Current" ya cubre los **3 más críticos** (webhooks + SMTP + OIDC). Esta release completa el resto para alcanzar **100% coverage** del meter `Asterisk.Sdk.Resilience` sobre call-sites retriables.

**Por qué patch y no minor:**
- Zero API surface changes (solo reemplazamos implementaciones internas por `policy.ExecuteAsync(...)`)
- Zero breaking changes para consumers de Platform.Api
- Ship rápido (~1-2 semanas) — no bloquea R4 Platform.Web (tramitándose en paralelo sobre frontend, no depende de estos cambios)
- Observabilidad comprehensiva sin feature scope

**Por qué NO meter esto en v1.9.0:**
- v1.9.0 ya carga P0 security + 3 Sub-A fixes + foundation bump — scope completo
- Tier 2 monolítico costaría 4-6 sprints y retrasaría seguridad
- Coverage horizontal se presta naturalmente a un release dedicado (single theme, repeatable pattern)

---

## Alcance

Seis frentes horizontales. Cada uno sigue el mismo pattern:

```csharp
// Antes
await httpClient.PostAsync(url, content, ct);

// Después
await _resiliencePolicy.ExecuteAsync(
    "channel.{provider}",
    async innerCt => await httpClient.PostAsync(url, content, innerCt),
    ct);
```

Policies se declaran en `Program.cs` via `services.AddKeyedSingleton<IResiliencePolicy>("channel.{provider}", ...)` con budgets apropiados por endpoint.

### A. 9 channel connectors — per-provider HttpClient wrap

Cada connector gana su propia policy con circuit/retry/timeout apropiados al SLA del provider.

| Connector | File | Policy name | Budget sugerido |
|---|---|---|---|
| Twilio SMS | `src/Asterisk.Platform.Channels.Sms/Providers/TwilioSmsProvider.cs` | `channel.twilio-sms` | circuit 5/30s + retry 3/200ms + timeout 10s |
| Twitter/X | `src/Asterisk.Platform.Channels.Twitter/TwitterConnector.cs` | `channel.twitter` | circuit 5/60s + retry 2/500ms + timeout 15s |
| Instagram | `src/Asterisk.Platform.Channels.Instagram/InstagramConnector.cs` | `channel.instagram` | circuit 5/60s + retry 2/500ms + timeout 15s |
| Telegram | `src/Asterisk.Platform.Channels.Telegram/TelegramConnector.cs` | `channel.telegram` | circuit 5/45s + retry 3/300ms + timeout 10s |
| Messenger | `src/Asterisk.Platform.Channels.Messenger/MessengerConnector.cs` | `channel.messenger` | circuit 5/60s + retry 2/500ms + timeout 15s |
| WhatsApp | `src/Asterisk.Platform.Channels.WhatsApp/WhatsAppConnector.cs` | `channel.whatsapp` | circuit 5/60s + retry 2/500ms + timeout 15s |
| Video | `src/Asterisk.Platform.Channels.Video/VideoConnector.cs` | `channel.video` | circuit 3/60s + retry 2/1s + timeout 30s |
| RCS | `src/Asterisk.Platform.Channels.Rcs/RcsConnector.cs` | `channel.rcs` | circuit 5/60s + retry 2/500ms + timeout 15s |
| Email (HTTP) | `src/Asterisk.Platform.Api/Services/HttpEmailService.cs` + `HttpEmailTemplateService.cs` | `channel.email-http` | circuit 5/45s + retry 3/300ms + timeout 10s |

**Acceptance:**
- Cada connector emite `retry_attempts{policy=channel.{provider}}` bajo fallo transitorio
- Integration tests (Testcontainers donde aplica) verifican retry + eventual failure mode
- Regression: tests existentes de cada connector siguen green

### B. 3 flow/service wrappers

| Servicio | File | Policy name | Budget |
|---|---|---|---|
| User-defined flow HTTP | `src/Asterisk.Platform.Flows/Nodes/HttpRequestNodeHandler.cs` | `flow.http-request` | circuit 3/60s + retry 2/500ms + timeout configurable (expose via flow config) |
| PDF report rendering | `src/Asterisk.Platform.Api/Services/Reports/HttpPdfReportRenderer.cs` | `report.pdf-render` | circuit 3/120s + retry 1/1s + timeout 30s |
| Graph mailbox ops | `src/Asterisk.Platform.Mail/Services/GraphMailboxService.cs` | `mail.graph` | circuit 5/60s + retry 2/500ms + timeout 20s |
| Graph token refresh | `src/Asterisk.Platform.Mail/Services/TokenRefreshService.cs` | `mail.token-refresh` | circuit 3/120s + retry 3/1s + timeout 15s |

**Acceptance:**
- `HttpRequestNodeHandler` expone timeout configurable al flow designer (frontend side → R4)
- `TokenRefreshService` ya no silently-swallows exceptions (log + metric emitted)

### C. 16 BackgroundServices — wrap DB/external calls

Policies genéricas `worker.{service-name}` con budget base `circuit 5/60s + retry 2/500ms + timeout 10s`. Ajustes per-service donde corresponda.

| BackgroundService | File | Wrap |
|---|---|---|
| Conversation timeout | `src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs` | `_conversationStore.*` + `_switchboard.*` calls |
| Queue distribution | `src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs` | `_conversationStore.*` + `_agentSelector.*` calls |
| Dunning | `src/Asterisk.Platform.Billing/DunningService.cs` | invoice/tenant store calls |
| Report scheduler | `src/Asterisk.Platform.Api/Services/Reports/ReportSchedulerService.cs` | report generation + email send |
| Bot analytics persistence | `src/Asterisk.Platform.Api/Services/BotAnalyticsPersistenceService.cs` | DB appends |
| Asterisk capacity sync | `src/Asterisk.Platform.Api/Services/AsteriskCapacitySyncService.cs` | AMI/ARI calls |
| Retention purge | `src/Asterisk.Platform.Api/Services/RetentionPurgeService.cs` | DB DELETE batches |
| Audit retention | `src/Asterisk.Platform.Api/Services/AuditRetentionService.cs` | DB DELETE batches |
| Realtime state bridge | `src/Asterisk.Platform.Api/Services/RealtimeStateBridge.cs` | push bus publish |
| Campaign metrics poller | `src/Asterisk.Platform.Api/Services/CampaignMetricsPoller.cs` | Pro.Dialer query calls |
| Agent assist bridge | `src/Asterisk.Platform.Api/Services/AgentAssistBridge.cs` | Pro.AgentAssist event subscription |
| Timer polling | `src/Asterisk.Platform.Automation/TimerPollingService.cs` | timer store calls |

**Nota:** `WebhookDeliveryService` ya migrado en v1.9.0. `BackgroundServiceHealthCheck` cubierto en frente D.

**Acceptance:**
- Cada worker ya no silently-swallows exceptions transitorios — policy captura, logea, emite métrica
- Regression: workers siguen progresando (no blocked por circuit abierto permanente — verificar reset semantics)

### D. 3 HealthChecks con circuit-state awareness

Pattern: si la operación subyacente está protegida por policy `foo`, el HealthCheck consulta `IResilienceRegistry.GetState("foo")` para reportar `Degraded` cuando circuit `Open` (en lugar de timeout propio).

| HealthCheck | File | Circuit-watched policies |
|---|---|---|
| AMI | `src/Asterisk.Platform.Api/Health/AsteriskAmiHealthCheck.cs` | `asterisk.ami.*` (de SDK) — report Degraded si circuit Open > 5min |
| Postgres | `src/Asterisk.Platform.Api/Health/PostgresHealthCheck.cs` | wrap query-test con policy `healthcheck.postgres` (timeout 2s, no retry) |
| Background services | `src/Asterisk.Platform.Api/Health/BackgroundServiceHealthCheck.cs` | aggregate heartbeat + circuit states de workers (A–F) |

**Acceptance:**
- `/health/ready` devuelve JSON con breakdown per-circuit (policy name + state)
- Prometheus metric `asterisk_sdk_resilience_circuit_state{policy,state}` observable en `/metrics`

### E. S3/MinIO storage wrapper

| File | Policy name | Budget |
|---|---|---|
| `src/Asterisk.Platform.Media/S3MediaStorage.cs` | `storage.s3` | circuit 5/60s + retry 3/500ms + timeout 30s (separate para upload vs get vs delete si hace falta) |

Wrap `UploadAsync`, `DownloadAsync`, `DeleteAsync`. Mantener AWS SDK retry defaults **deshabilitados** (`RetryMode.Standard` → `RetryMode.None` en config) para evitar double-retry.

**Acceptance:**
- Integration tests con LocalStack/MinIO verifican retry + eventual failure
- Métricas emiten per-operation (upload vs download)

### F. Unified Grafana starter dashboard

Crear `docs/operations/dashboards/resilience-overview.json` — dashboard Grafana que visualiza:
- `retry_attempts{policy}` time series (top 10 policies by volume)
- `circuit_opened_total{policy}` + `circuit_closed_total{policy}` counters
- `circuit_state{policy}` heatmap (open/closed per policy)
- `timeout_fired_total{policy}` counter
- Per-tenant breakdown (si el meter emite `tenant.id`)

Pairing con docs: `docs/operations/resilience-runbook.md` — interpretación de métricas + troubleshooting typical (p.ej. "¿cómo investigo retry storms en channel.whatsapp?").

**Acceptance:**
- Dashboard importable via Grafana UI (JSON valid)
- Runbook contiene 5+ scenarios con queries PromQL ejemplo

---

## Criterios de éxito (Acceptance global)

- ✅ `dotnet build Asterisk.Platform.slnx /warnaserror` — 0 warnings
- ✅ `dotnet test` — baseline preserved (v1.9.0 ~1,623+ tests) + nuevos tests per-wrapper
- ✅ Meter `Asterisk.Sdk.Resilience` emite para **todas** las 29+ policies declaradas (verificable vía scrape de `/metrics` + assertion en integration test)
- ✅ Grep `HttpClient.*PostAsync\|SendAsync` SIN `ResiliencePolicy.ExecuteAsync` en contexto → 0 matches en Platform.Channels.*, Platform.Mail, Platform.Api/Services/Reports
- ✅ Grep retry-loop patterns (`for.*attempt`, `while.*retry`, custom `CircuitBreakerPolicy`) → 0 matches
- ✅ `/health/ready` expone circuit-state breakdown (D)
- ✅ Grafana dashboard JSON importable (F)
- ✅ Docker compose full.yml green
- ✅ Zero API surface changes (`dotnet publicapi diff` muestra empty diff para Platform.Api)

---

## Archivos críticos (agrupados por frente)

**A (9 connectors):**
`Platform.Channels.Sms/Providers/TwilioSmsProvider.cs`, `Platform.Channels.Twitter/TwitterConnector.cs`, `Platform.Channels.Instagram/InstagramConnector.cs`, `Platform.Channels.Telegram/TelegramConnector.cs`, `Platform.Channels.Messenger/MessengerConnector.cs`, `Platform.Channels.WhatsApp/WhatsAppConnector.cs`, `Platform.Channels.Video/VideoConnector.cs`, `Platform.Channels.Rcs/RcsConnector.cs`, `Platform.Api/Services/HttpEmailService.cs` + `HttpEmailTemplateService.cs`

**B (3 services):**
`Platform.Flows/Nodes/HttpRequestNodeHandler.cs`, `Platform.Api/Services/Reports/HttpPdfReportRenderer.cs`, `Platform.Mail/Services/GraphMailboxService.cs` + `TokenRefreshService.cs`

**C (12 BGs):**
Ver tabla §C.

**D (3 HCs):**
`Platform.Api/Health/AsteriskAmiHealthCheck.cs`, `PostgresHealthCheck.cs`, `BackgroundServiceHealthCheck.cs`

**E (1 storage):**
`Platform.Media/S3MediaStorage.cs`

**F (docs/ops):**
`docs/operations/dashboards/resilience-overview.json` (nuevo), `docs/operations/resilience-runbook.md` (nuevo)

**DI wiring (único Program.cs):**
`src/Asterisk.Platform.Api/Program.cs` — registrar ~29 keyed `IResiliencePolicy` (podemos factorizar en `ResiliencePolicyRegistration.cs` si >50 líneas).

---

## Verification

```sh
cd /media/Data/Source/IPcom/Asterisk.Platform

dotnet restore
dotnet build Asterisk.Platform.slnx --nologo /warnaserror
dotnet test Asterisk.Platform.slnx --filter "FullyQualifiedName!~Postgres"
dotnet test tests/Asterisk.Platform.IntegrationTests/

# Docker E2E con LocalStack para S3
docker compose -f docker/docker-compose.full.yml -f docker/docker-compose.localstack.yml up --build --abort-on-container-exit

# Metrics verification — todas las policies emiten
curl -s http://localhost:8080/metrics | grep asterisk_sdk_resilience_retry_attempts_total | wc -l
# Expected: ≥ 29 (una por policy registrada)

# Grep hygiene — ningún HttpClient externo sin wrap
grep -rE 'httpClient\.(Post|Get|Send)Async' src/Asterisk.Platform.Channels.* | grep -v ResiliencePolicy
# Expected: 0 matches

# Health check formato nuevo
curl -s http://localhost:8080/health/ready | jq '.entries'
# Expected: breakdown per circuit
```

---

## Commits esperados (~18-22)

1. `docs(plans): mirror v1.9.1 plan to active/`
2. `feat(resilience): register per-provider policies in Program.cs` (DI wiring)
3. `refactor(channels/sms): wrap TwilioSmsProvider with ResiliencePolicy`
4. `refactor(channels/twitter): wrap TwitterConnector with ResiliencePolicy`
5. `refactor(channels/instagram): wrap InstagramConnector with ResiliencePolicy`
6. `refactor(channels/telegram): wrap TelegramConnector with ResiliencePolicy`
7. `refactor(channels/messenger): wrap MessengerConnector with ResiliencePolicy`
8. `refactor(channels/whatsapp): wrap WhatsAppConnector with ResiliencePolicy`
9. `refactor(channels/video): wrap VideoConnector with ResiliencePolicy`
10. `refactor(channels/rcs): wrap RcsConnector with ResiliencePolicy`
11. `refactor(channels/email-http): wrap HttpEmail services with ResiliencePolicy`
12. `refactor(flows): wrap HttpRequestNodeHandler with ResiliencePolicy`
13. `refactor(reports): wrap HttpPdfReportRenderer with ResiliencePolicy`
14. `refactor(mail): wrap GraphMailboxService + TokenRefreshService with ResiliencePolicy`
15. `refactor(workers): wrap 12 BackgroundServices with ResiliencePolicy`
16. `refactor(health): add circuit-state awareness to AMI/Postgres/BG health checks`
17. `refactor(media): wrap S3MediaStorage operations with ResiliencePolicy`
18. `docs(operations): add resilience Grafana dashboard + runbook`
19. `test(integration): add retry/circuit assertions per wrapper`
20. `chore(release): bump Platform 1.9.0 → 1.9.1 + CHANGELOG`

---

## Permissions previstos

- `Bash`: `dotnet` (build/test/pack), `docker compose` (up/down/logs), `git` (status/log/diff — NO push sin confirmación), `curl` + `jq` (metric validation)
- `Edit`/`Write`: `src/**`, `tests/**`, `docs/**`, `Directory.Build.props` (solo version bump)
- **No push sin confirmación explícita del usuario**

---

## Kickoff (tras v1.9.0 shipped)

1. Confirmar que v1.9.0 está shipped + tag `v1.9.0` pushed (pre-requisito)
2. `git pull origin main` — sync
3. **Ejecución Subagent-Driven con FCM batching:**
   - **Phase A (Foundation, batch):** DI wiring (commit 2) + docs skeleton (commit 1) + common resilience infrastructure
   - **Phase B (Critical wrappers, subagents paralelos — hasta 4 simultáneos):**
     - Subagent 1: 9 channel connectors (frente A, commits 3-11)
     - Subagent 2: 3 flow/service wrappers (frente B, commits 12-14)
     - Subagent 3: 12 BackgroundServices (frente C, commit 15)
     - Subagent 4: 3 HealthChecks + S3 (frentes D+E, commits 16-17)
   - **Phase C (Integration, batch):** Grafana dashboard + runbook + integration tests + release (commits 18-20)

---

## Scope fuera de v1.9.1

- **Per-tenant isolation** (policies custom por tenant SLA) — diferido a v2.0 o feature-driven
- **Adaptive timeouts** (history-based per endpoint) — diferido
- **Fallback chains** (webhook → email on failure) — diferido
- **Integration con Pro.Dialer circuit state** (trunk health) — diferido (Pro.Dialer ya tiene su propio `CircuitBreakerState`, coordinación se evalúa en R2 Event Model v2)

---

## Referencias

- **R3 v1.9.0 plan:** [`2026-04-20-r3-platform-v1.9.0-secure-current.md`](./2026-04-20-r3-platform-v1.9.0-secure-current.md)
- **Track A execution order:** [`../../../../Asterisk.Sdk.Pro/docs/plans/active/2026-04-20-track-a-execution-order.md`](../../../../Asterisk.Sdk.Pro/docs/plans/active/2026-04-20-track-a-execution-order.md)
- **ADR-0029 Pro.Resilience sunset:** [`../../../../Asterisk.Sdk.Pro/docs/decisions/0006-pro-resilience-sunset.md`](../../../../Asterisk.Sdk.Pro/docs/decisions/0006-pro-resilience-sunset.md)
- **Asterisk.Sdk.Resilience (MIT) source:** `/media/Data/Source/IPcom/Asterisk.Sdk/src/Asterisk.Sdk.Resilience/`
