# Changelog

All notable changes to **Asterisk.Platform** are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased] — R5.1 Task H — Live Queue Metrics wiring

### Added

- **`GET /operations/queue-metrics`** now returns real-time `Waiting` +
  `AvgWaitSeconds` values sourced from the Pro.Analytics.Live
  `ILiveQueueMetricsProvider` (Asterisk.Sdk.Pro v1.12.0-pro). When the
  provider is unregistered or has no snapshot for a queue, the fields
  return `null` (instead of the previous hardcoded `0`) and the response
  sets `X-Metrics-Available: false` so clients can render placeholder UI.
- `AddAsteriskProAnalyticsLive()` + `UsePostgresProAnalyticsLive(...)`
  wired in `Program.cs`. Connection string: new
  `ASTERISK__ANALYTICS__LIVE__CONNECTION` config key with fallback to the
  shared Analytics connection string (same DB).
- `QueueMetricsDto.Waiting` + `QueueMetricsDto.AvgWaitSeconds` are now
  nullable (`int?` + `double?`). `QueueMetricsDto` + `QueueMetricsDto[]`
  registered in `ApiJsonContext` for AOT JSON serialization.

### Changed

- Pro pin bumped from `1.11.0-pro` → `1.12.0-pro` across 21
  `Directory.Packages.props` entries.

### Known limitations

- Platform currently registers `AddAsteriskAnalytics()` as a process-scope
  singleton with an empty `DefaultTenantId`, so `LiveQueueSnapshotWriter`
  persists rows with `tenant_id=""`. The endpoint therefore queries the
  provider with `tenantId=""` so it can read back the rows the writer
  produced. A per-tenant scope refactor is tracked as a follow-up
  (future Platform patch / R5.2) and is out of scope for Task H.

---

## [1.9.3] — 2026-04-21 — Speech Analytics + Compliance Aggregations API

Adds `/api/v1/call-analytics/*` endpoint group with aggregation-focused
operations that complement the existing `/api/v1/analytics/qa` list+detail
endpoints (which already expose Pro.CallAnalytics raw results):

### Added

- **`GET /api/v1/call-analytics/topics/trends`** — Speech Analytics: top
  topics over a date range, sorted by occurrence count with average
  confidence. Foundation for a supervisor-facing topic trends dashboard.
- **`GET /api/v1/call-analytics/sentiment/trends`** — time-bucketed
  (day or ISO week) sentiment aggregation: avg score + positive/neutral/
  negative counts per bucket. Enables tracking tenant / queue sentiment
  evolution over time.
- **`GET /api/v1/call-analytics/compliance/summary`** — compliance
  violations grouped by (RuleId, Severity) with occurrence +
  sessions-affected counts + first/last seen timestamps + severity
  breakdown totals. Compliance-officer view complementing the per-session
  violations already in `/api/v1/analytics/qa` detail.
- All three endpoints gated by `SupervisorPlus` authorization policy
  and `LicenseFeature.Analytics` license gate. Returns `503` when
  `ICallAnalyticsStore` is not registered in DI.
- `CallAnalyticsEndpoints.cs` — 7 AOT-safe DTOs (`TopicTrendDto`,
  `TopicTrendsResponse`, `SentimentTrendPointDto`, `SentimentTrendsResponse`,
  `ComplianceRuleSummaryDto`, `ComplianceSeverityBreakdownDto`,
  `ComplianceSummaryResponse`) registered in `ApiJsonContext`.
- `CallAnalyticsEndpointTests.cs` — 6 tests covering topic trend
  aggregation, sentiment day-bucketing, queue filter acceptance,
  compliance rule aggregation, severity filter, severity breakdown totals,
  and 401 auth guard.

**Note** — an initial iteration of this endpoint group (shipped in
commits ca84105 + bd5c498) duplicated the existing `/api/v1/analytics/qa`
list+detail functionality and was refactored forward in this release to
aggregations only. No duplicated routes ship in v1.9.3.

---

## [1.9.2] — 2026-04-21 — "Hardening Follow-Through" (R3c)

Closes the five orthogonal security / compatibility concerns that v1.9.0
and v1.9.1 audits explicitly deferred to this patch. Zero API surface
breakage — ships safely in parallel with R4 Platform.Web.

### Security

- **JWT tokens now carry `jti` claims** (`GenerateAccessToken` +
  `GenerateImpersonationToken`). Enables future revocation flows via
  the new `IJtiRevocationCache` (in-memory impl shipped;
  `ValidateTokenAsync` consults the cache after standard validation
  and returns `null` for revoked tokens).
- **Signing key is now wrapped at rest via `DataProtection`.** Existing
  deployments with plaintext `jwt-signing-key.xml` are migrated silently
  on first restart — the file is read, re-encrypted, and overwritten.
  No config change required.
- **`kid` header is now derived from the key fingerprint**
  (`platform-jwt-<16 hex>` from SHA-256 of the public modulus). Survives
  restarts, changes on key rotation.
- **Removed the `?token=` query-string fallback in
  `ApiKeyAuthenticationHandler`.** API keys must now be presented via
  the `Authorization: Bearer` header only. Key leakage via access logs,
  referer headers, and browser history is blocked.
- **OIDC callback now enforces tenant MFA policy** before issuing
  tokens. Two new redirect branches:
  - `#oidc_mfa_enrollment_required&...` when the policy requires MFA
    for the user's role but the user has not enrolled.
  - `#oidc_mfa_challenge&challenge_token=...` when the user is enrolled
    and must complete TOTP verification; the existing
    `/auth/mfa/verify` endpoint handles the challenge unchanged.
  Frontend fragment handlers are needed to surface these redirects to
  the user — R4 Platform.Web will land the UI side.
- **`/auth/change-password` now requires MFA step-up** when the user
  has MFA enrolled. `ChangePasswordRequest` gains an optional `MfaCode`
  field; when the user has MFA enabled and the code is missing, the
  endpoint returns 401 with a new `MfaStepUpRequiredResponse` body
  (`{ mfaStepUpRequired: true, reason: "…" }`). An invalid code
  returns 401. MFA is checked before the old-password verification to
  avoid burning the password-guess budget on a pre-MFA attack.

### Changed

- **`IMfaPolicyEvaluator`** extracted from `AuthEndpoints`'
  private static helper. Now lives in `Asterisk.Platform.Identity.Mfa`
  and is injected into `AuthEndpoints.Login`, `AuthEndpoints.Refresh`,
  `AuthEndpoints.ApiKeyLogin`, and `OidcEndpoints.OidcCallback`.
  Behavior identical to v1.9.0 / v1.9.1 — this is a pure refactor that
  opens the extension point for policy overrides.
- **`IMfaPendingCache` + `IPasswordResetCache`** extracted from the
  static `ConcurrentDictionary` fields in `AuthEndpoints`. In-memory
  implementations in `Asterisk.Platform.Identity.Mfa` preserve the
  previous semantics; `TakeAsync` atomically removes-and-returns.
  `MfaPendingEntry` and `PasswordResetEntry` records move from
  `internal` in `Asterisk.Platform.Api` to `public` in
  `Asterisk.Platform.Identity.Mfa`.

### Added

- **Asterisk 23 Standard build support** — `docker/Dockerfile.asterisk`
  now accepts an `ASTERISK_VERSION` build argument (default 22), and
  `docker-compose.full.yml` forwards it via `ASTERISK_VERSION` env var.
  The codec_opus download URL + directory name are parameterized.
  Default behavior is unchanged: `docker compose up --build` still
  builds Asterisk 22 LTS. Test both with
  `ASTERISK_VERSION=23 docker compose -f docker/docker-compose.full.yml build asterisk`.
- **Interface contract tests** for `InMemoryMfaPendingCache`,
  `InMemoryPasswordResetCache`, and `InMemoryJtiRevocationCache` in
  `Asterisk.Platform.Identity.Tests` and `Asterisk.Platform.Api.Tests`.

### Known limitations / deferred

- **No Redis-backed cache implementation yet.** `IMfaPendingCache` and
  `IPasswordResetCache` create the extension point; Redis wiring lands
  in v1.9.3 when a concrete multi-instance deployment driver emerges.
  Until then, MFA challenges initiated on one instance will not be
  redeemable on another if a failover occurs mid-flow.
- **Full multi-key JWT rotation** (simultaneous old + new valid keys
  during a rolling window) is not included. `kid` is fingerprint-based
  so it survives restarts, but key rotation still requires an
  in-flight-tokens flush. Full rotation deferred to v1.10+.

### Tests

- +22 new tests (8 JWT hardening, 3 IMfaPolicyEvaluator, 3 OIDC MFA
  enforcement, 4 ChangePassword step-up, 8 in-memory cache contract,
  minus 4 test consolidations from the Frente C + E test-harness moves).
  All non-Postgres assemblies green — 0 failures, 0 warnings.

---

## [1.9.1] — 2026-04-21 — "Resilience Coverage" (R3b)

Horizontal completion of v1.9.0's Resilience MVP. Every remaining
external/retriable call-site on the Platform backend now emits to the
`Asterisk.Sdk.Resilience` Prometheus meter. Zero API surface changes —
this release ships safely in parallel with R4 Platform.Web.

### Added

- **9 channel connectors** (`channel.{twilio-sms|twitter|instagram|
  telegram|messenger|whatsapp|video|rcs|email-http}`) now wrap their
  outbound HttpClient calls with keyed `ResiliencePolicy` instances.
  Each connector owns a DI extension (`AddXxxResiliencePolicy()`) with
  per-provider budgets tuned to the provider's SLA.
- **3 service wrappers:** `flow.http-request` (user-defined flow HTTP
  node; per-call timeout still sourced from flow config),
  `report.pdf-render` (PDF renderer microservice), and `mail.graph` +
  `mail.token-refresh` (Microsoft Graph mailbox + OAuth token refresh
  in the Mail microservice).
- **S3 storage wrapper** — `storage.s3` policy covers
  `S3MediaStorage.UploadAsync/DownloadAsync/DeleteAsync`. AWS SDK's
  built-in retry is disabled (`MaxErrorRetry = 0`) to prevent
  double-retry (AWS retry × policy retry = 9+ attempts).
- **12 BackgroundServices** — `worker.{name}` keyed policies wrap each
  worker's inner tick work. The outer `while`/timer loop is NOT
  wrapped — a circuit-open state causes the worker to skip the current
  tick and retry on the next scheduled tick instead of crashing the
  host. `CircuitBreakerOpenException` + generic exceptions are caught
  per-tick. Workers covered: conversation-timeout, queue-distribution,
  dunning, report-scheduler, bot-analytics-persistence,
  asterisk-capacity-sync, retention-purge, audit-retention,
  realtime-state-bridge, campaign-metrics-poller, agent-assist-bridge,
  timer-polling.
- **HealthCheck upgrades** — `AsteriskAmiHealthCheck`,
  `PostgresHealthCheck`, `BackgroundServiceHealthCheck` now consult an
  `IResilienceStateObserver` (MeterListener-backed singleton that
  tracks circuit_opened_total + circuit_closed_total counters) and
  report `Degraded` when a relevant circuit has been open >60s,
  `Unhealthy` at >300s. Thresholds are configurable via
  `PlatformHealthCheckOptions`.
- **`healthcheck.postgres`** — new keyed policy (timeout 2s, no
  circuit, no retry) wrapping `PostgresHealthCheck`'s test query so
  DB-under-load surfaces as `Unhealthy` within 2s instead of hanging.
- **`/health/ready`** — now emits structured JSON via
  `HealthReportJsonWriter`, including per-policy circuit-state
  breakdown for operator visibility. Replaces the default plain-text
  ASP.NET Core response writer.
- **`docs/operations/resilience-runbook.md`** — operator runbook
  covering meter instruments, policy-key taxonomy, golden signals,
  5 troubleshooting scenarios with PromQL queries, and the worker-
  policies reference table.
- **`docs/operations/dashboards/resilience-overview.json`** — Grafana
  starter dashboard (5 panels: open circuits, retry rate, open/close
  events, timeout firings, circuit-state matrix).

### Changed

- **`RealtimeStateBridge`** — DB sync and AMI `QueuePause` are now
  wrapped as **independent** policy calls (same key, share circuit
  aggregation), preserving the v1.9.0 "best-effort" semantic where a
  DB failure does NOT prevent the AMI call. Previous bundled wrap
  broke this invariant.
- **`TokenRefreshService`** — no longer silently-swallows transient
  exceptions. Logs structured warnings + lets the policy retry; on
  exhaustion, the policy emits `retry_attempts_total` + the
  application logs a warning with structured metadata.

### Known limitations (carried forward from v1.9.0)

No changes in v1.9.1. See v1.9.0 §Known limitations — JWT hardening,
OIDC MFA enforcement, ChangePassword step-up, MFA cache cross-instance
consistency, Asterisk 23 matrix (still tracked for v1.9.2).

### Metrics

- **1,733 unit tests** across 29 assemblies, 0 failures (baseline 1,699
  from v1.9.0 + 34 new regression + contract tests for v1.9.1)
- **0 build warnings / 0 errors** with `TreatWarningsAsErrors=true`
- 7 commits since v1.9.0

---

## [1.9.0] — 2026-04-20 — "Secure + Current" (R3)

Cross-repo coordination: consumes **SDK v1.15.0 + Pro v1.10.0-pro**
(shipped 2026-04-20 as R1 Pre-v2 Foundation). This release closes two P0
security vulnerabilities, lands the foundation layer for observable
resilience, and migrates Platform onto the post-ADR-0029 MIT resilience
primitives.

### Security

- **Impersonation privilege escalation (P0).** `/management/impersonate`
  now verifies the target tenant is in the caller's tenant hierarchy
  (`ParentTenantId` walk, depth-16 cycle protection, fail-closed on
  broken chains). Platform-tenant callers retain their documented
  ability to impersonate any customer tenant; non-platform callers can
  only impersonate themselves or their descendants. Attacks where a
  Tenant A admin issued a JWT for an unrelated Tenant B are now
  rejected with `403 Forbidden` + audit entry.
- **Impersonation audit evasion (P0).** Successful impersonations now
  emit audit entries to **both** the caller tenant (action
  `impersonation_started`, preserved) and the target tenant (new action
  `impersonation_target_accessed`). Target-tenant admins gain full
  visibility of inbound impersonation events.
- **Tenant MFA policy bypass (P0).** `TenantAuthConfig.MfaPolicy` is now
  enforced on all four auth entry points — login, refresh, password
  reset, and user-bound API key authentication. Previously the policy
  was advisory: users with `MfaEnabled=false` could bypass `required_all`
  tenant policies via any of the four paths. Management-type API keys
  (machine-to-machine, `UserId=null`) remain exempt by design. New
  response DTOs `MfaEnrollmentRequiredResponse` and
  `PasswordResetMfaRequiredResponse` signal enrollment/verification
  flows to the frontend.

### Added

- **OpenTelemetry wiring.** `AddAsteriskOpenTelemetry(...)` +
  `AddAsteriskProOpenTelemetry()` + `WithPrometheusExporter()` now
  registered in `Program.cs`. Enrols the full SDK + Pro meter catalog
  (15 SDK meters including the new `Asterisk.Sdk.Resilience` + 15 Pro
  meters) and activity sources. `/metrics` endpoint is now a real
  Prometheus scraping endpoint (was a JSON stub).
- **T27 event bridges** (Pro 1.8.0-pro opt-ins): cluster / conversation
  / agent state transitions now published to `IPushEventBus` via
  `WithClusterEventBridge()` / `WithConversationBridge()` /
  `WithAgentBridge()`. Each bridge throttles per key (100ms cluster /
  50ms conversation / 200ms agent) and captures `Activity.Current` for
  W3C trace propagation.
- **Resilience MVP** — three critical external call-sites now use
  `Asterisk.Sdk.Resilience` keyed policies (pattern matches Pro engine
  precedent):
  - `WebhookDeliveryService` → policy `webhook.delivery` (circuit 5/30s,
    retry 3/500ms, timeout 10s). Wraps per-attempt `HttpClient.SendAsync`
    within the existing 8-attempt user-visible backoff schedule.
  - `SmtpSender` → policy `smtp.send` (circuit 3/60s, retry 2/1s, timeout
    15s). Replaces the hand-rolled `for (attempt = 1..2)` loop.
  - `OidcTokenExchangeService.ExchangeCodeAsync` → policy
    `oidc.token-exchange` (circuit 3/120s, retry 2/500ms, timeout 10s).
    Wraps the token endpoint `PostAsync` only; JWT validation + caching
    intentionally unwrapped.
- New `Asterisk.Platform.Mail.Tests` project (SmtpSender coverage).

### Changed

- **Bot handoff routing.** `WebhookEndpoints.cs` now calls
  `IConversationSwitchboard.TransferToQueueAsync` (drives
  `Active → Escalated → Queued`, releases agent capacity, publishes
  correct state-change event) instead of `AssignToQueueAsync` when the
  bot emits `BotResponse(BotResponseAction.TransferToQueue, queueId)`.
  The previous behavior skipped the `Escalated` transition and broke
  state-machine invariants relied on by downstream analytics and
  supervisor UX.
- **Dependencies**: SDK pinned from `1.11.1` to `1.15.0`; Pro pinned from
  `1.8.1-pro` to `1.10.0-pro` (21 refs). Added explicit
  `Asterisk.Sdk.Resilience` + `Asterisk.Sdk.OpenTelemetry` +
  `Asterisk.Sdk.Pro.OpenTelemetry` pins (previously transitive).

### Removed

- `Asterisk.Sdk.Pro.Resilience` reference. Package was sunset in Pro
  `1.9.0-pro` via ADR-0029 (migration to MIT `Asterisk.Sdk.Resilience`).
  `Program.cs` now uses `Asterisk.Sdk.Resilience.DependencyInjection`
  and `AddAsteriskResilience()`.

### Internal / tests

- Added regression tests pinning tenant-isolation invariants in
  `DefaultConversationService.GetOrCreateForContactAsync` (no production
  change — end-to-end chain was already correctly scoped).
- T27 bridges wiring contract test (`BridgeOptions.DefaultTenantId` +
  `BridgeMetrics` registration).
- 4 impersonation privilege-escalation scenarios (hierarchy check +
  dual audit).
- 10 MFA policy enforcement scenarios across all 4 auth entry points.
- Baseline preserved: **1,669 → 1,699 unit tests** (+30 across 28
  assemblies). 0 warnings, 0 errors.

### Known limitations (flagged for follow-up)

Subagent audits surfaced orthogonal hardening opportunities that are
**not** fixed in this release; each is tracked for a future session:

- JWT signing key persisted as plaintext XML on disk; no key rotation;
  no `jti` claim on impersonation tokens (no replay protection); API key
  `?token=<raw>` query-string fallback risks log leakage.
- OIDC callback (`OidcEndpoints.cs`) does **not** enforce tenant MFA
  policy — users authenticated via external IdP skip the gate.
- `ChangePassword` does **not** require MFA step-up even when policy
  requires MFA — stolen session cookie enables silent password change.
- `MfaPendingCache` / `PasswordResetCache` are in-memory
  `ConcurrentDictionary` instances; MFA challenges are lost on node
  failover in multi-instance deployments. Move to Redis / Pro.Push
  backplane in a later release.

### Asterisk version matrix

Platform continues to run against **Asterisk 22 LTS** (default). Full
smoke validation against **Asterisk 23 Standard** is pending a separate
patch release — the `docker/Dockerfile.asterisk` currently hardcodes
`andrius/asterisk:22` and the codec_opus download URL to the 22.0
series. Parameterizing via `ASTERISK_VERSION` build-arg is tracked for
**v1.9.2** alongside a CI matrix job.

---

## [1.8.1] — 2026-03-31 — "Operations"

Earlier releases are not tracked in this file. Consult
`git log --oneline v1.8.1` for historical context or the roadmap in
[`docs/`](docs/) for milestone summaries.
