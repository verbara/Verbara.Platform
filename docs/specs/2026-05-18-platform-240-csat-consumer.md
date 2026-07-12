# Platform v2.4.0 — CSAT Consumer Migration

**Created:** 2026-05-18 · **Status:** Spec frozen, execution paused (awaiting Platform-side pending work completion) · **Target release:** Platform `2.4.0` (calendar target ~2026-07-05 to 2026-07-12, post Pro 2.6.0-pro ship)

**Related:**

- Pro-side spec: [`Verbara.Sdk.Pro/docs/specs/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-18-pro-260-csat-runner-v1.md)
- ADR: [`docs/decisions/0020-csat-brownfield-survey-domain-extension.md`](../decisions/0020-csat-brownfield-survey-domain-extension.md)
- Execution plan (shipped, moved to `completed/` — PR #144): [`docs/plans/completed/2026-05-18-platform-240-csat-consumer.md`](../plans/completed/2026-05-18-platform-240-csat-consumer.md)
- System-path canonical plan (planning-mode source): `~/.claude/plans/si-refactored-pascal.md`

---

## Purpose

Platform v2.4.0 consumes Pro v2.6.0-pro's `Verbara.Sdk.Pro.CsatRunner` engine + 4 channel adapters and wires the Platform-side surfaces required for end-to-end CSAT:

1. **Survey domain extension** — `Verbara.Platform.Surveys.SurveyResponse` gains channel/queue/rating-shortcut fields per ADR-0020.
2. **Postgres migration** — `survey_responses` table gains corresponding columns + indexes.
3. **Per-queue CSAT config** — `Queue` entity + admin endpoint.
4. **Public token-signed endpoints** — for WebChat / Email / SMS response capture.
5. **Email IMAP gap-fill** — `ImapInboundPoller` + `CsatReplyMailHandler` (currently missing in `Verbara.Platform.Mail`).
6. **SMS correlator** — `CsatSmsCorrelator` in `Verbara.Platform.Channels.Sms` for digit-reply matching.
7. **Per-tenant template store** — `csat_templates` table + admin endpoints + `ICsatTemplateProvider` DI service (consumed by Pro's email adapter).
8. **Hub typed method** — `IPlatformHubClient.OnCsatResponseRecorded` for real-time supervisor dashboard pushes.
9. **Analytics endpoint** — `GET /api/v1/analytics/csat/queues/{queueId}` powering the Web dashboard KPI card.

This release pairs with Pro v2.6.0-pro (engine + license feature) + Web v3.2.0-web (admin tab + dashboard card + embed rating panel) for a complete end-to-end ship.

---

## In-scope changes (delta against Platform 2.3.0 baseline)

### Postgres migration (`0XX_SurveyCsatExtensions.sql`)

Migration number assigned at execution time (depends on what shipped between 021 and execution date). Migration contents:

```sql
-- Extend survey_responses for CSAT (channel + queue + rating shortcut fields).
ALTER TABLE survey_responses
  ADD COLUMN channel TEXT,
  ADD COLUMN queue_name TEXT,
  ADD COLUMN rating SMALLINT,
  ADD COLUMN comment TEXT,
  ADD COLUMN captured_at TIMESTAMPTZ,
  ADD COLUMN call_id TEXT,
  ADD CONSTRAINT survey_responses_channel_check
    CHECK (channel IS NULL OR channel IN ('voice','webchat','email','sms')),
  ADD CONSTRAINT survey_responses_rating_check
    CHECK (rating IS NULL OR (rating BETWEEN 1 AND 5));

CREATE INDEX idx_survey_resp_tenant_queue_time
  ON survey_responses (tenant_id, queue_name, captured_at DESC)
  WHERE channel IS NOT NULL;
CREATE INDEX idx_survey_resp_tenant_agent_time
  ON survey_responses (tenant_id, agent_id, captured_at DESC)
  WHERE channel IS NOT NULL;

-- SMS correlation pending-dispatches table (Pro inserts; Platform correlator reads).
CREATE TABLE csat_pending_dispatches (
  dispatch_id TEXT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  conversation_id TEXT NOT NULL,
  agent_id TEXT,
  queue_name TEXT,
  channel TEXT NOT NULL CHECK (channel IN ('email','sms')),
  correlator TEXT NOT NULL,  -- phone number for SMS, token for email
  sent_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,
  consumed_at TIMESTAMPTZ
);
CREATE INDEX idx_csat_pending_correlator
  ON csat_pending_dispatches (tenant_id, channel, correlator)
  WHERE consumed_at IS NULL;

-- Per-queue CSAT config.
ALTER TABLE queues
  ADD COLUMN csat_enabled BOOLEAN NOT NULL DEFAULT false,
  ADD COLUMN csat_channel TEXT,
  ADD COLUMN csat_prompt_id TEXT,
  ADD COLUMN csat_sampling_rate INT NOT NULL DEFAULT 100
    CHECK (csat_sampling_rate BETWEEN 0 AND 100);

-- Per-tenant CSAT templates.
CREATE TABLE csat_templates (
  template_id TEXT NOT NULL,
  tenant_id TEXT NOT NULL,
  channel TEXT NOT NULL CHECK (channel IN ('voice','email','sms')),  -- webchat uses i18n strings, no per-tenant template
  locale TEXT NOT NULL,
  subject TEXT,
  body TEXT NOT NULL,
  voice_prompt_text TEXT,
  voice_prompt_audio_url TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, template_id)
);
CREATE INDEX idx_csat_templates_tenant_channel_locale
  ON csat_templates (tenant_id, channel, locale);
```

### Survey domain extensions

**`src/Verbara.Platform.Surveys/SurveyResponse.cs`** — extend with additive init-only properties:

```csharp
public sealed record SurveyResponse {
    // ... existing properties ...
    public string? Channel { get; init; }
    public string? QueueName { get; init; }
    public short? Rating { get; init; }
    public string? Comment { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public string? CallId { get; init; }
}
```

Back-compat: all 6 fields are nullable; existing rows + consumers unaffected.

**`src/Verbara.Platform.Surveys/ISurveyAnalytics.cs`** — extend with channel/queue filter overload:

```csharp
public interface ISurveyAnalytics {
    Task<SurveyScoreSummary> GetByQueueAsync(EntityId queueId, DateRange range, CancellationToken ct);
    // NEW v2.4.0:
    Task<SurveyScoreSummary> GetByQueueAndChannelAsync(
        EntityId queueId, string? channel, DateRange range, CancellationToken ct);
}
```

The existing `GetByQueueAsync` keeps working (back-compat); new code prefers `GetByQueueAndChannelAsync`. Marked `[Obsolete]` in v2.5.0 + removed in v2.6.0 per the same 2-release deprecation pattern as ADR-0012.

**`src/Verbara.Platform.Surveys/SurveyQuestionIds.cs`** (NEW) — well-known IDs:

```csharp
public static class SurveyQuestionIds {
    public const string CsatRating = "csat-rating-v1";
}
```

### Queue entity extension

**`src/Verbara.Platform.Queues/Queue.cs`** — add nested record:

```csharp
public sealed record CsatConfig(
    bool Enabled,
    string? PreferredChannel,   // 'voice' | 'webchat' | 'email' | 'sms' | null=native
    EntityId? PromptTemplateId,
    int SamplingRatePercent);

public sealed record Queue {
    // ... existing properties ...
    public CsatConfig? Csat { get; init; }
}
```

### New endpoints

**`src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs`** (NEW):

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/v1/csat/responses/webchat` | Anonymous + WebChat session-token-signed | Visitor submits rating from embed iframe |
| `POST /api/v1/csat/responses/email` | Internal (IMAP worker API key) | Platform.Mail IMAP poller forwards parsed reply |
| `POST /api/v1/csat/responses/sms` | Internal (SMS webhook pipeline) | `CsatSmsCorrelator` forwards matched reply |
| `GET /api/v1/analytics/csat/queues/{queueId}?range=24h` | `SupervisorPlus` | Dashboard KPI card data source |

All 4 endpoints:
- Validate tenant + queue + token + rating.
- Persist `SurveyResponse` via `ISurveyResponseStore.SaveAsync`.
- Publish `CsatResponseRecordedEvent` via `IPushEventBus`.
- Audit row via `IAuditService.RecordAsync(category="csat", action="csat.recorded", ...)`.
- Return 402 Payment Required (RFC 9457 ProblemDetails per v2.2.0 contract) when license-gate fails.

**`src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs`** (NEW):

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /api/v1/admin/csat/templates` | `AdminOnly` | List per-tenant templates |
| `PUT /api/v1/admin/csat/templates/{id}` | `AdminOnly` | Upsert template |
| `DELETE /api/v1/admin/csat/templates/{id}` | `AdminOnly` | Remove template |
| `POST /api/v1/admin/csat/templates/{id}/preview-voice` | `AdminOnly` | Synthesize TTS prompt for preview |

### IMAP gap-fill (`Verbara.Platform.Mail`)

**`src/Verbara.Platform.Mail/Services/ImapInboundPoller.cs`** (NEW, `IHostedService`):

- Polls IMAP mailbox `csat@platform-mail.{tenantDomain}` for each configured tenant.
- Parses `MimeMessage` for `+{token}` in envelope (`Delivered-To` header or `Original-To` if forwarded).
- Falls back to matching `In-Reply-To: <message-id>` against dispatched `Message-Id` (covers mail-forwarders that strip `+suffix`).
- Calls `CsatReplyMailHandler.HandleAsync(token, parsedRating)`.
- Idempotent — uses IMAP UID to skip already-processed messages.

**`src/Verbara.Platform.Mail/Services/CsatReplyMailHandler.cs`** (NEW):

- Validates HMAC-signed token (7-day TTL).
- Regex `\b([1-5])\b` against subject first, then plain-text first 200 chars.
- Forwards to `POST /api/v1/csat/responses/email` (internal-only via API key).
- Auto-reply to user thanking them (optional, configurable per tenant).

### SMS correlator (`Verbara.Platform.Channels.Sms`)

**`src/Verbara.Platform.Channels.Sms/CsatSmsCorrelator.cs`** (NEW):

- Plugs into the existing inbound SMS dispatch path (after `SmsWebhookHandler`).
- On inbound `(tenantId, fromNumber)`:
  - Look up `csat_pending_dispatches WHERE tenant_id=$1 AND channel='sms' AND correlator=$2 AND consumed_at IS NULL AND sent_at > now() - interval '24h'`.
  - If matched AND body matches `^\s*[1-5]\s*$`:
    - Forward to `POST /api/v1/csat/responses/sms` (internal-only).
    - Mark `consumed_at = now()`.
  - Else fall through to normal conversation routing (don't eat user messages that aren't ratings).

### Hub typed method

**`src/Verbara.Platform.Api/Hubs/IPlatformHubClient.cs`** — extend interface:

```csharp
public interface IPlatformHubClient {
    // ... existing methods ...
    Task OnCsatResponseRecorded(CsatResponseRecordedEvent evt);
}
```

**`src/Verbara.Platform.Api/Services/PushToHubRelay.cs`** — add new event-type branch around the existing relay-switch:

```csharp
case CsatResponseRecordedEvent csat:
    await _hubContext.Clients
        .Group($"supervisor:{csat.TenantId}")
        .OnCsatResponseRecorded(csat);
    break;
```

### `ICsatTemplateProvider` DI service

**`src/Verbara.Platform.Api/Services/CsatTemplateProvider.cs`** (NEW, implements public Pro-defined contract `Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider`):

- Tenant-locale-channel template fetch from `csat_templates` table.
- Fallback chain: tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US.
- Templates seeded via `TenantProvisioningService` on tenant create.

Pro's email adapter calls `ICsatTemplateProvider.GetTemplateAsync(tenantId, channel, locale)` instead of bundling default prompts.

### AOT JsonContext registrations

**`src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs`** — register:

- `CsatResponseRecordedEvent`
- `CsatResponseRequest` (anonymous endpoint body)
- `CsatResponseDto` (analytics response)
- `QueueCsatConfigDto`
- `CsatTemplateDto`

### CHANGELOG draft

```markdown
## [2.4.0] — 2026-XX-XX — CSAT consumer migration (Pro 2.6.0-pro)

MINOR bump consuming Pro v2.6.0-pro's `Verbara.Sdk.Pro.CsatRunner` engine and
wiring Platform-side surfaces: Survey domain extension (per ADR-0020) +
Postgres migration + per-queue CSAT config + token-signed response endpoints
+ IMAP gap-fill + SMS correlator + per-tenant template store + Hub typed
method + analytics endpoint.

### Added

- Postgres migration `0XX_SurveyCsatExtensions.sql` (extends `survey_responses`
  + adds `csat_pending_dispatches` + `csat_templates` + Queue CSAT columns).
- `Verbara.Platform.Surveys.SurveyResponse` — 6 new init-only properties
  (channel, queue_name, rating, comment, captured_at, call_id).
- `ISurveyAnalytics.GetByQueueAndChannelAsync(...)` (existing
  `GetByQueueAsync` marked `[Obsolete]` for v2.5.0 removal).
- `SurveyQuestionIds.CsatRating = "csat-rating-v1"`.
- `Verbara.Platform.Queues.Queue.Csat` nested `CsatConfig` record.
- `CsatResponseEndpoints` — 4 new public/internal endpoints under
  `/api/v1/csat/responses/*` + `/api/v1/analytics/csat/queues/{id}`.
- `CsatTemplateAdminEndpoints` — 4 admin endpoints under
  `/api/v1/admin/csat/templates/*`.
- `Verbara.Platform.Mail.ImapInboundPoller` + `CsatReplyMailHandler` (IMAP
  gap-fill; new `IHostedService` that polls `csat@...` mailbox).
- `Verbara.Platform.Channels.Sms.CsatSmsCorrelator` (digit-reply matcher).
- `IPlatformHubClient.OnCsatResponseRecorded(CsatResponseRecordedEvent)`
  typed Hub method.
- `CsatTemplateProvider` (implements Pro-defined `ICsatTemplateProvider`).

### Changed

- `PushToHubRelay` — new event-type branch for `CsatResponseRecordedEvent`.
- `TenantProvisioningService` — seeds default CSAT templates per locale on
  tenant create.
- `ApiJsonContext` — registers 5 new DTOs for AOT-safe serialization.
- `Directory.Packages.props` — bumps Pro pins `2.5.0-pro` → `2.6.0-pro`.

### Deprecated

- `ISurveyAnalytics.GetByQueueAsync` — superseded by
  `GetByQueueAndChannelAsync`; removed in v2.5.0.

### Cross-repo coordination

- Pro `2.6.0-pro` (engine + 4 channel adapters + license feature).
- Web `3.2.0-web` + embed `v3.1.0` (admin tab + dashboard card + rating panel).
```

---

## Verification matrix

| Scenario | Expected behavior |
|---|---|
| `dotnet test` on Platform slnx with new migration applied | All existing tests + new CSAT endpoint contract tests pass. |
| WebChat conversation ends → embed shows rating → submits 4 stars | `SurveyResponse` row persisted with `Channel="webchat"`, `Rating=4`, `QueueName` populated; `OnCsatResponseRecorded` fires for supervisor session. |
| Email CSAT sent → recipient replies with "5" in subject | IMAP poller picks up reply within 60s; `CsatReplyMailHandler` parses + forwards to internal endpoint; `SurveyResponse` row persisted. |
| Email CSAT sent → reply forwarded through forwarder stripping `+suffix` | `In-Reply-To` header matches dispatched `Message-Id`; fallback correlation succeeds. |
| SMS CSAT sent → recipient replies "3" within 24h | `CsatSmsCorrelator` matches phone+window; persists rating; marks `consumed_at`. |
| SMS CSAT sent → recipient replies "Hello agent" | Falls through to normal conversation routing (no rating recorded, no user message lost). |
| Two CSAT SMS dispatched to same phone within 24h → recipient replies "4" | Attributed to most-recent dispatch; older marked expired without consumption. |
| Tenant has 0 CSAT templates | Falls back through chain: tenant-default-locale → global-default-locale → global-default-en-US. |
| `LicenseFeature.CsatRunner` absent from license | All 4 response endpoints return HTTP 402 + RFC 9457 ProblemDetails per v2.2.0 contract. |
| AOT publish on Platform.Api with Pro 2.6.0-pro pin | 0 trim/AOT warnings; all 5 new DTOs serialize via source-gen. |

---

## Risks

1. **Postgres migration on production-sized `survey_responses`** — `ALTER TABLE ADD COLUMN` is fast (Postgres 11+ uses metadata-only for nullable columns w/o default), BUT the 2 new indexes scan the table. Mitigation: use `CREATE INDEX CONCURRENTLY` for large-table tenants; document migration runbook in `docs/operations/`.
2. **IMAP poller reliability + idempotency** — IMAP UID-based dedup is the standard pattern; test rigor on duplicate-receive, mailbox-rebuild, and EXPUNGE-during-poll edge cases.
3. **SMS correlation collision (2 dispatches to same phone in 24h)** — Mitigated by FIFO + strict expiry; documented as known limitation in operator FAQ.
4. **`ICsatTemplateProvider` DI contract** — defined in Pro, implemented in Platform. Coordinated ship: Pro 2.6.0-pro must include the interface; Platform 2.4.0 must implement it. Tested via integration test against the actual Pro 2.6.0-pro nupkg.
5. **`survey_responses` indexes growth** — every CSAT response adds 2 index entries. Expected volume (CSAT rate ~20% per queue, ~10 conv/min/queue, 200 queues per large tenant) → ~57M entries/year/tenant. Within Postgres comfort range but should monitor.
6. **Survey admin UI back-compat** — `Verbara.Platform.Web/src/admin/surveys/` UI assumes multi-question surveys. CSAT uses 1 well-known question (`csat-rating-v1`). Verify the UI doesn't break when displaying a single-question CSAT survey (likely fine but explicit test needed).

---

## Acceptance criteria

- New migration applied + back-compat verified (existing `SurveyResponse` rows load cleanly with new columns null).
- All 4 new response endpoints + 4 admin template endpoints + 1 analytics endpoint pass contract tests.
- IMAP poller end-to-end with Testcontainers MailHog: dispatched email → parsed reply → forwarded → `SurveyResponse` row.
- SMS correlator end-to-end with mocked Twilio webhook: dispatched SMS → parsed reply → forwarded.
- AOT publish succeeds with 0 warnings on Pro 2.6.0-pro pin.
- HTTP 402 license-gate fires consistently with v2.2.0 contract.
- CHANGELOG.md `[2.4.0]` section complete.
- `Directory.Build.props` `PackageVersion` bumped `2.3.0` → `2.4.0`.
- Migration guide section in `docs/migration/` if any consumer-facing behavior change; otherwise CHANGELOG suffices.

---

## References

- ADR: [`0020-csat-brownfield-survey-domain-extension.md`](../decisions/0020-csat-brownfield-survey-domain-extension.md)
- Pro spec: [`Verbara.Sdk.Pro/docs/specs/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-18-pro-260-csat-runner-v1.md)
- Pro plan: [`Verbara.Sdk.Pro/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md)
- Existing Survey domain: [`src/Verbara.Platform.Surveys/`](../../src/Verbara.Platform.Surveys/)
- Existing SurveyEndpoints (audit pattern): [`src/Verbara.Platform.Api/Endpoints/SurveyEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/SurveyEndpoints.cs)
- Existing SMS infrastructure: [`src/Verbara.Platform.Channels.Sms/`](../../src/Verbara.Platform.Channels.Sms/)
- Existing Mail outbound: [`src/Verbara.Platform.Mail/Services/SmtpSender.cs`](../../src/Verbara.Platform.Mail/Services/SmtpSender.cs)
- Existing Hub typed pattern (Pro v1.11.0-pro): [`Verbara.Sdk.Pro.Push.SignalR.Hubs.IPlatformHubClient`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/src/Verbara.Sdk.Pro.Push.SignalR/Hubs/IPlatformHubClient.cs)
- HTTP 402 contract precedent (v2.2.0 / ADR-0012): [`docs/decisions/0012-...`](../decisions/) (Pro repo)
