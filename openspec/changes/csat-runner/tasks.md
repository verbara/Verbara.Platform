# Tasks: csat-runner (Platform host — CSAT consumer)

Translated from the frozen execution plan `docs/plans/active/2026-05-18-platform-240-csat-consumer.md`
(Phases A–H). Cross-repo pre-condition: Pro's CSAT engine package (engine + `LicenseFeature.CsatRunner`
+ `ICsatTemplateProvider`) shipped and available on the local feed before Phase B integration.

## 1. Survey domain extension + Postgres migration (Phase A)

- [x] 1.1 `src/Verbara.Platform.Surveys/SurveyResponse.cs` — append 6 nullable init-only properties (`Channel`, `QueueName`, `Rating`, `Comment`, `CapturedAt`, `CallId`)
- [x] 1.2 `src/Verbara.Platform.Surveys/SurveyQuestionIds.cs` (NEW) — `CsatRating = "csat-rating-v1"`
- [x] 1.3 `src/Verbara.Platform.Surveys/ISurveyAnalytics.cs` — add `GetByQueueAndChannelAsync` overload; mark `GetByQueueAsync` `[Obsolete]` (removed one minor later)
- [x] 1.4 `src/Verbara.Platform.Surveys/InMemorySurveyAnalytics.cs` — implement the new overload; channel-filter cases in `QueueChannelAnalyticsTests.cs` (no `InMemorySurveyAnalyticsTests.cs` existed — analytics tests are split per-concern; new file matches the convention)
- [x] 1.5 `src/Verbara.Platform.Storage.Postgres/Migrations/016_SurveyCsatExtensions.sql` (NEW) — extend `survey_responses` (6 nullable columns + 2 CHECK constraints + 2 partial indexes `WHERE channel IS NOT NULL`), create `csat_pending_dispatches`, extend `queue_configs` (the real table; spec says "queues") (4 CSAT columns + repair pre-existing missing `wrap_up`), create `csat_templates`
- [x] 1.6 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyResponseStore.cs` — extend SELECT/INSERT + `static Map` for the 6 new columns (Verbara.Sdk.Data.Npgsql, explicit NpgsqlDbType on nullable params; no Dapper); `PostgresSurveyResponseStoreTests.cs` (NEW)
- [x] 1.7 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyAnalytics.cs` (NEW) — implement `GetByQueueAndChannelAsync` (DB-side COUNT/AVG) over the new partial indexes; registered to override the in-memory analytics under Postgres storage
- [x] 1.8 `src/Verbara.Platform.Queues/CsatConfig.cs` (NEW) + `Queue.cs` — nested `CsatConfig?` record + property; `PostgresQueueStore.cs` hydrate/persist the 4 CSAT columns; queue round-trip tests (domain `QueueTests.cs` + integration `PostgresQueueStoreCsatTests.cs`)
- [x] 1.9 Verify migration applies cleanly to fresh + existing Postgres; back-compat (existing rows load with new columns null) — MigrationsTests + null-column round-trip test

## 2. CSAT response endpoints + DTOs + Hub (Phase B)

- [x] 2.1 `src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs` (NEW) — `POST /api/v1/csat/responses/{webchat,email,sms}` + `GET /api/v1/analytics/csat/queues/{queueId}`; binds the frozen wire shape (`responseToken`, `surveyId`, `questionId`, `channel`, `queueName`, `rating`, `comment`, `capturedAt`, `conversationId`) via `CsatResponseRequest`. Webchat is anonymous + session-token-verified; email/sms are internal (`X-Service-Key`); analytics is `SupervisorPlus`.
- [x] 2.2 Each endpoint persists `SurveyResponse` via `ISurveyResponseStore.SaveAsync`, publishes `CsatResponseRecordedEvent` via `PlatformEventBus.Publish` (which forwards to `IPushEventBus`), writes an audit row via `IAuditService.RecordAsync(category="csat")`
- [x] 2.3 License gate — reuses the established declarative `.RequireLicenseFeature(LicenseFeature.CsatRunner)` + `LicenseGateMiddleware` (Program.cs) which emits HTTP 402 + RFC 9457 ProblemDetails via `ApiJsonContext.Default.ProblemDetails` when absent (no new gate invented)
- [x] 2.4 `src/Verbara.Platform.Api/Dtos/` — `CsatResponseRequest`, `CsatResponseDto`, `QueueCsatConfigDto`, `CsatTemplateDto` (typed sealed records, `namespace Verbara.Platform.Api.Dtos`)
- [x] 2.5 `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — registered the 4 DTOs + `CsatResponseRecordedEvent` (in `Verbara.Platform.Core`) for AOT source-gen
- [x] 2.6 `PushToHubRelay.cs` (in `Verbara.Platform.Realtime`) — new `CsatResponseRecordedEvent` branch to `supervisor:{tenantId}` via `OnCsatResponseRecorded` + a `CsatResponseRecordedPayload` in `Realtime.Contracts`. **Boundary note:** `IPlatformHubClient` is a Pro nupkg type (`Verbara.Sdk.Pro.Push.SignalR.Hubs`, 2.9.0-pro has NO `OnCsatResponseRecorded`), NOT a Platform file — adding the typed method requires a Pro edit + repack (out of Phase B scope / `dotnet pack` banned). The relay therefore fans out over the untyped `IHubContext<PlatformHub>` using the `"OnCsatResponseRecorded"` client-method name (identical wire contract); the typed-interface addition is a Pro-side follow-up (like the 5b.x seams).
- [x] 2.7 Registered endpoints in `Program.cs` (`v1.MapCsatResponseEndpoints(...)`); `tests/Verbara.Platform.Api.Tests/CsatResponseEndpointsTests.cs` — 4 endpoint contracts + 402 gate (webchat + analytics) + `csat`-category audit-row assertion + `responseToken` validation cases (missing / malformed / expired / tampered-sig / signed-queue mismatch / rating out of range)

## 3. Email IMAP gap-fill (Phase C)

- [x] 3.1 `src/Verbara.Platform.Mail/Services/ImapInboundPoller.cs` (NEW, `BackgroundService`/`IHostedService`) + `ImapPollerOptions.cs` — per-tenant IMAP endpoint, ~30s poll (`PeriodicTimer`), per-mailbox last-UID tracking (`SearchQuery.Uids(range)` above last-UID → idempotent dedup), MailKit `MimeMessage` parse; registered in the NEW `AddPlatformMail(...)` extension (Mail is a standalone Web microservice — there was no prior `AddPlatformMail`; created one mirroring the SMS `AddSms` convention, called from `Program.cs`). Internal `IImapClient`-factory ctor for deterministic poll-loop tests.
- [x] 3.2 `src/Verbara.Platform.Mail/Services/CsatReplyMailHandler.cs` (NEW) — reuses Pro's `ICsatReplyTokenSigner`/`HmacCsatReplyTokenSigner` (7-day TTL) via its `(secret, ttl)` ctor (HMAC NOT re-hand-rolled; added `Verbara.Sdk.Pro.CsatRunner` `PackageReference` to `Verbara.Platform.Mail`), regex `\b([1-5])\b` (subject then first 200 chars of body), `In-Reply-To` fallback via `ICsatEmailDispatchResolver` seam (concrete `PostgresCsatEmailDispatchResolver` over `csat_pending_dispatches`), forwards to the internal email endpoint via `ICsatEmailCaptureForwarder` (concrete `HttpCsatEmailCaptureForwarder`, `X-Service-Key` + `X-Tenant-Id`, source-gen JSON), optional per-tenant auto-reply via `IEmailService`.
- [x] 3.3 `tests/Verbara.Platform.Mail.Tests/` — Testcontainers **MailHog** end-to-end (real SMTP round-trip → HTTP API → MailKit parse → handler) + `CsatReplyMailHandlerTests.cs` (token valid/tampered/expired, regex subject-vs-body precedence + first-200-chars boundary, In-Reply-To fallback, no-correlation) + `ImapInboundPollerTests.cs` (UID idempotency across two polls via fake `IImapClient`). 23/23 green.

## 4. SMS correlator (Phase D)

- [x] 4.1 `src/Verbara.Platform.Channels.Sms/CsatSmsCorrelator.cs` (NEW) — plugs in after `SmsWebhookHandler`; looks up `csat_pending_dispatches WHERE tenant_id=$1 AND channel='sms' AND correlator=$2 AND consumed_at IS NULL AND sent_at > now()-interval '24h' ORDER BY sent_at DESC` via the Npgsql facade (explicit `NpgsqlDbType.TimestampTz` on the window/consumed params, no Dapper); on `^\s*[1-5]\s*$` match forwards via `ICsatCaptureForwarder` seam + marks `consumed_at` (collision → most-recent wins, older open dispatches marked expired without capture); else returns false to fall through to normal routing. Registered in `AddSms(...)` (the actual SMS registration method — there is no `AddPlatformChannelsSms`).
- [x] 4.2 `tests/Verbara.Platform.Channels.Sms.Tests/CsatSmsCorrelatorTests.cs` — Testcontainers Postgres (`csat_pending_dispatches` DDL mirrored from migration 016): window logic (in/out of 24h, already-consumed, no-dispatch), collision (most-recent wins, older expired), non-rating fall-through (open dispatch untouched), digit-out-of-range/not-bare fall-through, whitespace-padded digit capture. Suite 48/48 green.

## 5. Template store + ICsatTemplateProvider + admin endpoints (Phase E)

- [x] 5.1 `src/Verbara.Platform.Surveys/CsatTemplateStore.cs` (NEW `ICsatTemplateStore` + `CsatTemplateEntry` + `CsatDefaultTemplates`) + `src/Verbara.Platform.Storage.Postgres/Stores/PostgresCsatTemplateStore.cs` (Npgsql facade over `csat_templates`, explicit `NpgsqlDbType` on nullable params) + `src/Verbara.Platform.Storage.InMemory/InMemoryCsatTemplateStore.cs` (Testing-env parity with `InMemorySurveyStore`); registered in `AddPostgresStorage` / `AddInMemoryStorage`. Default templates seeded per locale (en-US/es-419/pt-BR) per templatable channel (email/sms/voice) via `CsatDefaultTemplates.ForTenant`
- [x] 5.2 `src/Verbara.Platform.Api/Services/CsatTemplateProvider.cs` (NEW, implements Pro's `Verbara.Sdk.Pro.CsatRunner.Contracts.ICsatTemplateProvider`) — fallback chain tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US; maps `CsatTemplateEntry` → Pro `CsatTemplate` (`Subject`/`Body`/`Locale`=resolved); `AddSingleton<ICsatTemplateProvider, CsatTemplateProvider>()` in Program.cs
- [x] 5.3 `TenantProvisioningService.cs` — seed default CSAT templates on tenant create via `CreateDefaultCsatTemplatesAsync` (mirrors `CreateDefaultCsatSurvey` golden-default hook; injects `ICsatTemplateStore`)
- [x] 5.4 `src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs` (NEW) — `GET`(list+by-id)/`PUT`/`DELETE /api/v1/admin/csat/templates/{id}` + `POST …/{id}/preview-voice`, all `AdminOnly` + `csat`-category audit; `UpsertCsatTemplateRequest` + `CsatTemplateDto[]` registered in `ApiJsonContext`. **`preview-voice` returns HTTP 501 "voice preview deferred"** — Pro voice/TTS is deferred (no `ITtsSynthesizer`, `CsatVoiceOptions` intentionally absent); endpoint shape present, no fake synthesis. `tests/…/CsatTemplateAdminEndpointsTests.cs` (15 tests: CRUD, 501, AdminOnly 403, provider fallback)

## 5b. Pro engine hosting + dispatch/trigger seams (Phase E2)

> Added 2026-07-11 reconciling Pro's `csat-runner` apply. Pro (upstream) cannot reference Platform
> (downstream), so its apply used dependency-inversion and introduced **4 Pro-owned seams beyond
> `ICsatTemplateProvider`** plus an `IHostedService` orchestrator. Platform implements the seams and
> hosts the engine. The frozen 2026-05-18 plan predates this; this section records the accepted
> boundary expansion (operator decision 2026-07-11, "accept & extend Platform").

- [x] 5b.0 Advance the Pro pin: `Directory.Packages.props` `Verbara.Sdk.Pro.*` → `2.9.0-pro` (already on
      the local feed from stage 1); add a `Verbara.Sdk.Pro.CsatRunner` `PackageReference` to
      `Verbara.Platform.Api` (and any project hosting a seam impl); clear cache + `dotnet restore`.
      **Pre-req for Phase B (2.3 license) and Phase E (5.2 ICsatTemplateProvider) — pull forward from 8.1.**
      ✅ Done 2026-07-11 (pulled forward): all Pro pins → 2.9.0-pro, CsatRunner pkg added to Api;
      Platform builds 0-warning against Pro 2.9.0-pro.
- [x] 5b.1 `CsatConversationSignalAdapter : ICsatConversationSignal` — bridges to `IConversationService.SendMessageAsync` (webchat `csat_requested` system message).
      ✅ `src/Verbara.Platform.Api/Services/CsatConversationSignalAdapter.cs` — sends a `System`-kind
      message (`ConversationOwnerKind.System`, `senderId = EntityId.From("system")`) carrying a single
      `TextBlock(systemMessageType)`; matches `DefaultActionExecutor`'s system-message convention.
- [x] 5b.2 `CsatEmailDispatcherAdapter : ICsatEmailDispatcher` — bridges to `IEmailService.SendAsync` (Reply-To via Platform `MailMessage` headers).
      ✅ `src/Verbara.Platform.Api/Services/CsatEmailDispatcherAdapter.cs`. Platform's `EmailMessage`
      carried no Reply-To/header bag, so added an additive nullable `EmailMessage.ReplyToAddress`
      (`src/Verbara.Platform.Core/Email/EmailMessage.cs`) and `SmtpSender.BuildMimeMessage` now stamps
      `mime.ReplyTo` (`src/Verbara.Platform.Mail/Services/SmtpSender.cs`). Back-compat (nullable; both
      `ApiJsonContext` + `MailJsonContext` source-gen `EmailMessage` auto-pick it up).
- [x] 5b.3 `CsatSmsDispatcherAdapter : ICsatSmsDispatcher` — bridges to `ISmsProvider.SendAsync`; writes the `csat_pending_dispatches` row the Phase-D SMS correlator consumes.
      ✅ `src/Verbara.Platform.Api/Services/CsatSmsDispatcherAdapter.cs` — sends via `ISmsProvider`
      (from = Twilio config), then INSERTs the row via the Npgsql facade (`channel='sms'`,
      `correlator`=phone, explicit `NpgsqlDbType.Text`/`.TimestampTz` on nullable/timestamp params,
      `ON CONFLICT (tenant_id, dispatch_id) DO NOTHING` for idempotency). The seam `CsatSmsRequest`
      carries no survey/queue, so `survey_id`/`queue_name` are resolved from the conversation → queue
      → active CSAT survey. Verified against a real Postgres: INSERT succeeds + the correlator's
      verbatim SELECT reads the row back (survey_id/queue_name/conversation_id) + idempotent re-insert.
- [x] 5b.4 `CsatConversationEndSource : ICsatConversationEndSource` — hot `IObservable<CsatConversationEndedSignal>` driven from Platform's conversation-end lifecycle + per-queue CSAT config snapshot (Enabled / PreferredChannel / SamplingRatePercent).
      ✅ `src/Verbara.Platform.Api/Services/CsatConversationEndSource.cs` — subscribes to
      `PlatformEventBus.Events.OfType<ConversationStateChangedEvent>()` filtered to the terminal
      `Closed` transition (the canonical conversation-end signal; no dedicated ended event exists —
      same event `PushToHubRelay`/`RealtimeStateBridge` consume). Resolves the queue's `CsatConfig`
      snapshot via `IQueueStore`, the tenant's active CSAT `Survey` id via `ISurveyStore`, and the
      recipient address (null for webchat) + locale via `IContactStore`; mints `SurveyResponseId` and
      pushes onto an internal `Subject`. Implements both `ICsatConversationEndSource` (`Ended`) and
      `BackgroundService`; only digital channels (webchat/email/sms) proceed.
- [x] 5b.5 Composition root (`Program.cs`): `AddProCsatRunner(...)` + register all 5 seam impls; the Pro orchestrator runs as a hosted service, gated by `LicenseFeature.CsatRunner`.
      ✅ `src/Verbara.Platform.Api/Program.cs` — registers all 4 new seams (+ the existing
      `ICsatTemplateProvider`) and calls `AddProCsatRunner(...)` with `WithEmail` (ReplyToDomain +
      HMAC token secret) + `WithWebChat("csat_requested")`. `AddProCsatRunner` registers the Pro
      orchestrator (`IHostedService`) + the 3 channel adapters; the orchestrator self-gates at runtime
      on `LicenseFeature.CsatRunner` via the Pro `ILicenseGuard`. SMS dispatcher deps
      (`ISmsProvider`/`NpgsqlDataSource`) resolve optionally so DI holds under the in-memory profile.
- [x] 5b.6 Tests: each seam adapter bridges correctly; end-to-end wiring (conversation-end → orchestrator → adapter → Platform service) with fakes.
      ✅ `tests/Verbara.Platform.Api.Tests/CsatSeamAdaptersTests.cs` (per-seam bridge unit tests with
      NSubstitute Platform-service fakes) + `tests/Verbara.Platform.Api.Tests/CsatRunnerWiringTests.cs`
      (DI resolution of the Pro orchestrator + all 5 seams + 3 channel adapters; end-to-end
      conversation-end signal → orchestrator routes → webchat adapter → `IConversationService`
      asserted called). 10/10 new green; full `Verbara.Platform.Api.Tests` 1628/1628 green.

## 6. AOT validation + cross-package tests (Phase F)

- [ ] 6.1 Full `dotnet test Verbara.Platform.slnx --filter "Category!=Integration&FullyQualifiedName!~Postgres"` green
- [ ] 6.2 Integration tests (Testcontainers Postgres + MailHog) — end-to-end migration + IMAP gap-fill against the real Pro CSAT nupkg (not a stub)
- [ ] 6.3 AOT publish `src/Verbara.Platform.Api -c Release -r linux-x64 /p:PublishAot=true` → 0 trim/AOT warnings; all 5 DTOs serialize via source-gen

## 7. Docs + CHANGELOG (Phase G)

- [ ] 7.1 `CHANGELOG.md` — `[2.18.0]` CSAT consumer section (Added / Changed / Deprecated / Cross-repo coordination)
- [ ] 7.2 `docs/operations/csat-runbook.md` (NEW) — enable CSAT per queue, configure templates, troubleshoot IMAP / SMS correlation, `CREATE INDEX CONCURRENTLY` guidance
- [ ] 7.3 `docs/roadmap.md` — bump Pro pin; add the Platform CSAT row to the Shipped table
- [ ] 7.4 `git mv docs/plans/active/2026-05-18-platform-240-csat-consumer.md docs/plans/completed/` on ship

## 8. Pack + tag + ship (Phase H)

- [ ] 8.1 `Directory.Build.props` `<PackageVersion>` → `2.18.0`; `Directory.Packages.props` — advance the Pro pin to the CSAT engine package
- [ ] 8.2 Clear NuGet cache + `dotnet restore Verbara.Platform.slnx` + `dotnet build /warnaserror` + `dotnet test` green
- [ ] 8.3 Commit + push + `git tag -a v2.18.0` + push tag; CI (`release.yml`) publishes the signed AOT image to `ghcr.io/verbara/platform/api`
- [ ] 8.4 Archive this OpenSpec change (sync → archive) and update roadmap / project memory per the closing routine
