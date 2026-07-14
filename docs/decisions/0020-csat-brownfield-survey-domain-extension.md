# ADR-0020: CSAT Runner as Brownfield Extension of `Verbara.Platform.Surveys`

- **Status:** Accepted
- **Date:** 2026-05-18
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - Specs: [`docs/specs/2026-05-18-platform-240-csat-consumer.md`](../specs/2026-05-18-platform-240-csat-consumer.md), [`Verbara.Sdk.Pro/docs/specs/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-18-pro-260-csat-runner-v1.md)
  - Plans: [`docs/plans/completed/2026-05-18-platform-240-csat-consumer.md`](../plans/completed/2026-05-18-platform-240-csat-consumer.md) (moved to `completed/` on ship, PR #144), [`Verbara.Sdk.Pro/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md)
  - System plan file: `~/.claude/plans/si-refactored-pascal.md` (planning-mode canonical until materialized in repos)

## Context

The next Pro feature train (`Verbara.Sdk.Pro.CsatRunner` v1, Option F multi-channel native — voice IVR via Stasis bridge hold + WebChat inline rating panel + Email reply-to-rate + SMS-back) needs a place to persist customer satisfaction responses and a domain to model the per-tenant survey configuration.

During Phase 1 planning exploration (2026-05-18), an Explore agent discovered that **Verbara.Platform already ships substantial Survey scaffolding** that was not in scope when the CSAT idea was first floated:

- [`src/Verbara.Platform.Surveys/`](../../src/Verbara.Platform.Surveys/) — full DDD project with `Survey`, `SurveyResponse`, `SurveyAnswer`, `SurveyQuestion`, `SurveyType` enum (Csat, Nps, Custom), `SurveyScoreSummary`, `ISurveyStore`, `ISurveyResponseStore`, `ISurveyAnalytics`, `ISurveyDeliveryService` (sends through `IConversationService` as `InteractiveBlock` with `QuickReply 1-5`), `InMemorySurveyAnalytics` with CSAT averages + NPS promoter/passive/detractor math already implemented.
- [`src/Verbara.Platform.Storage.Postgres/Migrations/001_Baseline.sql:497-514`](../../src/Verbara.Platform.Storage.Postgres/Migrations/001_Baseline.sql) — `surveys` + `survey_responses` tables exist (PK `(tenant_id, response_id)`, indexed on `conversation_id` + `survey_id`; answers stored as JSONB).
- [`src/Verbara.Platform.Api/Endpoints/SurveyEndpoints.cs`](../../src/Verbara.Platform.Api/Endpoints/SurveyEndpoints.cs) — admin CRUD `/admin/surveys` + analytics `/analytics/surveys/{id}/summary`/`/responses`, with `AdminOnly` + `SupervisorPlus` policies and full audit integration.
- [`Verbara.Platform.Web/src/admin/surveys/`](https://github.com/verbara/Verbara.Platform.Web/tree/main/src/admin/surveys) + [`src/analytics/surveys/survey-results-page.tsx`](https://github.com/verbara/Verbara.Platform.Web/tree/main/src/analytics/surveys/survey-results-page.tsx) — admin + analytics surfaces already wired.

This was a brownfield discovery — the original "build a new `csat_responses` table + admin CRUD + analytics + Web pages" framing duplicates ~3-4 days of existing functionality.

## Decision

**`Pro.CsatRunner` becomes the delivery + capture orchestrator layered on top of the Platform Survey domain. We extend `Verbara.Platform.Surveys`; we do NOT create a parallel `csat_responses` table or duplicate admin/analytics surfaces.**

Concretely:

### Survey domain extensions (additive, back-compat)

- `SurveyResponse` gains init-only properties: `Channel?` (string ∈ `{voice, webchat, email, sms}`), `QueueName?`, `Rating?` (SMALLINT 1-5), `Comment?`, `CapturedAt?`, `CallId?`. Pre-existing rows without these populated continue to load via default null values.
- `ISurveyAnalytics` gains channel/queue filter overloads: `GetByQueueAndChannelAsync(queueId, channel, range)`. Existing `GetByQueueAsync` (which uses a `__queue` answer-marker hack today) remains backward-compatible but is deprecated for new code.
- `survey_responses` table gains the same columns via new Postgres migration. Constraints enforced: `channel ∈ {voice, webchat, email, sms}` and `rating BETWEEN 1 AND 5` (both nullable).
- Two new indexes for query performance: `(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL` and `(tenant_id, agent_id, captured_at DESC) WHERE channel IS NOT NULL`.

### Pro-side internal state (additive, not in Survey domain)

Pro owns:
- A separate `csat_pending_dispatches` table for SMS correlation (phone + 24h window). This is a Pro-internal lookup, not a domain entity.
- A separate `csat_templates` table for per-tenant prompt management (Platform-side, since admin UI lives in Web).
- TTS prompt-audio cache (object storage or local disk; not in Postgres).

### Pro/Platform boundary

- Pro's `Verbara.Sdk.Pro.CsatRunner` consumes Platform's `ISurveyResponseStore.SaveAsync` via DI — NOT via API call. They live in the same process.
- Pro's channel adapters publish `CsatResponseRecordedEvent` via `IPushEventBus`; Platform's `PushToHubRelay` forwards to SignalR clients.
- Token-signed anonymous endpoints (`POST /api/v1/csat/responses/{webchat,email,sms}`) live in Platform.Api, NOT in Pro — they are public-facing HTTP surface.

## Alternatives considered

| Option | Why rejected |
|---|---|
| **Parallel `csat_responses` table + duplicate admin/analytics** | Adds ~3-4d of code duplicating `Verbara.Platform.Surveys`; splits the operator surface ("which screen has my CSAT data?"); requires custom analytics for what `InMemorySurveyAnalytics` + `PostgresSurveyResponseStore` already provide. |
| **Replace `Verbara.Platform.Surveys` with a CSAT-only domain** | Existing NPS + Custom survey support has consumers in Platform v1.4.0+; replacing breaks back-compat for any tenant that has configured a `Custom` survey today. |
| **Pro-side standalone domain in `Verbara.Sdk.Pro.CsatRunner.Storage.Postgres`** | Forces Platform admin UI + analytics endpoints to call Pro DI (already in process) AND to know about a Pro-private table. Violates the Pro/Platform abstraction boundary. |
| **Use `Verbara.Platform.Surveys` AS-IS (no schema changes)** | The existing JSONB `answers` field can hold rating data but lacks: indexed channel filtering, indexed queue filtering, indexed agent filtering for the supervisor dashboard real-time aggregations. Without indexes, dashboard queries don't meet the <2s real-time SLA. |

## Consequences

### Positive

- **~3-4 days of scope eliminated** — no parallel admin CRUD or analytics implementation.
- **Single operator mental model** — surveys (all kinds: CSAT, NPS, Custom) live in one place; same admin and analytics workflow.
- **Reuses production-proven code** — `PostgresSurveyResponseStore`, `InMemorySurveyAnalytics`, `SurveyEndpoints` audit integration.
- **Back-compat preserved** — pre-existing surveys + tenants unaffected; the new columns are additive and nullable.
- **AOT-safe** — no new reflection introduced; all new types registered in existing `ApiJsonContext`.

### Negative

- **`SurveyResponse` becomes slightly schizophrenic** — it now models both "multi-question generic survey response" and "single-question rating capture". The model code stays clean (additive properties), but consumers reading the entity need to know that CSAT-flavoured responses populate `Rating` directly instead of relying on parsing `Answers[csatRatingQuestionId]`.
- **`survey_responses` index growth** — two new indexes scoped `WHERE channel IS NOT NULL` keep the cost bounded for non-CSAT surveys, but every CSAT response adds index entries. Expected volume (CSAT rate ~20% per queue, ~10 conv/min/queue, 200 queues per large tenant) → ~57M index entries/year per tenant — within Postgres comfort range.
- **`ISurveyAnalytics.GetByQueueAsync` (current) and `GetByQueueAndChannelAsync` (new) co-exist** for a few releases. We mark the former `[Obsolete]` after Platform v2.4.0 ships and remove in v2.5.0 (the next minor cycle) — same 2-release deprecation pattern as ADR-0012.

### Neutral

- **Per-tenant template management** still requires a new admin UI section (`/admin/csat/templates`) + new endpoints + new `csat_templates` table. The brownfield-aware decision is about **response capture**, not about template management. Both decisions are independent.

## Implementation notes

- `Verbara.Platform.Storage.Postgres` migration number depends on what ships between today (2026-05-18) and execution kickoff. The plan file references it generically as `0XX_SurveyCsatExtensions.sql`.
- New Pro `LicenseFeature.CsatRunner` enum value is additive in `Verbara.Sdk.Pro.Licensing` — no consumer break. Tier-mapping table updated to include this feature in tier 1+ (paid) plans.
- A well-known constant `SurveyQuestionIds.CsatRating = "csat-rating-v1"` lives in `Verbara.Platform.Surveys.SurveyQuestionIds` so all four Pro channel adapters reference the same question ID.

## References

- Plan file (system path, planning-mode canonical until repos are materialized): `~/.claude/plans/si-refactored-pascal.md`
- Pro spec: [`Verbara.Sdk.Pro/docs/specs/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/specs/2026-05-18-pro-260-csat-runner-v1.md)
- Platform spec: [`docs/specs/2026-05-18-platform-240-csat-consumer.md`](../specs/2026-05-18-platform-240-csat-consumer.md)
- Existing Survey domain: [`src/Verbara.Platform.Surveys/`](../../src/Verbara.Platform.Surveys/)
- Pro pipeline template (mirror): [`Verbara.Sdk.Pro/src/Verbara.Sdk.Pro.CallAnalytics/Engine/CallAnalyticsEngine.cs`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/src/Verbara.Sdk.Pro.CallAnalytics/Engine/CallAnalyticsEngine.cs)
- Related ADR: [`Verbara.Sdk.Pro 0012`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md) — same 2-release deprecation pattern applied to `GetByQueueAsync` here.

## Deferred follow-ups (post-ship — recorded 2026-07-12)

The digital CSAT slice shipped 2026-07-11 (Pro `2.9.0-pro` + Platform `2.18.0` +
Web `3.13.0-web`). Of the three post-ship follow-ups identified during close-out,
the Web analytics-contract fix landed as `3.13.1-web` (2026-07-12, PR
Verbara.Platform.Web#159). The two below remain **deferred / not yet scheduled**
and are recorded here — against the CSAT decision anchor — so cross-repo backlog
discovery (`/xr:pending`, which mines `docs/decisions/` for items marked
deferred/follow-up) surfaces them:

- **[owner: Pro] Typed `IPlatformHubClient.OnCsatResponseRecorded` Hub method.**
  The real-time supervisor push currently fans out through the untyped
  `IHubContext` name-based relay. The typed path adds
  `OnCsatResponseRecorded(CsatResponseRecordedEvent)` to
  `Verbara.Sdk.Pro.Push.SignalR/Hubs/IPlatformHubClient.cs` (+ a
  `CsatResponseRecordedEvent` under `Pro.Push.SignalR/Events/`) plus a matching
  typed branch in Platform's `Verbara.Platform.Realtime/Services/PushToHubRelay.cs`.
  **Priority: low** — the untyped relay is functionally correct; this is
  type-safety hardening only. Deferred to avoid a Pro release cascade
  (Pro → Platform re-pin) for a non-functional change.

- **[owner: Web] CSAT KPI card queue scope — ⟨NEEDS PRODUCT-OWNER INPUT⟩.** The
  supervisor wallboard KPI card is scoped to a single queue (`sortedQueues[0]`
  in `wallboard-page.tsx`). Whether it should instead aggregate CSAT across all
  visible queues (which would require a new tenant-wide analytics endpoint) or
  expose an explicit queue selector is a **product/UX decision, not a bug**.
  Blocked on: product-owner call. If aggregation is chosen, it cascades a new
  Platform read endpoint (API-first) before the Web surface.

decision_ref: Platform/ADR-0020 · csat-runner train close-out.

## Addendum (2026-07-14): both deferred follow-ups resolved by `csat-completion` (append-only)

The two follow-ups recorded above as **deferred / not yet scheduled** were both
delivered by the cross-repo change `csat-completion` (host: Platform PR #155,
merged 2026-07-14; Pro + Web children archived alongside). They are no longer
open — recorded here so `/xr:pending` (which mines this ADR for items marked
deferred/follow-up) stops surfacing them; the items above are preserved
unchanged per the append-only convention.

- **[owner: Pro] Typed `IPlatformHubClient.OnCsatResponseRecorded` Hub method — RESOLVED.**
  The Pro child added the typed `OnCsatResponseRecorded(CsatResponseRecordedPayload)`
  method to `IPlatformHubClient`, and Platform's `PushToHubRelay` CSAT branch was
  converted from the untyped `IHubContext` name-based relay to the typed
  `IPlatformHubClient.OnCsatResponseRecorded` path (mirroring
  `SendConversationAsync`/`SendAgentAsync`). The wire method name and
  `CsatResponseRecordedPayload` shape are unchanged, so no SignalR client observed
  a wire change. Living spec: `csat` → "CsatResponseRecordedEvent real-time push"
  (now MODIFIED to the typed relay).

- **[owner: Web] CSAT KPI card queue scope ⟨NEEDS PRODUCT-OWNER INPUT⟩ — RESOLVED to aggregation.**
  The product owner decided (2026-07-13) in favor of a scope-wide aggregate over
  a per-queue selector. As the API-first prerequisite, Platform added the
  scope-wide `GET /api/v1/analytics/csat` aggregate read (`SupervisorPlus`,
  license-gated, backed by the existing `WHERE channel IS NOT NULL` partial index,
  no schema change); the Web child replaced the single-queue wallboard card with
  aggregate consumption. Living spec: `csat` → "Scope-wide aggregate CSAT
  analytics read" (new requirement, coexisting with the per-queue read).

decision_ref: Platform/ADR-0020 · csat-completion train close-out.
