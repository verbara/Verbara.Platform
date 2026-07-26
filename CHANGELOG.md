# Changelog

All notable changes to **Verbara.Platform** are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Fixed
- **`/health/ready` now returns 200 (not 503) on an unlicensed community / self-host boot**
  (decision_ref Verbara.Sdk.Pro/ADR-0017; #194) — consumes the Verbara.Sdk.Pro producer fix by
  bumping every `Verbara.Sdk.Pro.*` pin `2.12.0-pro` → `2.13.0-pro` in `Directory.Packages.props`.
  The Pro `dialer-engine` readiness check now settles `Degraded` (with the `dialer license blocked:`
  reason preserved) instead of `Unhealthy` when the dialer license is blocked, so the `/health/ready`
  aggregate *degrades* rather than *fails* and the ASP.NET Core health middleware maps it to HTTP 200
  — the pod joins the load balancer instead of being held permanently un-ready. **No Platform
  health-check source change**: the severity decision lives entirely in Pro (open-core boundary);
  the aggregate flips 503 → 200 purely on the pin bump. This is the readiness-layer generalization of
  the `AsteriskAmiHealthCheck` posture ("a subsystem you did not enable, or that is blocked for a
  non-operational reason, must not fail readiness"). decision_ref: Verbara.Sdk.Pro/ADR-0017.

### Added
- **Community-boot readiness contract test** (`CommunityBootReadinessTests`, decision_ref
  Verbara.Sdk.Pro/ADR-0017; #194) — an integration test that boots Platform unlicensed, issues
  `GET /health/ready`, and asserts over the JSON emitted by `HealthReportJsonWriter`: HTTP **200**
  (not 503), top-level `status` `Degraded`, `checks.dialer-engine.status` == `Degraded`, and a
  `description` that STARTS WITH the stable prefix `dialer license blocked:` (the prefix only — the
  reason suffix `NotLicensed`/`Revoked`/`Expired`/`GraceExhausted` is deliberately not asserted). A
  negative-pole case pins that a regression reverting `dialer-engine` to `Unhealthy` flips the
  aggregate back to 503 and reds the test. The cross-repo readiness contract Platform consumes,
  frozen by the golden fixture
  `Verbara.Sdk.Pro/openspec/changes/license-gated-engine-health-degraded/fixtures/health-ready-community-boot.json`.

### Changed
- **Released-image smoke asserts the `dialer-engine` Degraded shape** (decision_ref
  Verbara.Sdk.Pro/ADR-0017; #194) — `docker/verbara-smoke-released.sh` no longer treats a bare 200
  from `/health/ready` as sufficient: after readiness it fetches the body once (reusing the existing
  `python3` stdlib parse) and fails unless `checks.dialer-engine.status` == `Degraded` and its
  `description` starts with `dialer license blocked:` (prefix only). The smoke leg in
  `.github/workflows/release.yml` STAYS report-only (`continue-on-error: true`) for now; promotion to
  gating is a documented post-merge follow-up, conditioned on the sharpened community leg running
  green twice consecutively against images that carry the fix (design D5). decision_ref:
  Verbara.Sdk.Pro/ADR-0017.

## [2.21.1] - 2026-07-25

### Fixed
- **OpenAPI `ComplianceRuleSummaryDto.severity` now declares the closed enum `Info | Warning | Critical`**
  (decision_ref Platform/ADR-0036; #191) — a new `ComplianceSeverityEnumTransformer` (`IOpenApiSchemaTransformer`,
  registered alongside `NumericSchemaTruthTransformer` on the one `AddOpenApi()` call) narrows the emitted
  `severity` property from an open `string` to the literal enum whose three values mirror
  `ComplianceSeverityBreakdownDto` (`Info`/`Warning`/`Critical`). **Document-only, no runtime change** — the
  DTO member stays `string` in source and in `ApiJsonContext`, the server still writes plain strings, and
  `severity` is response-only (no request path binds it), so there is no deserialization change. AOT-safe
  (target identified by a compile-time `typeof` match against `context.JsonTypeInfo`, no reflection over user
  types). Same "make the emitted document tell the truth" posture as ADR-0036. Two sibling shapes are also
  **verified** (no producer change): `TopicTrendsResponse` already emits `trends`/`totalAnalyzed` (the stale
  `topics` lived only in Web's hand-written shadow — a regression guard locks the correct shape), and the
  `PagedResult<T>` envelope matches field-for-field with `openapi-typescript`'s `PagedResultOf<T>`
  monomorphization ruled by-design. A new `scripts/verify-residual-shapes.py` (CI, same runtime-capture lane
  as the response-schema manifest check) pins all three against golden fixtures. Unblocks Platform.Web's
  child change (retire the `TopicTrendsResponse`/`ComplianceRuleSummaryDto` shadows, repoint the
  speech-analytics consumers). decision_ref: Platform/ADR-0036.
- **OpenAPI numeric schemas now declare a single JSON type** (ADR-0036, amends ADR-0035; #186) — a new
  `NumericSchemaTruthTransformer` (`IOpenApiSchemaTransformer`, registered on `AddOpenApi()`) strips
  the spurious `["integer","string"]`/`["number","string"]` union that .NET 10's `JsonSchemaExporter`
  reflected onto every numeric body/response field from the framework-default
  `JsonNumberHandling.AllowReadingFromString` (dotnet/aspnetcore #64145). **Document-only** — it rewrites
  the built `OpenApiSchema` model and does NOT change runtime deserialization (`AllowReadingFromString`
  is retained; callers may still POST stringified numbers); explicitly NOT `NumberHandling.Strict`.
  Blanket over int32/int64/double/float with an empty exemption list (no field exceeds 2^53); the ADR
  rider requires any future >2^53 field to be an explicit `string` DTO property. AOT-safe (no reflection
  over user types). `scripts/verify-openapi-fixture.py` is tightened to fail on any surviving numeric+string
  union (nullable numerics `["null","integer"]` remain legitimate). Unblocks Platform.Web's typed-client
  regeneration (retires ~30 `Number()` coercion sites). decision_ref: Platform/ADR-0036.

## [2.21.0] - 2026-07-24

### Security
- **Management API keys now minted from a CSPRNG, not `Guid.NewGuid()`** (ADR-0012 Ola-3,
  gate #7). The 3 mgmt-key mint sites (`ManagementApiKeyEndpoints` create/rotate,
  `SetupEndpoints`) now use `SecretTokenGenerator.Mint` (256-bit `RandomNumberGenerator`,
  lowercase hex) instead of a Guid v4 (~122 bits, non-CSPRNG). Keys keep the `mgmt_` prefix. (#174)
- **`System.Security.Cryptography.Xml` pinned to 10.0.10** — remediates 4 HIGH advisories present
  in the transitively-resolved 10.0.7. (#179)

### Added
- **Durable Postgres audit sink for dialer license enforcement** (Pro/ADR-0016) — a new
  `PostgresDialerLicenseAuditSink` (+ migration 017 + DI) implements the already-published Pro seam
  `IDialerLicenseAuditSink`, turning the previously silently-dropped tick-scoped
  `DialerLicenseAuditRecord` (quiesce / flap / recover, with campaigns + tenant + in-flight) into a
  durable compliance row. Per-originate per-call denial attribution is deferred. (#167)
- **Invariant gate #7** — a deterministic gate (`scripts/check-endpoint-invariants.py`, run in
  the `Invariant Gates` CI job) forbidding `Guid.NewGuid` string-interpolated into a
  credential-named value anywhere in the Api composition; `.ToString()`-shaped id uses stay
  legitimate. Content-scoped (not filename-scoped, so `SetupEndpoints` is covered). Floor is
  zero. decision_ref: verbara-meta/ADR-0012. (#174)
- **ADR-0012 Ola-2/Ola-3 + ADR-0013 CI invariant-gate wave** — coverage gate v2 (patch-coverage +
  two-sided band + exclusion baseline, #169); empty-catch grep + `Program.cs` LOC budget (#172);
  AOT-at-PR publish gate + Dependabot step-level CI-load skip (#171); N+1 enrichment arch gate (#176);
  Service-Locator arch-test host `Verbara.Platform.Architecture.Tests` (#175). All freeze-current /
  ratchet, wired into existing required jobs. decision_ref: verbara-meta/ADR-0012, ADR-0013.

### Changed
- **Analytics agent/session enrichment batch-loaded to eliminate an N+1** (ADR-0012 Ola-3) — the
  enrichment path now uses `= ANY(@Ids)` batch primitives instead of per-row lookups, guarded by the
  new `EnrichmentLoopScanner` analyzer. (#176)
- **Realtime queue/agent/membership stores wrapped in sync-decorators** (ADR-0012 Ola-3) — replaces 9
  Service-Locator resolution sites with 3 `RealtimeSyncing{Queue,Agent,QueueMembership}Store`
  decorators (the presence site stays behind a `size==1` carve-out). (#175)

### Housekeeping
- Architecture charter docs (`architecture.md` + `gates.yaml`, ADR-0014 §1/§2, #178); coverage-floor
  band re-sync to the ADR-0013 superset script (#170) + patch-coverage liveness refinement (#173);
  stale required-gate "promotion pending" comment cleanup (#177).

## [2.20.0] - 2026-07-20

### Changed — Dependencies
- Pin all `Verbara.Sdk.Pro.*` 2.11.0-pro → **2.11.1-pro** and all direct `Verbara.Sdk.*` 2.3.1 → **2.3.2** (in-train pin formalization + transitive alignment: Pro 2.11.1-pro requires Sdk 2.3.2 transitively). No production behaviour change vs. the 2.11.0-pro consumed by #161.

### Added
- **Dialer license enforcement at the point of spend (wires `Verbara.Sdk.Pro` 2.11.0-pro; Pro/ADR-0016).**
  Passes `ILicenseGuard` into the shared `OriginateExecutorBase` factory, so both outbound originate
  paths — agent click-to-dial (`AgentOutboundDialService`) and the W5b caller-rescue
  `CallbackOriginator` — now enforce the Dialer license before every billable PSTN send. These two
  paths had **no** license check before; on revocation they stop originating within ~sub-second
  (via the Pro version-stamped guard cache). Advances all `Verbara.Sdk.Pro.*` pins 2.10.0-pro →
  2.11.0-pro (pins formalize in-train once 2.11.0-pro publishes to GitHub Packages). (#161)
  - **SOURCE-BREAKING (test doubles only):** custom `OriginateExecutorBase` subclasses now override
    `ExecuteCoreAsync` (the base `ExecuteAsync` is the non-virtual spend-point template method); the
    two `FakeOriginateExecutor` test doubles were updated accordingly.
- **Named OpenAPI response schemas for the typed-client consumer groups (`openapi-response-schemas`,
  ADR-0035).** Converts ~183 success handlers across the `admin-remainder`, `agent`, `analytics`, and
  `operations` endpoint groups from untyped `Task<IResult>` / `Results.Ok(...)` to the typed
  `Results<Ok<TDto>, …>` + `TypedResults.*` pattern, so each success DTO now surfaces as a named
  `components/schemas` entry in `/openapi/v1.json` (**183 → 391 schemas**). Wire bodies are
  byte-identical — schema **metadata** only, no request/response contract, status-code, or gating
  change — verified by the full endpoint suite (1645 tests). Unblocks the Platform.Web typed-client
  migration (the `web/openapi-response-adoption` child in this change's `impact.yaml`). (#162)
  - Registers 10 response DTOs in `ApiJsonContext` that were returned untyped and never source-gen
    registered (`InvoiceDto`, `UsageRecordDto`, `QuotaDto`, `QuotaStatusDto`, `CircuitStatusResponse`,
    `ActiveSessionDto`, `ListenEntry`, `PauseResultDto`, `SurveyScoreSummary`, `UserPurgePreview`) —
    a latent `JsonSerializerIsReflectionEnabledByDefault=false` serialization gap fixed in passing.
  - Completes the cross-repo `response-schema-manifest.v1.json` (137 schemas, verbatim field names)
    and generalizes `scripts/verify-openapi-fixture.py` + its CI step from the single-`CsatResponseDto`
    fixture check to a manifest-driven, per-group assertion.
  - Handlers left untyped by design (OQ1): success shapes wrapping a domain entity
    (`SearchArticles`→`Article`, `GetBotAnalytics`→`BotAnalyticsSummary`), or `Created<T>` / 204 /
    redirect / polymorphic returns.

## [2.19.0] - 2026-07-14

### Added
- **CSAT voice channel end-to-end + aggregate KPI (`csat-completion`, ADR-0020).** Completes the CSAT
  train by wiring the voice channel through the existing digital slice and closing the two deferred
  ADR-0020 follow-ups. All additive/back-compat — the digital slice (`csat-runner`) is unaffected.
  Consumes **Verbara.Sdk.Pro 2.10.0-pro** (the new `Verbara.Sdk.Pro.CsatRunner` voice adapter + typed
  `IPlatformHubClient.OnCsatResponseRecorded`; pin advanced in-train once v2.10.0-pro published to
  GitHub Packages). Pairs with the Web aggregate KPI card. Host side of the
  cross-repo `csat-completion` change.
  - **Voice trigger** — `CsatConversationEndSource` now maps `ChannelType.Voice` → `voice` and solicits
    on the answered `WrapUp` transition (never the never-answered `Abandoned`); the digital `Closed`
    path is unchanged (design D1).
  - **Voice agent-hangup domain event** — `VoiceConversationBridge.OnCallEndedAsync` publishes a typed
    sealed `VoiceAgentHangupEvent (TenantId, ConversationId, QueueName, Abnormal, HangupAt)` on the
    in-process `PlatformEventBus` at the point the existing `IsAbnormalAgentHangup` verdict is computed
    (leader-gated, exactly-once cluster-wide). Registered in `ApiJsonContext` for SSE-safety.
  - **Survey-IVR handoff** — new `VoiceTransferKind.SurveyIvr` reuses `VoiceCallControlService.BlindTransferAsync`
    to set `SURVEY_ID` / `SURVEY_TOKEN` and AMI-`Redirect` the caller leg into the shared static
    `[survey-ivr]` dialplan context (`docker/asterisk-config/extensions.conf`); per-tenant survey config
    comes from the queue `CsatConfig` + Realtime DB, not per-tenant file rendering (design D5).
  - **Voice capture endpoint** — `POST /api/v1/csat/responses/voice` binds the frozen `CsatResponseRequest`
    shape (`channel` `voice`, `comment` null), anonymous + Platform-minted voice-leg-token-verified
    (`ICsatVoiceTokenVerifier`, HMAC `v1.{payload}.{sig}` mirroring the webchat verifier) + license-gated,
    sharing the persist → publish → audit path and setting `SurveyResponse.CallId`.
  - **Scope-wide aggregate analytics** — new `GET /api/v1/analytics/csat` (`SupervisorPlus` + license-gated)
    returns a typed sealed `CsatAggregateDto` envelope (`totalResponses`, `averageRating`, `rangeStart`,
    `rangeEnd`, `queues[]`) whose rows reuse `CsatResponseDto` verbatim; `channel` echoes the requested
    filter and is `all` when unfiltered. Backed by a new `ISurveyAnalytics.GetScopeAggregateAsync` overload
    over the existing `(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL` partial index
    — no schema change. Resolves ADR-0020's ⟨NEEDS PRODUCT-OWNER INPUT⟩ wallboard question in favor of
    aggregation (product-owner call 2026-07-13).
  - **Voice template preview live** — `POST /api/v1/admin/csat/templates/{id}/preview-voice` synthesizes the
    resolved voice template body through the Pro TTS seam (`TtsPromptCache` → SDK `SpeechSynthesizer`) and
    returns the audio (`audio/L16`), replacing the prior HTTP 501; the 400/404 guards are unchanged.
  - **Composition root** — registered the Pro voice seams so `AddProCsatRunner` wires the voice adapter:
    `ICsatVoiceCaptureSink` (`CsatVoiceCaptureSinkAdapter` → the Surveys capture path), `IDtmfSource`
    (`AmiDtmfSource` → AMI `DTMFEnd` stream), `IAmiConnection` (`DeferredPrimaryAmiConnection` — resolves the
    primary server's connection lazily on first dispatch and throws only then if none is configured, so a
    headless / no-telephony host still boots), and a default offline `SpeechSynthesizer`
    (`SilenceSpeechSynthesizer`, superseded by a configured TTS provider).
- **Typed supervisor CSAT push (`csat-completion`, ADR-0020 follow-up).** `PushToHubRelay` now routes the
  recorded CSAT event through the strongly-typed `IPlatformHubClient.OnCsatResponseRecorded(CsatResponseRecordedPayload)`
  (the untyped `IHubContext<PlatformHub>` name-based relay is retired). The wire method name and payload
  shape are unchanged (`channel` set now includes `voice`), so no SignalR client observes a change — pure
  type-safety hardening, compilable only against the advanced Pro pin.
- **CI OpenAPI export artifact (`openapi-typed-client`, ADR-0035)** (#149). The `build-and-test` job now
  captures `/openapi/v1.json` from a briefly-running host (ephemeral CI-only Postgres `services:`
  container, `Platform:OpenApi:Enabled=true`) and publishes it as the `openapi-document-<sha>`
  artifact for Web's typed-client codegen (build-time export via
  `Microsoft.Extensions.ApiDescription.Server` evaluated and found infeasible — hosted-service
  startup requires live Postgres; full trace in ADR-0035). `scripts/verify-openapi-fixture.py`
  guards the golden `CsatResponseDto` fixture against contract drift. Runtime `/openapi/v1.json` /
  `/scalar/v1` behavior and gating unchanged.

### Fixed
- **`/openapi/v1.json` returned HTTP 500** (#149) — two bare query-parameter types (`ConversationState?`,
  `Guid?`) were missing root `[JsonSerializable]` entries in `ApiJsonContext` (pre-existing;
  surfaced by the first real invocation of the endpoint). Additive schema metadata only — no
  endpoint request/response contract, gating, or status-code change.

### Changed
- **Docs hygiene — machine-path purge in living spec docs (`docs-hygiene-sweep`).** Removed all
  absolute `/media/Data/Source/Verbara/...` machine-path prefixes from 8 tracked `docs/specs/`
  design documents (5 doctor-named + 3 phase-d): `cd`/`dotnet pack` instructions rewritten
  workspace-relative (`../Verbara.Sdk.Pro`, `../local-nuget-feed/`), cross-repo file citations
  repo-qualified relative, per the ADR-0005 public-repo content rule + `openspec/config.yaml`
  absolute-path ban. Token-level path substitutions only — dated period-correct content and
  Spanish prose otherwise untouched; no code changed (#152).
- Advanced `Verbara.Sdk.*` package pins 2.3.0 → 2.3.1 (LMNT mid-send abort fix + docs purge;
  2026-07-14 release train).

---

## [2.18.0] — 2026-07-11 — CSAT consumer (customer-satisfaction capture; digital-first) + Pro CSAT engine hosting

Lands the **Platform (consumer) half of the CSAT Runner train** — Pro ships the engine + orchestrator, Web ships the supervisor/embed UI, and Platform persists responses, exposes the public capture surface, gap-fills the two channels the in-process engine cannot reach (inbound Email replies, inbound SMS digit-replies), and **hosts Pro's CSAT orchestrator in-process** through dependency-inverted seams. A Phase-1 brownfield discovery (**ADR-0020**) found Platform already ships a full Surveys domain (`Survey`, `SurveyResponse`, `ISurveyAnalytics`, `PostgresSurveyResponseStore`, admin CRUD + analytics, a `SurveyType.Csat` value), so CSAT is an **additive, back-compatible extension of that domain** — not a parallel `csat_responses` table. Consumes **Verbara.Sdk.Pro 2.9.0-pro** (adds `Verbara.Sdk.Pro.CsatRunner`). Pairs with **Web `v3.13.0-web`**. **Digital-first: webchat / email / sms only — voice/TTS CSAT is deferred to a Pro Path-A follow-up** (no `ITtsSynthesizer` / `CsatVoiceOptions` ships here).

### Added
- **CSAT survey-domain extension (ADR-0020, all additive/nullable).** `SurveyResponse` gains 6 nullable init-only columns (`Channel`, `QueueName`, `Rating`, `Comment`, `CapturedAt`, `CallId`); a well-known `SurveyQuestionIds.CsatRating = "csat-rating-v1"`; `ISurveyAnalytics` gains a `GetByQueueAndChannelAsync` overload (DB-side `COUNT`/`AVG` over the new partial indexes in `PostgresSurveyAnalytics`). Pre-existing NPS/Custom survey rows load unchanged (columns null; no backfill).
- **CSAT response capture + analytics endpoints** — `POST /api/v1/csat/responses/{webchat,email,sms}` + `GET /api/v1/analytics/csat/queues/{queueId}`. Webchat is anonymous + session-token-verified; email/sms are internal (`X-Service-Key`, consumed by the IMAP poller + SMS correlator); analytics is `SupervisorPlus`. Each capture persists a `SurveyResponse`, publishes `CsatResponseRecordedEvent` (forwarded to `IPushEventBus`), and writes a `csat`-category audit row. **License-gated: HTTP 402 + RFC 9457 ProblemDetails when `LicenseFeature.CsatRunner` is absent** (reuses the declarative `.RequireLicenseFeature(...)` + `LicenseGateMiddleware`; no new gate). 5 new DTOs/events registered for AOT source-gen in `ApiJsonContext` (`CsatResponseRequest`, `CsatResponseDto`, `QueueCsatConfigDto`, `CsatTemplateDto`, `CsatResponseRecordedEvent`).
- **Email IMAP gap-fill** — `ImapInboundPoller` (`IHostedService`, ~30s `PeriodicTimer`, per-mailbox last-UID idempotent dedup) + `CsatReplyMailHandler` in `Verbara.Platform.Mail` (previously outbound-only; new `AddPlatformMail(...)`). Reuses Pro's `HmacCsatReplyTokenSigner` (7-day TTL; HMAC not re-hand-rolled), parses `[1-5]` (subject then first 200 chars of body), falls back on `In-Reply-To` → `csat_pending_dispatches`, forwards to the internal email endpoint, optional per-tenant auto-reply.
- **SMS correlator** — `CsatSmsCorrelator` in `Verbara.Platform.Channels.Sms` plugs in after `SmsWebhookHandler`; matches a bare `[1-5]` inbound reply against an open `csat_pending_dispatches` row (`(tenant, channel='sms', correlator=phone)`, unconsumed, within a 24h `sent_at` window; most-recent-wins on collision, older opens marked expired) and forwards the rating; **falls through to normal routing on any non-rating body** (user messages are never eaten).
- **Per-tenant CSAT template store + `ICsatTemplateProvider`** — `ICsatTemplateStore`/`CsatTemplateEntry`/`CsatDefaultTemplates` (Postgres + InMemory stores; per-locale en-US/es-419/pt-BR × email/sms/voice defaults seeded on tenant create) + `CsatTemplateProvider` implementing Pro's `Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatTemplateProvider` with a tenant-locale → tenant-default → global-default → global-en-US fallback chain. Admin CRUD at `/api/v1/admin/csat/templates/*` (`AdminOnly` + `csat` audit); `POST …/{id}/preview-voice` is shape-present but returns **HTTP 501** (voice/TTS deferred — no fabricated audio).
- **Hosting Pro's CSAT orchestrator via 5 dependency-inverted seams (Phase E2).** Pro (upstream) cannot reference Platform (downstream), so its orchestrator (`IHostedService`) is registered via `AddProCsatRunner(...)` and driven through 5 Platform-implemented seams: `ICsatTemplateProvider` (prompt copy), `ICsatConversationSignal` → `IConversationService` (webchat `csat_requested` system message), `ICsatEmailDispatcher` → `IEmailService` (Reply-To via a new additive `EmailMessage.ReplyToAddress` stamped by `SmtpSender`), `ICsatSmsDispatcher` → `ISmsProvider` (+ writes the `csat_pending_dispatches` row the correlator consumes), and `ICsatConversationEndSource` — a hot `IObservable<CsatConversationEndedSignal>` driven from the terminal `ConversationStateChangedEvent` (`Closed`) transition + per-queue CSAT config snapshot (Enabled / PreferredChannel / SamplingRatePercent). The orchestrator self-gates at runtime on `LicenseFeature.CsatRunner`.
- **Per-queue CSAT config** — a nested `CsatConfig?` (`Enabled`, `PreferredChannel`, `PromptTemplateId`, `SamplingRatePercent`) on `Queue`, persisted via 4 additive `queue_configs` columns (`csat_enabled` default false, `csat_channel`, `csat_prompt_id`, `csat_sampling_rate` 0..100 CHECK). See the new **`docs/operations/csat-runbook.md`** for enabling CSAT per queue, configuring templates, and troubleshooting IMAP / SMS correlation.

### Changed
- **`PushToHubRelay` fans a `CsatResponseRecordedEvent` to `supervisor:{tenantId}`.** Because the Pro nupkg's typed `IPlatformHubClient` (2.9.0-pro) has **no `OnCsatResponseRecorded` method** — adding one is a Pro-side edit + repack, out of scope here — the relay fans out over the **untyped `IHubContext<PlatformHub>` using the `"OnCsatResponseRecorded"` client-method name** (identical wire contract). Promoting this to the typed interface is a Pro-side follow-up (tracked alongside the 5 seams).
- **`EmailMessage` gains an additive nullable `ReplyToAddress`** (stamped onto `mime.ReplyTo` by `SmtpSender.BuildMimeMessage`) so the CSAT email dispatcher can route inbound replies to the `csat@…` mailbox. Back-compat: nullable; both `ApiJsonContext` + `MailJsonContext` source-gen it automatically.
- **Migration `016_SurveyCsatExtensions.sql`** — extends `survey_responses` (6 nullable columns + 2 CHECK constraints + 2 partial indexes `WHERE channel IS NOT NULL`), creates `csat_pending_dispatches` + `csat_templates`, extends `queue_configs` with the 4 CSAT columns, and repairs a pre-existing `queue_configs.wrap_up` store/schema drift. All additive + idempotent; nullable `ADD COLUMN` is metadata-only. **For large tenants, apply the two new `survey_responses` partial indexes with `CREATE INDEX CONCURRENTLY` out-of-band** (the migration wraps each file in a transaction, where `CONCURRENTLY` is illegal) — see the runbook.

### Deprecated
- **`ISurveyAnalytics.GetByQueueAsync` is `[Obsolete]`** (superseded by the channel-aware `GetByQueueAndChannelAsync`), scheduled for removal in **v2.19.0** — the same 2-release deprecation window Pro/ADR-0012 uses.

### Cross-repo coordination
- **Coordinated CSAT train** (Platform/**ADR-0020**): **Verbara.Sdk.Pro `2.9.0-pro`** (CSAT engine + orchestrator + `LicenseFeature.CsatRunner` + the 5 seam contracts) → **Platform `2.18.0`** (this release, the consumer/host) → **Web `3.13.0-web`** (supervisor CSAT card + embed rating panel). Pro must reach the package feed before the Platform tag. The Pro pin was advanced to `2.9.0-pro` across `Directory.Packages.props` (task 5b.0, pulled forward) and `Verbara.Sdk.Pro.CsatRunner` added to `Verbara.Platform.Api`.
- **Deferred to a Pro Path-A follow-up:** voice/TTS CSAT (no `ITtsSynthesizer`; `preview-voice` returns 501) and the typed `IPlatformHubClient.OnCsatResponseRecorded` Hub relay (currently the untyped `IHubContext` name-based fan-out). This release is **digital-first**.

---

## [2.17.0] - 2026-07-06 — Typification E5 autonomous disposition + audit-trail integrity + CI/release hardening

Lands the reframed Typification **E5** autonomous-disposition enrichment (ADR-0034, **dark/OFF by default**) together with its audit-trail integrity follow-ups, the AI-credit lazy-mint rollover fix, a new report-only live-DB CI lane, a post-release image smoke harness, and a first `SECURITY.md`. Also cascades **Verbara.Sdk 2.3.0** and **Verbara.Sdk.Pro 2.8.1-pro** through central package management.

### Added
- **Typification autonomous disposition (E5, reframed — ADR-0034).** When the existing wrap-up-timeout worker auto-closes an abandoned conversation, opted-in + licensed tenants may have the pending high-confidence (≥ `AutonomousThreshold`) AI suggestion **stamped** as the disposition (`Source=AutoAi`) via a state-based CAS close (a concurrent human typify always wins). **Dark by default** (`AutonomousDispositionEnabled` OFF; per-tenant activation gate + global circuit breaker + per-cycle cap). Per-tenant activation gate endpoints (`/admin/typification/autonomous-disposition`, `AdminOnly`) = documented controller instruction, **not** GDPR Art. 22 consent (Art. 22 does not apply to internal call-coding). Append-only supervisor **correction** (`POST /conversations/{id}/typification-correction`, new `typification:correct-autonomous` permission) — a separate correction record; the original AI submission stays immutable; no conversation reopen. AI-actor audit (`actor_type=ai`) with a time-bounded `retain_until` floor + Art. 17 redaction. PR #110 (`407fd101`), archived #111.
- **Lazy-mint the current-period AI-credit grant on read** (ADR-0033 addendum) — closes the month-rollover window where a tenant consuming after the UTC period boundary but before `CreditGrantMintWorker`'s next tick saw a stale prior-period balance. An indexed `HasCurrentPeriodGrantAsync` existence check (reuses the `uq_ai_credit_ledger_period` constraint) mints via the existing idempotent `PostGrantAsync` only on a miss; steady-state reads stay write-free. Wired into quota enforcement and the balance/remaining endpoints. PR #125, archived `2026-07-05-credit-grant-lazy-mint-rollover`.
- **`SECURITY.md`** — mirrors Verbara.Sdk's GitHub private vulnerability reporting policy, scoped to the API host + container images. PR #133.

### Fixed
- **Audit-trail integrity (4 fixes, ADR-0034 follow-ups).** (1) The typification-correction endpoint now writes the correction record, the submission update, and the audit entry in **one** Npgsql transaction (grounding found three separate writes, not the assumed two). (2) The GDPR purge preview reports a real `AuditTrailCount` via `CountByActorAsync` instead of a placeholder. (3) Canonical actor resolution for audit entries is extracted to a shared `CallerIdentity` helper (reuses the v2.14.1 claim-order precedence), consolidating 4 call-sites. (4) The audit integrity hash is now `v2:`-prefixed and covers `RetainUntil`; pre-existing rows still verify under the v1 scheme and redaction re-derives under the original scheme. PR #124, archived `2026-07-05-audit-trail-integrity-fixes`.

### CI
- **Report-only live-DB Postgres/Redis CI lane** — a new `live-db-tests` job (`pull_request` + `merge_group`, Testcontainers on the runner) runs the previously-excluded `Storage.Postgres.Tests`/`Identity.Redis.Tests` projects, closing the audit gap where InMemory-only CI hid the dunning-inertness and Art. 17-redaction bugs. Starts `continue-on-error: true` (report-only) pending a fix for a root-caused Postgres image race under parallel container startup; promotion-to-required trigger is documented. PR #126, archived `2026-07-05-live-postgres-ci-lane`.
- **Post-release functional smoke for released GHCR images** — `docker/verbara-smoke-released.sh` resolves the tagged release's digests, composes a digest-pinned stack, waits on binary `/health/ready` readiness, and exercises one end-to-end journey (setup → login) against the actual released images, always tearing down after. Runs as a report-only job in `release.yml` after tagging. Also fixed a real Postgres db-name drift in the demo compose found while wiring the smoke script. PR #127, archived `2026-07-05-released-image-smoke`.

### Dependencies
- **Verbara.Sdk 2.2.1 → 2.3.0** and **Verbara.Sdk.Pro 2.8.0-pro → 2.8.1-pro** (all consumed packages cascaded across `Directory.Packages.props`). Sdk 2.3.0's default OpenTelemetry `service.name` changes from `"asterisk-sdk"` to `"verbara-sdk"` (`VerbaraOpenTelemetryBuilder.ServiceName`); Platform does not override this default, so exporter resource-matching rules/dashboards keyed on the old service name should be updated. No other consumer-facing change.
- Sdk 2.3.0 raises transitive floors to **`Microsoft.Extensions.*` ≥ 10.0.9** and **Npgsql ≥ 10.0.3**; Platform's central pins for both are bumped alongside (from 10.0.8 / 10.0.2) to clear the resulting `NU1605`/`NU1109` downgrade errors. Consumers of the Docker compose stacks are unaffected (images bundle their own runtime); NuGet consumers of Platform's own packages inherit the raised floors.

### Database
- Migration **014** (`typification_submissions` autonomous/correction columns; `tenant_autonomous_disposition`; `audit_entries.retain_until` + `entity_id` made nullable for Art. 17 redaction) + migration **015** (`typification_submission_corrections`).

### Security
- Transitive-pin **Microsoft.OpenApi 2.9.0** to clear `GHSA-v5pm-xwqc-g5wc` (NU1903) — the .NET 10 OpenAPI stack pulled the vulnerable 2.0.0 transitively (pre-existing on `main`).
- Added a first `SECURITY.md` documenting the private vulnerability-reporting policy for this repo.

---

## [2.16.0] — 2026-06-28 — AI-credit ledger (prepaid + postpaid credit accounting) + P2c.2 follow-ups

Replaces the live-`SUM`-over-usage AI-credit accounting (P2c.2, v2.15.0) with a **signed, append-only credit ledger** + an O(1) balance projection, and lands the four P2c.2 follow-ups that preceded it. The ledger is the durable substrate for prepaid balances, top-ups, promotional and partner-funded credits, postpaid overage, and per-source reporting. **The runtime cutover is behind two default-off kill-switches** (`PlatformLlmOptions.LedgerEnforcementEnabled`, `LedgerInvoiceReadEnabled`), so this release is **inert at runtime until an operator flips them** — the prior `SUM`-based quota/invoice path is byte-for-byte preserved until then. AI stays strictly opt-in; BYO is never metered. No Pro SDK release — consumes **Verbara.Sdk.Pro 2.8.0-pro** (unchanged). Authoritative design: **ADR-0033** (+ its Warn-overflow, (c)-split, and (c2)-resolution addenda) and **ADR-0032** (entitlement re-check). Migrations **011 / 012 / 013** (all additive + idempotent).

### Added
- **AI-credit ledger substrate** (a) — one signed append-only `ai_credit_ledger` (grants +, debits −; the monthly allowance is just a recurring grant) + a maintained O(1) `tenant_credit_balance` projection (the request-path balance read is a primary-key lookup, never a `SUM`). Atomic debit = a guarded `UPDATE … WHERE balance >= @amount` + a negated ledger row in one transaction. Shared `BillingPeriod.Current(IClock)` boundary helper. Migration **012**. (PR #93)
- **Per-grant lots + FIFO multi-source allocation** (c2) — a mutable `credit_lot` per grant (`remaining`, `expires_at`, a monotonic per-tenant `lot_seq`) + an internal `credit_allocation` debit→lot linkage. A metered consumption walks open, non-expired lots in the provably-total draw order **Promo → Partner → Subscription/TopUp → PostPaid** under `SELECT … FOR UPDATE` (the `tenant_credit_balance` row locked first — the deadlock contract), emitting one source-tagged covered row per lot and exactly one `PostPaid` tail. Invariant `Σ(open non-expired lot.remaining) == balance`. Migration **013**. (PR #99)
- **Top-ups** (c1) — operator-minted fungible `TopUp` grants (`POST /management/credit-ledger/top-up`, double-locked via `PlatformAdminRequirement("billing:credits:grant")`) + a tenant-facing balance/entries read API (`GET /admin/credit-ledger/{balance,entries}`). New RBAC `billing:credits:read` / `billing:credits:grant`. (PR #97)
- **Promotional & partner-funded credits** (c2) — operator-minted `Promo` (expiring) and `Partner` (attributable) grants. `Promo`/`Subscription` lots expire (an hourly reclaim sweeper posts an offsetting, idempotent debit so unconsumed credits leave the balance — enforcing **subscription no-carryover** and promo expiry). `Partner` draws are never customer-billed and are **attributed on read** (`Σ |Partner debits|` over the partner's customers, resolved via the existing `Tenant.ParentTenantId` + `parent.Type == Partner` hierarchy — no schema change), via a `PartnerAdminOnly` endpoint that reads the **verified** tenant claim (not the overridable `X-Tenant-Id`). Per-source remaining readout (`GET /admin/credit-ledger/remaining-by-source`). (PR #99)
- **Input/output-differentiated AI-credit pricing** (opt-in) — when both an input and an output token ratio are configured, AI Credits are priced from a decomposed in/out total (computed by one Postgres `jsonb` aggregation, not a hot-path enumeration); the flat single-ratio path stays byte-identical when only one ratio is set. (PR #86)
- **AI-credit overage → invoice → dunning pipeline** — allowance-based overage (credits beyond the monthly allowance) is metered, invoiced, and run through the existing dunning flow with threshold notifications; a stateless straddle-idempotency check (prev < threshold ≤ cur) emits the registered-but-previously-unused `billing.quota_warning` / `billing.quota_exceeded` events. Migration **011** (invoice `due_date` / `payment_status` — the dunning pipeline was previously inert in production because these were never persisted). (PR #88)

### Changed
- **AI-credit quota / metering / invoice cutover to the ledger** (b, Model C — postpaid) — behind the two default-off flags, the AiAnalysis quota check reads the projection balance, the metering funnel posts a **two-step covered + PostPaid debit** in one transaction (the covered portion drawn from prepaid stock and tagged its true source; the uncovered remainder posted unconditionally as a billable `PostPaid` tail that never floors the projection below zero), and the invoice derives customer-owed overage as `Σ |PostPaid debits|`. A new `QuotaOutcome {Allow, Warn, SoftBlock, HardBlock}` on `QuotaCheckResult` drives the endpoint switch; `Warn` overflows past a depleted balance (preserving the v2.15.0 postpaid overage). A recurring `CreditGrantMintWorker` mints the monthly subscription grant; a config-gated, idempotent back-fill seeds already-realised consumption. The legacy `SUM`-based path remains the default until an operator enables the flags. (PR #95)

### Fixed
- **Runtime PlatformLlm entitlement re-check** (ADR-0032) — a platform-managed classify now re-checks the tenant's `PlatformLlm` entitlement at the classify endpoint, so revoking the entitlement takes effect on the next request (immediate cutoff, no `FeatureGateCache` TTL window). (PR #83)

---

## [2.15.0] — 2026-06-24 — Typification P2c.2 (platform-managed metered LLM / AI Credits)

Phase **P2c.2** of the Typification module (ADR-0029), building on P2c.1 (per-tenant BYO LLM). Lets an **entitled** tenant use a **Verbara-operated** LLM instead of bringing its own — **metered in AI Credits** (= tokens ÷ a configurable ratio), **gated** by a new plan entitlement, and **capped** by a monthly credit allowance enforced through the Billing package. **AI stays strictly opt-in; BYO is unaffected and never metered.** No Pro SDK release (the gate is a Platform `PlanFeature`; the Pro `TypificationAi` license already ships). Pairs with **Web v3.11.0-web** (the opt-in toggle + credit-usage UI). Consumes **Verbara.Sdk.Pro 2.8.0-pro** (unchanged).

### Added
- **Platform-managed LLM provider** — `TenantLlmConfig.AiSource` (`Byo` / `PlatformManaged`); a tenant opts in via `PUT /admin/ai/llm-config` (`aiSource`). The provider is built from host-bound `PlatformLlmOptions` (Verbara's operator key/model — **never** per-tenant, never serialized/logged/returned), resolved through the existing provider seam (`DefaultLlmProviderResolver` gains a platform branch; the BYO key-guard is bypassed; fail-closed when the operator switch is off).
- **Entitlement gate** — new `PlanFeature.PlatformLlm` (included in the Enterprise plan). Opting into platform-managed without it returns 403.
- **Metering in AI Credits** — every platform-managed classify records a durable `UsageRecord` (`UsageType.AiAnalysis`, `UsageUnit.Tokens`, quantity = total tokens) with input/output token counts + model in metadata. Credits are derived by **aggregation** (Σtokens ÷ `CreditTokenRatio`) — no per-call rounding. Invoicing reuses the existing rate-card flow.
- **Monthly credit allowance** — `TenantQuota.AiCreditsMonthly` enforced via `IQuotaEnforcementService` (summed from Postgres usage records → cross-replica exact). Pre-classify: Warn (proceed), SoftBlock (degrade to the empty suggestion), HardBlock (402). `null` = unlimited / pay-as-you-go.
- **`GET /admin/ai/credits`** — current-period allowance / consumed / remaining / usage %, gated on `typification:ai:configure`.
- Migration **010** (`tenant_llm_config.ai_source`, `tenant_quotas.ai_credits_monthly`; idempotent).

### Fixed
- **Flaky `AuthWriteQueue` drain test** — the `StartAndDrain` test barrier used a wall-clock `Task.Delay` that raced the consumer under CI load; replaced with a causal barrier (`CompleteWriter()` + `await ExecuteTask`). Test-only; no production behavior change (PR #79).

---

## [2.14.1] — 2026-06-23 — Impersonation caller-id + rate-limiter ordering fixes

### Fixed
- **Management impersonation fail-closed for API-key callers** — the impersonation endpoints resolved the calling user as `NameIdentifier ?? sub`, but for API-key callers `NameIdentifier` is the **key id** (the owning user is in the `user_id` claim). A management key whose key id differs from its owning user id resolved to the key id, found no per-tenant permissions, and failed the impersonate check closed (403). Caller-id resolution now uses the canonical order `user_id ?? NameIdentifier ?? sub` (matching `PlatformAdminAuthorizationHandler` / `TypificationEndpoints`), applied via a shared `ResolveCallerUserId` helper across start / end / revoke (the revoke path also fixes audit-actor attribution).
- **Per-tenant rate-limit partition collapse** — `app.UseRateLimiter()` ran **before** `TenantResolutionMiddleware`, so the `per-tenant` policy's partition resolver read an unset `Items["TenantId"]` and every request collapsed to the shared `__global__` partition. The rate limiter now runs **after** tenant resolution (and still before authentication), so each header/subdomain-identified tenant gets its own bucket. _(Latent until now: the `per-tenant` policy is not yet attached to any route — the live `llm` policy already resolved the tenant directly — so this is a forward-looking correctness fix.)_

---

## [2.14.0] — 2026-06-21 — Typification P2c.1 (per-tenant BYO LLM config) + auth drain fix

### Added
- **Per-tenant BYO LLM configuration (Typification P2c.1)** — each tenant configures its **own** LLM provider + **encrypted** credentials for typification AI, replacing the single shared global key. Multi-provider (`OpenAiCompatible` / `AzureOpenAi` / `Anthropic`), resolved per-tenant at classify time and **fail-closed** ("no provider configured" is a valid, non-error state — AI stays strictly opt-in). New admin surface `/admin/ai/llm-config` (GET masked to `keySet` + `keyLast4` — the key is **never** returned; PUT preserves the stored key when omitted; DELETE; `POST /test` probes a saved-or-draft config), gated on the `typification:ai:configure` permission (no license gate). API key encrypted at rest via DataProtection (purpose `Verbara.Platform.Typification.TenantLlmApiKey.v1`); `tenant_llm_config` table (migration `009`).

### Changed
- **BREAKING (multi-tenant) — the shared global LLM key is retired.** The typification classifier now resolves **each tenant's own** provider (`ITypificationAiClassifier.ClassifyAsync` takes the tenant id first; a tenant with no configured/disabled provider degrades to the empty suggestion). Tenants that want AI typification **MUST configure their own provider** at the new admin page. **Single-tenant / dev installs are migrated automatically:** an idempotent startup seed materialises the appsettings global `LlmProviderOptions` into the single operational tenant's per-tenant config (only when a global key is set, exactly one operational tenant exists, and it has no config row yet). The global `ILlmProvider` registration is **kept** for the Flows engine (`ai_classify`/`ai_generate`) — unchanged.

### Fixed
- **Auth-event double-write on graceful shutdown** — `AuthWriteQueue`'s drain on shutdown could re-enqueue an in-flight item and persist a duplicate `auth_events` row. The drain now de-dupes the in-flight write (PR #72). _Merged to `main` 2026-06-21, after the `v2.13.0` tag (`5877e8c4`); ships in the next release._

---

## [2.13.0] — 2026-06-18 — Typification AI AutoFill (safe) + entity prefill (P2b)

Phase **P2b** of the Typification module (ADR-0029): **human-in-the-loop** AI auto-fill of the wrap-up disposition (cascade + field values) once measured calibration clears a graduated confidence band — on top of the trust / safety / calibration / observability substrate that makes it production-safe. The agent always commits; autonomous (no-human-review) commit is intentionally **deferred** (see below). No new Pro license feature (RBAC lives in Platform) — still consumes `Verbara.Sdk.Pro` **2.8.0-pro**. PR #70 / verbara/Verbara.Platform.Web#112.

### Added
- **LLM operability foundation** — token-usage capture on `LlmResponse`; the `verbara.platform.llm` meter + `[LoggerMessage]` events + OTel; the `llm.completions` keyed resilience policy (previously a silent no-op); the `typification_ai_suggestions` shadow/provenance store (migration `004`). New RBAC permissions `typification:ai:configure` + `typification:ai:autonomous`.
- **Graduated `AiMode {Off, Shadow, SuggestOnly, AutoFill}`** + confidence bands (clean break of the flat `ConfidenceThreshold`, persisted as a resilient string). Shadow mode persists every suggestion (model id + prompt version) for reconciliation.
- **Calibration gate** — `ITypificationCalibration` derives AutoFill/autonomous readiness from reconciled accuracy at the published thresholds; exposed via a calibration-status endpoint and surfaced in the admin Mode selector.
- **Entity prefill under a PII allow-list** — AI named-entity extraction remapped to fields via `EntityFieldMap`, PII-screened **at extraction** (card/Luhn, national-id, phone, email; Unicode-digit normalized) and re-screened on the AI write path (defense-in-depth).
- **Per-binding AI config override** — effective-config resolution with the autonomous/AutoFill write-gates applied.
- **Cost & abuse controls** — per-tenant daily token budget (**fail-closed**: degrades before the LLM call) + a dedicated per-tenant `llm` rate-limit + a prompt-size guard (caps enumerated leaves, prefers subtree).
- **Frontend** (Web #112) — admin Mode selector gated on calibration + bands + calibration panel + entity-map / PII allow-list editor + per-binding override + anti-clobber AutoFill UX with Undo.
- Migrations `004`–`008` (additive / idempotent): AI-suggestion shadow store, submission AI-provenance + suggested-vs-committed correction signal, audit `ai` actor type, `surfaced_band` for calibration correctness, schema-binding AI override.

### Changed
- **`/typify` provenance is now server-authoritative** — client-sent source flags are ignored; the server derives `Source` and records the suggested-vs-committed correction signal (migration `005`).
- **Calibration accuracy is integrity-scoped** — counts only samples from the published `schema_version` and **excludes** AutoFill-band samples to avoid self-confirmation (migration `007` `surfaced_band`).

### Security
- **Prompt-injection hardening** — untrusted-transcript fence, role-marker neutralization, Unicode / zero-width stripping; classification keyed on the stable leaf `Code`. Multilingual / code-switching hardening.
- **AI-disposition audit** — tamper-evident `AuditEntry.IntegrityHash` + a dedicated `ai` actor on automated writes (migration `006`).

### Fixed
- **AOT** — STJ source-gen ignores C# property initializers for keys absent from the JSON, so `PiiPolicy` deserialized to `null` and would have **silently disabled AI on every pre-existing schema**; now fail-safe to `DenyAll` (coalesce at consumer + normalize on store read).
- Server-authoritative provenance now reconciles on validation **failure** (calibration-accuracy integrity).
- API-key callers are resolved via the `user_id` claim for AI-config authorization.
- Per-binding overrides no longer bypass the autonomous/AutoFill write-gates.
- The `llm` rate-limit now partitions **per tenant** — a prior bug collapsed every tenant into a single shared bucket.

### Deferred
- **E5 autonomous-commit worker** (auto-close with no human review) → its own dedicated spec: it is GDPR **Art. 22** automated decision-making and needs a compliance layer (tenant opt-in attestation, right to contest, dispute/reopen) beyond the worker mechanics. Its config substrate (autonomous threshold / calibration / permission / write-gate) ships here, forward-compatible.

---

## [2.12.0] — 2026-06-10 — Typification AI auto-disposition (P2a) + deterministic resolution

Phase **P2a** of the Typification module (ADR-0029): AI suggests the disposition node path + field values + confidence at wrap-up; the agent confirms/overrides. The first real LLM integration in the platform. Consumes `Verbara.Sdk.Pro` **2.8.0-pro** (new `TypificationAi` license feature). PR #53 + #52 / Pro #3 / verbara/Verbara.Platform.Web#93.

### Added
- **`Verbara.Platform.Llm`** (new, open, AOT) — the LLM seam (`ILlmProvider` relocated here to break the `Flows→Typification` cycle) + **`OpenAiCompatibleLlmProvider`** (HttpClient + source-gen JSON + `Sdk.Resilience`; covers OpenAI / Azure OpenAI / local Ollama-vLLM by base-URL+model) + `LlmProviderOptions` (deployment-level config, read AOT-safe from the `Llm` config section) + the `DisabledLlmProvider` stub. Registered before the flow engine — also activates the `ai_classify`/`ai_generate` flow nodes.
- **`ITypificationAiClassifier`** — reads the conversation transcript + resolved schema → strict JSON `{leafCode, confidence, sentiment, fields}` → validated root→leaf node path. Never throws (graceful degradation everywhere).
- **`POST /conversations/{id}/typification-suggestion`** — gated `AdvancedTypification + TypificationAi` (402 unless both); confidence-threshold + sentiment gating; `SuggestOnly`. `/typify` records `Source=AutoAi` provenance when an AI suggestion is accepted; `AiConfig` exposed on the schema admin DTO.

### Fixed
- **Deterministic typification binding/hint resolution.** Both resolvers tie-broke equal-priority same-scope candidates by a random `EntityId`, so two same-scope/same-priority bindings (or reason hints) resolved non-deterministically per process. Added `CreatedAt` to `SchemaBinding` + `ReasonHint` (migration `003`, stamped on create, preserved on update); tie-break by `CreatedAt DESC` (most-recent wins) with the id as the final stable tiebreak. Isolated `TypificationEndpointTests` (per-test factory). Eliminates an intermittent test flake at its root.

---

## [2.11.0] — 2026-06-08 — Typification shared taxonomy capture — P1

Phase **P1** of the Typification module (ADR-0029 D2): what the IVR/bot/routing captures about the customer now travels with the conversation and **pre-selects the wrap-up cascade + pre-fills fields** (the agent confirms instead of re-classifying). Platform + Web only — no `Verbara.Sdk.Pro` change (`AdvancedTypification` already shipped in P0). PR #50 / verbara/Verbara.Platform.Web#92.

### Added
- **Attribute-bag contract** on `Conversation.Metadata`: well-known key `reasonPath` (JSON array of node `Code`s) plus arbitrary prefill keys; one consumer reads it at wrap-up. Four capture writers: flow-variable propagation at bot handoff (`BotResponse`/`FlowStepResult.FlowMetadata`); a new **`collect_reason`** flow node (cascade menu → reasonPath); implicit-digital via `ReasonHintMiddleware` → the previously-unused `RouteResult.Metadata`; implicit-voice via a `VERBARA_REASON` Asterisk channel variable read in `VoiceConversationBridge`.
- **`ReasonHint`** domain (scope Did/Channel/Queue → reasonPath) + most-specific-wins resolver + InMemory/Postgres stores + migration `002_reason_hints.sql`; `/admin/reason-hints` CRUD gated `AdvancedTypification`.
- **`ITypificationPrefillResolver`** (Code→NodeId, longest-valid-prefix, subtree-aware, never throws) + metadata field prefill; `GET /conversations/{id}/typification-form` now returns `PrefilledNodePath` + `PrefilledFieldValues`. Field `PrefillSource` exposed in the schema admin DTOs.

### Fixed
- The bot **flow engine was never registered** (`AddPlatformFlows` uncalled; `ILlmProvider` had no implementation) — bot/webhook requests failed DI activation. Wired with a disabled-by-default `ILlmProvider` (AI nodes fail clearly only if executed; AI auto-disposition is P2); added a DI composition smoke test.
- Best-effort reason capture in the voice (`StasisInboundConsumer`) and routing (`ReasonHintMiddleware`) critical paths is now guarded so a transient store failure can't drop a live call or fail a route.

---

## [2.10.0] — 2026-06-07 — Typification (cascading + conditional disposition forms) — P0

New first-class **Typification** module (ADR-0029) replacing the flat single-select disposition model with cascading, conditional, schema-driven disposition forms. Consumes `Verbara.Sdk.Pro` **v2.7.5-pro** (new `AdvancedTypification` license feature). PRs #48 (Platform) / #2 (Pro) / verbara/Verbara.Platform.Web#82.

### Added
- **`Verbara.Platform.Typification`** domain project: versioned `TypificationSchema` (cascading `TypificationNode` tree, depth configurable default 5/max 8), conditional `TypificationField`s (`VisibleWhen`), `SchemaBinding` (per queue/campaign/channel/direction + tenant default), `TypificationSubmission`. Server-authoritative validator (publish + submit) + most-specific-wins binding resolver (direction inferred from conversation metadata).
- Admin endpoints `/admin/typification/{schemas,bindings}` — gated by `LicenseFeature.AdvancedTypification` + new RBAC permission `system:typification:configure`. Runtime `GET /conversations/{id}/typification-form` + `POST /conversations/{id}/typify` (not license-gated — agents always wrap up).
- Cross-pod `TypificationSubmittedEvent` (registered in `ApiJsonContext`, `PlatformPushJsonContext`, `RemoteEventDispatcher`).
- InMemory + raw-Npgsql Postgres stores with JSONB schema persistence.

### Changed
- **Dialer bridge preserved** — an outbound campaign leaf's `dialerCode` maps to the Pro campaign `DispositionCode` (matched by code) and schedules callbacks (`callback_date` conditional field); `CampaignDispositionSubmittedEvent` unchanged.
- Pinned `Verbara.Sdk.Pro.*` from `2.7.4-pro` to **`2.7.5-pro`** (carries the `AdvancedTypification` flag).

### Removed
- **Clean-break (pre-launch):** the flat `Disposition` / `WrapUpRecord` domain, `IDispositionStore`/`IWrapUpStore`, and the `POST /conversations/{id}/wrapup` endpoint.

### Database
- **Migrations consolidated into a single `001_Baseline.sql`** (all prior `001..034` folded into final table shapes — verified byte-identical to the prior chain via Postgres schema-diff — minus the dropped `dispositions`/`wrap_up_records`/`conversations.wrap_up`, plus the 3 `typification_*` tables).

---

## [2.9.1] — 2026-06-07 — Audit category vocabulary fix

Patch over v2.9.0. Migration **034**. No API behaviour change beyond audit persistence.

### Fixed
- **`audit_entries` category CHECK constraint widened to the full emitted vocabulary** (`Migrations/034_AuditCategoryVocabulary.sql` + `AuditEntry.cs`). Some categories the app emits — including the W6 capacity-audit entries (`queues` for `agent.capacity_override`, `operational` for `tenant.capacity_default_changed`) — were not in the DB CHECK list, so the audit insert could be rejected. The constraint now matches what the code emits.

### Changed
- **Synced the hand-rolled Postgres test fixtures with migrations 029–033** (`AuditEntriesNormalizationFixture` et al.) — closes the stale-fixture drift that surfaced as `pending_state` / `queue_priority` "column does not exist" failures in `Storage.Postgres.Tests` during the W6 gate.

---

## [2.9.0] — 2026-06-07 — Session/Auth overhaul: agent presence, liveness & work continuity (ADR-0009 W1–W6)

The complete [ADR-0009](docs/decisions/0009-agent-presence-session-work-continuity.md) north-star, shipped as six sequenced tracks (W1–W6, 2026-06-05→06-07) over PRs #41–#46. Native AOT preserved; no new cross-pod event escaped its `[JsonSerializable]` context. Migrations **029–033**. Ships with **Web v3.5.0-web**.

### Added
- **W3 — server-side agent liveness / anti-zombie.** Web heartbeat `POST /agents/me/heartbeat` (~20s) → Redis `presence:agent:{tenant}:{agent}` with per-tenant TTL `AgentLivenessTimeoutSeconds`; leader-gated `AgentLivenessReaper` reconciles "Postgres-routable AND Redis-dead → ForceOffline → AMI QueuePause". `pagehide` departure beacon `POST /agents/me/offline`; admin `POST /admin/agents/{id}/force-offline`. Migration 029.
- **W4 — deferred pause ("pause-when-free").** `Agent.PendingState` blocks new work instantly but the visible state flips only when active work drains (leader-gated `PendingPauseDrainWorker` + per-tenant `PendingPauseTimeoutMinutes` force + audit). Migration 030.
- **W5 — digital work failover.** Leader-gated `WorkFailoverWorker` re-queues orphaned digital conversations to the FRONT of the origin queue when the owner goes Offline past grace (`Agent.OfflineSince` + cancel-on-return + 3-attempt anti-loop). Supervisor stuck-work view + reassign.
- **W5b — voice caller-rescue.** Abnormal agent-leg-hangup detection (per-leg `HangupCause` on the existing `CallSession` + W3 liveness in a grace window) → leader-gated `CallbackRescueWorker` priority-callbacks the dropped customer into the front of the origin queue (`CallbackOriginator`, anti-loop, `retry-callback`). Per-tenant `VoiceCallbackGraceSeconds`. Migration 032.
- **W6 — agent channel-capacity configurability.** Per-tenant DEFAULT capacity + sparse per-agent OVERRIDE (`ChannelCapacityOverride`, per-field nullable, resolved at read via `IAgentCapacityResolver`/`ICapacityDefaultsProvider` reusing the auth hot-path cache). `MaxTotal` now ENFORCED over the async aggregate (chat-pool + email + sms), voice an exclusive lane (`MaxVoice` pinned 1). Capacity override on agent create/update + tenant defaults in operational settings; capacity-change audit. Migration 033 (tenant-default columns + legacy `agents.capacity` normalization).

### Changed
- **W1 — refresh-token / session hardening.** Refresh cookie re-scoped `/api/v1/auth` (centralized `RefreshTokenCookie`) — fixes the forced-logout-at-15-min root cause; refresh lifetime 24h absolute; per-tenant `TokenResponse.sessionIdleTimeoutMinutes`; rotation grace window (fail-closed); `Busy→Offline` + `Agent.ForceOffline()`.
- **W6 — chat-family capacity pooling.** `ChannelCapacity.GetMax` + the in-memory load ledger now pool the whole chat family (WebChat/WhatsApp/Messenger/Instagram/Telegram/Twitter/Video/Rcs) into one `MaxChat` bucket across live/persist/reconcile.

### Fixed
- **W6 — chat-pool counter bug.** Previously each chat sub-channel counted separately against `MaxChat`, so an agent could hold ~24 chats while "respecting" MaxChat=3. Now correctly pooled.
- **W6 — `MaxTotal` was a dead field.** Defined but never enforced (0 usages); now gates the async aggregate.

---

## [2.8.1] — 2026-06-01 — Hotfix: reference-smb realtime leader-election connection

Patch over v2.8.0. No code change to the API — same binaries.

### Fixed
- **`docker-compose.reference-smb.yml` realtime crash-loop.** Since v2.8.0, `Verbara.Platform.Realtime` hard-requires `ConnectionStrings:Cluster` (or `:Postgres` fallback) for leader election; the reference-smb compose provided neither, so the realtime container crash-looped on a fresh single-host v2.8.0 deploy. Added a Postgres-backed leader-election connection string to the realtime service.

### Changed
- `docker-compose.reference-smb.yml` default `PLATFORM_API_TAG` → **v2.8.1** (`PLATFORM_WEB_TAG` stays `v3.4.0-web` — Web unchanged).

---

## [2.8.0] — 2026-06-01 — Telephony admin: usable SIP trunk + DID configuration

Closes the trunk/DID configuration gaps from the [trunk & DID audit](docs/research/2026-06-01-trunk-did-audit.md). Consumes **Pro 2.7.4-pro**. Ships with **Web v3.4.0-web** (the trunk form, DID module, wizard and connectivity-test UI). Native AOT preserved.

### Fixed
- **Trunk `match_host` (IP-ACL) now persists** (Pro 2.7.4-pro). It previously flowed only to Asterisk realtime, so `GET` returned null and an edit that omitted it silently dropped the inbound identify.

### Added
- **DID validation hardening** — `DidRouteEndpoints` rejects non-E.164 DIDs (`[GeneratedRegex]`) and non-existent target queues (`IQueueStore`) with typed 400s; no more silent DID-without-destination / orphan-queue routes.
- **Trunk connectivity test** — leader-gated `POST /api/v1/admin/trunks/{id}/test-connectivity` runs AMI `pjsip show endpoint/registrations/identify` and returns a structured `TrunkConnectivityResult` (endpoint / registration / IP-ACL identify per auth mode). Gated on `voiceAmiEnabled`.

### Changed
- `Directory.Packages.props` Pro pins → **2.7.4-pro**. `docker-compose.reference-smb.yml` defaults → `PLATFORM_API_TAG=v2.8.0` + `PLATFORM_WEB_TAG=v3.4.0-web`.

---

## [2.7.0] — 2026-06-01 — Inbound Conversation Delivery: voice reaches the agent in the browser

Closes the *Inbound Conversation Delivery* epic — a visitor's chat **and** an inbound phone call now both reach an available agent who handles them entirely in the browser. Consumes **Pro 2.7.3-pro**. Native AOT preserved (0 IL2026/IL3050/IL207x). Ships with **Web v3.3.0-web** (the in-browser softphone UI).

### Added
- **Voice inbound → queue (P2).** `did_routes` table + `IDidRouteStore` + `DidRouteEndpoints` (`/admin/did-routes` CRUD, DID→queue 1:1, unique per tenant); leader-gated `StasisInboundConsumer` (consumes ARI app `verbara`, resolves tenant from the `TENANT_ID` channel var, maps DID→queue, `Answer`+`Continue([stasis-queue])`); `[stasis-queue]` dialplan. IP-ACL trunk identify (`Trunk.MatchHost` → `ps_endpoint_id_ips`) + `TENANT_ID` `set_var` on the trunk endpoint (cross-repo Pro 2.7.0-pro).
- **In-browser voice softphone (3A).** Self-scoped `sipPassword` on `/agents/me` (`AgentMeResponseDto`; admin DTOs never echo the secret; `[JsonIgnore]` defense-in-depth); WebRTC the resolved default endpoint profile; Asterisk entrypoint auto-generates a self-signed WSS cert on boot.
- **Voice as a tracked Conversation (3B.0/3B.1).** `VoiceConversationBridge` (leader-gated, idempotent by Asterisk `LinkedId`, lifecycle Queued→Offered→Active→WrapUp/Abandoned, `voice_linked_id` migration 027); `VoiceScreenPopEvent` screen-pop (caller + history) + per-conversation agent-assist (transcript/sentiment) + disposition/wrap-up + CDR.
- **In-call control (3B.2).** Client hold/unhold/mute/DTMF (SimpleUser); per-agent + per-queue **auto-answer** cascade (migration 028); leader-gated **blind transfer** to queue/agent/external (`VoiceCallControlService` AMI `Redirect`, `POST /conversations/{id}/voice-transfer`); **outbound click-to-dial** (`AgentOutboundDialService` + `POST /voice/dial`) reusing the Pro Dialer stack (DNC + route→trunk + tenant outbound caller-ID), tracked as an outbound Conversation via `VERBARA_OUTBOUND_ID` correlation.

### Fixed
- `PostgresAgentStore.GetByUserIdAsync` omitted `extension`/`sip_password` from its SELECT → softphone got a null `sipPassword` on Postgres (InMemory masked it). All SELECTs now project them.
- `PostgresConversationStore` upsert dropped `voice_linked_id` from the `DO UPDATE SET` clause → late LinkedId stamps (outbound) were lost.

### Changed
- `Directory.Packages.props` Pro pins → **2.7.3-pro**. `docker-compose.reference-smb.yml` defaults → `PLATFORM_API_TAG=v2.7.0` + `PLATFORM_WEB_TAG=v3.3.0-web`. Manual `06-canal-voz-sip.md` reconstructed with the in-browser softphone (§9) + honest roadmap (§10).

---

## [2.4.0-rc] — 2026-05-18 — Pro v2.5.0-pro consumer migration (drops EnforcementMode); **NOT PUBLICLY SHIPPED** ([ADR-0022](docs/decisions/0022-platform-api-aot-shipping-path.md))

Internal RC tag for the Pro v2.5.0-pro consumer migration ([ADR-0012](../Verbara.Sdk.Pro/docs/decisions/0012-eliminate-enforcement-mode-for-license-required-model.md)). The `-rc` suffix signals that the code is correct + tests pass (958/958 Api.Tests green, 0 warnings, 0 errors) BUT the canonical `ghcr.io/verbara/platform/api:*` image cannot be published from this commit per [ADR-0022](docs/decisions/0022-platform-api-aot-shipping-path.md) — the current Dockerfile produces a non-AOT image that ships 68 closed-source `Verbara.Sdk.Pro.*` DLLs as decompilable IL. Public image cutover blocked on ADR-0022 Phases A+B+C (SignalR Hub extraction + EF Core DataProtection migration + AOT publish), estimated 3–4 maintainer-days.

### Changed (consumer migration)

- [`Program.cs`](src/Verbara.Platform.Api/Program.cs) — removed the `CS0618` pragma + back-compat header + `EnforcementMode` config parsing block (~15 lines). License path: `LicenseFilePath` only.
- [`Middleware/LicenseGateMiddleware.cs`](src/Verbara.Platform.Api/Middleware/LicenseGateMiddleware.cs) — full rewrite. Constructor no longer takes `IOptions<LicenseOptions>`. Logic collapses to "metadata + feature licensed → next; metadata + not licensed → HTTP 402 + RFC 9457 ProblemDetails". 158 → 100 lines.
- [`Directory.Packages.props`](Directory.Packages.props) — 21 `Verbara.Sdk.Pro.*` `PackageVersion` entries bumped `2.4.1-pro` → `2.5.0-pro`.
- [`Verbara.Platform.Api.Tests/LicenseTestHelpers.cs`](tests/Verbara.Platform.Api.Tests/LicenseTestHelpers.cs) — NEW. `services.AddAllProFeaturesLicensed()` + `services.AddNoProFeaturesLicensed()` extension methods replacing the old `services.Configure<LicenseOptions>(o => o.EnforcementMode = Disabled)` pattern across test factories.
- 14 test fixture files migrated to the new helper.
- `LicenseGateTests` rewritten to test the simplified middleware contract (no `EnforcementMode` parameter). Tests for `WarnOnly` and `Disabled` modes deleted (behaviour gone).

### Added

- [ADR-0022](docs/decisions/0022-platform-api-aot-shipping-path.md) — Platform.Api Native AOT shipping path. Empirical AOT publish attempt 2026-05-18 produced 8 errors (5× IL3050, 3× IL2026) in two classes: SignalR `IHubContext.Clients.get` (`PushToHubRelay.cs:163,179,195`) + EF Core DataProtection (`Program.cs:515,523,525`). Roadmap Phases A-E documented (~3-4 maintainer-days).
- [`docs/operations/compressed-validation-report-v250pro.md`](docs/operations/compressed-validation-report-v250pro.md) — Pro v2.5.0-pro compressed-validation evidence + verdict (**NO-GO** for public release until ADR-0022 closed).
- [`docs/operations/compressed-validation-evidence/`](docs/operations/compressed-validation-evidence/) — Scenario E baseline metrics + boot logs captured against `v2.3.1` + valid license + `WarnOnly` mode in the K8s preview namespace.
- [`infra/k8s/helm/platform/values-preview-warnonly.yaml`](infra/k8s/helm/platform/values-preview-warnonly.yaml) — Helm overlay for compressed-validation preview deploys.
- [`infra/k8s/manifests/network-policies.yaml`](infra/k8s/manifests/network-policies.yaml) — `allow-postgres-from-workloads` + `allow-redis-from-platform` now honour `verbara.io/postgres-access: allowed` + `verbara.io/redis-access: allowed` namespace labels for preview / test / blue-green opt-in.
- [`Dockerfile`](Dockerfile) — prominent IP-leak warning header + commented AOT pathway ready to activate post-ADR-0022.

### Documentation

- [`CLAUDE.md`](CLAUDE.md) — corrected the misleading "Native AOT" claim for Platform.Api; now states the host disables AOT pending ADR-0022.
- [`../CLAUDE.md`](../CLAUDE.md) (monorepo) — same correction applied to the per-repo stack table.

### Test impact

- Platform.Api.Tests: **958/958 PASS** against Pro v2.5.0-pro packages, 0 warnings, 0 errors.

### Known gaps (deferred)

- `docs/manuales/smb/` still references `LICENSING_MODE` / `EnforcementMode` — separate small commit pending.
- Scenarios B/C/D/E against the v2.5.0-pro RC image cannot run until ADR-0022 unblocks AOT shipping. Scenario E pre-swap baseline is captured.
- Test-license trust-anchor mismatch (local `private.pem` ≠ `OfficialPublicKeyFingerprintSha256`) — blocks Scenario C until either an offline 5-minute license is issued from Cloudflare or a `LICENSE_TRUST_ANCHOR_PEM_FILE` env override is added to `Program.cs` as a development affordance.

---

## [2.3.1] — 2026-05-18 — Security fix: `LicenseTrustAnchor.OfficialPublicKey` was overridden by empty `byte[]` (Program.cs DI race)

PATCH bump for a one-line bug that silently rendered **every signed Pro license invalid at startup** unless the operator explicitly set `Licensing__PublicKeyPath`. The bug was masked for the entire post-rebrand period by the legacy `Licensing__EnforcementMode=Disabled` short-circuit which skipped the validation call entirely. Surfaced during the v2.3.0 K8s lab deploy when we dropped `EnforcementMode=Disabled` in the Helm chart per the Pro v2.4.0-pro deprecation + v2.5.0-pro removal pathway.

### The bug

[`src/Verbara.Platform.Api/Program.cs:307-309`](src/Verbara.Platform.Api/Program.cs#L307-L309) unconditionally registered a `byte[]` singleton even when `Licensing__PublicKeyPath` was unset:

```csharp
var licensePublicKey = !string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath)
    ? File.ReadAllBytes(publicKeyPath)
    : Array.Empty<byte>();
builder.Services.AddSingleton(licensePublicKey);  // ← BUG
```

Because Platform's `AddSingleton` runs **before** Pro's `AddProLicensing()`, the empty array won the DI race against Pro's `TryAddSingleton<byte[]>(LicenseTrustAnchor.OfficialPublicKey)`. `LicenseValidationHostedService` then received `Array.Empty<byte>()` as its `publicKey` parameter, `ECDsa.ImportSubjectPublicKeyInfo(empty span)` threw, `VerifySignature` returned `false`, and every license returned `LicenseValidationResult.Invalid` with the (mis-attributed) "invalid signature" error.

Sequence that masked the bug since pre-rebrand:

1. Old Helm chart shipped `Licensing__EnforcementMode=Disabled` for community/OSS deployments.
2. `LicenseValidationHostedService.StartAsync` short-circuited at line 63: `if (_options.EnforcementMode == Disabled) { _tracker.Update(Valid, null); return; }` — never invoked `Validate(...)`.
3. Pro v2.4.0-pro (Platform v2.2.0 consumer) marked `EnforcementMode` `[Obsolete]` but preserved back-compat. Lab continued running with `Disabled`.
4. v2.3.0 Helm migration off `EnforcementMode=Disabled` activated the previously-dormant code path. Every Pro license attempted at startup hit the broken `byte[]` path → `Invalid`.

### Fix

[`src/Verbara.Platform.Api/Program.cs:307-325`](src/Verbara.Platform.Api/Program.cs#L307-L325) — only register the `byte[]` when an operator-supplied custom trust anchor file actually exists. Otherwise let `AddProLicensing()` register `LicenseTrustAnchor.OfficialPublicKey` via its own `TryAddSingleton`.

```csharp
if (!string.IsNullOrEmpty(publicKeyPath) && File.Exists(publicKeyPath))
{
    builder.Services.AddSingleton<byte[]>(File.ReadAllBytes(publicKeyPath));
}
// else: AddProLicensing's TryAddSingleton<byte[]>(LicenseTrustAnchor.OfficialPublicKey) wins.
```

### Impact assessment

- **Pre-fix consumers running `EnforcementMode=Disabled`**: no operational impact — validation was being skipped anyway. The Pro tier on these deployments effectively ran without runtime gating, exactly as documented in the SMB v1.x reference docs.
- **Pre-fix consumers running `EnforcementMode=Enforce/WarnOnly` WITHOUT setting `Licensing__PublicKeyPath`**: license validation always failed. `Enforce` mode crashed the host at boot; `WarnOnly` logged warnings continuously. Operators would have noticed immediately — no customer has reported this, which corroborates that the dominant deployment pattern was `Disabled`.
- **Pre-fix consumers setting `Licensing__PublicKeyPath`**: not affected — the operator-supplied path took precedence as designed.

No Pro license bytes were ever leaked, no signature material was misused. This is a "validation always denies" bug, not a "validation always allows" bug.

### Required for v2.5.0-pro readiness

Pro v2.5.0-pro removes `EnforcementMode` entirely (ADR-0012 transition). Without this Platform fix, ANY consumer that doesn't explicitly set `Licensing__PublicKeyPath` would have hit the validation-always-fails bug at v2.5.0-pro upgrade time. This patch unblocks the v2.5.0-pro consumer migration (Platform v2.4.0).

### Test coverage

Adds regression test `tests/Verbara.Platform.Api.Tests/Licensing/LicenseTrustAnchorWiringTests.cs`:

- `Resolved_ShouldBeOfficialPublicKey_WhenPublicKeyPathUnset` — builds host with no `Licensing:PublicKeyPath` config; asserts `IServiceProvider.GetRequiredService<byte[]>()` equals `LicenseTrustAnchor.OfficialPublicKey` (not `Array.Empty<byte>()`).
- `Resolved_ShouldBeCustomKey_WhenPublicKeyPathPointsToValidFile` — confirms operator override still works.

---

## [2.3.0] — 2026-05-18 — Worker resilience hardening + Pro 2.4.1-pro cascade (ADR-0021)

MINOR bump because this release wires a new host-level switch (`HostOptions.BackgroundServiceExceptionBehavior = StopHost`) and applies the outer try-catch + LogWorkerCrash + rethrow discipline to **all 14 Platform `BackgroundService` implementations**. The pair with Pro v2.4.1-pro (ADR-0013) closes the silent-worker-death architectural bug exposed by the D-LK 24h K8s soak (2026-05-17/18) when `QueueDistributionWorker` stopped heart-beating at T+16h36m and the pod stayed "Running" for 21 h.

Canonical spec: [`docs/specs/2026-05-18-worker-resilience-pattern-hardening.md`](docs/specs/2026-05-18-worker-resilience-pattern-hardening.md). Execution plan: [`docs/plans/completed/2026-05-18-platform-v230-worker-resilience.md`](docs/plans/completed/2026-05-18-platform-v230-worker-resilience.md). Decision: [ADR-0021](docs/decisions/0021-stophost-on-worker-crash-house-style.md). Pro counterpart: [Verbara.Sdk.Pro ADR-0013](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0013-stophost-on-worker-crash-house-style.md).

**Coordinated cross-repo:** SDK `2.1.2` (unchanged) · Pro **`2.4.1-pro`** (cascade) · Web `3.0.3-web` (unchanged — no client-side worker-resilience surface).

### Host-level wiring

- **`src/Verbara.Platform.Api/Program.cs` (L96-110)** — `builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = StopHost)`. With this switch any worker (Platform's or Pro's) that rethrows from its outer try-catch causes the host process to stop, K8s observes the exit, and the operator sees `Last State Reason: Error` plus the `WorkerCrash` Critical log in `--previous`. Without it, the .NET default `Ignore` swallows the rethrow silently — the failure mode D-LK exposed.

### Hardened (14 workers)

**Pattern A — polling (timer / `while`-loop), 11 files:**
- `Services/QueueDistributionWorker.cs` ⭐ (the one that died in D-LK)
- `Services/ConversationTimeoutWorker.cs`
- `Services/CampaignMetricsPoller.cs`
- `Services/WebhookDeliveryService.cs` (dual-loop — both `ProcessChannelAsync` + `PollPendingRetriesAsync` wrapped)
- `Services/RetentionPurgeService.cs`
- `Services/AuditRetentionService.cs`
- `Services/ImpersonationSessionTimeoutService.cs`
- `Services/Reports/ReportSchedulerService.cs`
- `Verbara.Platform.Automation/TimerPollingService.cs`
- `Verbara.Platform.Mail/Services/TokenRefreshService.cs`
- `Verbara.Platform.Billing/DunningService.cs`

**Pattern B — Rx subscribe, 2 files:**
- `Services/BotAnalyticsPersistenceService.cs` (subscription nullification on `OnError`; `IsSubscriptionHealthy` property; `HandleEventSafely` wraps fire-and-forget)
- `Services/VerbaraCapacitySyncService.cs` (`async ExecuteAsync` with outer try-catch; subscription nullification; `HandleCapacityChangedSafely`)

**Channel consumer, 1 file:**
- `Services/AuthWriteQueue.cs` (outer catch was OCE-only; extended to full Critical-log + rethrow for non-OCE fatals; `LogWorkerCrash` source-gen added)

### Added

- **25 new resilience tests** in `tests/Verbara.Platform.Api.Tests/Workers/Resilience/`:
  - **Tier-1 deep (4 workers × ~4 tests = 15):** `QueueDistributionWorkerResilienceTests`, `ConversationTimeoutWorkerResilienceTests`, `WebhookDeliveryServiceResilienceTests`, `BotAnalyticsPersistenceServiceResilienceTests`
  - **Smoke (7 workers, 1 test each = 7):** `SimpleWorkerSmokeTests.cs` covering `CampaignMetricsPoller`, `RetentionPurgeService`, `AuditRetentionService`, `ImpersonationSessionTimeoutService`, `ReportSchedulerService`, `VerbaraCapacitySyncService`, `AuthWriteQueue`
  - **Integration:** `WorkerResilienceHostOptionsTests` asserts Platform DI resolves `IOptions<HostOptions>` with `BackgroundServiceExceptionBehavior = StopHost`
  - **Helper:** `WorkerResilienceTestHelpers.AwaitExecuteFaultAsync` uses `BackgroundService.ExecuteTask` (public .NET 8+) to assert outer rethrow without reflection
- **`[LoggerMessage]` source-gen** per-worker (matches existing Platform convention — colocated `partial void LogXxx(...)` methods inside the worker class):
  - `LogWorkerCrash(string workerName, string reason, Exception ex)` — Critical, `[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}`
  - For Pattern B: `LogSubscriptionFault(string reason)` — Critical
  - For Pattern B with fire-and-forget: `LogFireAndForgetSwallowed(string reason)` — Warning

### Changed

- **`NuGet.Config`** — extended `packageSourceMapping` to also map `Verbara.Sdk.Pro*` patterns to the `local` source for the maintainer's dev-iteration loop. The `Dockerfile` already removes the `local` source before production restore, so production builds remain GitHub-Packages-exclusive. Dev-only change.
- **`Directory.Packages.props`** — bumped 21 `Verbara.Sdk.Pro.*` package pins from `2.4.0-pro` → `2.4.1-pro`.
- **Platform.Api workers' `ExecuteAsync` signatures** preserved (still `protected override async Task` for Pattern A, `protected override Task` for Pattern B). `VerbaraCapacitySyncService.ExecuteAsync` is now `async Task` (added `await Task.Delay(Timeout.Infinite, stoppingToken)` so the outer try-catch can surface fatal exceptions during the worker's lifetime; previously was sync `Task.CompletedTask` after Subscribe).

### Pro 2.4.1-pro cascade (`Directory.Packages.props`)

| Package | Was | Now |
|---|---|---|
| Verbara.Sdk.Pro.EventStore | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Analytics | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.CallAnalytics | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Dialer | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Dialer.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.EventStore.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.CallAnalytics.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Analytics.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Licensing | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.AgentAssist | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.AgentAssist.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Routing | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Realtime | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Realtime.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Cluster | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Cluster.Storage.Postgres | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.MultiTenant | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Push | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Push.SignalR | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.Storage.Common | 2.4.0-pro | 2.4.1-pro |
| Verbara.Sdk.Pro.OpenTelemetry | 2.4.0-pro | 2.4.1-pro |

### Back-compat

- No public API removed. No DTO contract changes. No DB migrations.
- All 938 pre-existing `Verbara.Platform.Api.Tests` tests pass unchanged after hardening.
- Workers' business logic preserved 100% — the change is invisible until a worker would have died.
- HTTP 402 (RFC 9457) license-gate contract from v2.2.0 unchanged.

### Follow-up work tracked (non-blocking)

- Per-worker resilience tests for `TimerPollingService` (Automation), `TokenRefreshService` (Mail), `DunningService` (Billing) — the workers themselves are hardened; their resilience contract is covered transitively by `WorkerResilienceHostOptionsTests`. Per-worker smoke tests deferred to v2.4.0 or v2.4.1 maintenance.
- D-LK soak repeat with the hardened image to confirm the silent-stale-heartbeat failure mode is impossible by construction.

---

## [2.2.0] — 2026-05-17 — License-status surface + HTTP 402 RFC 9457 contract + Pro 2.4.0-pro cascade

MINOR bump because this release introduces a new platform-admin-visible endpoint (`GET /management/system/license/status`) and changes the `LicenseGateMiddleware` response contract from HTTP 403 → HTTP **402 Payment Required** with RFC 9457 ProblemDetails extension members carrying actionable `tier_required`, `trial_url`, `upgrade_url`, and `contact_sales_url` sourced from the enriched `LicenseGuardResult` in Pro v2.4.0-pro. Adds back-compat plumbing to suppress the new Pro deprecation event 12001 (`Licensing:EnforcementMode`) in demo / dev / Helm surfaces until we migrate in lockstep with Pro v2.5.0-pro.

Canonical execution plan: `~/.claude/plans/si-refactored-pascal.md`. Platform-side memory: [`Verbara.Platform/.project-memory/project_v22_pro_240_consumer.md`](Verbara.Platform/.project-memory/project_v22_pro_240_consumer.md).

**Coordinated cross-repo:** SDK `2.1.2` (unchanged) · Pro **`2.4.0-pro`** (cascade) · Web `3.0.3-web` (unchanged — no client-side 402 branching needed today; follow-up v2.3.x track).

### Pro 2.4.0-pro cascade (Directory.Packages.props)

- 21 `Verbara.Sdk.Pro.*` `PackageVersion` pins bumped from `2.3.0-pro` → `2.4.0-pro`. Adds new `ILicenseStatusReader` admin surface + `LicenseStatusSnapshot` record + enriches `LicenseGuardResult` (record struct extended with `TierRequired`, `UpgradeUrl`, `TrialUrl`, `ContactSalesUrl` init-only properties — positional ctor unchanged). Back-compat preserved verbatim: `Licensing:EnforcementMode` still accepted (deprecated; removal in Pro v2.5.0-pro). See [Pro CHANGELOG `[2.4.0-pro]`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/CHANGELOG.md).
- All 24 Pro nupkg packages pushed to GitHub Packages via manual `dotnet nuget push --source github` (Pro CI on `verbara/Verbara.Sdk.Pro` is currently 0-run state since rebrand 2026-05-05; Actions billing setup pending org-admin fix).

### New endpoint

- `GET /management/system/license/status` (`PlatformAdminOnly` policy, sibling of existing `GET /management/system/license`). Returns the raw Pro `LicenseStatusSnapshot` record directly — no Platform DTO wrapper. Pro guarantees the contract via the public `LicensingJsonContext` for AOT-safe serialization. Fields: `IsLoaded`, `IsValid`, `Tier`, `ExpiresAt`, `MaxAgents`, `MaxNodes`, `AuthorizedDigestsCount`, `LastValidationResult`, `LastValidationAt`, `RevalidationInterval`, `Licensee`. Companion to existing `GET /license` (which wraps in `LicenseInfoDto` because it synthesises grace-period derived state; the new snapshot carries the full evaluated view).
- Registered in `ApiJsonContext` via `[JsonSerializable(typeof(Verbara.Sdk.Pro.Licensing.LicenseStatusSnapshot))]`.

### Contract change — LicenseGateMiddleware

- **HTTP 402 Payment Required** (was HTTP 403 Forbidden) on Pro feature blocks in `EnforcementMode.Enforce`. RFC 9110 reserves 402 for subscription/payment gates (Stripe-style). 4xx-class so it does NOT consume the SLO error budget (5xx would have); HTTP clients do not auto-retry 402.
- Response body is **RFC 9457 ProblemDetails** (same family the Platform already uses for all errors) with new extension members populated from `LicenseGuard.Evaluate`:
  - `tier_required` — populated for tier-limit scenarios.
  - `trial_url` — populated for `NotLicensed` / `Expired` / `GraceExhausted` reasons (points at `https://verbara.io/developer-license`).
  - `upgrade_url` — same set as above (points at `https://verbara.io/pricing`).
  - `contact_sales_url` — populated for `Revoked` (points at `https://verbara.io/contact-sales`).
  - All omitted for `UnauthorizedImage` reason — operator's remedy is to redeploy with the authorized image, not upgrade.
- Middleware now injects `ILicenseGuard` (in addition to `ILicenseStatus`) so the response surfaces the enriched URLs automatically — no Platform-side string composition. Constructor signature changed (4-arg → 5-arg).
- `WarnOnly` and `Disabled` paths unchanged. `LicenseFeatureMetadata` and the per-endpoint gating mechanism are intact.

### Deprecation handling

- **Dev / demo compose** ([`docker/docker-compose.full.yml`](docker/docker-compose.full.yml) + [`docker/demo/docker-compose.demo.yml`](docker/demo/docker-compose.demo.yml)): keep `Licensing__EnforcementMode: Disabled` (removing it would break startup under v2.4.0-pro back-compat — Enforce-default throws on null `LicenseFilePath`). Add `Logging__LogLevel__Verbara.Sdk.Pro.Licensing.LicensingDeprecationHostedService: Warning` to suppress the boot-time deprecation log event 12001 (`LogInformation`-level message from Pro). Migration to the post-`EnforcementMode` model lands in lockstep with Pro v2.5.0-pro.
- **Helm chart** ([`infra/k8s/helm/platform/values.yaml`](infra/k8s/helm/platform/values.yaml) + [`templates/platform-api-deployment.yaml`](infra/k8s/helm/platform/templates/platform-api-deployment.yaml)): add `api.licensing.licenseFilePath` value (default empty string = community mode). Existing `api.licensing.enforcementMode` documented as deprecated. Template emits both env vars conditionally — existing chart consumers keep working without touching values.

### SMB operator docs

- [`docker/.env.reference-smb.example`](docker/.env.reference-smb.example) — section "9. LICENSING" rewritten. `LICENSING_MODE=Disabled/Enforce` → `LICENSE_PATH=` model. Points operators at https://verbara.io/developer-license for free Tier 0.5 acquisition.
- [`docker/docker-compose.reference-smb.yml`](docker/docker-compose.reference-smb.yml) — line 162 swap: `Licensing__EnforcementMode: ${LICENSING_MODE:-Disabled}` → `Licensing__FilePath: ${LICENSE_PATH:-}`. Added commented-out bind-mount for the .lic file.
- [`docs/manuales/smb/99-troubleshooting.md`](docs/manuales/smb/99-troubleshooting.md) — `Failed to load license` row updated to reference `LICENSE_PATH` + HTTP 402 community mode + Tier 0.5 link.
- [`docs/operations/first-realistic-demo.md`](docs/operations/first-realistic-demo.md) — Admin → License section rewritten to reflect HTTP 402 community mode + Tier 0.5 acquisition.

### Tests

- [`tests/Verbara.Platform.Api.Tests/LicenseGateTests.cs`](tests/Verbara.Platform.Api.Tests/LicenseGateTests.cs):
  - `BuildMiddleware` helper now accepts an optional `ILicenseGuard` substitute (defaults to a `NotLicensed` result with the canonical URLs).
  - `InvokeAsync_ShouldReturn403_WhenUnlicensedInEnforceMode` → `InvokeAsync_ShouldReturn402_WhenUnlicensedInEnforceMode`. Assertion flipped to `Status402PaymentRequired`.
  - Phase I follow-up: 4 new tests asserting RFC 9457 extension members per `LicenseBlockReason` (added separately if needed; back-compat coverage stays).
- 14 existing test factory / fixture files file-scoped `#pragma warning disable CS0618` (back-compat: they continue to construct `EnforcementMode={Disabled,Enforce,WarnOnly}`).

### Cross-repo v2.5.0-pro unlock

- Pre-condition #2 (≥1 Platform release consumed Pro 2.4.0-pro) — **satisfied** on `v2.2.0` tag push.
- Pre-condition #3 (`.env.reference-smb.example` no longer references `LICENSING_MODE`; manuales updated) — **satisfied** by this release.
- Pre-condition #1 (≥6 weeks since Pro v2.4.0-pro tag 2026-05-17) — elegible from **2026-06-28** onwards.

---

## [2.1.0] — 2026-05-10 — Image-binding (Pro/ADR-0011 + ADR-0018 Trigger 5 machinery) + Pro 2.3.0-pro cascade

Minor bump because this release introduces operator-visible new surface: Helm chart values default to `ghcr.io/verbara/platform/{api,web}` (was local KVM registry), new admission-policy template, new docker-compose verification toolkit, new CI workflow that publishes signed OCI images to GitHub Container Registry, and the consumed Pro v2.3.0-pro adds `LicenseValidator.UnauthorizedImage` semantics. Coordinated cross-repo with **Pro 2.3.0-pro** + **verbara-website Worker integration** that ships license-issuance with `AuthorizedImageDigests` claims.

This is the Platform-side closure of [ADR-0018](docs/decisions/0018-visibility-decision-3-private-now-public-on-trigger.md) Trigger 5 (last visibility-flip gate). Once the first signed Platform image is published to `ghcr.io/verbara/platform/api` (by tagging this version) AND the digest is registered in `verbara-website/data/authorized-digests.json`, Trigger 5 flips to ✅ GREEN and the dashboard reaches 7/7.

### Cosign image-signing machinery

- **`.github/workflows/release.yml`** — first-ever GitHub Actions workflow in this repo. Triggered by pushing a `v*` tag. Two-pass build via `docker/build-push-action@v6`:
  1. Pass 1: build + push `ghcr.io/verbara/platform/api:${tag}-staging` with placeholder `VERBARA_IMAGE_DIGEST` → captures the resulting manifest-list digest from the action output.
  2. Pass 2: rebuild + push `ghcr.io/verbara/platform/api:${tag}` with the real digest baked in via `--build-arg`.
  3. Sigstore cosign sign of the final manifest-list digest using the `COSIGN_PRIVATE_KEY` + `COSIGN_PASSWORD` Actions secrets.
  4. Verify-after-sign against the committed `.github/cosign.pub` to fail-fast on signing errors.
  5. Best-effort cleanup of the staging tag.
- **`.github/cosign.pub`** — the official Verbara cosign public key (ECDSA P-256), committed for offline verification by the workflow + customers. Private key custody: `~/.verbara/keys/cosign.key` + Cloudflare Worker secret `VERBARA_COSIGN_PRIVATE_KEY` (see `Verbara.Sdk.Pro/docs/operations/2026-05-10-cosign-keypair-bootstrap.md`).
- **`Dockerfile`** — accepts `ARG VERBARA_IMAGE_DIGEST` (default empty for local dev). Runtime stage bakes the value into `/etc/verbara-image-digest` so Pro v2.3.0-pro's `ContainerImageDigest.ReadFromEnvironment()` can read it. When the arg is empty (local dev `docker build` without --build-arg), the file is empty → Pro's permissive path applies.

### NuGet GitHub Packages integration (resolves the CI build path)

- **`NuGet.Config`** — adds `github` source pointing at `https://nuget.pkg.github.com/verbara/index.json` for private Verbara.Sdk.Pro.* packages. Adds `packageSourceMapping` so private Pro packages resolve exclusively via `github`, everything else via `nuget.org` (required by Central Package Management when multiple sources are defined; previously triggered NU1507).
- **`Dockerfile`** — replaces the old IPcom-era sed-mangling logic with a BuildKit secret mount (`--mount=type=secret,id=nuget_auth_token`) + `dotnet nuget update source github -u verbara -p ...` runtime credential injection. Cleanly handles both CI (token via `GITHUB_TOKEN`) and local docker builds (token via maintainer's `GITHUB_PACKAGES_PAT`). Removes the `local` source inside the build context (its host path doesn't exist there; NuGet errors hard NU1301 on missing local sources).
- **`release.yml`** — passes `GITHUB_TOKEN` as BuildKit secret `nuget_auth_token` to both `build-push-action@v6` invocations; the auto-provisioned token has `read:packages` scope for the verbara org.
- **48 Pro packages now live on GitHub Packages** (`https://nuget.pkg.github.com/verbara/`): 24 v2.3.0-pro + 24 v2.2.0-pro backfill. See `Verbara.Sdk.Pro/docs/operations/2026-05-10-pro-packages-to-github-packages.md`.

### Helm chart updates

- **`infra/k8s/helm/platform/values.yaml`** — `api.image.repository` and `web.image.repository` defaults changed from `192.168.122.1:5050/verbara-platform/{api,web}` (the maintainer's local KVM cluster registry) to **`ghcr.io/verbara/platform/{api,web}`** (production). Local KVM remains as documented `--set` override for dev; chart README has the snippet.
- **`infra/k8s/helm/platform/templates/cosign-admission-policy.yaml`** — new Kyverno `ClusterPolicy` template (opt-in via `imageVerification.enabled: true`) that calls `verifyImages` with the embedded cosign public key. Rejects pods whose images aren't signed by the official Verbara cosign key. Gatekeeper variant deferred to a future enhancement; operators can author their own ConstraintTemplate.
- **`infra/k8s/helm/platform/values.yaml`** — new `imageVerification` section (`enabled: false` default for back-compat with clusters without admission-policy controllers; `cosignPublicKey` accepts inline string or `--set-file ...=cosign.pub`).
- **`infra/k8s/helm/platform/files/cosign.pub`** — chart asset (same bytes as `.github/cosign.pub`).
- Chart README documents the verification flow + the local KVM dev override.

### Docker Compose verification toolkit

- **`docker/verbara-verify-image.sh`** — operator pre-flight: HEADs the OCI manifest, runs `cosign verify --key https://verbara.io/.well-known/cosign.pub`, prints the manifest-list digest. Run before `docker compose up`.
- **`docker/docker-compose.verified.yml`** — template using digest-pinned image references (`@sha256:REPLACE_WITH_MANIFEST_LIST_DIGEST`). Operator copies + substitutes their resolved digest from `verbara.io/data/authorized-digests.json`.
- **`docker/verbara-quickstart.sh`** — wrapper that fetches the digest, runs verify, generates the verified compose file, and runs `docker compose up`. Fully working (not a stub); `TODO(web-image-binding)` marker for future v2.4 enhancement.
- **`docker/README.md`** — verification flow documented for both quick (`verbara-quickstart.sh`) and power-user (`docker-compose.verified.yml` template) paths.

### Operator runbooks (new)

- [`docs/operations/2026-05-10-update-authorized-digests-after-release.md`](docs/operations/2026-05-10-update-authorized-digests-after-release.md) — post-release runbook: capture the new manifest-list digest from the workflow output → PR new entry to `verbara-website/data/authorized-digests.json`'s `current` array. Future automation (cross-repo Action) tracked as deferred enhancement.

### Pro 2.3.0-pro cascade (Directory.Packages.props)

- 21 `Verbara.Sdk.Pro.*` PackageVersion pins bumped from `2.2.0-pro` → `2.3.0-pro`. Adds `UnauthorizedImage` validation + `ContainerImageDigest` helper + `LicenseGuardMetrics.RecordImageUnauthorized` counter + parse-time digest format validation on Platform's consumption surface.
- Verified: `dotnet build Verbara.Platform.slnx -c Release` clean; `dotnet test Verbara.Platform.slnx --filter Category!=Integration` all green (Api.Tests 932/932, Storage.Postgres.Tests 30/30, Storage.InMemory 125/125, Identity.Redis 34/34, plus rest). Back-compat preserved: Pro 2.3.0-pro accepts v2.2.0-pro signed `.lic` files unchanged.

### Trigger dashboard delta

```
Before this release: ✅ 6/7 (1, 2, 3, 4, 6, 7) · 🟡 1/7 (5) · ❌ 0/7
After this release ships AND first signed image lands on ghcr.io
AND first digest registers in verbara-website:
                     ✅ 7/7 — visibility flip can proceed (operator action)
```

### Maintainer follow-ups

1. **Tag this release** (`v2.1.0` or RC `v2.1.0-rc1` first per the rigorous 21-step plan) → triggers `release.yml` for the first time → first `ghcr.io/verbara/platform/api` push creates the package.
2. **Verify package** lands as private-by-default in `verbara/Verbara.Platform` → Settings → Packages.
3. **Run** `docs/operations/2026-05-10-update-authorized-digests-after-release.md` runbook to register the new digest.
4. **Smoke-test** the full F+B+C chain end-to-end (see `Verbara.Sdk.Pro/docs/operations/2026-05-10-cosign-keypair-bootstrap.md` companion runbook).
5. **ADR-0018 Status update** flipping Trigger 5 ✅ GREEN.
6. **Optional**: install `actionlint` + `shellcheck` locally; install Kyverno + add admission-policy as a follow-up if K8s test cluster reachable.

---

## [2.0.1] — 2026-05-10 — Security: ADR-0018 Trigger 3 closure (2 P0 + 4 P1 fixes)

First patch since the v2.0.0 rebrand release. Closes the 6 grep-able-from-source security findings raised in the 2026-05-09 pre-public security review (`docs/security/2026-05-09-pre-public-security-review.md`), unblocking ADR-0018 visibility-flip Trigger 3.

### Security (P0 — closes cross-tenant data exposure + plaintext OAuth secret)

- **MT-001 — Cross-tenant data access via `X-Tenant-Id` header on `/admin/*` and legacy `/admin/audit`** (`3a90300b`). New `TenantBoundaryValidationMiddleware` rejects requests where `X-Tenant-Id` (header / subdomain) conflicts with the JWT `tid` claim, except for `key_type=management` API keys and callers resolving to `TenantType.Platform` / `TenantType.Partner`. Bare `RequireRole("Admin")` policy is no longer sufficient to read another tenant's users, queues, agents, teams, or audit log.
- **ADMIN-001 — OIDC client secret persisted and returned plaintext on `/admin/auth/config`** (`23409c55`). `PostgresTenantAuthConfigStore` now `IDataProtectionProvider`-wraps `oidc_client_secret` on write and unwraps on internal read. `GET /admin/auth/config` returns a new `TenantAuthConfigResponse` DTO carrying only `OidcClientSecretSet: bool` + 8-hex SHA-256 fingerprint — never the raw value. New `OidcClientSecretEncryptionMigrator` IHostedService idempotently encrypts existing rows on first deploy. New migration `024_EncryptOidcClientSecret.sql`.

### Security (P1 — closes tenant-scoping bypass on MFA admin, billing audit gap, scope-bypass on management API keys)

- **MFA-001 — `?targetTenant=` on `/management/mfa/users/*` accepts arbitrary tenant id without ownership check** (`baa7aaef`). New async `ResolveTargetTenantAsync` mirrors the impersonation-hierarchy pattern from `ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync`. Foreign-hierarchy attempts emit a new `MfaPrivilegeEscalationAttempted` audit event and return 403.
- **BILL-001 — 8 of 9 billing mutations emitted no audit entries** (`2b83604a`). New `BillingAuditEventTypes` constants + `IAuditService.AppendAsync` emissions added to `CreateRateCard`, `UpdateRateCard`, `DeleteRateCard`, `GenerateInvoice`, `IssueInvoice`, `PayInvoice`, `UpdateQuota`, `PauseDunning`. `PayInvoice` records both `payment_status_before/after` and `tenant_status_before/after`.
- **BILL-002 — `PayInvoice` derived tenant from path-supplied invoice id without caller cross-check** (`2b83604a` — bundled). `PayInvoice` now requires an explicit `?tenantId=` query parameter and asserts it matches the dunning record's tenant; rejects mismatched IDs with `400` + audit-emit. Validates `invoiceId` shape via new `EntityId.IsValid` before the store call.
- **ADMIN-002 — Management API keys short-circuited every `PlatformAdminRequirement` permission check** (`c35a0d17`). `PlatformAdminAuthorizationHandler` now reads the API key's `scopes` array and succeeds iff the requested `requirement.Permission` is contained. Legacy `platform:*` wildcard kept working for back-compat through v2.0.x patches; deprecation warning v2.1.0; wildcard removal v3.0.0 per [ADR-0019](docs/decisions/0019-scope-aware-management-api-keys.md).

### Tests

- 35 new tests added (14 P0 + 21 P1). New shared fixture `tests/Verbara.Platform.Api.Tests/Multitenancy/CrossTenantHeaderAttackFixture.cs` (`4718a870`) seeds a 5-tenant hierarchy and is reused by MT-001, MFA-001, and ADMIN-002 tests.
- `Verbara.Platform.Api.Tests`: **932/932** (897 baseline + 35 new). `Verbara.Platform.Storage.Postgres.Tests`: **34/34** (30 baseline + 4 new ADMIN-001 Testcontainers). Full slnx: 30 test assemblies all green. Zero source-code warnings under `TreatWarningsAsErrors`.

### Docs

- New: `docs/security/threat-model.md` — public threat model published to close ADR-0018 Trigger 4.
- New: `docs/security/2026-05-09-pre-public-security-review.md` — focused 60-endpoint audit (Trigger 3 evidence).
- New: `docs/decisions/0019-scope-aware-management-api-keys.md` (Accepted) — documents the management-key permission model change with v2.0.x → v2.1.0 → v3.0.0 timeline.
- ADR-0018 Status updates flipping Triggers 4, 7 → ✅ GREEN; Trigger 3 → BLOCKED → ✅ GREEN as code shipped.
- Web ADR-0007 mirror updates.
- `docs/plans/active/2026-05-09-trigger-3-p0-p1-remediation-plan.md` → `docs/plans/completed/` on this release.

### ADR-0018 visibility-flip dashboard delta

```
Before this release: ✅ 5/7 (1, 2, 4, 6, 7) · 🟡 1/7 (5) · ❌ 1/7 (3)
After this release:  ✅ 6/7 (1, 2, 3, 4, 6, 7) · 🟡 1/7 (5) · ❌ 0/7
```

Visibility flip is now gated **only** by Trigger 5 (Pro v2.3.x image binding execution; plan published at `Verbara.Sdk.Pro/docs/plans/active/2026-05-09-pro-v23x-image-binding-execution.md`).

> **Note on the version gap (1.14.6 → 2.0.1):** The intervening v1.15.0 (Pre-v2 Foundation) and v2.0.0 (Verbara rebrand) releases were originally shipped without inline CHANGELOG entries; both have been **backfilled below on 2026-05-10** sourced from `git log` ranges + ADR-0016 + ADR-0017 + `docs/plans/completed/`.

---

## [2.0.0] — 2026-05-05 — Verbara rebrand + R5.5 K8s Phase 0LK

**Major release.** Closes the brand transition from `Asterisk.Platform` to `Verbara.Platform` per [ADR-0016 license + rebrand](docs/decisions/0016-license-and-rebrand-to-verbara.md) (Accepted 2026-05-03) and [ADR-0017 rebrand execution](docs/decisions/0017-verbara-rebrand-execution.md) (Accepted 2026-05-05). Coordinated cross-repo with **SDK 2.1.0** + **Pro 2.0.0-pro**. Pre-rebrand artefacts archived under the `pre-rebrand` git tag.

### License

- **Apache License 2.0** adopted (was previously license-unspecified — README mentioned "open-core" without naming the governing license for the Platform backend itself).
- `LICENSE` file added at repo root.
- `NOTICE` file added with attributions.
- README updated with the canonical 4-row stack table (Sdk MIT / Web Apache 2.0 / Platform Apache 2.0 / Pro commercial) + the "engineering moat is the runtime ECDSA license-key validation in `Pro.Licensing`, not source-license restrictions" framing.
- Trademark note: "Asterisk" remains a registered trademark of Sangoma Technologies / Digium; this project builds *on top of* Asterisk PBX as a runtime dependency. The "Verbara" name + branding are distinct.

### Rebrand

- `Asterisk.Platform.*` namespace → `Verbara.Platform.*` (mechanical rename across all `src/` + `tests/` + project files).
- Repository renamed (GitHub URL).
- `<Product>` and `<PackageTags>` in `Directory.Build.props` updated to Verbara branding.
- `RepositoryUrl` updated to `https://github.com/verbara/verbara-platform`.
- All cross-repo SDK + Pro pins bumped to consume the new `Verbara.Sdk.*` + `Verbara.Sdk.Pro.*` package names (SDK 2.1.0 + Pro 2.0.0-pro).
- All references to "Asterisk.Platform" in docs/ updated where pre-rebrand context is no longer needed (historical references in `docs/plans/completed/` and old ADRs preserved as-is per append-only convention).

### Infrastructure — R5.5 K8s Phase 0LK (live K8s deployment baseline)

12 infrastructure commits landing the local-K8s validation environment that R5.5 Phase 0LK requires. Brought up before the rebrand merge to validate the new package names on a real cluster.

- **Talos K8s cluster bootstrap** (P0LK.2) — 1 control-plane + 3 workers on local KVM.
- **Cilium eBPF networking** (P0LK.3) — replaces Flannel + MetalLB + Traefik with the Cilium full stack.
- **CloudNativePG 3-instance HA Postgres** (P0LK.4) — operator-managed cluster with PgBouncer pooler.
- **Redis 8 StatefulSet** (P0LK.5) — AOF persistence enabled.
- **Asterisk Helm chart** (P0LK.6) + **Kamailio/RTPEngine SBC layer** (P0LK.7) — telephony plane.
- **Platform.Api + Web Helm chart** (P0LK.8) — application layer.
- **Observability stack** (P0LK.9) — kube-prometheus-stack + Loki + blackbox-exporter values.
- **PrometheusRule CRD** (P0LK.10) — wraps `alerts.yml` (17 rules).
- **K8s staging docs + bootstrap script** (P0LK.11+12) — `k8s-apps.sh`.
- **Production hardening sprint** — PDBs + NetworkPolicies + SecurityContexts + probes across all workloads.
- **K8s live deployment** of Platform API + Web + observability with 6 production-readiness fixes.

### Cross-repo coordination

- SDK pin: `1.15.x → 2.1.0` (rebrand cascade).
- Pro pin: `1.16.0-pro → 2.0.0-pro` (rebrand cascade).
- Verbara.Platform.Web (separate repo): tracking 2.0.0 + R5.5 Phase 0LK in parallel.

---

## [1.15.0] — 2026-05-02 — Pre-v2 Foundation: IP allowlist + R5.5 D-L 24h soak + observability hardening

**Final pre-rebrand release.** Lands the IP allowlist tenant feature (the first concrete `PlanFeature.IpAllowlist` capability), closes R5.5 Phase D-L production-validation, and tightens observability. Last release under the `Asterisk.Platform` brand before the rebrand to Verbara in v2.0.0.

### IP allowlist (per-tenant) — first PlanFeature.IpAllowlist capability

13-task FCM-batched implementation per `docs/plans/completed/2026-04-28-ip-allowlist-implementation.md`. Tenant-scoped CIDR allowlist with cached lookup, request-time enforcement middleware, and admin CRUD surface.

- **`PlanFeature.IpAllowlist`** — new enum value gating the feature behind the appropriate plan tier.
- **`Verbara.Platform.Identity`** — `TenantAuthConfig.IpAllowlistEnabled` flag; `IpAllowlistEntry` record + `ITenantIpAllowlistStore` contract; `IIpAllowlistEvaluator` + `DefaultIpAllowlistEvaluator` (CIDR matching).
- **`Verbara.Platform.Storage.Postgres`** — migration `023_TenantIpAllowlist.sql` (tenant_ip_allowlist table + ip_allowlist_enabled column on tenant_auth_config); `PostgresTenantIpAllowlistStore` + Testcontainers integration tests.
- **`Verbara.Platform.Storage.InMemory`** — `InMemoryTenantIpAllowlistStore` for dev/tests.
- **`Verbara.Platform.Api`** — `CachedTenantIpAllowlistStore` decorator (per-tenant cache, TTL); `IpAllowlistMiddleware` per-request enforcement; `ManagementTenantIpAllowlistEndpoints` admin CRUD; `ForwardedHeaders` config wiring; `tenant-settings` surface exposes `IpAllowlistEnabled` toggle.
- All endpoints + middleware AOT-compatible, source-gen-registered DTOs, integration-tested via Testcontainers.

### R5.5 Phase D-L — 24h production-validation soak PASS

- 24-hour soak test executed locally on Phase D-L hardware envelope. Closure report at `docs/operations/soak-test-report-local.md`.
- ~959M requests, **0 failures**, p99 average 60.66 ms across the run.
- New `scripts/soak-log-watchdog` + `scripts/soak-drift-snapshot` introduced to guard for log-flood + drift during long-running tests.
- Drift snapshot output path fixed (avoid NBomber's `load-test-reports` wipe).
- Synthetic monitoring verified during the soak (Phase E-L closure-precondition).

### Observability hardening

- **NodeDiskSpaceLow P0 alert** added — fired during R5.5 Phase 0LK pre-soak when an unrelated disk-fill incident surfaced; runbook documented at `docs/operations/alerts-runbook.md`.
- **Datasource UIDs pinned** in Grafana dashboards — prevents UID drift across environments.
- **Per-service Docker log rotation** pinned in `docker-compose.full.yml` (`max-size 100m`, `max-file 5`) — prevents host disk exhaustion under high-traffic conditions.

### Other

- Endpoint group count refreshed in CLAUDE.md: 59 → **70** (post-R5.5 endpoint additions).
- R5.5 Phase C-L.1 stress sweep documented (knee crossing post-Phase-2).
- Pre-existing PlatformEventBus consumers audit (Sprint 1 Task A5, 2026-04-13) archived to `docs/research/archived/`.
- Operations docs: `staging-environment.md` updated with K8s hardening sprint preview; `compose` comments + `ConnectionStringDefaults` rephrased Phase 1 → Phase 2 wording for clarity.

### Cross-repo

- SDK pin: unchanged (`1.15.x`).
- Pro pin: unchanged (`1.16.0-pro`).
- Verbara.Platform.Web (separate repo): aligned cosmetic-track with Platform 1.14.x → 1.15.x.

---

## [1.14.6] — 2026-04-28 — ADR-0015 Phase 2 — Shared `NpgsqlDataSource` adoption (Pro 1.16.0-pro)

**Closes the architectural connection-pool sprawl** identified in R5.5 Phase C-L by collapsing the 14-pool sprawl into **1 shared `NpgsqlDataSource` per distinct connection string** per platform-api instance. Coordinated ship with **Pro 1.16.0-pro** which exposes the `Use*Storage(IServiceCollection, NpgsqlDataSource)` overloads on every storage entry point.

### Pro 1.16.0-pro pin bump

- `Directory.Packages.props` — all 20 `Verbara.Sdk.Pro.*` pins bumped `1.15.0-pro → 1.16.0-pro`.
- Local NuGet feed sync: 24 Pro packages packed at 1.16.0-pro.

### Platform.Storage.Postgres `(NpgsqlDataSource)` overload

- New `AddPostgresStorage(IServiceCollection, NpgsqlDataSource)` overload alongside the existing `(string, Action<NpgsqlDataSourceBuilder>?)`. The string overload now delegates to the DataSource form via `new NpgsqlDataSourceBuilder(connectionString).Build()`.
- All 30+ store registrations stay verbatim — only the DataSource construction path changed.

### Platform.Api `Program.cs` shared-DataSource composition

- `sharedCoreDataSource` built once when `coreConnectionString` is set; passed to `AddPostgresStorage(NpgsqlDataSource)`.
- New `ResolveDataSource(connStr)` helper returns the shared core DataSource when conn string matches the core; otherwise builds a dedicated DataSource for the distinct conn string. Single instance per distinct conn string, never per package.
- All 6 Pro `Use*Storage` / `Add*Postgres` call sites updated to the DataSource overload:
  - `UsePostgresDialerStorage(ResolveDataSource(dialerConnectionString)!)`
  - `UsePostgresClusterTransport(ResolveDataSource(clusterConn)!)`
  - `UsePostgresRealtimeStorage(ResolveDataSource(realtimeConn)!)`
  - `UsePostgresEventStore(analyticsDataSource)` + `AddProCallAnalyticsPostgres(analyticsDataSource)` + `UsePostgresAnalyticsStore(analyticsDataSource)`
  - `UsePostgresProAnalyticsLive(liveAnalyticsDataSource)` (reuses analytics DS when conn string matches)
  - `AddProAgentAssistPostgres(analyticsDataSource)`

### Phase 2 measured impact

`presence` scenario, AMD 9900X / 60 GB / `docker-compose.smb.yml`, same ladder:

| VU | Phase 1 (14 pools × 10) | Phase 2 (1 pool × 10) | Δ p99 |
|---:|---|---|---|
| 100 | p99 16.62 ms · 11 029 RPS | p99 **16.13 ms** · 11 046 RPS | clean |
| 250 | p99 34.59 ms | p99 **32.27 ms** | -2 ms |
| 500 | p99 69.50 ms | p99 **57.06 ms** | **-12.4 ms** |
| 1000 | p99 115.97 ms | p99 **~107 ms** | **-9 ms** |
| 1500 | p99 174.21 ms | p99 **~154 ms** | **-20 ms** |

- Latency improvement at high concurrency (VU 500–1500: 9–20 ms p99 reduction).
- Aggregate throughput unchanged (~11 k RPS) — Postgres-bound, not pool-bound.
- `pg_stat_activity` post-sweep: 13 idle conns (Phase 1 had 21).
- Zero failures, zero `Npgsql.PostgresException` across the entire sweep.

**Capacity-planning Postgres tier table refreshed:**

| Tier | Phase 1 `max_connections` | Phase 2 `max_connections` | Δ |
|---|---:|---:|---:|
| Small | 200 | **50** | -75 % |
| Medium | 400 | **120** | -70 % |
| Large | 600 | **240** | -60 % |
| XL | 1000 | **400** | -60 % |

### Files changed

- `Directory.Packages.props` (20 Pro pins bumped)
- `Directory.Build.props` (PackageVersion 1.14.5 → 1.14.6)
- `src/Verbara.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` (new `(NpgsqlDataSource)` overload)
- `src/Verbara.Platform.Api/Program.cs` (`sharedCoreDataSource` + `ResolveDataSource` helper + 6 wraps to DataSource overloads)
- `docs/decisions/0015-npgsql-datasource-sharing-strategy.md` (Phase 2 status + measured impact section)
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` (Update 2026-04-28 v1.14.6 — `scale.yml` math correct again)
- `docs/operations/capacity-planning.md` (Postgres tier table refreshed)
- `docs/operations/load-test-baseline.md` (Phase C-L Phase 2 section)
- `local-nuget-feed/` (24 Pro 1.16.0-pro packages synced from shared feed)

### Tests

- **882 / 882** Api.Tests passing (no regression from Phase 1 baseline).
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.

### Wire compatibility

- Operators consuming Platform.Api as deployed image: no action required — Phase 2 is internal composition refactor.
- Custom Pro consumers (third-party hosts using `Use*Storage(string)`): NO CHANGE. Pro 1.16.0-pro preserves the string overload verbatim.
- `docker-compose.smb.yml` / `production.yml` `max_connections=200` setting STILL applies (no operator-side change required) — Phase 2 just means the actual demand sits comfortably below it (1 pool × 10 = 10 vs 14 × 10 = 140 in Phase 1).

### Cross-repo coordination

- **Verbara.Sdk: unchanged** (1.15.1).
- **Verbara.Sdk.Pro: 1.16.0-pro** (this release pairs with Pro 1.16.0-pro; ADR-0008 describes the new `Use*Storage(NpgsqlDataSource)` overloads).
- **Verbara.Platform.Web: unchanged** (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.5] — 2026-04-28 — ADR-0015 Phase 1 — Postgres connection-pool sprawl mitigation

**Closes the architectural connection-pool sprawl exposed by R5.5
Phase C-L `presence` sweep** — Platform.Api with all Pro features
active spawns 14 separate `NpgsqlDataSource` instances across the Pro
storage packages + Platform.Storage.Postgres, each previously
inheriting Npgsql's default `Maximum Pool Size=100`. Theoretical
worst-case per-instance demand: 1 400 connections. Real impact
measured at VU=100 concurrent reads on `docker-compose.full.yml`
(postgres-alpine default `max_connections=100`): 13 % HTTP 500 with
`Npgsql.PostgresException (53300): sorry, too many clients already`.

This release ships the Phase 1 mitigation per ADR-0015. Phase 2 (Pro
1.16.0-pro shared `NpgsqlDataSource` overload) is captured as a
plan-skeleton and deferred to the Pro repo cycle.

### 1. `ConnectionStringDefaults` helper at composition root

- New `Verbara.Platform.Api.Services.ConnectionStringDefaults` static
  helper applies SMB-tier pool sizing — `Maximum Pool Size=10`,
  `Minimum Pool Size=2`, `Connection Idle Lifetime=300` — to a
  connection string IF (and only if) the operator did not specify
  them. Detection is case-insensitive substring match over the raw
  connection string.
- `Program.cs` invokes the helper at all 6 connection-string read
  sites: core (`Postgres`), `Dialer`, `Cluster`, `Realtime`,
  `Analytics`, `AnalyticsLive`. Operator-specified values pass
  through verbatim.
- Math: `14 data sources × 10 pool size = 140` conn demand ceiling
  per platform-api instance, comfortable under `max_connections=200`
  shipped in `docker-compose.smb.yml` and `docker-compose.production.yml`.

### 2. `docker-compose.smb.yml` — SMB tier production-ready stack

- New layered overlay representing the canonical SMB tier deployment
  shape (single platform-api + single postgres + supporting services).
- Postgres tuning: `max_connections=200`, `shared_buffers=512MB`,
  `effective_cache_size=2GB` sized for 16 GB RAM SMB tier hardware.
- Connection string: belt-and-suspenders explicit `Maximum Pool Size=10;
  Minimum Pool Size=2;Connection Idle Lifetime=300;Pooling=true`.
- Stack matrix in compose-file headers clarifies:
  - `full.yml` — dev/loadtest stack (lax tuning)
  - `smb.yml` — SMB tier production-ready overlay
  - `production.yml` — SMB tier production-ready (env-file shape)
  - `scale.yml` — Enterprise tier 4-replica overlay

### 3. `docker-compose.full.yml` + `production.yml` patches

- `full.yml` postgres now ships `max_connections=200` (vs alpine
  default 100) so dev/loadtest sweeps don't crash on the 14-pool
  sprawl. Other tunings live in the smb.yml overlay.
- `production.yml` applies the **full SMB tier tuning** (max_connections
  200, shared_buffers 512MB, effective_cache_size 2GB) so customers
  running this directly + .env.production secrets get the correct
  capacity envelope without needing to layer smb.yml on top.

### Phase C-L SMB tier measured impact (post-fix vs pre-fix)

`presence` scenario (`GET /api/v1/admin/agents`, `KeepConstant(VU)`),
AMD 9900X / 60 GB / `docker-compose.smb.yml`:

| VU | Pre-fix | Post-fix |
|---:|---|---|
| 100 | 111 824 OK / 16 168 fail (87 % OK) · p99 91 ms · ~1 864 RPS | **661 738 OK / 0 fail · p99 16.62 ms · 11 029 RPS** |
| 250 | 0 OK / 44 413 Unauthorized | 678 772 OK / 0 fail · p99 34.59 ms |
| 500 | 0 OK / 44 299 Unauthorized | 646 262 OK / 0 fail · p99 69.50 ms |
| 1000 | 0 OK / 43 249 Unauthorized | 656 954 OK / 0 fail · p99 115.97 ms |
| 1500 | 0 OK / 37 267 Unauthorized | 662 023 OK / 0 fail · p99 174.21 ms |

- Concurrency capacity: 15× improvement (bug-saturation at VU=100 →
  clean operation through VU=1500).
- Aggregate throughput at VU=100: 6× improvement.
- Postgres `pg_stat_activity` post-sweep: 21 idle client backend conns
  (well under max_connections=200 cap).
- Zero `Npgsql.PostgresException (53300)` entries in `platform-api`
  logs across the entire 22-min sweep.

**SMB tier knee envelope (latency-defined, throughput plateau ~11 k RPS):**

| Latency budget | Max sustained VU |
|---|---:|
| p99 ≤ 50 ms | ≤ 250 |
| p99 ≤ 100 ms | ≤ 750 |
| p99 ≤ 200 ms | ≤ 1 500 |

### Files changed

- `src/Verbara.Platform.Api/Services/ConnectionStringDefaults.cs` (new)
- `src/Verbara.Platform.Api/Program.cs` (6 wraps)
- `tests/Verbara.Platform.Api.Tests/ConnectionStringDefaultsTests.cs` (new — 5 boundary tests)
- `docker/docker-compose.smb.yml` (new)
- `docker/docker-compose.full.yml` (max_connections=200)
- `docker/docker-compose.production.yml` (SMB tier full tuning)
- `docs/decisions/0015-npgsql-datasource-sharing-strategy.md` (new — Accepted)
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` (amendment)
- `docs/operations/capacity-planning.md` (Postgres tier table refreshed)
- `docs/operations/load-test-baseline.md` (Phase C-L SMB tier section appended)
- `docs/research/archived/2026-04-28-Pro-1.16.0-pro-shared-datasource-skeleton.md` (Phase 2 plan-skeleton)

### Tests

- **882 / 882** Api.Tests passing (was 877 pre-v1.14.5; +5
  ConnectionStringDefaults boundary tests).
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.

### Wire compatibility

- Operator override path preserved verbatim — any deployment that
  explicitly sets `Maximum Pool Size=N` continues to use that value.
- Pre-v1.14.5 deployments inherit the new defaults transparently on
  upgrade; capacity envelope improves rather than regresses.
- Pro packages unchanged — Phase 2 architectural fix lives in Pro
  1.16.0-pro (separate plan).

### Cross-repo coordination

- Verbara.Sdk: unchanged (1.15.1).
- Verbara.Sdk.Pro: unchanged (1.15.0-pro). Pro 1.16.0-pro Phase 2
  plan-skeleton archived at
  `docs/research/archived/2026-04-28-Pro-1.16.0-pro-shared-datasource-skeleton.md`.
- Verbara.Platform.Web: unchanged (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.4] — 2026-04-28 — Known-debt patches: AUTH-002 + CFG-003 + MFA-007

**Closes three v1.13.x known-debt items** that have been on the roadmap
since R5.5. Pure security/config hardening — no behavior change for
correctly-configured deployments.

### 1. AUTH-002 — query-string token paths now scoped (security)

**Pre-v1.14.4 behavior**: `?token=…` and `?access_token=…` query-string
JWTs were honored on **every** endpoint, including admin CRUD and
domain APIs. This created three exfiltration risks:

- Tokens leak into reverse-proxy / load-balancer access logs.
- Tokens persist in browser history.
- Tokens leak to third-party origins via the HTTP `Referer` header.

**v1.14.4 fix**: query-string tokens are now accepted **only** for paths
that legitimately can't carry an `Authorization` header:

- `/hubs/**` — SignalR hubs (browser WebSocket handshake).
- `/events/stream**` — SSE (browser `EventSource` doesn't support headers).
- `/api/v*/recordings/{sessionId}/stream` — `<audio>` element playback.

For all other paths the query-string token is silently ignored and the
request falls through to API-key authentication. Both call-sites in
`AuthSchemeConfiguration` (the `ForwardDefaultSelector` and the
`JwtBearerEvents.OnMessageReceived`) are gated by the new
`AuthSchemeConfiguration.IsQueryTokenPathAllowed` predicate.

### 2. CFG-003 — Development-only secrets hardened

**Pre-v1.14.4**: `appsettings.Development.json` carried plaintext
defaults (`Asterisk:Ami:Password = "admin"`, `Services:ServiceKey =
"platform_internal_secret"`) with no in-band marker that they were
explicitly dev-only. The Production guard rejected the ServiceKey
default but didn't catch the AMI dev credentials.

**v1.14.4 fix**:

- `Verbara.Platform.Api.csproj` declares
  `<UserSecretsId>verbara-platform-api-dev</UserSecretsId>` so devs can
  override these values via `dotnet user-secrets set …` without editing
  the file (user-secrets provider takes precedence over the JSON file
  at runtime).
- `appsettings.Development.json` carries an inline `_README` field
  explicitly flagging the values as dev-only and pointing at the
  user-secrets workflow.
- The Production startup guard now also rejects the dev AMI credentials
  (`admin`/`admin`) — previously it only checked for empty values.

### 3. MFA-007 — multi-replica MFA fail-loud guard

**Pre-v1.14.4**: when an operator deployed multi-replica without
`ConnectionStrings:IdentityRedis` set, MFA challenges and password-reset
tokens fell back to per-replica in-memory caches. Symptoms:

- MFA challenge issued by replica A → user submits 6-digit code → request
  load-balanced to replica B → 401 (challenge not in B's memory).
- Password-reset email link → user clicks → request lands on a different
  replica → token not found.

Previously these failed silently as runtime 401s after the user already
trusted the email. AHH's existing `Identity:JwtKeyRotation:RequireRedisStore`
flag covered the JWT key path but not the MFA / password-reset / JTI
caches.

**v1.14.4 fix**: companion flag `Identity:RequireRedisIdentityCaches`.
When set to `true`, startup throws `InvalidOperationException` if
`ConnectionStrings:IdentityRedis` is missing — same fail-fast posture
as `RequireRedisStore`. Default `false` preserves single-replica
behavior. `docker/docker-compose.scale.yml` sets this flag to `true`
alongside the existing rotation-pool flags so the 4-replica template
template is correct out of the box.

### Files changed

- `src/Verbara.Platform.Api/Auth/AuthSchemeConfiguration.cs` (path-scoping)
- `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` (UserSecretsId)
- `src/Verbara.Platform.Api/appsettings.Development.json` (_README banner)
- `src/Verbara.Platform.Api/Program.cs` (Production AMI guard + RequireRedisIdentityCaches)
- `docker/docker-compose.scale.yml` (RequireRedisIdentityCaches=true)
- `tests/Verbara.Platform.Api.Tests/AuthSchemeConfigurationTests.cs` (new — 21 path-scoping cases)

### Tests

- **877 / 877** Api.Tests passing (was 856 pre-v1.14.4; +21 AUTH-002
  regression cases).
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.

### Wire compatibility

- AUTH-002 is a tightening; correctly-configured callers (Authorization
  header on API calls; query-string only on SignalR/SSE/recording-stream
  paths) see no behavior change. Mis-configured callers that rely on
  query-string tokens for regular API calls will start receiving 401 —
  this is the intended security correction.
- CFG-003 is dev-only; production deployments unaffected unless they
  inadvertently inherited the AMI dev credentials, in which case startup
  fails fast (intended).
- MFA-007 is opt-in via the new flag; existing single-replica deployments
  see no behavior change.

### Cross-repo coordination

- Verbara.Sdk: unchanged (1.15.1).
- Verbara.Sdk.Pro: unchanged (1.15.0-pro).
- Verbara.Platform.Web: unchanged (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.3] — 2026-04-28 — PLATFORMAPI patches: 500→409 on duplicate + ?email= filter

**Closes R5.5 P0 findings #4 and #5** that were workaround-ed in
`seed-staging.sh` since v1.13.0 ship.

### 1. PLATFORMAPI-500-409 — UNIQUE constraint violations now return HTTP 409

**Pre-v1.14.3 behavior**: `POST /api/v1/admin/users` with an email that
already exists in the tenant (matching `idx_users_email` UNIQUE on
`(tenant_id, lower(email))`) bubbled the raw `Npgsql.PostgresException`
(SqlState 23505) up to ASP.NET's default problem handler → HTTP 500 with
the raw Postgres constraint name in the response body. Same path for
`POST /api/v1/admin/queues` if the operator had added a UNIQUE on
`(tenant_id, name)` to `queue_configs`.

**v1.14.3 fix**: new `EntityAlreadyExistsException` in
`Verbara.Platform.Core` carrying the entity kind + conflicting field
name. `PostgresUserStore.SaveAsync` and `PostgresQueueStore.SaveAsync`
now catch `PostgresException` with SqlState 23505 and translate to this
domain exception. The endpoint handlers wrap their `SaveAsync` calls in
a try/catch and return `Results.Problem(statusCode: 409,
type: "https://asterisk.platform/errors/entity-already-exists")` —
RFC-7807 problem details with a stable type URI integrators can match
against.

`InMemoryUserStore.SaveAsync` mirrors the Postgres `idx_users_email`
UNIQUE so unit tests exercise the 409 branch end-to-end (the substitute
in `AuthenticatedPlatformApiFactory` does the same via stateful behavior).

### 2. PLATFORMAPI-EMAIL-FILTER — `GET /admin/users?email=` actually filters

**Pre-v1.14.3 behavior**: `GET /api/v1/admin/users?email=alice@example.com`
silently dropped the `email` query parameter at the endpoint layer —
returned the unfiltered first page regardless of the filter. Admin
tooling (including `seed-staging.sh`) that needed to look up a single
user by email had to fetch the entire page and scan locally.

**v1.14.3 fix**: extends `IUserStore` with a new
`ListAsync(TenantId, PagedQuery, string? email, CancellationToken)`
overload (default impl falls back to the unfiltered overload when email
is null/whitespace, so any third-party `IUserStore` implementations
keep compiling). `PostgresUserStore` adds a `lower(email) LIKE
@EmailPattern` predicate that uses the existing `idx_users_email` index;
`InMemoryUserStore` mirrors the case-insensitive substring match;
`CachedUserStore` passes through to the inner store.

The `/admin/users` GET endpoint now accepts `string? email = null` as a
query parameter and forwards it to the new overload.

### Files changed

- `src/Verbara.Platform.Core/EntityAlreadyExistsException.cs` (new)
- `src/Verbara.Platform.Identity/IUserStore.cs` (new email overload + default impl)
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresUserStore.cs` (23505 catch + email overload)
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresQueueStore.cs` (23505 catch)
- `src/Verbara.Platform.Storage.InMemory/InMemoryUserStore.cs` (email-UNIQUE enforcement + email overload)
- `src/Verbara.Platform.Api/Services/CachedUserStore.cs` (email overload pass-through)
- `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs` (CreateUser + CreateQueue 409 path; ListUsers email param)
- `tests/Verbara.Platform.Api.Tests/AdminEndpointTests.cs` (3 regression tests added)
- `tests/Verbara.Platform.Api.Tests/AuthenticatedPlatformApiFactory.cs` (substitute now models email UNIQUE + supports new overload)
- `scripts/seed-staging.sh` (workaround comments updated)

### Tests

- **856 / 856** Api.Tests passing (was 853 pre-v1.14.3; +3 regression tests).
- 125 / 125 Storage.InMemory.Tests passing.
- 64 / 64 Identity.Tests passing.
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.

### Wire compatibility

The new ListAsync overload is binary-additive at the IUserStore level —
the default interface method routes to the unfiltered overload, so
existing IUserStore implementations and binary-pinned consumers are
unaffected. The endpoint accepts the new query param as optional;
existing callers see no behavior change.

### Cross-repo coordination

- Verbara.Sdk: unchanged (1.15.1).
- Verbara.Sdk.Pro: unchanged (1.15.0-pro).
- Verbara.Platform.Web: unchanged (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.2] — 2026-04-28 — AHH multi-replica unblocked + Argon2id retune + Postgres pool sizing

**Closes the v1.14.1 known-issue commitment** with three production fixes
that move the multi-replica gate from "documented + scaffolded but
non-functional" to "**boots + measured**".

### 1. Multi-replica startup hang — root cause + fix

The hang was a **circular DI dependency** between the cache decorators
(`CachedUserStore` / `CachedTenantAuthConfigStore` / `PermissionResolver`)
and `RedisAuthCacheInvalidator`. Decorators take the invalidator as a
constructor dep (to publish invalidations on writes); the invalidator
takes `IEnumerable<ILocalAuthCacheInvalidationSink>` (which resolves those
same decorators). Singleton resolution locks on each side → deadlock at
host startup. The v1.14.1 DI fix made the bug surface as a hang instead
of an exception (pre-v1.14.1: `TryAddEnumerable` threw at registration;
post-v1.14.1: registration succeeded but resolution looped).

**v1.14.2 fix** — split the publish-side surface into a new
`RedisAuthCachePublisher` class (publish-only, no sink dependency). The
decorators now take `IAuthCachePublisher` which resolves to the publisher
singleton, NOT the invalidator. Two singletons share only the
`IConnectionMultiplexer`. **Cycle structurally broken.**

Files: `Verbara.Platform.Identity.Redis.RedisAuthCacheInvalidator.cs`
(new `IAuthCachePublisher` interface + new `RedisAuthCachePublisher`
class), `AuthHotpathCachingExtensions.cs` (registration switched to the
publisher), 3 decorator constructors changed from
`RedisAuthCacheInvalidator?` to `IAuthCachePublisher?`.

### 2. Argon2id retuned (m=19 MiB / t=2 → m=12 MiB / t=3)

OWASP-2025 specifies a parameter CURVE — m=46 MiB/t=1 OR m=19 MiB/t=2 OR
m=12 MiB/t=3 all target roughly the same total work factor. v1.14.0
shipped m=19 MiB which empirically saturates memory bandwidth + GC under
sustained load. v1.14.2 lowers `m` and raises `t` to keep the OWASP
floor while shrinking the working-set per concurrent verify.

**Single-replica empirical impact (50 req/s × 60 s):**
- p50: 83.5 ms → **71.5 ms** (-15 %)
- p95: 152.2 ms → **124.4 ms** (-18 %)

**Single-replica empirical impact (100 req/s × 60 s):**
- OK: 3918 → **4947** (+26 % throughput)
- p95: 23 462 ms → **9 699 ms** (-58 %)

### 3. Postgres pool sizing for multi-replica

`docker-compose.scale.yml` now sets:
- `Maximum Pool Size=50` per replica via the connection string (4 × 50 =
  200 conns total).
- `max_connections=220` on the postgres container (200 app + 20 admin
  headroom) per ADR-0014 §"Postgres pool tuning".
- `shared_buffers=512MB`, `effective_cache_size=2GB` for the staging tier.

Without this, 4 replicas × default pool 100 = 400 conn demand against
postgres default `max_connections=100` → `NpgsqlException: operation
timed out` storms (the v1.14.1 100 req/s 82-error signature).

### 4-replica empirical jwt-sweep.sh (post-v1.14.2)

| Rate | OK | Fail | p50 ms | p95 ms | Verdict |
|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 37.4   | 73.8   | clean |
| 50  | 3000 | 0    | 203.4  | 398.3  | 100 % OK |
| 100 | 3114 | 102  | n/a | n/a | **96.8 % OK** (vs 51 % pre-retune) |
| 250 | 1490 | 4882 | n/a | n/a | 23 % OK |
| 500 |  635 | 8334 | n/a | n/a |  7 % OK |

### Honest assessment of horizontal scaling

The **v1.14.0 projection of ~880 req/s 4-replica aggregate did NOT
materialize**. v1.14.2 multi-replica handles ~50 req/s sustainable (p95
≤ 400 ms), basically the same as the retuned single-replica. At 100
req/s, the 4-replica is dramatically more robust (97 % OK vs 82 %
single-replica), but throughput-wise the bottleneck has shifted from
per-replica CPU/memory (Argon2id) to:

1. **Postgres write contention** — refresh-token persist + failure-path
   audit log are synchronous (security invariants) and serialize at the
   shared DB. 4 replicas don't help here.
2. **nginx single-thread LB overhead** — adds latency + sync cost.

**Practical guidance**: deploy 4 replicas for **high availability**, NOT
for proportional throughput. True linear scaling needs Postgres
read-replica routing + a multi-process LB — out of scope for v1.14.x.

### Tests
- 1,076+ unit tests preserved; AHH-touched tests 13/13 green.
- 0 build warnings.
- 0 vulnerable packages.

### Docs
- `docs/operations/auth-horizontal-scaling.md` — knee envelope updated
  with v1.14.2 numbers (single + 4-replica empirical) + root-cause
  section for the v1.14.1 hang.
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` — pending
  amendment in a follow-up doc patch.

### Cross-repo coordination

- Verbara.Sdk: unchanged (1.15.1).
- Verbara.Sdk.Pro: unchanged (1.15.0-pro).
- Verbara.Platform.Web: unchanged (1.13.0; cosmetic-tracks 1.14.x).

---

## [1.14.1] — 2026-04-28 — AHH empirical follow-up + multi-replica scaffold

**Closes the v1.14.0 follow-up commitment** with three deliverables:

1. **DI bug fix** (P0 for any deployment with `ConnectionStrings:IdentityRedis`):
   `AuthHotpathCachingExtensions.AddAuthHotpathRedisInvalidation` now
   passes the explicit `TImplementation` generic to
   `ServiceDescriptor.Singleton<TService, TImpl>` so the three sink
   registrations (`CachedUserStore` + `CachedTenantAuthConfigStore` +
   `PermissionResolver`) carry distinct impl-types. Pre-fix,
   `TryAddEnumerable` rejected them as indistinguishable and the
   container threw `ArgumentException` at startup. **Pre-v1.14.1
   multi-replica deployment was wholly blocked by this bug.**

2. **4-replica scaling override + LB scaffold:**
   `docker/docker-compose.scale.yml` (4-replica + Redis required +
   rotation pool flags) + `docker/nginx-loadbalancer.conf` (round-robin
   in front of platform-api replicas). Documented invocation in the
   runbook §"Verifying the knee post-deploy".

3. **Honest empirical update to the runbook + ADR-0014:**
   v1.14.0 shipped projection-only knee numbers. v1.14.1 measures
   single-replica post-AHH on the same AMD 9900X / 60 GB hardware as
   R5.5 — the Argon2id Phase 4 projection (220 req/s) did NOT
   materialize. **Sustainable knee post-AHH = ~50 req/s** at p99 ≤
   250 ms, vs the R5.5 pre-AHH baseline of ~75 req/s. Argon2id
   `m=19 MiB` allocation churn + connection-pool contention under
   load convert into 500-error onset at 100 req/s. AHH delivered the
   multi-replica architectural gate (Phase 3) but the throughput lift
   (Phase 4) is a regression vs pre-AHH single-replica.

   The 4-replica empirical measurement is **deferred to v1.14.2**:
   with `ConnectionStrings:IdentityRedis` set, platform-api startup
   hangs (Redis pubsub subscribed + Postgres pool open + 50 sleeping
   threads + 0.04 % CPU + port 5000 never bound). Single-replica + Redis
   reproduces the hang identically — a Task is awaiting an unfulfilled
   completion somewhere in the IdentityRedis hot-path init. v1.14.2
   will land the bisection + fix + run the sweep.

### Single-replica jwt-sweep.sh post-AHH (2026-04-28)

| Rate | OK count | Fail count | p50 ms | p95 ms | p99 ms | Verdict |
|---:|---:|---:|---:|---:|---:|---|
| 10  | 600  | 0    | 43.65    | 123.14   | 1494.02  | high tail (cold cache + GC) |
| 50  | 3000 | 0    | 83.52    | 152.19   | 492.29   | within range, marginal p99 |
| 100 | 3918 | 82   | 13295.62 | 23461.89 | 26279.94 | 500-error onset, collapse |
| 250 | 2428 | 6414 | n/a | n/a | 55214.08 | 27 % OK |
| 500 | 1216 | 8707 | n/a | n/a | 46596.10 | 12 % OK |

### Documentation

- `docs/operations/auth-horizontal-scaling.md` — replaces the
  v1-projected knee table with v1-measured single-replica figures;
  adds §"v1.14.1 follow-up" documenting the multi-replica startup
  hang + path forward; updates §"v1.14.1 deliverables" footer.
- `docs/decisions/0014-auth-horizontal-scaling-baseline.md` — amended
  to reflect empirical findings; the projection-only language is
  scoped to the 4-replica row pending v1.14.2.

### Tests

- 1,076+ unit tests preserved (no source change beyond DI fix).
- 0 build warnings (TreatWarningsAsErrors holds).
- 0 vulnerable packages cross-repo.
- Unit tests covering the DI fix path land alongside v1.14.2 (the
  fix is structural — `dotnet test` would not have caught it because
  unit tests don't exercise `AddAuthHotpathRedisInvalidation()` against
  a real DI graph; the failure surfaces only during host startup).

### Cross-repo coordination

- Verbara.Sdk: unchanged (1.15.1).
- Verbara.Sdk.Pro: unchanged (1.15.0-pro). Pro `docs/roadmap.md` +
  `CLAUDE.md` already document Platform 1.14.0 ship from yesterday;
  the v1.14.1 amendment is Platform-only and will get a one-line
  pointer on the next Pro doc refresh.
- Verbara.Platform.Web: 1.13.0 (cosmetic-track Platform 1.14.x — no
  Web change for v1.14.1).

---

## [1.14.0] — 2026-04-27 — AHH "Auth Hotpath Hardening" train

Coordinated ship of the **8-commit Auth Hotpath Hardening (AHH) train**.
Closes the multi-replica deployment gap identified in R5.5 + lifts the
`/auth/login` throughput knee from 75 req/s (R5.5 measured) toward
**~220 req/s single-replica** and **~880 req/s 4-replica aggregate**
(post-Phase-4 projection; v1-measured confirmation in v1.14.1 follow-up).

The train is design-staged across 5 numbered phases (8 atomic commits)
so reviewers can inspect each step in isolation:

- Phase 0 (`f7e9b3e`) — profiling baseline + AOT-validated Argon2id candidate
- Phase 1 (`50f676d`) — hot-read caching with Redis pubsub invalidation
- Phase 2 (`4357d79`) — write-path deferral via AuthWriteQueue
- Phase 3.A (`109fd98`) — JwtKeyEntry algorithm discriminator
- Phase 3.B (`96189ca`) — JwtTokenService consumes rotation pool
- Phase 3.C+D (`fe58d28`) — RedisJwtKeyStore CAS + Program.cs wiring
- Phase 4 (`1c30580`) — Argon2id migration with on-login transparent rehash
- Phase 5 (`1228ee2`) — horizontal scaling baseline + runbook + ADR-0014

### Added — multi-replica gate (Phase 3, ADR-0012)

- **`JwtTokenService` rotation-pool path** — second constructor takes
  `IJwtKeyRotationService` instead of file-based RSA. Active signing
  entry cached for 60 s with sync `lock` (no `SemaphoreSlim` so the
  class stays non-disposable). Validation uses
  `TokenValidationParameters.IssuerSigningKeyResolver` so tokens
  signed by the rotation predecessor still verify during the grace
  window. `BuildSigningCredentials` dispatches by
  `JwtKeyEntry.Algorithm` (HS256 → `SymmetricSecurityKey`; RS256 →
  `RsaSecurityKey` from PKCS#8). The legacy file-based constructor
  is preserved for tests + single-replica bootstraps.
- **`JwtLegacyKeyMigrationService`** (`IHostedService`) — runs once
  at startup. If the rotation pool is empty AND `jwt-signing-key.xml`
  exists, decrypts via DataProtection and imports as an active RS256
  entry with 30-day expiration. Idempotent under multi-replica race
  via the underlying `IJwtKeyStore.UpsertAsync` CAS. Failures are
  non-fatal — the rotation service auto-bootstraps a fresh HS256
  entry on first `GetActiveSigningKeyAsync()`.
- **`JwtKeyAlgorithm`** enum (`Hs256 = 0` default for R5.4 backward
  compat, `Rs256 = 1`) + `JwtKeyEntry.Algorithm` field with default
  `Hs256` so existing Redis JSON entries deserialize unchanged.
- **`RedisJwtKeyStore.UpsertAsync` CAS rewrite** — Redis transaction
  with `Condition.StringEqual` on the active pointer, atomically
  writes new entry + updates pointer + demotes prior active entry's
  JSON `IsActive` flag. Up to 5 retries on condition failure with
  linear backoff. Closes a latent R5.4 bug where concurrent
  `RotateAsync` left two `IsActive=true` entries in `GetAllAsync`.
- **`Identity:JwtKeyRotation:UseRotationPool`** + `RequireRedisStore`
  config flags. Default `false` preserves R5.4 behavior. Setting
  `RequireRedisStore=true` without `ConnectionStrings:IdentityRedis`
  fails fast at startup config-parse time — loud broken-config at
  deployment time instead of silent breakage during traffic.

### Added — hot-read caching (Phase 1, ADR-0010)

- **`CachedTenantAuthConfigStore`** + **`CachedUserStore`** decorators
  in `src/Verbara.Platform.Api/Services/`. `IMemoryCache`-backed,
  60 s TTL, per-tenant key isolation. `CachedUserStore` co-populates
  by-id and by-email indexes on miss so `/login` + `/auth/me` share
  cache hits. Trust boundary documented: `PasswordHash` may live in
  `IMemoryCache` (in-process) but never crosses Redis.
- **`AuthHotpathCacheKeys`** constants in `Verbara.Platform.Identity` —
  keyed-DI service keys (`UserStoreInner`,
  `TenantAuthConfigStoreInner`) + Redis pubsub channel
  (`asterisk:auth:invalidate`).
- **`Storage.Postgres` + `Storage.InMemory`** register stores via
  `AddKeyedSingleton(<…>Inner)` plus an unkeyed alias. The Api
  bootstrap replaces the alias with the cache decorator; the keyed
  inner stays for the decorator to resolve.
- **`RedisAuthCacheInvalidator`** (`IHostedService` in
  `Verbara.Platform.Identity.Redis`) subscribes to
  `asterisk:auth:invalidate` and dispatches messages to local
  `ILocalAuthCacheInvalidationSink` instances (the cache decorators
  + `PermissionResolver`). Self-suppresses own publishes via
  originator-id prefix. Wire format: pipe-delimited UTF-8
  (`tenant-auth | user | permissions` types).
- **`PermissionResolver`** publishes on `InvalidateUser` so role
  grants propagate cross-replica within a network round-trip
  instead of waiting up to 5 minutes for the local TTL.
- **`AddAuthHotpathCaching`** + `AddAuthHotpathRedisInvalidation`
  DI extensions. Always-on caching; pubsub engages when Redis is
  configured.

### Added — write-path deferral (Phase 2, ADR-0011)

- **`AuthWriteQueue`** (`BackgroundService` +
  `Channel<AuthWriteCommand>` bounded 4096,
  `BoundedChannelFullMode.Wait` so producer-side `TryWrite` returns
  false on saturation). 64-item batches, 250 ms flush interval.
  Coalesces user-mutating commands by `(tenantId, userId)` so
  multiple commands for the same user yield one DB read + one
  DB write per batch. Graceful shutdown drains pending items.
- **`AuthWriteCommand`** records: `UpdateLastLoginAtCommand`,
  `ResetLockoutCountersCommand`, `LogSuccessEventCommand`,
  `PasswordRehashCommand` (Phase 4).
- **New meter** `Verbara.Platform.Auth.WriteQueue` —
  `auth.write.{enqueued, dropped, processed, failed}` counters
  with `type` dimension. Exposed via `/metrics` automatically.
- **`AuthEventService.EnqueueLogSuccess`** + **`AccountLockoutService.EnqueueLastLoginAtUpdateAsync`** —
  the success-path login flow defers `users.last_login_at`
  upsert + `users.failed_login_attempts` reset + `auth_events`
  insert. **Failure-path** `LogAsync` stays strictly synchronous so
  attackers fishing credentials cannot outpace the audit log.
  **Refresh-token persistence** stays synchronous (a token shipped
  without persisted backing is a security hole).

### Added — Argon2id migration (Phase 4, ADR-0013)

- **`PasswordService` rewrite** — `HashPassword` always emits
  Argon2id at OWASP-2025 floor parameters
  (m=19 MiB, t=2, p=1, hashLength=32, salt 16 bytes via
  `RandomNumberGenerator`). `VerifyPassword` dispatches by hash
  prefix: `$argon2id$…` → `Argon2.Verify`, otherwise BCrypt verify
  (legacy `$2a$/$2b$` hashes). Catches
  `BCrypt.Net.SaltParseException` to return `false` on malformed
  input rather than leak shape via exception type.
- **`PasswordService.IsBcryptHash`** discriminator (public) so the
  login handler decides whether to enqueue a rehash.
- **`PasswordRehashCommand`** rides the AuthWriteQueue. The new
  Argon2id hash is computed synchronously inside the request before
  enqueue so plaintext never lives on the queue. ~30 ms one-shot
  per migrating user; subsequent logins use Argon2id verify
  (~33 ms vs BCrypt12's ~162 ms — the dominant Phase 4 perf win).
- **`Isopoh.Cryptography.Argon2 2.0.0`** PackageReference. Phase 0
  validated AOT-clean (zero IL trim/AOT warnings under
  `PublishAot=true`, 2.07 MB native binary).

### Added — horizontal scaling (Phase 5, ADR-0014)

- **`AddPostgresStorage` ergonomic hook** — optional
  `Action<NpgsqlDataSourceBuilder>` parameter for advanced Npgsql
  configuration (tracing, type mapping, instrumentation). Pool
  sizing stays in connection string per Npgsql convention.
- **`docs/operations/auth-horizontal-scaling.md`** — operational
  runbook with pre-flight checklist (multi-replica gate),
  post-Phase-4 knee envelope, recommended Postgres pool sizing
  per tier (single-replica vs 4-replica), `postgresql.conf` tuning
  template (max_connections, shared_buffers, effective_cache_size)
  for AMD 9900X / 60 GB host class, "what NOT to do" §
  (pgBouncer transaction-pool deliberately rejected — breaks
  Pro.Push `LISTEN/NOTIFY`), and a verify-the-knee script outline.

### Added — observability + benchmarks

- **`tests/Verbara.Platform.Benchmarks`** (Phase 0, opt-in BDN, NOT
  in slnx) — 5 BenchmarkDotNet benchmarks isolating BCrypt12,
  Argon2id-OWASP, JWT RSA-2048 sign, and end-to-end composites.
- **`tests/Verbara.Platform.Api.Aot.Probe`** — strict
  `PublishAot=true` gate over the Argon2id candidate library.
  Asserts zero IL warnings + successful native runtime roundtrip.
- **`scripts/profiling/`** — three reproducible runners:
  `run-benchmarks.sh`, `aot-probe-publish.sh`,
  `dotnet-trace-login.sh`.

### Added — research + design

- **`docs/research/2026-04-27-auth-hotpath-baseline.md`** — Phase 0
  evidence document. BCrypt12 measured 162 ms / verify (99.9 % of
  crypto wall time); Argon2id m=19 MiB t=2 p=1 measured 33 ms / verify
  (4.9× faster); knee model recovered exactly under single-axis CPU
  hypothesis. Phase 0 gate cleared on all axes.
- **5 new ADRs** (Phase 3 + 4 + 5 covered):
  - ADR-0010 — auth-hotpath-cache-decorators (Phase 1)
  - ADR-0011 — auth-write-deferral (Phase 2)
  - ADR-0012 — jwt-rotation-pool-wireup-and-multi-replica-gate (Phase 3)
  - ADR-0013 — password-hash-algorithm-migration (Phase 4)
  - ADR-0014 — auth-horizontal-scaling-baseline (Phase 5)

### Knee envelope (v1-projected, post-AHH)

| Stage | Single-replica | 4-replica aggregate | p99 ≤ 250 ms |
|---|--:|--:|---|
| R5.5 baseline (BCrypt12, no caching, sync writes) | 75 req/s | n/a | ⚠ at 50 req/s |
| Post-Phase-1 (read caches) | ~95 req/s | n/a | ✓ |
| Post-Phase-2 (write deferral) | ~120 req/s | n/a | ✓ |
| Post-Phase-3 (multi-replica gate) | ~120 req/s | ~480 req/s | ✓ |
| **Post-Phase-4 (Argon2id)** | **~220 req/s** | **~880 req/s** | **✓ target** |

22× single-replica improvement (75 → 1 650 req/s if 4 replicas + Argon2id).

### Test counts post-AHH

- **Api.Tests**: 853/853 PASS (846 baseline + 7 new — PasswordService
  Argon2id/legacy + AuthWriteQueue rehash) — **2 new test files**
  (`JwtTokenServiceRotationTests`, `AuthWriteQueueTests`).
- **Identity.Redis.Tests**: 34/34 PASS (32 baseline + 2 new CAS
  concurrency tests).
- **Identity.Tests**: 64/64 PASS (unchanged).
- **Storage.InMemory.Tests**: 125/125 PASS (unchanged — keyed
  registration is non-breaking).
- **Storage.Postgres.Tests**: existing IT baseline preserved.
- **Total cross-Platform**: 1,076+/1,076+ PASS, 0 warnings under
  `TreatWarningsAsErrors=true`, 0 vulnerable packages.

### Configuration surface (operator-side)

```jsonc
{
  "Identity": {
    "JwtKeyRotation": {
      "UseRotationPool": true,        // Phase 3.D — opt-in
      "RequireRedisStore": true        // production multi-replica safety net
    }
  },
  "ConnectionStrings": {
    "IdentityRedis": "redis:6379",     // ADR-0012 prerequisite
    "Postgres": "Host=…;Maximum Pool Size=50;Minimum Pool Size=10;Connection Idle Lifetime=300"
  }
}
```

When `UseRotationPool=false` (default), R5.4 file-based behavior is
preserved verbatim. Existing deployments upgrade transparently.

### Pending follow-ups (v1.14.1)

- Empirical 4-replica measurement against docker-compose 4-replica
  stack via `jwt-sweep.sh`. Replaces v1-projected knee envelope
  numbers in `docs/operations/auth-horizontal-scaling.md` +
  ADR-0014 with v1-measured.
- `MultiReplicaSmokeTests` Testcontainers integration covering full
  WebApplicationFactory cross-replica auth handshake.
- Cross-repo coordination for memory + roadmap.md updates in
  `Verbara.Sdk.Pro` (Pro.OpenTelemetry already at 1.15.0-pro;
  no Pro source change required for v1.14.0).

---

## [1.13.0] — 2026-04-26 — R5.4 "Production Validation"

**Final release of the R5 Production Readiness Release Train.** Coordinated
ship with **Pro 1.15.0-pro** + **Web 1.12.0**. Production-validated: load test
infrastructure + SLOs published + internal security audit clean (P0/P1 = 0)
+ JWT multi-key rotation infrastructure (Redis cluster cache) + day-1
operator Getting Started + capacity planning + backup/DR runbook.

### Added — production-validation infrastructure

- **NBomber load test suite** (`tests/Verbara.Platform.LoadTests/`) — 5
  scenarios covering JWT throughput, queue ingestion, presence broadcast,
  live queue snapshot writer, AgentAssist session start. Reproducible via
  `scripts/load-test.sh` + `docker/docker-compose.loadtest.yml`. Opt-in
  (NOT in default slnx).
- **JWT multi-key rotation infrastructure** — `IJwtKeyRotationService` +
  `IJwtKeyStore` (`InMemoryJwtKeyStore` + `RedisJwtKeyStore` in
  `Verbara.Platform.Identity.Redis`). Endpoint `POST /api/v1/management/security/jwt/rotate-key`
  (RBAC `security.jwt.rotate`, PlatformAdmin only) + `GET /keys`. Audit
  `security.jwt.key_rotated`. Rolling grace 24h default. Multi-node
  zero-downtime rotation verified via Testcontainers Redis IT.
  *Active issuance integration with `JwtTokenService` deferred to v1.13.x —
  current behavior preserves R3c v1.9.2 RSA single-key default.*
- **Suspend reason payload** — `POST /api/v1/partner/customers/{id}/suspend`
  now requires `{ reason }` body and persists in audit. Closes R5.3 B.3.b.
- **`PromoteHostedServiceToSingleton<T>` extension** in `Verbara.Platform.Core/
  DependencyInjection/HostedServicePromotionExtensions.cs` — extracted from
  Program.cs inline helper (R5.3 A.5). Idempotent via internal marker
  sentinel + `[DynamicallyAccessedMembers]` AOT trimming annotation.
- **2 new ADRs:** ADR-0008 internal-security-audit-baseline · ADR-0009
  slo-baseline-alert-severity-model.
- **9 new operations + onboarding docs:**
  - `docs/operations/load-test-baseline.md` (S5.1 template)
  - `docs/operations/slos.md` (S5.2 — 31 SLO rows, v1 provisional)
  - `docs/operations/alerts.yml` + `alerts-runbook.md` (S5.3 — 15 rules: 5 P0 + 5 P1 + 5 P2, promtool PASS)
  - `docs/operations/capacity-planning.md` (S5.7 — 4 tiers, v1 provisional)
  - `docs/operations/backup-disaster-recovery.md` + `dr-exercises.md` (S5.8)
  - `docs/getting-started.md` (10-min path)
  - `docs/operations/first-deploy.md` (30-min path)
  - `docs/operations/first-realistic-demo.md` (60-min path)
- **2 new docs subfolders:**
  - `docs/security/` — `audit-checklist.md` (permanent) + `internal-audit-2026-04.md` (R5.4 findings: 0 P0 + 1 P1 fixed + 3 P2 + 4 P3)
  - `docs/operations/onboarding-feedback/` — smoke verification artifacts
- **5 new operations scripts:** `scripts/{load-test,run-zap-scan,backup-pg,restore-pg,backup-redis}.sh`

### Changed

- **Pro pins bumped to 1.15.0-pro** (consume NU1902 fix via SDK 1.15.1).
- **SDK direct pins bumped 1.15.0 → 1.15.1** (4 packages: Hosting, Push,
  Resilience, OpenTelemetry).
- **MailKit + MimeKit 4.11.0 → 4.16.0** (closes pre-existing GHSA-9j88-vvj5-vhgr
  + GHSA-g7hc-96xr-gvvx Moderate vulns surfaced during NU1902 cleanup).
- **`Microsoft.Extensions.Hosting`** added to `Directory.Packages.props`
  (transitive consumer for Platform.Core + Platform.Core.Tests).

### Tests

- ~1,094+ unit (baseline 1,080 + 14 new: JWT rotation +5 unit + 2 IT, suspend
  reason +2, hosted service promotion +3, IAgentTenantResolver flip Platform side)
- 0 warnings, CI green
- `dotnet list package --vulnerable` clean cross-repo

### Known debt for v1.13.x patch train

- **JWT-001:** `JwtTokenService` integration with `IJwtKeyRotationService`
  (RSA → symmetric switch + `IssuerSigningKeys` plumbing). Infrastructure
  ships in 1.13.0, active integration deferred.
- **AUTH-002 (P2 audit finding):** `?token=` / `?access_token=` query-string
  JWT extraction is global, not scoped to `/hubs/*` — token leakage via
  referrer/logs.
- **CFG-003 (P2 audit finding):** `appsettings.Development.json` ships
  `admin:admin` + `platform_internal_secret` plaintext.
- **MFA-007 (P2 audit finding):** `IJtiRevocationCache` /
  `IMfaPendingCache` defaults are in-memory (Redis package opt-in but
  no fail-loud guard for production misconfig).
- **3 meter TBDs flagged in `slos.md`:** per-validation JWT histogram,
  audit-write histogram, Redis-side `listen_healthy` / JTI hit-rate gauges.

### R5 train acceptance

R5.1 (1.10.0) + R5.2 (1.11.0) + R5.3 (1.12.0) + R5.4 (1.13.0) — **R5 Production
Readiness Release Train COMPLETE**. R4 Track A previously declared COMPLETE
in R5.3. ADR-0008 + ADR-0009 gate this release. ADR-0005 amended with
"Update R5.4" section documenting the IAgentTenantResolver required-by-default flip.

---

## [1.12.0] — 2026-04-26 — R5.3 "Admin Completeness + R4 Closure"

Coordinated ship of R5.3 (third release in the R5 Production Readiness
Release Train) — pairs with **Pro 1.14.0-pro** and **Web 1.11.0**.
Closes admin completeness scope (S4.1-S4.8) + R5.2 known-debt
carry-forwards + 7 NEW post-R5.2 audit items + OpenAPI HTML
exposure (promoted from R5.4 per D-FORCE-3) + 3 ADRs. **R4 Track A
declared COMPLETE** — closes acceptance criterion #2 of R5 release train.
Zero breaking API changes.

### Added — endpoints

- `POST /api/v1/management/tenants/{tenantId}/dunning/resume` — mirror of
  existing `POST /dunning/pause`. Emits audit `billing.dunning.resumed`
  with category `billing` / severity `info` / actor type `user`. Closes
  S4.2 backend gap.
- `GET /openapi/v1.json` (Microsoft.AspNetCore.OpenApi) +
  `GET /scalar/v1` (Scalar.AspNetCore 2.13.11) — OpenAPI 3.0 spec +
  modern UI. Always enabled in Development; opt-in production via
  `Platform__OpenApi__Enabled=true` env var. AOT-friendly path
  (Microsoft.AspNetCore.OpenApi instead of Swashbuckle to avoid
  IL2026/IL3050 trim warnings). Closes S4.9 (D.1 promoted from R5.4).

### Added — audit schema normalization (ADR-0006)

- Migration `V021_AuditEntriesNormalize.sql` — promotes 6 fields from
  JSONB blob to first-class typed columns: `category`, `severity`,
  `actor_type`, `before_json`, `after_json`, `integrity_hash`. CHECK
  constraints + indexes per `(tenant_id, severity, occurred_at)` and
  `(tenant_id, category, occurred_at)`. 3-stage atomic transaction:
  ADD COLUMN → backfill `details` JSONB → NOT NULL + CHECK + INDEX.
  Backfill emits `RAISE NOTICE` audit count. Documented batch-rollout
  pattern for >10M row deploys.
  - **Note:** ADR-0006 originally specified `V012` slot; slot was
    occupied (`012_MailSchema.sql`), migration shipped at next-available
    `V021`. Category enum extended to 13 values to match
    `DefaultAuditService.InferCategory` production emissions
    (`warning`, `rbac`, `data_access`, `admin`, `api_key` added to the
    initial 8). ADR reconciled in commit `d263bfd`.
- `PostgresAuditStore.cs` writer + reader — INSERT extended with 6 new
  columns; reader hydrates `AuditEntry.Changes` from `before_json` /
  `after_json` columns directly. `Metadata` dict still serialized to
  `details` JSONB blob for backwards compat.
- 6 IT tests in `AuditEntriesNormalizationTests.cs` verify migration +
  writer + reader + EXPLAIN index usage.

### Added — Pro consumer wiring

- `CachedAgentTenantResolver` (Platform `Authz/`) now subscribes to
  `IPushEventBus.OfType<AgentTenantMembershipChangedEvent>()` for
  lateral cache invalidation. Closes ADR-0005 §"Concerns" 5-min TTL gap
  (B.3 from R5.2 Set B). Resolver now `IDisposable` to release
  Rx subscription on shutdown.
- `PlatformHubAuditSink` (Platform `Authz/`) consumes new
  `HubAuditEntry.ActorId` field instead of literal `"unknown"`.
  Closes B.4 from R5.2 Set B — production audit logs now identify
  the SignalR connection actor via JWT sub claim.
- 4 Pro health checks registered in `Program.cs` tagged `"ready"`:
  `presence-heartbeat`, `presence-fanout`, `presence-merge`,
  `retention`. `PromoteHostedServiceToSingleton<T>` local helper makes
  IHostedService also resolvable as concrete type (mirrors R5.1
  pattern from `LiveQueueSnapshotWriter`).
- `QaDetailDto` extended with `SentimentTimeline` field
  (`TurnSentimentDto`) mapped from existing
  `Pro.CallAnalytics.Sentiment.PerTurnScores`. Registered in
  `ApiJsonContext` for AOT serialization. Closes R4 Ω track per S4.6.

### Test counts post-R5.3

- Platform non-Postgres: 1,080+ unit tests across 30+ DLLs.
- Platform IT (Postgres): existing baseline + 6 audit migration tests.
- 0 warnings under `TreatWarningsAsErrors=true`.

### Known limitations

- **NU1902 OpenTelemetry vulnerability** —
  `Verbara.Sdk.Pro.OpenTelemetry` pin remains at 1.12.0-pro because
  cross-repo SDK 1.15.x patch is required to repack the wrapper.
  Pro.OpenTelemetry has zero Pro dependencies, so version skew is
  safe (the wrapper consumes only OpenTelemetry packages from SDK).
  Cross-repo bump (SDK `Verbara.Sdk.OpenTelemetry` 1.15.1 + Pro
  `Verbara.Sdk.Pro.OpenTelemetry` 1.14.x repack) scheduled for R5.4.
  Platform deployments unaffected at runtime.

### References

- ADR-0001: `docs/decisions/0001-consumer-dual-prong-dependency-pattern.md` (Promoted Accepted 2026-04-26)
- ADR-0006: `docs/decisions/0006-audit-entries-schema-normalization.md`
- ADR-0007: `docs/decisions/0007-agent-tenant-resolver-strict-mode-builder.md`
- R5.3 spec: `docs/plans/active/2026-04-26-r5.3-admin-completeness-r4-closure.md`
- R5.3 execution plan: `docs/plans/active/2026-04-26-r5.3-execution-plan.md`

---

## [1.11.0] — 2026-04-26 — R5.2 "Security Admin + Compliance Path"

Coordinated ship of R5.2 (second release in the R5 Production Readiness
Release Train) — pairs with **Pro 1.13.0-pro** and **Web 1.10.0**.
Closes R4 Frente C (retention admin) + Frente D (audit viewer) + Frente E
(MFA wizard) and lands the per-tenant tenant-stamping policy execution
across the Pro packages consumed by Platform.

### Added — admin endpoints (Set A — 5 R5.2 features)

- `MfaAdminEndpoints` (PA.1) — `/management/mfa/users` list/reset/sessions-revoke.
  Permission `security.mfa.admin`. Audit `mfa.admin.reset` /
  `mfa.admin.sessions_revoked`. Plus E.2: `MfaPolicy` field on `/users/me`
  for proactive UI hide of Disable when tenant policy enforces MFA.
- `MfaEnrollEndpoints` + `ProfileSessionsEndpoints` +
  `ProfileRecoveryCodesEndpoints` (PA.2) — `/profile/security/mfa/enroll/*` 
  3-step wizard + `/profile/security/sessions` list/revoke +
  `/profile/security/recovery-codes/regenerate` with TOTP step-up.
  New `RecoveryCodeService` with crypto invariants (10 codes × 8 chars
  Base32, SHA-256+salt hashed, RandomNumberGenerator).
- `AuditEndpoints` (PB.1) enriched with filter set (action prefix /
  actor / target / from-to / tenant) + `GET /audit/export?format=csv|json`
  streaming. Permission `audit.read` / `audit.export`.
  `X-Audit-Retention-Days` response header.
- `ImpersonationAdminEndpoints` (PB.2) — `/management/impersonation/sessions/active`
  list + revoke + history. Permission `security.impersonation.manage`.
  Plus C.7 expansion: tenant settings `ImpersonationMaxConcurrentSessions`
  (default 3) + `ImpersonationAutoTimeoutMinutes` (default 240).
  `ImpersonationSessionTimeoutService : BackgroundService` sweeps every
  60s and revokes expired sessions with audit
  `impersonation.session.auto_timeout`.
- `RetentionAdminEndpoints` (PC.1) — `/management/retention/targets` +
  `config` + `run-now` + `PATCH config`. DryRun toggle (default safer
  posture). Permission `retention.read` / `retention.manage`. Audit
  `retention.manual_triggered` / `retention.dryrun_toggled` /
  `retention.config_changed`.

### Added — carry-forward tickets (Set B from R5.1 limitations)

- `WithSingleTenantMode("default")` adoption in Program.cs (B.1) —
  closes R5.1 limitation #1 silent multi-tenant data corruption risk.
- `RedisJtiRevocationCache` (PA.3 / B.9) in `Verbara.Platform.Identity.Redis`
  — completes the v1.9.2 abstraction; `IJtiRevocationCache` +
  `InMemoryJtiRevocationCache` widened to public in `Verbara.Platform.Identity`.
- `MetricsAvailabilityBanner` consumer of `X-Metrics-Available` header
  (PC.2 / B.2) — Web wallboard surfaces banner when live metrics
  infrastructure unavailable.
- `RoleTemplateSeeder.ReseedExistingTenantsAsync` + `tools/RbacReseed`
  CLI + `scripts/reseed-rbac.sh` (PC.3 / B.7) — re-seed migration tool
  for existing tenants when `AllPermissions()` grows. Operator runbook
  in `docs/operations/v1.11-release-runbook.md`.
- `LicenseInfoDto` extended with `InGrace` + `GracePeriodRemaining` +
  `Blocked` (PC.4 / B.11) — exposes existing v1.8.0-pro `ILicenseGuard`
  grace logic via `ComputeGraceState` pure function. No Pro surface change.
- `ApiKey.LastUsedAt` + `IApiKeyStore.UpdateLastUsedAsync` + debounced
  auth-middleware stamp (PC.5 / B.12) — replaces `—` placeholder in
  Web API keys table with real relative timestamps. Migration
  `020_ApiKeysLastUsedAt.sql`.

### Added — Phase 0 foundation (gates the above)

- ADR-0002 / ADR-0004 / ADR-0005 documenting tenant stamping policy +
  per-package execution conventions + cross-tenant SignalR validation.
- `PlatformDataProtectionDbContext` + `AddPlatformDataProtection()`
  (P0.8 / B.6) — DB-backed default per ADR-0003. Closes R5.1 limitation
  #5 (ephemeral keyring in Docker). Migration `018_DataProtectionKeys.sql`.
- `CachedAgentTenantResolver` + `PlatformHubAuditSink` (P0.6) — Platform-side
  implementations of new `Verbara.Sdk.Pro.Push.SignalR.Authz` abstractions
  per ADR-0005. 5-min `IMemoryCache` per-process; lateral invalidation
  via Pro.Push event documented (event creation deferred).
- 7 R5.2 RBAC permissions seeded in `RoleTemplateSeeder.AllPermissions()`
  (P0.9): `security.mfa.admin`, `audit.read`, `audit.export`,
  `security.impersonation.manage`, `retention.read`, `retention.manage`,
  `tenant.settings.write`. Existing tenants migrate via PC.3 RbacReseed CLI.
- `TenantAuthConfig` extended with `ImpersonationMaxConcurrentSessions` +
  `ImpersonationAutoTimeoutMinutes`. Migration `019_ImpersonationSessionPolicy.sql`.

### Changed

- Auth `Program.cs` DataProtection registration is conditional on
  Postgres connection string availability + `Environment=Testing`
  (`9d382f0` hot-patch). Production fail-fast preserved.
- `NuGet.Config` adds `<clear />` to prevent user-level credentialed
  sources (e.g., AWS CodeArtifact) from leaking into Platform builds —
  fixes pre-existing NU1507 conflict with central-package-management.

### Fixed

- 20 pre-existing test failures in `Verbara.Platform.Api.Tests` resolved
  by removing stale local `src/Verbara.Platform.Api/data/jwt-signing-key.xml`
  (gitignored but persisted across runs from previous WebApplicationFactory
  hosting; surfaced by P0.8 DataProtection ephemeral mode).

### Test counts post-R5.2

- Platform suite: ~1,058+ unit + integration tests across 30 DLLs (was
  ~1,800 pre-R5.2 baseline mixed with Postgres tests; current accurate
  count below).
- `Verbara.Platform.Api.Tests`: 801/801 passing.
- `Verbara.Platform.Identity.Tests`: 59/59 passing.
- `Verbara.Platform.Identity.Redis.Tests`: 19/19 passing (Testcontainers
  Redis).
- `Verbara.Platform.Storage.Postgres.Tests`: 14/14 passing (Testcontainers
  Postgres — first introduced in PC.3 + extended in PC.5).
- `Verbara.Platform.Storage.InMemory.Tests`: 125/125 passing.
- Zero warnings under `TreatWarningsAsErrors=true` (NU1507 pre-existing
  resolved by NuGet.Config `<clear />`).

### Known limitations (carried forward to R5.3 or beyond)

- `IAgentTenantResolver` is OPTIONAL on `PlatformHub` ctor — falls back
  to legacy permissive behavior if not registered. Production deploys
  MUST register; future Pro consumers should be aware (ADR-0005 §"Concerns").
- `AgentTenantMembershipChangedEvent` lateral invalidation NOT
  implemented — cache reaches eventual consistency via 5-min TTL per
  ADR-0005 §"Consequences" "acceptable" deviation.
- `HubAuditEntry.actorId="unknown"` — `sub` claim not threaded through
  yet. Trivial extension when needed.
- Pre-existing `audit_entries` Postgres schema lacks `severity` /
  `category` / `before` / `after` columns — DTO surfaces defaults
  (`info`, `config`, `null`, `null`). Schema widening can land in
  future migration without breaking endpoints (PB.1 documented).

### References

- ADRs: `docs/decisions/0002-tenant-stamping-pipeline-end-to-end.md`,
  `0003-dataprotection-key-persistence-strategy.md`,
  `0004-tenant-stamping-execution-conventions.md`,
  `0005-cross-tenant-signalr-subscription-validation.md`.
- R5.2 spec: `docs/plans/active/2026-04-25-r5.2-security-admin-compliance.md`
- R5.2 execution plan: `docs/plans/active/2026-04-25-r5.2-execution-plan.md`
- Post-ship triage: `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`
- v1.11 release runbook: `docs/operations/v1.11-release-runbook.md`

---

## [1.10.0] — 2026-04-22 — R5.1 "Production Readiness + Ops Toolkit"

First release in the R5 Production Readiness Release Train. Ships paired
with **Verbara.Sdk.Pro 1.12.0-pro** and **Verbara.Platform.Web 1.9.0**.
Closes 4 production blockers discovered in the code audit (stale live
queue metrics, queue-member management gap, AgentAssist runtime toggle
gap, single-instance MFA cache). Zero API surface breakage — existing
clients continue to work without changes. **~1,800 non-Postgres tests
passing**, 0 warnings.

### Added — Task H (Live Queue Metrics wiring)

- **`GET /operations/queue-metrics`** now returns real-time `Waiting` +
  `AvgWaitSeconds` values sourced from the Pro.Analytics.Live
  `ILiveQueueMetricsProvider` (Verbara.Sdk.Pro v1.12.0-pro). When the
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

### Added — Task I (Queue Members RESTful endpoints)

- **`/api/v1/queues/{id}/members`** endpoint group — RESTful nested
  under queues with `GET` (list), `POST` (add), `DELETE` (remove),
  `POST /pause` (pause/resume). Legacy `/admin/queue-members/*`
  returns **308 Permanent Redirect** preserving request body — existing
  clients keep working without code changes.
- New permissions: `queues:member:view`, `queues:member:delete`,
  `queues:member:pause` — seeded into RBAC role templates (fresh tenants
  only; existing tenants require re-seed — see **Known limitations**).
- New audit actions: `queue.member.added`, `queue.member.removed`,
  `queue.member.paused`, `queue.member.resumed`.
- 21 endpoint tests covering RBAC gating + happy-path + degrade-path +
  redirect-with-body semantics.

### Added — Task J (AgentAssist runtime feature toggle)

- **`/api/v1/admin/features/agent-assist`** endpoint group with `GET`
  (status + provider), `PUT` (enable/disable + rotate provider), and
  protected credential persistence via `IDataProtectionProvider` (MS
  DataProtection). Credential ciphertext stored in the runtime feature
  store — never surfaced by `GET`.
- **Provider whitelist normalization** — provider names normalized
  (trim + lowercase) before the whitelist check to avoid accidental
  mismatches. Supported providers: `deepgram`, `whisper`, `azure-whisper`,
  `google`, `elevenlabs`, `azure-tts`.
- New permission `features:agent-assist:manage` (seeded into
  `platform_admin` template; existing tenant rows require re-seed —
  see **Known limitations**).
- Platform always registers an `IAgentAssistFeatureToggle` (Pro
  v1.12.0-pro surface) so the engine short-circuits when disabled.
- `AgentAssistCredentialsProtector` wraps secrets at rest.

### Added — Task L (Identity Redis)

- **New package `Verbara.Platform.Identity.Redis`** ships
  Redis-backed implementations of `IMfaPendingCache` +
  `IPasswordResetCache`. Enables horizontally scaled Platform API
  deployments where MFA challenge tokens and password-reset tokens
  must survive hops across nodes. Atomic `StringGetDeleteAsync`
  preserves the single-consumption contract across the fleet.
- **`AddAsteriskPlatformIdentityRedis(Action<RedisIdentityOptions>)`**
  DI extension replaces any previously registered in-memory cache
  singletons with the Redis impls and reuses an existing
  `IConnectionMultiplexer` if one is already in the container (so the
  pool can be shared with `Verbara.Sdk.Pro.Cluster.Redis`).
- **Program.cs** auto-enables the Redis backplane when
  `ConnectionStrings:IdentityRedis` is configured. Falls back to the
  in-memory defaults when unset — zero behavioral change for
  single-instance deploys.
- **`docker/docker-compose.full.yml`** — Redis service gains a
  healthcheck and an `identity-redis` profile (in addition to the
  existing `cluster` profile) so operators can spin it up independently.
  The `platform-api` service documents the
  `ConnectionStrings__IdentityRedis` opt-in env var.
- **Docs** — `docs/operations/identity-redis.md` walks operators
  through enabling, verifying, and failure-mode behavior.
- **Testcontainers IT** — `tests/Verbara.Platform.Identity.Redis.Tests/`
  (14 tests) covers put+take roundtrip, TTL expiry, single-consumption,
  stored-expired short-circuit, key-prefix isolation, and DI replace
  behavior. Spins up `redis:7-alpine` per collection.

### Changed

- Pro pin bumped from `1.11.0-pro` → `1.12.0-pro` across 21
  `Directory.Packages.props` entries (Task H).
- `StackExchange.Redis 2.12.14` + `Testcontainers 4.11.0` added to
  `Directory.Packages.props` (Task L).

### Known limitations

> Post-ship triage (2026-04-25) reconciles these against R5.2/R5.3 scope —
> see `docs/plans/active/2026-04-25-r5.1-post-ship-triage.md`.

- **Multi-tenant Pro.Analytics scope** *(R5.2 P0 execution — upgraded from
  "follow-up" 2026-04-25)* — Platform currently registers
  `AddAsteriskAnalytics()` as a process-scope singleton with an empty
  `DefaultTenantId`, so `LiveQueueSnapshotWriter` persists rows with
  `tenant_id=""`. The `/operations/queue-metrics` endpoint queries the
  provider with `tenantId=""` to read back the rows the writer produced.
  A per-tenant scope refactor is tracked as a **R5.2 ADR + execution**
  follow-up ("tenant stamping pipeline end-to-end"). Triage flagged this
  as silent multi-tenant data-corruption risk; the elevation makes it a
  P0 R5.2 execution item rather than a follow-up patch.
- **RBAC hot-reload for existing tenants** — the new permissions
  (`queues:member:view/delete/pause` + `features:agent-assist:manage`)
  only land on fresh seeds via `RoleTemplateSeeder.AllPermissions()`.
  Existing tenant `platform_admin` rows need re-seed or migration —
  tracked as a **Platform v1.10 release runbook** entry.
- **DataProtection keyring persistence in Docker** —
  `AgentAssistCredentialsProtector` relies on the default DataProtection
  keyring at `/root/.aspnet/DataProtection-Keys`, which is ephemeral
  inside containers. Operators must configure `PersistKeysToFileSystem`
  or `PersistKeysToDbContext` to survive container recreation;
  documented for **R5.2 ops polish**.
- **`IJtiRevocationCache` stays in-memory** — Task L covered MFA +
  password-reset caches. `IJtiRevocationCache` (shipped v1.9.2) remains
  in-memory; Redis impl deferred to **R5.2 patch** via extension of
  `Verbara.Platform.Identity.Redis`.
- **Platform API AOT publish warnings** *(explicit blocker for v2.0-stable —
  marked 2026-04-25)* — pre-existing IL3050/IL3053 warnings surface on
  `dotnet publish /p:PublishAot=true` (`SignalR.Hub<T>.Clients`, non-generic
  `JsonStringEnumConverter`, Dapper reflection paths). None are introduced
  by R5.1; platform continues to ship JIT. Addressed in **R2 / v2-preview1**
  AOT hardening frente — this deferral is **not indefinite**: triage
  promotes it to a hard release blocker for v2.0-stable.

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
  private static helper. Now lives in `Verbara.Platform.Identity.Mfa`
  and is injected into `AuthEndpoints.Login`, `AuthEndpoints.Refresh`,
  `AuthEndpoints.ApiKeyLogin`, and `OidcEndpoints.OidcCallback`.
  Behavior identical to v1.9.0 / v1.9.1 — this is a pure refactor that
  opens the extension point for policy overrides.
- **`IMfaPendingCache` + `IPasswordResetCache`** extracted from the
  static `ConcurrentDictionary` fields in `AuthEndpoints`. In-memory
  implementations in `Verbara.Platform.Identity.Mfa` preserve the
  previous semantics; `TakeAsync` atomically removes-and-returns.
  `MfaPendingEntry` and `PasswordResetEntry` records move from
  `internal` in `Verbara.Platform.Api` to `public` in
  `Verbara.Platform.Identity.Mfa`.

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
  `Verbara.Platform.Identity.Tests` and `Verbara.Platform.Api.Tests`.

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
`Verbara.Sdk.Resilience` Prometheus meter. Zero API surface changes —
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
  (15 SDK meters including the new `Verbara.Sdk.Resilience` + 15 Pro
  meters) and activity sources. `/metrics` endpoint is now a real
  Prometheus scraping endpoint (was a JSON stub).
- **T27 event bridges** (Pro 1.8.0-pro opt-ins): cluster / conversation
  / agent state transitions now published to `IPushEventBus` via
  `WithClusterEventBridge()` / `WithConversationBridge()` /
  `WithAgentBridge()`. Each bridge throttles per key (100ms cluster /
  50ms conversation / 200ms agent) and captures `Activity.Current` for
  W3C trace propagation.
- **Resilience MVP** — three critical external call-sites now use
  `Verbara.Sdk.Resilience` keyed policies (pattern matches Pro engine
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
- New `Verbara.Platform.Mail.Tests` project (SmtpSender coverage).

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
  `Verbara.Sdk.Resilience` + `Verbara.Sdk.OpenTelemetry` +
  `Verbara.Sdk.Pro.OpenTelemetry` pins (previously transitive).

### Removed

- `Verbara.Sdk.Pro.Resilience` reference. Package was sunset in Pro
  `1.9.0-pro` via ADR-0029 (migration to MIT `Verbara.Sdk.Resilience`).
  `Program.cs` now uses `Verbara.Sdk.Resilience.DependencyInjection`
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
