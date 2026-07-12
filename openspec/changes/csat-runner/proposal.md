---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Contact-center operators, supervisors, and tenants running CSAT surveys
decision_ref: Platform/ADR-0020
---

# Proposal: csat-runner (Platform host — CSAT consumer)

## Why

The CSAT Runner train (Pro ships the engine + 4 channel adapters; Web ships the UI) needs a
Platform-side home to persist customer-satisfaction responses, expose the public capture surface,
and gap-fill the channels the engine cannot reach in-process (inbound Email replies, inbound SMS
digit-replies). A Phase-1 brownfield discovery (ADR-0020) found that Verbara.Platform **already
ships a full Surveys domain** — `Survey`, `SurveyResponse`, `ISurveyAnalytics`,
`ISurveyResponseStore`, `PostgresSurveyResponseStore`, admin CRUD + analytics endpoints, and a
`SurveyType.Csat` enum value — so the correct move is to **extend that domain**, not stand up a
parallel `csat_responses` table and duplicate ~3-4 days of admin/analytics surface. This is the
OpenSpec backlog home for the Platform (consumer) side of the train, translating the frozen,
approved spec + execution plan (both dated 2026-05-18, "APPROVED, execution paused") into the
living-spec workflow.

## What Changes

Brownfield extension of the existing `Verbara.Platform.Surveys` domain per ADR-0020 (all additive
and back-compat — pre-existing survey rows and consumers are unaffected):

- **Survey domain extension** — `SurveyResponse` gains 6 nullable init-only properties (`Channel`,
  `QueueName`, `Rating`, `Comment`, `CapturedAt`, `CallId`); `ISurveyAnalytics` gains a
  `GetByQueueAndChannelAsync` overload (existing `GetByQueueAsync` marked `[Obsolete]` for v2.19.0
  removal, same 2-release deprecation pattern as Pro/ADR-0012); a new `SurveyQuestionIds.CsatRating
  = "csat-rating-v1"` well-known constant.
- **Postgres migration** (`0XX_SurveyCsatExtensions.sql`) — extends `survey_responses` (6 nullable
  columns + channel/rating CHECK constraints + 2 partial indexes `WHERE channel IS NOT NULL`),
  adds the `csat_pending_dispatches` table (SMS correlation), adds the `csat_templates` table
  (per-tenant prompt store), and extends `queues` with 4 CSAT config columns.
- **Public capture endpoints** — `POST /api/v1/csat/responses/{webchat,email,sms}` (token-signed /
  internal-key) + `GET /api/v1/analytics/csat/queues/{queueId}`. Each persists a `SurveyResponse`,
  publishes `CsatResponseRecordedEvent`, writes an audit row, and returns HTTP 402 ProblemDetails
  when the license gate fails.
- **Email IMAP gap-fill** — `ImapInboundPoller` (`IHostedService`) + `CsatReplyMailHandler` in
  `Verbara.Platform.Mail` (currently outbound-only) to poll the `csat@…` mailbox and forward parsed
  digit replies.
- **SMS correlator** — `CsatSmsCorrelator` in `Verbara.Platform.Channels.Sms` matching inbound
  digit replies against `csat_pending_dispatches` within a 24h window, falling through to normal
  routing otherwise.
- **Real-time push** — `IPlatformHubClient.OnCsatResponseRecorded` typed Hub method +
  `PushToHubRelay` event branch to `supervisor:{tenantId}` groups.
- **`ICsatTemplateProvider`** — Platform-side `CsatTemplateProvider` implementing the Pro-defined
  DI contract (consumed in-process by Pro's Email adapter) + per-tenant admin template endpoints
  under `/api/v1/admin/csat/templates/*`.
- **AOT** — 5 new DTOs (`CsatResponseRequest`, `CsatResponseDto`, `QueueCsatConfigDto`,
  `CsatTemplateDto`, `CsatResponseRecordedEvent`) registered in `ApiJsonContext`.
- **Re-pin** — Platform target `2.4.0` → `2.18.0` (baseline `2.17.0`); Pro pin advanced to consume
  the CSAT engine package.

## Capabilities

### New Capabilities

- `csat`: customer-satisfaction response capture as a brownfield extension of the Surveys domain —
  the token-signed public capture wire shape, per-channel capture (webchat / email / sms), the
  survey-domain persistence extension, per-queue CSAT config, per-tenant templates, the
  `CsatResponseRecordedEvent` real-time push, and the license gate.

### Modified Capabilities

(none — the pre-existing Surveys domain has no OpenSpec living spec in `openspec/specs/`; the CSAT
extension is captured wholly in the new `csat` capability, which documents its brownfield reuse of
`SurveyResponse` / `ISurveyAnalytics` / `PostgresSurveyResponseStore`.)

## Impact

- **Code:** `Verbara.Platform.Surveys` (domain extension), `Verbara.Platform.Storage.Postgres`
  (migration + `PostgresSurveyResponseStore` / analytics stores), `Verbara.Platform.Queues`
  (Queue.Csat), `Verbara.Platform.Api` (endpoints, DTOs, Hub, relay, `CsatTemplateProvider`,
  `ApiJsonContext`), `Verbara.Platform.Mail` (IMAP gap-fill), `Verbara.Platform.Channels.Sms`
  (correlator).
- **APIs:** 9 new endpoints (4 capture/analytics + 4 admin template + 1 queue-config extension via
  existing admin endpoint).
- **Dependencies:** consumes Pro's `Verbara.Sdk.Pro.CsatRunner` (buildOrder 1) via the
  `ICsatTemplateProvider` contract + `LicenseFeature.CsatRunner` gate — same-process DI, not an
  API call. Pairs with Web (supervisor CSAT card + embed rating panel).
- **Data:** Postgres schema migration (additive; back-compat verified — existing rows load with new
  columns null; new indexes scoped `WHERE channel IS NOT NULL`).

## Architectural Risk

**Level:** MEDIUM — cross-repo in-process contract (`ICsatTemplateProvider` defined in Pro,
implemented in Platform) plus a schema migration on the production-sized `survey_responses` table.
**Affected:** Surveys domain, Postgres storage, Mail + SMS ingress. **Mitigation:** all domain
changes additive/nullable; `CREATE INDEX CONCURRENTLY` for large tenants (runbook); integration
test against the actual Pro CSAT nupkg (not a stub); SMS correlator falls through on non-rating
bodies so user messages are never eaten.
