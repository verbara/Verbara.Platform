---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Contact-center operators, supervisors, and tenants running voice CSAT surveys
decision_ref: Platform/ADR-0020
---

# Proposal: csat-completion (Platform host — voice channel + aggregate KPI)

## Why

The digital CSAT slice shipped (webchat / email / sms) but the voice channel — the highest-volume
channel in a contact center — was deliberately excluded: `CsatConversationEndSource.MapChannel`
returns `null` for `ChannelType.Voice`, and the voice adapter + TTS + DTMF machinery was parked in
Pro. Two follow-ups recorded against Platform/ADR-0020 "Deferred follow-ups (post-ship)" remain open:
the typed `IPlatformHubClient.OnCsatResponseRecorded` supervisor push (still fanned through the
untyped name-based relay), and the ⟨NEEDS PRODUCT-OWNER INPUT⟩ wallboard KPI-card scope. The product
owner decided (2026-07-13) to close the whole train in one cross-repo change and to resolve the KPI
question in favor of a scope-wide aggregate — which is API-first, so it cascades a new Platform read
endpoint before the Web surface can consume it.

## What Changes

This is the **HOST (Platform)** side of the `csat-completion` cross-repo change (the cross-repo
contract is `impact.yaml`; the Pro producer child and the Web consumer child are fanned out by
`/xr:propagate`). All changes are additive and back-compat — the digital CSAT slice and pre-existing
survey consumers are unaffected:

- **Voice trigger** — wire `ChannelType.Voice` into the existing Pro-defined trigger seam
  `CsatConversationEndSource`. Voice conversations terminate via `WrapUp` (answered) / `Abandoned`
  (never-answered), NOT the digital `Closed` transition the current source subscribes to, so the
  voice trigger reacts to those distinct terminal states.
- **Agent-hangup domain event** — publish a domain event from `VoiceConversationBridge.OnCallEndedAsync`
  (the already-computed `IsAbnormalAgentHangup` verdict is currently only stamped as conversation
  metadata, never published) so the voice-CSAT path can decide whether to solicit while the caller
  leg is still up.
- **Survey-IVR handoff** — a new `VoiceTransferKind.SurveyIvr` blind-transfer target reusing the AMI
  `Redirect` machinery of `VoiceCallControlService.BlindTransferAsync`, routing the caller leg into
  a **shared** survey-IVR dialplan context (`docker/asterisk-config/extensions.conf`) — per-tenant
  isolation comes from Asterisk Realtime DB config, NOT per-tenant file rendering.
- **Voice capture endpoint** — `POST /api/v1/csat/responses/voice`, reusing the frozen
  `CsatResponseRequest` shape (`fixtures/csat-voice-capture.v1.json`; `channel` `voice`, `comment`
  `null` — DTMF carries no free text), Platform-minted-voice-token gated.
- **Typed supervisor push** — replace the untyped `PushToHubRelay` CSAT branch
  (`SendAsync("OnCsatResponseRecorded", …)`) with a typed `IPlatformHubClient` branch following the
  `SendConversationAsync`/`SendAgentAsync` pattern. **Gated on the Pro child shipping
  `OnCsatResponseRecorded` on `IPlatformHubClient` (buildOrder 1, before Platform's stage 2).**
- **Aggregate analytics endpoint** — NEW `GET /api/v1/analytics/csat` (scope-wide roll-up across
  queues) per `fixtures/csat-aggregate-analytics.v1.json`, extending `ISurveyAnalytics` beyond the
  existing per-queue `GetByQueueAndChannelAsync`; the envelope + `queues[]` rows reuse
  `CsatResponseDto` verbatim. **This resolves Platform/ADR-0020's ⟨NEEDS PRODUCT-OWNER INPUT⟩
  wallboard-card question in favor of aggregation.**
- **Voice template preview** — bring `CsatTemplateAdminEndpoints` `preview-voice` (today HTTP 501)
  into scope: synthesize the seeded voice template (`CsatDefaultTemplates`) now that the voice path
  ships.

## Capabilities

### New Capabilities

(none — the `csat` capability already exists as a living spec at `openspec/specs/csat/spec.md`.)

### Modified Capabilities

- `csat`: extends the shipped CSAT capability with the voice channel end-to-end (trigger →
  agent-hangup signal → survey-IVR handoff → voice capture endpoint), the scope-wide aggregate
  analytics read, the typed-relay hardening of the supervisor push, and the now-live voice template
  preview. Adds new requirements for each and modifies the two existing requirements whose behavior
  changes (the supervisor-push relay becomes typed; the analytics requirement gains the aggregate
  read alongside the per-queue read).

## Impact

- **Code:** `Verbara.Platform.Api` (`CsatConversationEndSource` voice mapping, `VoiceConversationBridge`
  domain-event publish, `VoiceCallControlService` new transfer kind, `CsatResponseEndpoints` voice
  route, `CsatTemplateAdminEndpoints` preview-voice, new `CsatAggregateDto` in `ApiJsonContext`),
  `Verbara.Platform.Surveys` (`ISurveyAnalytics` aggregate overload), `Verbara.Platform.Storage.Postgres`
  (aggregate analytics query over the existing partial indexes — no schema change), `Verbara.Platform.Realtime`
  (`PushToHubRelay` typed CSAT branch), `docker/asterisk-config/extensions.conf` (shared survey-IVR context).
- **APIs:** 2 new endpoints (`POST /api/v1/csat/responses/voice`, `GET /api/v1/analytics/csat`); 1
  endpoint promoted from 501 (`POST /api/v1/admin/csat/templates/{id}/preview-voice`).
- **Wire contract:** 3 fixtures (all frozen at `/xr:change` time, unmodified here):
  `csat-voice-capture.v1.json`, `csat-aggregate-analytics.v1.json`, `csat-response-recorded-payload.v1.json`
  (the last gains `voice` in its `channel` enum; `webchat`/`email`/`sms` remain valid).
- **Dependencies:** cross-repo — consumes the Pro child's typed `IPlatformHubClient.OnCsatResponseRecorded`
  (buildOrder 1) via the SignalR client contract; the voice adapter / TTS / DTMF collector land in Pro,
  not Platform (`impact.yaml` deliberately omits `Verbara.Sdk` — its primitives already cover the path).
  Pairs with the Web child (aggregate KPI card + `OnCsatResponseRecorded` SignalR handler).
- **Data:** no schema migration — the aggregate read reuses the `survey_responses` partial indexes the
  digital slice already created (`(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL`).
