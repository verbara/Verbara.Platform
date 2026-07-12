# Design: csat-runner (Platform host — CSAT consumer)

## Context

Verbara.Platform is the consumer (buildOrder 2) in the CSAT Runner train: Pro (buildOrder 1) ships
the `Verbara.Sdk.Pro.CsatRunner` engine + 4 channel adapters + `LicenseFeature.CsatRunner` + the
public `ICsatChannelAdapter` / `ICsatTemplateProvider` interfaces; Web ships the operator UI. This
design translates the frozen Platform spec + execution plan (both 2026-05-18, "APPROVED, execution
paused") into the implementation approach for the host side.

The pivotal prior decision is **Platform/ADR-0020**: a Phase-1 Explore agent found Platform already
ships a substantial Surveys domain (`src/Verbara.Platform.Surveys/` — `Survey`, `SurveyResponse`,
`SurveyAnswer`, `SurveyType` enum with a `Csat` value, `ISurveyAnalytics` with CSAT-average + NPS
math, `PostgresSurveyResponseStore`) plus admin CRUD + analytics endpoints and Web surfaces. The
original "new `csat_responses` table + parallel admin/analytics" framing duplicated ~3-4 days of
existing, production-proven code. ADR-0020 therefore makes CSAT a **brownfield extension** of the
Surveys domain, not a parallel domain.

Current baseline: Platform `2.17.0`; this change re-pins the release target to `2.18.0` (the
original `2.4.0` number froze 2026-05-18 and has been spent) and advances the Pro pin to the CSAT
engine package.

## Goals / Non-Goals

**Goals:**

- Extend `Verbara.Platform.Surveys` additively (nullable, back-compat) to persist CSAT responses —
  reuse ~60% of the Platform side from the existing Survey domain per ADR-0020.
- Own the public-facing HTTP capture surface (`POST /api/v1/csat/responses/{webchat,email,sms}`)
  and the analytics read (`GET /api/v1/analytics/csat/queues/{queueId}`) — these are Platform.Api
  responsibilities, never Pro's.
- Gap-fill the two channels the in-process engine cannot reach: inbound Email replies (IMAP poller)
  and inbound SMS digit-replies (correlator).
- Implement the Pro-defined `ICsatTemplateProvider` DI contract in Platform (same-process), backed
  by a per-tenant `csat_templates` store.
- Stay Native AOT clean (ADR-0022): 5 new DTOs source-gen registered, no reflection, NO Dapper.

**Non-Goals:**

- The CSAT engine + channel-adapter orchestration itself — that is Pro's `csat-runner` child change.
- Web admin CSAT tab, supervisor dashboard KPI card, and WebChat embed rating panel — Web's child
  change (Platform only exposes the endpoints they consume).
- Backfill of historical `survey_responses` rows — new columns are nullable; existing rows keep
  their JSONB `answers` untouched, no backfill required.

## Decisions

### D1 — Brownfield extension of `Verbara.Platform.Surveys` (ADR-0020)

Extend `SurveyResponse` with 6 nullable init-only properties (`Channel`, `QueueName`, `Rating`,
`Comment`, `CapturedAt`, `CallId`) rather than creating a parallel `csat_responses` entity. CSAT
responses persist through the existing `ISurveyResponseStore.SaveAsync`; analytics reuse
`ISurveyAnalytics` with a new `GetByQueueAndChannelAsync` overload. A well-known
`SurveyQuestionIds.CsatRating = "csat-rating-v1"` constant lets all Pro channel adapters reference
the same question id. **Alternatives rejected** (per ADR-0020): a parallel table + duplicate
admin/analytics (~3-4d duplication, split operator surface); replacing the Surveys domain (breaks
NPS/Custom consumers since v1.4.0); a Pro-side standalone domain (forces Platform to know a
Pro-private table, violates the boundary); using Surveys as-is with no schema change (JSONB
`answers` cannot be indexed for the <2s supervisor-dashboard SLA).

**Trade-off** (ADR-0020 Negative): `SurveyResponse` now models both a multi-question generic
response and a single-question rating capture; consumers reading a CSAT-flavored row read `Rating`
directly instead of parsing `Answers[csatRatingQuestionId]`.

### D2 — Wire shape frozen by the fixture

The capture payload is fixed by `fixtures/csat-response-capture.v1.json`. The `POST
/api/v1/csat/responses/webchat` body binds exactly these fields: `responseToken`, `surveyId`,
`questionId`, `channel`, `queueName`, `rating`, `comment`, `capturedAt`, `conversationId`. These map
1:1 onto the `SurveyResponse` extension + `SurveyQuestionIds.CsatRating`. The delta spec cites these
names verbatim (verbatim-fixture-citation rule).

### D3 — Postgres migration `0XX_SurveyCsatExtensions.sql` (raw Npgsql, NO Dapper)

One additive migration (number assigned at execution time — it depends on what ships between freeze
and kickoff):

- `survey_responses`: +6 nullable columns; CHECK `channel IN ('voice','webchat','email','sms')`;
  CHECK `rating BETWEEN 1 AND 5`; 2 partial indexes `(tenant_id, queue_name, captured_at DESC)` and
  `(tenant_id, agent_id, captured_at DESC)`, both `WHERE channel IS NOT NULL` to bound cost for
  non-CSAT surveys.
- `csat_pending_dispatches` (NEW): SMS/email correlation lookup (`correlator` = phone for SMS /
  token for email; 24h `expires_at`; partial index `WHERE consumed_at IS NULL`). Pro inserts on
  dispatch; the Platform correlator reads + marks `consumed_at`.
- `queues`: +4 CSAT config columns (`csat_enabled`, `csat_channel`, `csat_prompt_id`,
  `csat_sampling_rate` with a 0–100 CHECK).
- `csat_templates` (NEW): per-tenant prompt store keyed `(tenant_id, template_id)`, channel ∈
  `{voice, email, sms}` (webchat uses i18n strings, no per-tenant template).

All stores use `Verbara.Sdk.Data.Npgsql` (`NpgsqlExecutor` + name-based reader getters, explicit
`NpgsqlParameter` with `NpgsqlDbType` on every nullable param that can be `DBNull.Value`) —
Dapper is banned (ADR-0022). `PostgresSurveyResponseStore` SELECT/INSERT extend to hydrate/persist
the 6 new columns via a hand-written `static Map(NpgsqlDataReader)`.

### D4 — Cross-repo in-process contract: `ICsatTemplateProvider` (Pro-defined, Platform-implemented)

Pro's Email adapter resolves per-tenant prompts by calling
`Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider.GetTemplateAsync(tenantId, channel, locale)` via
DI — same process, NOT an API call (ADR-0020 boundary). Platform ships `CsatTemplateProvider`
implementing it, backed by `csat_templates`, with a fallback chain: tenant-locale →
tenant-default-locale → global-default-locale → global-default-en-US. Registered
`AddSingleton<ICsatTemplateProvider, CsatTemplateProvider>()`. Because the interface lives in the
Pro nupkg, the integration test MUST run against the actual Pro CSAT package, not a stub.

### D5 — Email/SMS ingress gap-fill

`Verbara.Platform.Mail` is outbound-only today. Add `ImapInboundPoller` (`IHostedService`, polls the
`csat@…` mailbox, UID-based idempotent dedup) + `CsatReplyMailHandler` (HMAC token validation with
7-day TTL, regex `\b([1-5])\b` on subject then first 200 chars of body, `In-Reply-To` fallback for
forwarders that strip the `+token` suffix). SMS gap-fill: `CsatSmsCorrelator` plugs into the
existing inbound path after `SmsWebhookHandler`, matching `(tenant, fromNumber)` against
`csat_pending_dispatches` within 24h; on a `^\s*[1-5]\s*$` body it forwards + marks `consumed_at`,
otherwise it **falls through to normal conversation routing** so non-rating messages are never
consumed. Both forward to the internal `POST /api/v1/csat/responses/{email,sms}` endpoints.

### D6 — Real-time push + license gate

Capture endpoints publish `CsatResponseRecordedEvent` via `IPushEventBus`; `PushToHubRelay` gains a
branch routing it to the `supervisor:{tenantId}` SignalR group via the new typed Hub method
`IPlatformHubClient.OnCsatResponseRecorded`. The license gate consumes Pro's
`LicenseFeature.CsatRunner` decision and returns HTTP 402 + RFC 9457 ProblemDetails (the v2.2.0
license-contract precedent) when absent.

### D7 — AOT registrations (ADR-0022)

5 new DTOs — `CsatResponseRequest`, `CsatResponseDto`, `QueueCsatConfigDto`, `CsatTemplateDto`,
`CsatResponseRecordedEvent` — are typed sealed records registered in `ApiJsonContext` (no anonymous
`new {}`, `JsonSerializerIsReflectionEnabledByDefault=false`). AOT publish must show 0 trim/AOT
warnings on the advanced Pro pin.

### D8 — Deprecation of `GetByQueueAsync`

`ISurveyAnalytics.GetByQueueAsync` (which uses a `__queue` answer-marker hack) is marked
`[Obsolete]` on ship and removed one minor later (v2.19.0) — the same 2-release deprecation cadence
as Pro/ADR-0012.

## Risks / Trade-offs

- **Migration on production-sized `survey_responses`** → `ALTER TABLE ADD COLUMN` is metadata-only
  in Postgres 11+ for nullable columns without default, but the 2 new indexes scan the table.
  Mitigation: `CREATE INDEX CONCURRENTLY` for large tenants; document in the CSAT runbook.
- **IMAP poller reliability / idempotency** → UID-based dedup is standard; test duplicate-receive,
  EXPUNGE-during-poll, and mailbox-rebuild edge cases.
- **SMS correlation collision** (2 dispatches to same phone within 24h) → FIFO + strict expiry;
  most-recent dispatch wins, older marked expired without consumption; documented limitation.
- **`ICsatTemplateProvider` cross-repo contract** → Pro must publish the interface before Platform
  implements it; mitigate with an integration test against the real Pro CSAT nupkg, not a stub.
- **`survey_responses` index growth** → ~57M index entries/year/large-tenant (within Postgres
  comfort range); partial `WHERE channel IS NOT NULL` bounds non-CSAT cost; monitor.
- **Survey admin UI back-compat** → the existing Web surveys UI assumes multi-question surveys; a
  single-question CSAT survey must render without breaking (verified in the Web child change).

## Migration Plan

1. Apply `0XX_SurveyCsatExtensions.sql` (additive; existing rows load with new columns null).
2. Deploy Platform on the advanced Pro pin (engine + interface present).
3. Seed default `csat_templates` per locale on tenant create via `TenantProvisioningService`.
4. **Rollback:** the schema is additive — new columns/tables can remain unused with CSAT disabled
   per queue (`csat_enabled` defaults `false`); no data migration to reverse.

## Open Questions

- Final migration file number — assigned at execution kickoff (depends on what ships between freeze
  and kickoff); referenced generically as `0XX_SurveyCsatExtensions.sql`.
