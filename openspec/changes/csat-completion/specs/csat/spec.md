# csat — Delta

Completes the CSAT capability with the voice channel end-to-end and the two open Platform/ADR-0020
follow-ups (typed supervisor push, aggregate KPI). This delta ADDS the voice trigger, the agent-hangup
domain signal, the survey-IVR handoff, the voice capture endpoint, the scope-wide aggregate analytics
read, and the now-live voice template preview; it MODIFIES the two shipped requirements whose behavior
changes (the supervisor-push relay becomes typed; the analytics requirement gains the aggregate read).

Wire shapes are frozen by `fixtures/csat-voice-capture.v1.json`,
`fixtures/csat-aggregate-analytics.v1.json`, and `fixtures/csat-response-recorded-payload.v1.json`;
every requirement that describes them cites the field names verbatim (verbatim-fixture-citation rule).

## ADDED Requirements

### Requirement: Voice CSAT trigger on call wrap-up

`CsatConversationEndSource` MUST solicit CSAT for voice conversations, mapping `ChannelType.Voice` to
the channel string `voice` (its `MapChannel` currently returns `null` for voice). Voice conversations
terminate via `ConversationState.WrapUp` (answered) or `ConversationState.Abandoned` (never-answered),
NOT the digital `ConversationState.Closed` transition. The source MUST therefore fire the CSAT trigger
on the `WrapUp` transition for a voice conversation on a CSAT-configured queue, and MUST NOT solicit on
`Abandoned` (the call was never answered, so there is no served interaction to rate). The pushed
`CsatConversationEndedSignal` reuses the same queue-config / active-survey / recipient resolution the
digital channels use; the orchestrator's license, queue-enabled, and sampling gates still own the
final solicit decision.

#### Scenario: Answered voice call solicits CSAT on wrap-up

- **GIVEN** a voice conversation on a CSAT-enabled queue that was answered and transitions to `WrapUp`
- **WHEN** `CsatConversationEndSource` observes the terminal transition
- **THEN** it resolves the queue-config + active CSAT survey and pushes a `CsatConversationEndedSignal` with `NativeChannel` `voice`

#### Scenario: Abandoned voice call does not solicit

- **GIVEN** a voice conversation that was never answered and transitions to `Abandoned`
- **WHEN** `CsatConversationEndSource` observes the terminal transition
- **THEN** NO `CsatConversationEndedSignal` is pushed for it

#### Scenario: Non-CSAT-queue voice call defers to the orchestrator gate

- **GIVEN** an answered voice conversation on a queue whose `csat_enabled` is `false`
- **WHEN** it transitions to `WrapUp`
- **THEN** the signal is pushed with `CsatEnabled` `false` so the orchestrator's queue-disabled skip path owns the decision (single source of truth)

### Requirement: Voice agent-hangup domain event

`VoiceConversationBridge.OnCallEndedAsync` MUST publish a typed sealed `VoiceAgentHangupEvent` on the
in-process `PlatformEventBus` carrying `TenantId`, `ConversationId`, `QueueName`, an `Abnormal` verdict
computed by the existing `IsAbnormalAgentHangup(agentCause, agentLeftAt, callerLeftAt)`, and the
hangup instant, so the voice-CSAT path can decide whether to solicit while the caller leg is still up.
The event MUST be published inside the same leader-gated, per-call-stripe-locked handler as the wrap-up
transition, so it inherits the exactly-once-cluster-wide guarantee. `VoiceAgentHangupEvent` MUST be
reflection-free (Native AOT, Platform/ADR-0022).

#### Scenario: Clean agent hangup publishes a non-abnormal event

- **GIVEN** an answered voice call whose agent leg ends with `HangupCause.NormalClearing`
- **WHEN** `OnCallEndedAsync` runs on the leader pod
- **THEN** a `VoiceAgentHangupEvent` is published with `Abnormal` `false`

#### Scenario: Abnormal agent leg death publishes an abnormal event

- **GIVEN** an answered voice call whose agent leg dies with a non-normal cause and left before the caller
- **WHEN** `OnCallEndedAsync` runs on the leader pod
- **THEN** a `VoiceAgentHangupEvent` is published with `Abnormal` `true`

#### Scenario: Follower pod does not publish

- **GIVEN** the same call ended on a pod that does NOT hold the AMI-owner lease
- **WHEN** `OnCallEndedAsync` runs
- **THEN** NO `VoiceAgentHangupEvent` is published (side-effects are leader-only, single cluster-wide)

### Requirement: Survey-IVR blind-transfer handoff

`VoiceCallControlService` MUST gain a `VoiceTransferKind.SurveyIvr` transfer target that reuses the AMI
`Redirect` machinery of `BlindTransferAsync` to move the customer (trunk) leg into a shared survey-IVR
dialplan context. The dialplan MUST be a single shared `[survey-ivr]` context in
`docker/asterisk-config/extensions.conf` (like `[stasis-queue]` / `[transfer-agent]`), NOT a per-tenant
rendered file; per-tenant survey configuration MUST come from the queue `CsatConfig` and Asterisk
Realtime DB. The handoff MUST set the channel variable(s) the survey IVR reads (the survey id and the
Platform-minted voice-leg token) before the `Redirect`, and MUST remain leader-gated (only the AMI-owner
pod emits the AMI command).

#### Scenario: Survey-IVR transfer redirects the customer leg into the shared context

- **GIVEN** an answered voice conversation whose customer leg channel is known and a survey-IVR handoff is requested
- **WHEN** `BlindTransferAsync` is called with a `VoiceTransferKind.SurveyIvr` target on the leader pod
- **THEN** it sets the survey channel variable(s) and AMI-`Redirect`s the customer leg into the shared `[survey-ivr]` context, returning an accepted outcome

#### Scenario: Non-leader pod does not emit the transfer

- **GIVEN** the same handoff requested on a pod that does not hold the AMI-owner lease
- **WHEN** `BlindTransferAsync` is called
- **THEN** it returns a `not-leader` outcome and emits no AMI command

#### Scenario: Unknown customer channel is rejected

- **GIVEN** a voice conversation with no persisted customer-leg channel
- **WHEN** a survey-IVR handoff is requested
- **THEN** the transfer returns a `channel-unknown` outcome and no `Redirect` is emitted

### Requirement: Voice CSAT capture wire shape

The `POST /api/v1/csat/responses/voice` endpoint MUST accept a JSON body carrying exactly the fields
`responseToken`, `surveyId`, `questionId`, `channel`, `queueName`, `rating`, `comment`, `capturedAt`,
and `conversationId`, as frozen by `fixtures/csat-voice-capture.v1.json` — the same frozen
`CsatResponseRequest` shape as the digital channels, with `channel` `voice` and `comment` `null` (DTMF
carries no free text). The endpoint SHALL be anonymous but require a valid Platform-minted voice-leg
`responseToken` (HMAC-signed `v1.{payload}.{sig}`); it MUST reject a request whose `responseToken` is
missing, malformed, expired, or whose signed tenant/queue/channel does not match the submitted
`queueName`/`channel`. `questionId` MUST equal `csat-rating-v1` and `rating` MUST be in `1..5`; the body
deserializes via the typed sealed `CsatResponseRequest` record in `ApiJsonContext` (Native AOT, no
reflection). On success the endpoint persists a `SurveyResponse` (setting `CallId` from the correlated
voice conversation), publishes a `CsatResponseRecordedEvent`, and writes a `csat`-category audit row —
the same capture path as the digital channels, license-gated by `LicenseFeature.CsatRunner`.

#### Scenario: Voice DTMF rating is captured

- **GIVEN** a survey-IVR leg holding a valid voice `responseToken` for tenant `ten-42`, `surveyId` `srv-csat-v1`, `queueName` `support-tier1`, `channel` `voice`
- **WHEN** it POSTs `{ responseToken, surveyId: "srv-csat-v1", questionId: "csat-rating-v1", channel: "voice", queueName: "support-tier1", rating: 4, comment: null, capturedAt: "2026-07-13T09:15:00Z", conversationId: "conv-8f2a1c4e" }`
- **THEN** the request is accepted and a `SurveyResponse` is persisted with `rating` `4`, `channel` `voice`, `comment` null, `queueName` `support-tier1`, `capturedAt`, and the correlated `conversationId`

#### Scenario: Rejects a tampered or expired voice token

- **GIVEN** a voice capture request whose `responseToken` is expired or whose signed `queueName`/`channel` differs from the submitted values
- **WHEN** the request is POSTed
- **THEN** the endpoint rejects it and NO `SurveyResponse` is persisted

#### Scenario: Voice rating outside 1..5 is rejected

- **GIVEN** a voice capture request with a valid `responseToken` but `rating` of `0` or `6`
- **WHEN** the request is POSTed
- **THEN** the endpoint rejects it with a validation error and NO `SurveyResponse` is persisted

### Requirement: Scope-wide aggregate CSAT analytics read

`GET /api/v1/analytics/csat` SHALL require the `SupervisorPlus` policy, be license-gated by
`LicenseFeature.CsatRunner`, and return a scope-wide CSAT roll-up frozen by
`fixtures/csat-aggregate-analytics.v1.json`: a top-level envelope carrying `totalResponses`,
`averageRating`, `rangeStart`, and `rangeEnd`, plus a `queues` array whose rows reuse the existing
`CsatResponseDto` verbatim (`queueName`, `channel`, `totalResponses`, `averageRating`, `rangeStart`,
`rangeEnd`). `channel` MUST echo the requested filter and MUST be `all` when unfiltered. The aggregate
MUST be computed by a new `ISurveyAnalytics` scope-wide overload backed by the existing
`(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL` partial index (no schema change),
via `Verbara.Sdk.Data.Npgsql` (no Dapper, Platform/ADR-0022). The response body MUST be a typed sealed
`CsatAggregateDto` registered in `ApiJsonContext`. This read resolves Platform/ADR-0020's
⟨NEEDS PRODUCT-OWNER INPUT⟩ wallboard question in favor of aggregation (product-owner decision
2026-07-13) and is the API-first prerequisite the Web aggregate KPI card consumes.

#### Scenario: Supervisor reads the scope-wide CSAT roll-up

- **GIVEN** a `SupervisorPlus` caller and a tenant with captured CSAT responses across `support-tier1` and `billing` in the range
- **WHEN** it GETs `/api/v1/analytics/csat?range=7d`
- **THEN** the endpoint returns a `CsatAggregateDto` whose `totalResponses` / `averageRating` sum the scope and whose `queues` array carries one `CsatResponseDto` row per queue with `channel` `all`

#### Scenario: Channel filter echoes into the response

- **GIVEN** a `SupervisorPlus` caller
- **WHEN** it GETs `/api/v1/analytics/csat?channel=voice`
- **THEN** the envelope and every `queues[]` row report `channel` `voice`

#### Scenario: Non-supervisor is denied

- **GIVEN** a caller lacking the `SupervisorPlus` policy
- **WHEN** it GETs `/api/v1/analytics/csat`
- **THEN** the request is rejected by authorization and returns no analytics data

### Requirement: Voice template preview synthesis

`POST /api/v1/admin/csat/templates/{id}/preview-voice` (`AdminOnly`) MUST synthesize and return a
preview of the resolved voice template body now that the voice channel ships (it previously returned
HTTP 501 "voice preview deferred"). It MUST keep the existing guards — HTTP 400 on an invalid id and
HTTP 404 when the tenant has no template for the id — and resolve the template via `ICsatTemplateStore`.
Voice templates are already seeded per locale (`CsatDefaultTemplates`, `TemplatableChannels` including
`voice`). Synthesis MUST use the Pro-shipped TTS seam (available on the advanced Pro pin) and MUST NOT
fabricate audio when the seam is unavailable.

#### Scenario: Admin previews a seeded voice template

- **GIVEN** an `AdminOnly` caller and a tenant with a `voice`-channel template for the id
- **WHEN** it POSTs `/api/v1/admin/csat/templates/{id}/preview-voice`
- **THEN** the endpoint returns a synthesized preview of the template body (no longer HTTP 501)

#### Scenario: Missing template still 404s

- **GIVEN** an `AdminOnly` caller and an id with no template for the tenant
- **WHEN** it POSTs `/api/v1/admin/csat/templates/{id}/preview-voice`
- **THEN** the endpoint returns HTTP 404 and synthesizes nothing

## MODIFIED Requirements

### Requirement: CsatResponseRecordedEvent real-time push

On each successful capture, the endpoint SHALL publish a `CsatResponseRecordedEvent` via
`IPushEventBus`, and `PushToHubRelay` MUST route it to the `supervisor:{tenantId}` SignalR group
through the **typed** `IPlatformHubClient.OnCsatResponseRecorded(CsatResponseRecordedPayload)` client
method (replacing the prior untyped `IHubContext<PlatformHub>.Clients.Group(group).SendAsync("OnCsatResponseRecorded", …)`
name-based relay), following the `SendConversationAsync`/`SendAgentAsync` typed pattern in the same
relay. The typed `OnCsatResponseRecorded` method is defined on `IPlatformHubClient` in the Pro package
(buildOrder 1) — Platform's typed branch compiles only against the advanced Pro pin. The wire method
name and the `CsatResponseRecordedPayload` shape are unchanged
(`fixtures/csat-response-recorded-payload.v1.json` — verbatim fields `tenantId`, `responseId`,
`surveyId`, `conversationId`, `channel`, `queueName`, `rating`, `comment` (nullable), `capturedAt`;
the `channel` set now includes `voice` alongside `webchat`/`email`/`sms`), so no SignalR client
observes a wire change — this is type-safety hardening
(Platform/ADR-0020 deferred follow-up). `CsatResponseRecordedEvent` MUST remain a typed sealed record
registered in `ApiJsonContext` (Native AOT). Each capture MUST also write an audit row via
`IAuditService.RecordAsync` under the `csat` category.

#### Scenario: Supervisor session receives the recorded event via the typed method

- **GIVEN** a supervisor subscribed to the `supervisor:{tenantId}` group and a valid capture for that tenant
- **WHEN** the response is persisted
- **THEN** a `CsatResponseRecordedEvent` is published and delivered to the supervisor through the typed `IPlatformHubClient.OnCsatResponseRecorded`, and an audit row is written under category `csat`

#### Scenario: Voice capture delivers with channel voice

- **GIVEN** a supervisor subscribed to the `supervisor:{tenantId}` group and a valid voice capture for that tenant
- **WHEN** the voice response is persisted
- **THEN** the delivered `CsatResponseRecordedPayload` carries `channel` `voice`, `rating`, `queueName`, `capturedAt`, and a null `comment`

### Requirement: Per-queue CSAT analytics read

`GET /api/v1/analytics/csat/queues/{queueId}` SHALL require the `SupervisorPlus` policy and return
per-queue CSAT aggregates via `ISurveyAnalytics.GetByQueueAndChannelAsync(queueId, channel, range)`,
backed by the partial indexes. It coexists with the new scope-wide `GET /api/v1/analytics/csat`
aggregate read (see "Scope-wide aggregate CSAT analytics read"); both return `CsatResponseDto`-shaped
rows so a consumer can drill from the scope roll-up into a single queue without a shape change. The
existing `ISurveyAnalytics.GetByQueueAsync` MUST remain marked `[Obsolete]` and be removed one minor
release later (the 2-release deprecation cadence of Pro/ADR-0012). The per-queue response body MUST be
a typed sealed `CsatResponseDto` registered in `ApiJsonContext`.

#### Scenario: Supervisor reads queue CSAT summary

- **GIVEN** a `SupervisorPlus` caller and a queue with captured CSAT responses in the range
- **WHEN** it GETs `/api/v1/analytics/csat/queues/{queueId}?range=24h`
- **THEN** the endpoint returns a `CsatResponseDto` summary computed via `GetByQueueAndChannelAsync` over the channel-indexed rows

#### Scenario: Per-queue row shape matches an aggregate queues[] row

- **GIVEN** the scope-wide aggregate `queues[]` rows and the per-queue read
- **WHEN** both are serialized
- **THEN** each carries the identical `CsatResponseDto` fields (`queueName`, `channel`, `totalResponses`, `averageRating`, `rangeStart`, `rangeEnd`)

#### Scenario: Non-supervisor is denied

- **GIVEN** a caller lacking the `SupervisorPlus` policy
- **WHEN** it GETs `/api/v1/analytics/csat/queues/{queueId}`
- **THEN** the request is rejected by authorization and returns no analytics data
