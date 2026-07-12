# csat — Delta

Customer-satisfaction response capture as a brownfield extension of the `Verbara.Platform.Surveys`
domain (Platform/ADR-0020). This delta introduces the `csat` capability: the token-signed public
capture wire shape, per-channel capture (webchat / email / sms), the survey-domain persistence
extension, per-queue CSAT config, the per-tenant template store + `ICsatTemplateProvider`, the
`CsatResponseRecordedEvent` real-time push, and the license gate.

The capture wire shape is frozen by `fixtures/csat-response-capture.v1.json`; every requirement that
describes it cites the field names verbatim.

## ADDED Requirements

### Requirement: Token-signed WebChat CSAT capture wire shape

The `POST /api/v1/csat/responses/webchat` endpoint MUST accept a JSON body carrying exactly the
fields `responseToken`, `surveyId`, `questionId`, `channel`, `queueName`, `rating`, `comment`,
`capturedAt`, and `conversationId`, as frozen by `fixtures/csat-response-capture.v1.json`. The
endpoint SHALL be anonymous but require a valid WebChat session-signed `responseToken`; it MUST
reject a request whose `responseToken` is missing, malformed, expired, or whose signed tenant/queue
does not match the submitted `queueName`. The body deserializes via a typed sealed
`CsatResponseRequest` record registered in `ApiJsonContext` (Native AOT, no reflection).

#### Scenario: Visitor submits a valid webchat rating

- **GIVEN** an embed iframe holding a valid session-signed `responseToken` for tenant `ten-42`, `surveyId` `srv-csat-v1`, `queueName` `support-tier1`, `channel` `webchat`
- **WHEN** it POSTs `{ responseToken, surveyId: "srv-csat-v1", questionId: "csat-rating-v1", channel: "webchat", queueName: "support-tier1", rating: 5, comment: "Fast and friendly, thanks!", capturedAt: "2026-07-07T09:15:00Z", conversationId: "conv-8f2a1c4e" }`
- **THEN** the request is accepted and a `SurveyResponse` is persisted with the submitted `rating`, `comment`, `channel`, `queueName`, `capturedAt`, and correlated `conversationId`

#### Scenario: Rejects a tampered or expired token

- **GIVEN** a webchat capture request whose `responseToken` is expired or whose signed `queueName` differs from the submitted `queueName`
- **WHEN** the request is POSTed
- **THEN** the endpoint rejects it and NO `SurveyResponse` is persisted

#### Scenario: Rating outside 1..5 is rejected

- **GIVEN** a webchat capture request with a valid `responseToken` but `rating` of `0` or `6`
- **WHEN** the request is POSTed
- **THEN** the endpoint rejects it with a validation error and NO `SurveyResponse` is persisted

### Requirement: Survey-domain persistence extension (brownfield, back-compat)

CSAT responses SHALL persist through the existing `ISurveyResponseStore.SaveAsync` onto an extended
`Verbara.Platform.Surveys.SurveyResponse`, NOT a parallel `csat_responses` entity (Platform/ADR-0020).
`SurveyResponse` MUST gain the additive nullable init-only properties `Channel`, `QueueName`,
`Rating`, `Comment`, `CapturedAt`, and `CallId`, mapping 1:1 onto the fixture fields `channel`,
`queueName`, `rating`, `comment`, and `capturedAt`. All six properties MUST be nullable so
pre-existing rows and non-CSAT survey consumers load unchanged. The well-known constant
`SurveyQuestionIds.CsatRating` MUST equal `"csat-rating-v1"`, matching the fixture `questionId`, so
all Pro channel adapters reference the same question id. The `survey_responses` table gains the same
six columns with CHECK constraints `channel IN ('voice','webchat','email','sms')` and `rating
BETWEEN 1 AND 5` (both nullable), plus two partial indexes on `(tenant_id, queue_name, captured_at
DESC)` and `(tenant_id, agent_id, captured_at DESC)`, each scoped `WHERE channel IS NOT NULL`. All
data access MUST use `Verbara.Sdk.Data.Npgsql` — Dapper is banned (Platform/ADR-0022).

#### Scenario: CSAT-flavored row round-trips through PostgresSurveyResponseStore

- **GIVEN** a captured CSAT response with `channel` `webchat`, `rating` `5`, `queueName` `support-tier1`, `capturedAt` `2026-07-07T09:15:00Z`
- **WHEN** it is saved via `ISurveyResponseStore.SaveAsync` and re-read
- **THEN** the persisted row exposes `Channel`, `Rating`, `QueueName`, and `CapturedAt` populated directly (not parsed out of the JSONB `answers`)

#### Scenario: Pre-existing non-CSAT survey rows load unchanged

- **GIVEN** a `survey_responses` row written before this migration (no `channel`/`rating` set)
- **WHEN** it is read after the migration is applied
- **THEN** it loads successfully with `Channel`, `QueueName`, `Rating`, `Comment`, `CapturedAt`, and `CallId` all null and its JSONB `answers` intact

#### Scenario: Channel and rating CHECK constraints enforced

- **GIVEN** the migrated `survey_responses` table
- **WHEN** an insert sets `channel` to a value outside `{voice, webchat, email, sms}` or `rating` outside `1..5`
- **THEN** the database rejects the insert via the CHECK constraint

### Requirement: Internal Email CSAT capture and IMAP gap-fill

The `POST /api/v1/csat/responses/email` endpoint SHALL be internal-only (worker API-key auth) and
accept the same capture shape (`responseToken`, `surveyId`, `questionId`, `channel` `email`,
`queueName`, `rating`, `comment`, `capturedAt`, `conversationId`). Because inbound mail is not
reachable in-process, `Verbara.Platform.Mail` MUST add an `ImapInboundPoller` (`IHostedService`)
polling the `csat@…` mailbox with UID-based idempotent dedup, and a `CsatReplyMailHandler` that
validates the HMAC-signed token (7-day TTL), extracts the rating via regex `\b([1-5])\b` against the
subject first then the first 200 chars of the body, falls back to `In-Reply-To` header matching when
a forwarder strips the `+token` suffix, and forwards a matched reply to this endpoint.

#### Scenario: Emailed digit reply is parsed and captured

- **GIVEN** a dispatched CSAT email whose recipient replies with "5" in the subject
- **WHEN** the `ImapInboundPoller` picks up the reply and `CsatReplyMailHandler` validates the token
- **THEN** it forwards `{ rating: 5, channel: "email", … }` to `POST /api/v1/csat/responses/email` and a `SurveyResponse` is persisted

#### Scenario: Forwarder-stripped token falls back to In-Reply-To

- **GIVEN** a reply routed through a forwarder that stripped the `+token` envelope suffix
- **WHEN** `CsatReplyMailHandler` cannot read the token from the envelope
- **THEN** it correlates via the `In-Reply-To` header against the dispatched `Message-Id` and still captures the rating

#### Scenario: Already-processed message is not double-captured

- **GIVEN** an IMAP message whose UID was already processed
- **WHEN** the poller runs again
- **THEN** it skips the message and NO duplicate `SurveyResponse` is persisted

### Requirement: Internal SMS CSAT capture with correlation fall-through

The `POST /api/v1/csat/responses/sms` endpoint SHALL be internal-only. A `CsatSmsCorrelator` in
`Verbara.Platform.Channels.Sms` MUST plug into the inbound SMS path after `SmsWebhookHandler` and,
for an inbound `(tenantId, fromNumber)`, look up `csat_pending_dispatches WHERE tenant_id = $1 AND
channel = 'sms' AND correlator = $2 AND consumed_at IS NULL AND sent_at > now() - interval '24h'`.
When a dispatch matches AND the body matches `^\s*[1-5]\s*$`, it MUST forward the rating to the
endpoint and mark `consumed_at = now()`. When no dispatch matches OR the body is not a bare digit,
the correlator MUST fall through to normal conversation routing so no user message is consumed. When
two dispatches to the same phone are open within 24h, the reply MUST be attributed to the
most-recent dispatch and the older marked expired without consumption.

#### Scenario: Digit reply within window is captured

- **GIVEN** a CSAT SMS dispatched to a phone with an open, unconsumed `csat_pending_dispatches` row inside 24h
- **WHEN** that phone replies "3"
- **THEN** the correlator forwards `{ rating: 3, channel: "sms", … }`, a `SurveyResponse` is persisted, and the dispatch's `consumed_at` is set

#### Scenario: Non-rating reply falls through to routing

- **GIVEN** a CSAT SMS dispatched to a phone
- **WHEN** that phone replies "Hello agent"
- **THEN** the correlator does NOT capture a rating and the message falls through to normal conversation routing (not consumed)

#### Scenario: Collision attributes to most-recent dispatch

- **GIVEN** two CSAT SMS dispatched to the same phone within 24h
- **WHEN** the phone replies "4"
- **THEN** the rating is attributed to the most-recent dispatch and the older dispatch is marked expired without consumption

### Requirement: License gate returns HTTP 402

All four CSAT capture endpoints (`webchat`, `email`, `sms`, and the analytics read) SHALL consume
Pro's `LicenseFeature.CsatRunner` decision and, when the feature is absent from the tenant's license,
MUST reject the request with HTTP 402 and an RFC 9457 ProblemDetails body consistent with the
established license-gate contract, without persisting a `SurveyResponse`.

#### Scenario: Capture rejected when CsatRunner feature absent

- **GIVEN** a tenant whose license lacks `LicenseFeature.CsatRunner`
- **WHEN** any `POST /api/v1/csat/responses/{webchat,email,sms}` request arrives
- **THEN** the endpoint returns HTTP 402 with an RFC 9457 ProblemDetails body and NO `SurveyResponse` is persisted

### Requirement: CsatResponseRecordedEvent real-time push

On each successful capture, the endpoint SHALL publish a `CsatResponseRecordedEvent` via
`IPushEventBus`, and `PushToHubRelay` MUST route it through the typed Hub method
`IPlatformHubClient.OnCsatResponseRecorded` to the `supervisor:{tenantId}` SignalR group.
`CsatResponseRecordedEvent` MUST be a typed sealed record registered in `ApiJsonContext` (Native
AOT). Each capture MUST also write an audit row via `IAuditService.RecordAsync` under the `csat`
category.

#### Scenario: Supervisor session receives the recorded event

- **GIVEN** a supervisor subscribed to the `supervisor:{tenantId}` group and a valid capture for that tenant
- **WHEN** the response is persisted
- **THEN** a `CsatResponseRecordedEvent` is published and delivered to the supervisor via `OnCsatResponseRecorded`, and an audit row is written under category `csat`

### Requirement: Per-queue CSAT configuration

`Verbara.Platform.Queues.Queue` MUST gain a nullable nested `CsatConfig` record
`(bool Enabled, string? PreferredChannel, EntityId? PromptTemplateId, int SamplingRatePercent)`,
persisted via four additive `queues` columns (`csat_enabled` default `false`, `csat_channel`,
`csat_prompt_id`, `csat_sampling_rate` with a `0..100` CHECK). CSAT capture for a queue whose
`csat_enabled` is `false` SHALL NOT be solicited. The config round-trips through the existing admin
queue-update endpoint.

#### Scenario: Queue CSAT config round-trips

- **GIVEN** an admin updating a queue with `Csat = { Enabled: true, PreferredChannel: "webchat", SamplingRatePercent: 20 }`
- **WHEN** the queue is saved and re-read
- **THEN** the persisted `Queue.Csat` reports `Enabled` true, `PreferredChannel` `webchat`, and `SamplingRatePercent` `20`

#### Scenario: Sampling rate constraint enforced

- **GIVEN** a queue CSAT update
- **WHEN** `csat_sampling_rate` is set outside `0..100`
- **THEN** the database rejects it via the CHECK constraint

### Requirement: Per-tenant template store and ICsatTemplateProvider fallback

Platform MUST ship a `csat_templates` store (keyed `(tenant_id, template_id)`, channel ∈
`{voice, email, sms}`; webchat uses i18n strings, no per-tenant template) and a
`CsatTemplateProvider` implementing the Pro-defined `Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider`
contract, resolved in-process via DI (NOT an API call — Platform/ADR-0020 boundary). Template
resolution MUST follow the fallback chain tenant-locale → tenant-default-locale →
global-default-locale → global-default-en-US. `TenantProvisioningService` MUST seed default
templates per locale on tenant create. Admin CRUD endpoints under `/api/v1/admin/csat/templates/*`
(`AdminOnly`) MUST manage the store.

#### Scenario: Missing locale falls back through the chain

- **GIVEN** a tenant with 0 templates for the requested locale and channel
- **WHEN** Pro's Email adapter calls `ICsatTemplateProvider.GetTemplateAsync(tenantId, channel, locale)`
- **THEN** resolution falls back tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US and returns a non-null template

#### Scenario: Admin upserts and reads back a template

- **GIVEN** an `AdminOnly` caller
- **WHEN** it `PUT`s a template to `/api/v1/admin/csat/templates/{id}` then `GET`s `/api/v1/admin/csat/templates`
- **THEN** the upserted template is listed for the tenant

### Requirement: Per-queue CSAT analytics read

`GET /api/v1/analytics/csat/queues/{queueId}` SHALL require the `SupervisorPlus` policy and return
per-queue CSAT aggregates via `ISurveyAnalytics.GetByQueueAndChannelAsync(queueId, channel, range)`,
backed by the new partial indexes. The existing `ISurveyAnalytics.GetByQueueAsync` MUST be marked
`[Obsolete]` on ship and removed one minor release later (the 2-release deprecation cadence of
Pro/ADR-0012). The response body MUST be a typed sealed `CsatResponseDto` registered in
`ApiJsonContext`.

#### Scenario: Supervisor reads queue CSAT summary

- **GIVEN** a `SupervisorPlus` caller and a queue with captured CSAT responses in the range
- **WHEN** it GETs `/api/v1/analytics/csat/queues/{queueId}?range=24h`
- **THEN** the endpoint returns a `CsatResponseDto` summary computed via `GetByQueueAndChannelAsync` over the channel-indexed rows

#### Scenario: Non-supervisor is denied

- **GIVEN** a caller lacking the `SupervisorPlus` policy
- **WHEN** it GETs `/api/v1/analytics/csat/queues/{queueId}`
- **THEN** the request is rejected by authorization and returns no analytics data

## Architectural Risk

**Level:** MEDIUM — a cross-repo in-process contract (`ICsatTemplateProvider` defined in Pro,
implemented in Platform) plus an additive schema migration on the production-sized
`survey_responses` table and two new ingress paths (IMAP poller, SMS correlator).

**Affected:** `Verbara.Platform.Surveys`, `Verbara.Platform.Storage.Postgres`,
`Verbara.Platform.Queues`, `Verbara.Platform.Api`, `Verbara.Platform.Mail`,
`Verbara.Platform.Channels.Sms`; along the chain, Sdk.Pro (provides the engine + interface + license
feature) and Platform.Web (consumes the endpoints).

**Mitigation:** all domain/schema changes are additive and nullable (back-compat verified);
`CREATE INDEX CONCURRENTLY` for large tenants; integration test against the real Pro CSAT nupkg (not
a stub); the SMS correlator falls through on non-rating bodies so user messages are never consumed;
IMAP dedup is UID-based; all DTOs are `[JsonSerializable]` source-gen registered and all data access
uses `Verbara.Sdk.Data.Npgsql` (no Dapper) per Platform/ADR-0022.
