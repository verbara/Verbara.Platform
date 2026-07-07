# Tasks: csat-runner (Platform host — CSAT consumer)

Translated from the frozen execution plan `docs/plans/active/2026-05-18-platform-240-csat-consumer.md`
(Phases A–H). Cross-repo pre-condition: Pro's CSAT engine package (engine + `LicenseFeature.CsatRunner`
+ `ICsatTemplateProvider`) shipped and available on the local feed before Phase B integration.

## 1. Survey domain extension + Postgres migration (Phase A)

- [ ] 1.1 `src/Verbara.Platform.Surveys/SurveyResponse.cs` — append 6 nullable init-only properties (`Channel`, `QueueName`, `Rating`, `Comment`, `CapturedAt`, `CallId`)
- [ ] 1.2 `src/Verbara.Platform.Surveys/SurveyQuestionIds.cs` (NEW) — `CsatRating = "csat-rating-v1"`
- [ ] 1.3 `src/Verbara.Platform.Surveys/ISurveyAnalytics.cs` — add `GetByQueueAndChannelAsync` overload; mark `GetByQueueAsync` `[Obsolete]` (removed one minor later)
- [ ] 1.4 `src/Verbara.Platform.Surveys/InMemorySurveyAnalytics.cs` — implement the new overload; extend `InMemorySurveyAnalyticsTests.cs` with channel-filter cases
- [ ] 1.5 `src/Verbara.Platform.Storage.Postgres/Migrations/0XX_SurveyCsatExtensions.sql` (NEW) — extend `survey_responses` (6 nullable columns + 2 CHECK constraints + 2 partial indexes `WHERE channel IS NOT NULL`), create `csat_pending_dispatches`, extend `queues` (4 CSAT columns), create `csat_templates`
- [ ] 1.6 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyResponseStore.cs` — extend SELECT/INSERT + `static Map` for the 6 new columns (Verbara.Sdk.Data.Npgsql, explicit NpgsqlDbType on nullable params; no Dapper); update `PostgresSurveyResponseStoreTests.cs`
- [ ] 1.7 `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyAnalytics.cs` — implement `GetByQueueAndChannelAsync` over the new partial indexes
- [ ] 1.8 `src/Verbara.Platform.Queues/CsatConfig.cs` (NEW) + `Queue.cs` — nested `CsatConfig?` record + property; `PostgresQueueStore.cs` hydrate/persist the 4 CSAT columns; queue round-trip test
- [ ] 1.9 Verify migration applies cleanly to fresh + existing Postgres; back-compat (existing rows load with new columns null)

## 2. CSAT response endpoints + DTOs + Hub (Phase B)

- [ ] 2.1 `src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs` (NEW) — `POST /api/v1/csat/responses/{webchat,email,sms}` + `GET /api/v1/analytics/csat/queues/{queueId}`; bind the frozen wire shape (`responseToken`, `surveyId`, `questionId`, `channel`, `queueName`, `rating`, `comment`, `capturedAt`, `conversationId`)
- [ ] 2.2 Each endpoint persists `SurveyResponse` via `ISurveyResponseStore.SaveAsync`, publishes `CsatResponseRecordedEvent` via `IPushEventBus`, writes an audit row via `IAuditService.RecordAsync(category="csat")`
- [ ] 2.3 License gate — consume Pro's `LicenseFeature.CsatRunner` decision; return HTTP 402 + RFC 9457 ProblemDetails when absent
- [ ] 2.4 `src/Verbara.Platform.Api/Dtos/` — `CsatResponseRequest`, `CsatResponseDto`, `QueueCsatConfigDto`, `CsatTemplateDto` (typed sealed records)
- [ ] 2.5 `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — register all 5 DTOs (+ `CsatResponseRecordedEvent`) for AOT source-gen
- [ ] 2.6 `src/Verbara.Platform.Api/Hubs/IPlatformHubClient.cs` — add `OnCsatResponseRecorded(CsatResponseRecordedEvent)`; `PushToHubRelay.cs` — new event branch to `supervisor:{tenantId}`
- [ ] 2.7 Register endpoints in `Program.cs`; `tests/Verbara.Platform.Api.Tests/CsatResponseEndpointsTests.cs` — 4 contracts + 402 gate + audit-row assertion + token-validation cases

## 3. Email IMAP gap-fill (Phase C)

- [ ] 3.1 `src/Verbara.Platform.Mail/Services/ImapInboundPoller.cs` (NEW, `IHostedService`) + `ImapPollerOptions.cs` — per-tenant IMAP endpoint, ~30s poll, per-mailbox last-UID tracking, MailKit `MimeMessage` parse; register in `AddPlatformMail(...)`
- [ ] 3.2 `src/Verbara.Platform.Mail/Services/CsatReplyMailHandler.cs` (NEW) — HMAC token validation (7-day TTL), regex `\b([1-5])\b` (subject then first 200 chars), `In-Reply-To` fallback, forward to internal email endpoint, optional per-tenant auto-reply
- [ ] 3.3 `tests/Verbara.Platform.Mail.Tests/` — Testcontainers MailHog end-to-end + `CsatReplyMailHandlerTests.cs` (token, regex edge cases, In-Reply-To fallback, UID idempotency)

## 4. SMS correlator (Phase D)

- [ ] 4.1 `src/Verbara.Platform.Channels.Sms/CsatSmsCorrelator.cs` (NEW) — plug in after `SmsWebhookHandler`; look up `csat_pending_dispatches` (24h window, `consumed_at IS NULL`); on `^\s*[1-5]\s*$` match forward + mark `consumed_at`; else fall through to normal routing; register in `AddPlatformChannelsSms(...)`
- [ ] 4.2 `tests/Verbara.Platform.Channels.Sms.Tests/CsatSmsCorrelatorTests.cs` — window logic, collision (most-recent wins), non-rating fall-through, expired dispatch no-match

## 5. Template store + ICsatTemplateProvider + admin endpoints (Phase E)

- [ ] 5.1 `src/Verbara.Platform.Surveys/CsatTemplateStore.cs` (NEW `ICsatTemplateStore`) + `src/Verbara.Platform.Storage.Postgres/Stores/PostgresCsatTemplateStore.cs`; seed default templates per locale (en-US/es-419/pt-BR) per channel
- [ ] 5.2 `src/Verbara.Platform.Api/Services/CsatTemplateProvider.cs` (NEW, implements Pro's `Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider`) — fallback chain tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US; `AddSingleton<ICsatTemplateProvider, CsatTemplateProvider>()`
- [ ] 5.3 `TenantProvisioningService.cs` — seed default CSAT templates on tenant create
- [ ] 5.4 `src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs` (NEW) — `GET`/`PUT`/`DELETE /api/v1/admin/csat/templates/{id}` + `POST …/{id}/preview-voice` (Pro TTS synth), all `AdminOnly` + audit; `tests/…/CsatTemplateAdminEndpointsTests.cs`

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
