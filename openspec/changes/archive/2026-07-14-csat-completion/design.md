# Design: csat-completion (Platform host — voice channel + aggregate KPI)

## Context

Verbara.Platform is the **consumer / host** (buildOrder 2) in the `csat-completion` cross-repo change.
The digital CSAT slice (webchat / email / sms) shipped as `csat-runner` (Platform/ADR-0020, the
brownfield extension of `Verbara.Platform.Surveys`); this change completes the train by adding the
voice channel end-to-end and the two open follow-ups recorded against ADR-0020 "Deferred follow-ups".

The cross-repo contract is `impact.yaml` (T11) with 3 frozen fixtures. This design covers **only the
host side**; the Pro producer child (`pro/csat-completion`: `VoiceCsatChannelAdapter`, `TtsPromptCache`,
`DtmfCollector`, `CsatVoiceOptions`, and the typed `IPlatformHubClient.OnCsatResponseRecorded` method)
and the Web consumer child (`web/csat-completion`: aggregate KPI card + `OnCsatResponseRecorded`
SignalR handler) are fanned out by `/xr:propagate` and authored in their own repos. `Verbara.Sdk` is
deliberately absent from `impact.yaml`: its existing primitives (`BlindTransferAction`/`RedirectAction`,
`HangupEvent`, AGI `GetData`/`GetOption`, `SpeechSynthesizer`) fully cover the voice path — the net-new
components land in Pro per the parked plan `../Verbara.Sdk.Pro/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md`.

Existing host seams this change extends (all shipped in `csat-runner`):

- `CsatConversationEndSource` — the Pro-defined `ICsatConversationEndSource` trigger seam; its
  `MapChannel` deliberately returns `null` for `ChannelType.Voice` (line ~218) and only the terminal
  `ConversationState.Closed` transition is subscribed.
- `VoiceConversationBridge.OnCallEndedAsync` — computes `IsAbnormalAgentHangup` and stamps it as
  conversation metadata (`agentLegAbnormal`) for the W5b callback worker, but publishes no CSAT-facing
  domain event.
- `VoiceCallControlService.BlindTransferAsync` — AMI-`Redirect`s the customer (trunk) leg into a
  dialplan context, keyed by `VoiceTransferKind` (`Queue`/`Agent`/`External`), leader-gated.
- `PushToHubRelay.ForwardCsatRecorded` — routes `CsatResponseRecordedEvent` to `supervisor:{tenantId}`
  through the **untyped** `_untypedHubContext.Clients.Group(group).SendAsync("OnCsatResponseRecorded", …)`
  because the typed `IPlatformHubClient` (Pro package) had no such method yet.
- `CsatResponseEndpoints` — the digital capture group + per-queue analytics read.
- `ISurveyAnalytics.GetByQueueAndChannelAsync` — the only CSAT analytics read; there is nothing
  scope-wide.

## Goals / Non-Goals

**Goals:**

- Solicit CSAT on voice conversations by wiring `ChannelType.Voice` through the existing trigger seam,
  keyed on the voice-specific terminal states (`WrapUp`/`Abandoned`), not the digital `Closed`.
- Publish an agent-hangup domain event so the voice-CSAT path can decide to solicit while the caller
  leg is still up (the abnormal-hangup verdict already exists, just unpublished).
- Hand the caller leg off to a survey IVR via the existing AMI-`Redirect` machinery (new transfer kind
  into a **shared** survey-IVR context; per-tenant isolation via Realtime DB, not file rendering).
- Add the public `POST /api/v1/csat/responses/voice` capture endpoint on the frozen `CsatResponseRequest`
  shape.
- Replace the untyped supervisor-push relay branch with a typed `IPlatformHubClient` branch (type-safety
  hardening — the ADR-0020 low-priority follow-up), gated on the Pro child shipping the method.
- Add the scope-wide `GET /api/v1/analytics/csat` aggregate read, resolving ADR-0020's ⟨NEEDS
  PRODUCT-OWNER INPUT⟩ wallboard question in favor of aggregation.
- Bring `preview-voice` (HTTP 501 today) live now that voice templates are exercised.
- Stay Native AOT clean (Platform/ADR-0022): the one new DTO source-gen registered, no reflection,
  no Dapper — `Verbara.Sdk.Data.Npgsql` for the aggregate query.

**Non-Goals:**

- The Pro voice adapter / TTS synthesis / DTMF collector — that is `pro/csat-completion`.
- The Web aggregate KPI card + SignalR handler — that is `web/csat-completion`.
- Any change to the frozen fixtures or `impact.yaml` (child specs cite them verbatim).
- A schema migration — the aggregate read reuses the partial indexes the digital slice already created;
  `SurveyResponse.CallId` (already an additive nullable column from `csat-runner`) carries the voice
  correlation, so no new column is needed.

## Decisions

### D1 — Voice trigger keys on WrapUp/Abandoned, not Closed (the terminal-state difference)

`CsatConversationEndSource` subscribes to `ConversationStateChangedEvent` filtered to `Closed`. Voice
conversations never reach `Closed` — `VoiceConversationBridge.OnCallEndedAsync` transitions an answered
call to `WrapUp` and a never-answered call to `Abandoned` (`ConversationState`: `WrapUp = 20`,
`Abandoned = 51`). The trigger therefore widens its terminal-state filter to also fire on the voice
terminal transitions, and `MapChannel` gains `ChannelType.Voice => "voice"`. To avoid soliciting a
customer who was never actually served, voice CSAT is solicited on `WrapUp` (the call was answered);
`Abandoned` does not solicit. **Alternative rejected:** a second, voice-only end source — it would
duplicate the queue-config / survey-resolution / signal-push resolution already centralized in
`CsatConversationEndSource`; widening the existing source keeps one trigger seam (single source of
truth for the orchestrator's gates).

### D2 — Agent-hangup domain event published from `VoiceConversationBridge`

`OnCallEndedAsync` already computes `IsAbnormalAgentHangup(agentCause, agentLeftAt, callerLeftAt)` and
stamps `agentLegAbnormal` metadata for the W5b callback worker. This change publishes a
`VoiceAgentHangupEvent` (typed sealed record, `PlatformEventBus`) carrying `(TenantId, ConversationId,
QueueName, Abnormal, HangupAt)` at the same point, so a voice-CSAT consumer can react while the caller
leg is still up (before the survey-IVR handoff loses the leg). The event is published inside the same
leader-gated, per-call-stripe-locked handler, so it inherits the exactly-once-cluster-wide guarantee.
**Alternative rejected:** reusing `ConversationStateChangedEvent(→WrapUp)` alone — it carries no
abnormal-hangup verdict and fires for every wrap-up, so the CSAT path could not distinguish a clean
hangup (solicit) from a leg death (don't strand the customer in an IVR); the dedicated event carries
the verdict.

### D3 — Survey-IVR handoff as a new `VoiceTransferKind.SurveyIvr` (shared context, per-tenant config)

Add `VoiceTransferKind.SurveyIvr` to `VoiceCallControlService`. Like `Queue`/`Agent`/`External`, it
resolves a channel variable then AMI-`Redirect`s the customer leg — here into a new **shared**
`[survey-ivr]` dialplan context in `docker/asterisk-config/extensions.conf`, setting a `SURVEY_ID` /
`SURVEY_TOKEN` channel variable the IVR reads. The dialplan is static and shared across tenants (same
as `[stasis-queue]`/`[transfer-agent]`); per-tenant survey config (prompt template, sampling) is
resolved from the queue `CsatConfig` + Asterisk Realtime DB, NOT rendered per-tenant into a file.
**Alternative rejected:** per-tenant dialplan file rendering — it breaks the static shared-context
model the whole voice stack relies on and would require regenerating + reloading dialplan per tenant
mutation; Realtime-DB config is the established isolation mechanism.

### D4 — Voice capture endpoint reuses the frozen `CsatResponseRequest` shape

`POST /api/v1/csat/responses/voice` binds the frozen `CsatResponseRequest` record verbatim
(`fixtures/csat-voice-capture.v1.json`): `channel` `voice`, `comment` `null` (DTMF carries no free
text), `questionId` `csat-rating-v1`, `responseToken` the Platform-minted voice-leg token (HMAC, the
`v1.{payload}.{sig}` pattern of Pro's `HmacCsatReplyTokenSigner`). It routes into a new voice branch of
the existing capture group: token-verified (voice-token verifier), license-gated
(`LicenseFeature.CsatRunner`), and shares the `CaptureAsync` persist → publish
`CsatResponseRecordedEvent` → audit path. The persisted `SurveyResponse` sets `CallId` from the
correlated voice conversation (the additive nullable column shipped in `csat-runner`). **Alternative
rejected:** a distinct voice request DTO — the fixture reuses the frozen shape 1:1, so a new DTO would
fork the wire contract the fixture-completeness rule froze.

### D5 — Typed `IPlatformHubClient` relay branch (gated on Pro buildOrder 1)

Replace `PushToHubRelay.SendCsatAsync`'s `_untypedHubContext.Clients.Group(group).SendAsync("OnCsatResponseRecorded", payload)`
with the typed `_hubContext.Clients.Group(group).OnCsatResponseRecorded(payload)` (mirroring
`SendConversationAsync`/`SendAgentAsync`, which use the typed `IHubContext<PlatformHub, IPlatformHubClient>`).
The typed `OnCsatResponseRecorded(CsatResponseRecordedPayload)` method is added to `IPlatformHubClient`
in the **Pro** package (`pro/csat-completion`, buildOrder 1). Platform's typed branch therefore only
compiles once the advanced Pro pin is restored — the cross-repo build barrier (`cross-repo-pack.sh`
between stages, `/xr:apply`) enforces that ordering. The wire method name and payload shape are
unchanged (`fixtures/csat-response-recorded-payload.v1.json`), so no client observes a wire change —
this is purely type-safety hardening (ADR-0020 low-priority follow-up). **Alternative rejected:**
keeping the untyped relay — functionally correct but perpetuates the stringly-typed method name the
ADR-0020 follow-up exists to remove.

### D6 — Scope-wide aggregate analytics read (resolves ADR-0020's KPI question)

Add `GET /api/v1/analytics/csat` (`SupervisorPlus`, license-gated) returning the envelope frozen by
`fixtures/csat-aggregate-analytics.v1.json`: a tenant/scope roll-up (`totalResponses`, `averageRating`,
`rangeStart`, `rangeEnd`) plus a `queues[]` array whose rows reuse `CsatResponseDto` **verbatim**
(`queueName`, `channel`, `totalResponses`, `averageRating`, `rangeStart`, `rangeEnd`); `channel` echoes
the requested filter and is `all` when unfiltered. Extend `ISurveyAnalytics` with a scope-wide overload
(e.g. `GetScopeAggregateAsync(tenantId, channel, range, ct)`) implemented in `PostgresSurveyAnalytics`
over the existing `(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL` partial index —
`GROUP BY queue_name` for the rows plus a top-level aggregate — via `Verbara.Sdk.Data.Npgsql`
(`NpgsqlExecutor` + name-based reader getters, no Dapper). The response is a new typed sealed
`CsatAggregateDto(int TotalResponses, double AverageRating, DateTimeOffset RangeStart, DateTimeOffset
RangeEnd, IReadOnlyList<CsatResponseDto> Queues)` registered in `ApiJsonContext`. This is the API-first
prerequisite the Web aggregate KPI card consumes; it **resolves Platform/ADR-0020's ⟨NEEDS
PRODUCT-OWNER INPUT⟩** in favor of aggregation over a per-queue selector (product-owner call 2026-07-13).
**Alternative rejected:** a queue selector on the existing per-queue endpoint — the product owner chose
aggregation; a selector would push the roll-up into the client and re-fan N per-queue reads.

### D7 — Voice template preview brought live

`CsatTemplateAdminEndpoints` `preview-voice` (HTTP 501 today, "voice preview deferred") synthesizes the
resolved voice template body now that the voice path ships. Voice templates are already seeded
(`CsatDefaultTemplates`: `TemplatableChannels` includes `voice`, with es-419 / pt-BR / en-US bodies).
The endpoint resolves the tenant template via `ICsatTemplateStore` (unchanged 404-on-missing / 400-on-
bad-id guards) and returns the synthesized preview via the Pro-shipped TTS seam (available once the Pro
child pins in). **Alternative rejected:** keeping it 501 — the ADR-0020 note ("voice is a Path-A
follow-up") is precisely this change; leaving it 501 would ship a voice channel whose admin surface
still claims voice is deferred.

### D8 — AOT registration (Platform/ADR-0022)

The one net-new DTO (`CsatAggregateDto`) is a typed sealed record registered in `ApiJsonContext`
(`JsonSerializerIsReflectionEnabledByDefault=false`, no anonymous `new {}`). `VoiceAgentHangupEvent`
is a `PlatformEvent`-derived record (in-process bus, not serialized over the wire) but stays reflection-
free. AOT publish must show 0 trim/AOT warnings on the advanced Pro pin.

### D9 — `IAmiConnection` deferred to first use (fail-at-use, not fail-at-boot)

`AddProCsatRunner` wires the voice adapter **unconditionally**, and the `CsatRunnerOrchestrator`
BackgroundService constructs every channel adapter — voice included — during `Host.StartAsync`. The
voice adapter takes an `IAmiConnection`, so the composition-root registration is resolved at host start.
Registering it as a factory that eagerly reads the primary `VerbaraServer.Connection` and threw when none
was configured crashed **every headless / no-telephony boot** (the CI OpenAPI-export capture in
`ci.yml`, minimal deploys). Resolution: `IAmiConnection` is a Platform-owned `DeferredPrimaryAmiConnection`
that looks up `VerbaraServerPool.GetServer("primary")` on each member access and throws the descriptive
`InvalidOperationException` only when a voice CSAT dispatch genuinely needs AMI and no primary server
exists — the same fail-closed pool access `AmiDtmfSource` uses, and the same `GetServer("primary")` accessor
`VoiceConversationBridge` / `VoiceCallControlService` use (no parallel access path). Voice CSAT dispatch only
ever fires for voice conversations, which require a live AMI connection, so deferring the failure to the
dispatch call site is semantically correct. AOT-safe (no reflection; direct virtual dispatch onto the
resolved connection).

## Risks / Trade-offs

- **Voice trigger widening touches a hot event path** → `CsatConversationEndSource` now matches more
  terminal transitions. Mitigation: the added filter is `WrapUp` only (answered calls), the handler
  stays fire-and-forget + fully guarded (a bad conversation never faults the stream), and the
  orchestrator's existing license/queue/sampling gates still own the solicit decision.
- **Survey-IVR handoff redirects the live caller leg** → a stale/gone channel leaves the call as-is
  (best-effort AMI, mirrors the existing transfer kinds); a wrongly-solicited abnormal-hangup call is
  guarded by D2's abnormal verdict (don't strand a customer whose agent leg died) — conservative,
  favoring false negatives.
- **Typed relay branch has a cross-repo build dependency** → Platform's typed branch does not compile
  until Pro ships `OnCsatResponseRecorded`. Mitigation: buildOrder 1 (Pro) before stage 2 (Platform),
  enforced by `cross-repo-pack.sh` barriers in `/xr:apply`; the wire contract is unchanged so no
  runtime coordination is required.
- **Aggregate query fan-out** → `GROUP BY queue_name` over a large tenant's CSAT rows. Mitigation: it
  rides the same partial index the per-queue read uses (`WHERE channel IS NOT NULL`), bounding the scan
  to CSAT rows; the range filter caps the window.
- **Voice-token minting is new Platform surface** → the voice-leg token must be HMAC-signed with the
  same rigor as the webchat/email/sms tokens (reject missing/malformed/expired/mismatched). Mitigation:
  reuse the established HMAC pattern; the capture endpoint rejects and persists nothing on token failure.

## Migration Plan

1. Pro child (`pro/csat-completion`, buildOrder 1) ships the typed `IPlatformHubClient.OnCsatResponseRecorded`
   + the voice adapter/TTS/DTMF; pack to the local feed; Platform re-pins.
2. Deploy the shared `[survey-ivr]` dialplan context (`extensions.conf`) — additive, no existing context
   changes.
3. Deploy Platform (stage 2): voice trigger, agent-hangup event, survey-IVR transfer kind, voice capture
   endpoint, typed relay branch, aggregate endpoint, live preview-voice.
4. Web child (`web/csat-completion`, buildOrder 2, decoupled) consumes `GET /api/v1/analytics/csat`.
5. **Rollback:** all additive — voice CSAT is gated per-queue by the existing `csat_enabled` (defaults
   `false`) and per-tenant by `LicenseFeature.CsatRunner`; the typed relay branch is a drop-in for the
   untyped one with an identical wire contract; no schema/data migration to reverse.

## Open Questions

- None blocking. The KPI-scope question (ADR-0020) is resolved to aggregation by product-owner decision
  (2026-07-13); recorded in the proposal and D6. The Platform-minted voice-token signer's key rotation
  policy is inherited from the existing CSAT token infrastructure (no new decision here).
