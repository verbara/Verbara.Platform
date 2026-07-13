# Tasks: csat-completion (Platform host — voice channel + aggregate KPI)

The HOST (Platform) side of the `csat-completion` cross-repo change (contract: `impact.yaml`; the Pro
producer child `pro/csat-completion` and Web consumer child `web/csat-completion` are fanned out by
`/xr:propagate`). All changes are additive — the digital CSAT slice (`csat-runner`) is unaffected.

**Cross-repo pre-condition (buildOrder 1 before Platform's stage 2, enforced by `cross-repo-pack.sh`
in `/xr:apply`):** the Pro child ships the typed `IPlatformHubClient.OnCsatResponseRecorded` method +
the voice adapter / TTS / DTMF collector, packed to the local feed and the Pro pin advanced, before
Platform's typed-relay branch (task 5) and voice-template preview synthesis (task 6) can compile.

## 1. Voice trigger (Phase A)

- [x] 1.1 `src/Verbara.Platform.Api/Services/CsatConversationEndSource.cs` — add `ChannelType.Voice => "voice"` to `MapChannel`; widen the terminal-state subscription filter to also fire on the voice `WrapUp` transition (answered), and NOT on `Abandoned` (never-answered) — keeps the existing digital `Closed` path unchanged
- [x] 1.2 `tests/Verbara.Platform.Api.Tests/CsatConversationEndSourceTests.cs` — add voice cases: answered→WrapUp pushes a `CsatConversationEndedSignal` with `NativeChannel` `voice`; Abandoned pushes nothing; CSAT-disabled queue still pushes with `CsatEnabled=false` (orchestrator owns the skip)

## 2. Voice agent-hangup domain event (Phase A)

- [x] 2.1 `src/Verbara.Platform.Core/VoiceAgentHangupEvent.cs` (NEW) — typed sealed `PlatformEvent`-derived record `(TenantId, ConversationId, QueueName, Abnormal, HangupAt)`; reflection-free (Platform/ADR-0022)
- [x] 2.2 `src/Verbara.Platform.Api/Services/VoiceConversationBridge.cs` — in `OnCallEndedAsync`, publish `VoiceAgentHangupEvent` via `PlatformEventBus` at the point `IsAbnormalAgentHangup` is already computed (same leader-gated, per-call-stripe-locked handler → exactly-once cluster-wide)
- [x] 2.3 `tests/Verbara.Platform.Api.Tests/VoiceConversationBridgeTests.cs` — clean hangup (`NormalClearing`) → `Abnormal=false`; abnormal leg death → `Abnormal=true`; follower pod (no AMI-owner lease) publishes nothing

## 3. Survey-IVR handoff (Phase B)

- [x] 3.1 `src/Verbara.Platform.Api/Services/VoiceCallControlService.cs` — add `VoiceTransferKind.SurveyIvr`; in `BlindTransferAsync` set the survey channel variable(s) (survey id + Platform-minted voice-leg token) then AMI-`Redirect` the customer leg into the shared `[survey-ivr]` context; stays leader-gated; returns `channel-unknown` when no customer channel is persisted
- [x] 3.2 `docker/asterisk-config/extensions.conf` — add the shared `[survey-ivr]` context (static, shared across tenants like `[stasis-queue]`/`[transfer-agent]`; per-tenant survey config via queue `CsatConfig` + Realtime DB, NOT per-tenant file rendering)
- [x] 3.3 `tests/Verbara.Platform.Api.Tests/VoiceCallControlServiceTests.cs` — survey-IVR transfer sets the vars + Redirects into `[survey-ivr]` (accepted); non-leader returns `not-leader`; unknown customer channel returns `channel-unknown`

## 4. Voice capture endpoint (Phase B)

- [x] 4.1 `src/Verbara.Platform.Api/Services/` — add the Platform-minted voice-leg token verifier (HMAC `v1.{payload}.{sig}`, mirrors the webchat `ICsatWebChatTokenVerifier`; rejects missing/malformed/expired/mismatched tenant-queue-channel)
- [x] 4.2 `src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs` — add `POST /api/v1/csat/responses/voice` binding the frozen `CsatResponseRequest` (`channel` `voice`, `comment` null); anonymous + voice-token-verified + `LicenseFeature.CsatRunner`-gated; shares the `CaptureAsync` persist→publish→audit path, setting `SurveyResponse.CallId` from the correlated voice conversation
- [x] 4.3 `src/Verbara.Platform.Api/Program.cs` — register the voice route in the existing `MapCsatResponseEndpoints` group (no new group)
- [x] 4.4 `tests/Verbara.Platform.Api.Tests/CsatResponseEndpointsTests.cs` — voice capture happy path (fixture body `csat-voice-capture.v1.json`) persists `SurveyResponse` (channel `voice`, comment null, `CallId` set) + publishes `CsatResponseRecordedEvent` + `csat`-category audit; token rejection cases (missing/malformed/expired/queue-channel mismatch); rating out of 1..5 rejected; 402 when `LicenseFeature.CsatRunner` absent

## 5. Typed supervisor push (Phase B — gated on Pro buildOrder 1)

- [x] 5.1 `src/Verbara.Platform.Realtime/Services/PushToHubRelay.cs` — replace `SendCsatAsync`'s untyped `_untypedHubContext.Clients.Group(group).SendAsync("OnCsatResponseRecorded", payload)` with the typed `_hubContext.Clients.Group(group).OnCsatResponseRecorded(payload)` (mirrors `SendConversationAsync`/`SendAgentAsync`); remove the now-unused untyped-context CSAT path if no other branch needs it. Compiles only against the advanced Pro pin (`IPlatformHubClient.OnCsatResponseRecorded`)
- [x] 5.2 `tests/Verbara.Platform.Realtime.Tests/PushToHubRelayTests.cs` — assert the recorded event is delivered through the typed `IPlatformHubClient.OnCsatResponseRecorded` to `supervisor:{tenantId}` (unchanged wire method name + `CsatResponseRecordedPayload` shape, `channel` includes `voice`); not-leader / null-tenant skip paths still hold

## 6. Voice template preview (Phase B — gated on Pro buildOrder 1)

- [x] 6.1 `src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs` — replace the HTTP 501 `PreviewVoice` body with real synthesis of the resolved voice template body via the Pro-shipped TTS seam; keep the 400-on-bad-id / 404-on-missing guards and `AdminOnly`; update the class-level `<remarks>` that documents the 501
- [x] 6.2 `tests/Verbara.Platform.Api.Tests/CsatTemplateAdminEndpointsTests.cs` — replace the 501 assertion with a synthesized-preview assertion for a seeded `voice` template; keep the 404 (missing template) and 400 (bad id) cases

## 7. Aggregate analytics endpoint (Phase C)

- [x] 7.1 `src/Verbara.Platform.Surveys/ISurveyAnalytics.cs` — add a scope-wide overload (e.g. `GetScopeAggregateAsync(tenantId, channel, range, ct)`) returning the per-queue rows + a scope roll-up
- [x] 7.2 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyAnalytics.cs` — implement the overload over the existing `(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL` partial index (`GROUP BY queue_name` rows + top-level aggregate) via `Verbara.Sdk.Data.Npgsql` (name-based getters, explicit `NpgsqlDbType`; no Dapper) — no schema change
- [x] 7.3 `src/Verbara.Platform.Surveys/InMemorySurveyAnalytics.cs` — implement the overload for Testing-env parity
- [x] 7.4 `src/Verbara.Platform.Api/Dtos/CsatAggregateDto.cs` (NEW) — typed sealed record `(int TotalResponses, double AverageRating, DateTimeOffset RangeStart, DateTimeOffset RangeEnd, IReadOnlyList<CsatResponseDto> Queues)`; `queues[]` rows reuse `CsatResponseDto` verbatim (`csat-aggregate-analytics.v1.json`)
- [x] 7.5 `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — register `CsatAggregateDto` for AOT source-gen
- [x] 7.6 `src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs` — add `GET /api/v1/analytics/csat` (`SupervisorPlus` + license-gated) returning `CsatAggregateDto`; `channel` echoes the requested filter, `all` when unfiltered
- [x] 7.7 `src/Verbara.Platform.Api/Program.cs` — register the aggregate route in the existing analytics group
- [x] 7.8 `tests/Verbara.Platform.Api.Tests/CsatResponseEndpointsTests.cs` + `tests/Verbara.Platform.Storage.Postgres.Tests/PostgresSurveyAnalyticsTests.cs` — scope roll-up matches `csat-aggregate-analytics.v1.json` (envelope sums, one `CsatResponseDto` row per queue, `channel` `all`); `?channel=voice` echoes `voice` into envelope + rows; non-`SupervisorPlus` denied; per-queue row shape equals an aggregate `queues[]` row

## 8. AOT + validation gate

- [x] 8.1 `dotnet build` 0-warning (TreatWarningsAsErrors) + AOT publish of `Verbara.Platform.Api` shows 0 trim/AOT warnings on the advanced Pro pin
- [x] 8.2 `openspec validate --change csat-completion --strict` green; full `dotnet test` green (voice trigger, agent-hangup event, survey-IVR transfer, voice capture, typed relay, aggregate read, live preview-voice)
