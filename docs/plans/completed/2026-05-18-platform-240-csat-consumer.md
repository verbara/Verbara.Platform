# Platform v2.4.0 — CSAT Consumer Migration (Execution Plan)

**Created:** 2026-05-18 · **Status:** APPROVED, execution paused (awaiting Platform-side pending work) · **Canonical spec:** [`docs/specs/2026-05-18-platform-240-csat-consumer.md`](../../specs/2026-05-18-platform-240-csat-consumer.md) · **Origin:** system-path plan `~/.claude/plans/si-refactored-pascal.md` (planning-mode canonical until materialized here)

**Target release:** Platform `2.4.0` · **Calendar target:** ~2026-07-05 to 2026-07-12 (post Pro 2.6.0-pro ship).

## Pre-conditions (verify at kickoff)

- [ ] Platform repo on `main` with `Directory.Build.props` at `2.3.0` (or any v2.3.x patch from Pro 2.5.0-pro consumer release).
- [ ] **Pro v2.6.0-pro shipped + tag pushed + nupkgs available on GitHub Packages** (engine + license feature + `ICsatTemplateProvider` interface).
- [ ] **ADR-0020 committed** (this PR or prior).
- [ ] Pre-condition #1 (Stasis bridge ownership) verified upstream by the Pro plan — see [`Verbara.Sdk.Pro/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/plans/active/2026-05-18-pro-260-csat-runner-v1.md).
- [ ] All existing tests green: `dotnet test Verbara.Platform.slnx --filter "Category!=Integration&FullyQualifiedName!~Postgres"` returns ~2,048+ pass / 0 fail.
- [ ] Web v3.2.0-web spec coordinated (admin tab + dashboard card + embed rating panel covered in Web-side track).
- [ ] No uncommitted changes that conflict with the affected files.

## Goal

Ship Platform `2.4.0` with:

1. Postgres migration extending `survey_responses` + adding `csat_pending_dispatches` + `csat_templates` + per-queue CSAT columns.
2. `Verbara.Platform.Surveys.SurveyResponse` additive properties + `ISurveyAnalytics.GetByQueueAndChannelAsync` overload.
3. `Verbara.Platform.Queues.Queue.Csat` nested config.
4. 4 new public/internal endpoints under `/api/v1/csat/responses/*` + 4 admin endpoints under `/api/v1/admin/csat/templates/*` + 1 analytics endpoint.
5. `ImapInboundPoller` + `CsatReplyMailHandler` (IMAP gap-fill in `Verbara.Platform.Mail`).
6. `CsatSmsCorrelator` (digit-reply matcher in `Verbara.Platform.Channels.Sms`).
7. `IPlatformHubClient.OnCsatResponseRecorded` typed Hub method + `PushToHubRelay` new event-type branch.
8. `CsatTemplateProvider` implementing Pro's `ICsatTemplateProvider` interface.
9. AOT JsonContext registrations for 5 new DTOs.
10. CHANGELOG `[2.4.0]` + version bump + tag + push.

## Non-goals (deferred)

- **Web admin CSAT tab** — handled Web v3.2.0-web.
- **Web supervisor dashboard CSAT KPI card** — handled Web v3.2.0-web.
- **WebChat embed rating panel** — handled Web v3.2.0-web + embed v3.1.0.
- **Per-tenant template UI** — endpoint exists Platform-side; UI is Web-side scope.
- **Migration backfill of historical `survey_responses` rows** — new columns are nullable; existing rows keep their JSONB `answers`. No backfill required.

---

## Phase A — Survey domain extension + Postgres migration (~1.5d)

### A.1 — Survey domain (~0.5d)

- [ ] `src/Verbara.Platform.Surveys/SurveyResponse.cs` — append 6 init-only properties (`Channel`, `QueueName`, `Rating`, `Comment`, `CapturedAt`, `CallId`). All nullable.
- [ ] `src/Verbara.Platform.Surveys/SurveyQuestionIds.cs` (NEW) — well-known constants (`CsatRating = "csat-rating-v1"`).
- [ ] `src/Verbara.Platform.Surveys/ISurveyAnalytics.cs` — add `GetByQueueAndChannelAsync` overload signature.
- [ ] `src/Verbara.Platform.Surveys/InMemorySurveyAnalytics.cs` — implement new overload using LINQ over the in-memory store.
- [ ] Mark existing `GetByQueueAsync` `[Obsolete("Use GetByQueueAndChannelAsync; removed in v2.5.0")]`.
- [ ] Tests: `tests/Verbara.Platform.Surveys.Tests/InMemorySurveyAnalyticsTests.cs` — extend with channel-filter test cases.

### A.2 — Postgres migration (~0.5d)

- [ ] `src/Verbara.Platform.Storage.Postgres/Migrations/0XX_SurveyCsatExtensions.sql` (NEW; number assigned at execution time):
  - Extend `survey_responses` (6 nullable columns + 2 CHECK constraints + 2 partial indexes).
  - Create `csat_pending_dispatches` table.
  - Extend `queues` table (4 CSAT columns).
  - Create `csat_templates` table.
- [ ] `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyResponseStore.cs` — extend SELECT/INSERT to include the new columns. Tests: existing `PostgresSurveyResponseStoreTests.cs` updates + new tests for CSAT-flavored rows.
- [ ] `src/Verbara.Platform.Storage.Postgres/Stores/PostgresSurveyAnalytics.cs` — implement `GetByQueueAndChannelAsync` with the new partial indexes.

### A.3 — Queue entity + DTO (~0.5d)

- [ ] `src/Verbara.Platform.Queues/Queue.cs` — add `CsatConfig?` nested record + property.
- [ ] `src/Verbara.Platform.Queues/CsatConfig.cs` (NEW) — `(bool Enabled, string? PreferredChannel, EntityId? PromptTemplateId, int SamplingRatePercent)`.
- [ ] `src/Verbara.Platform.Storage.Postgres/Stores/PostgresQueueStore.cs` — extend SELECT/INSERT/UPDATE to hydrate/persist CSAT config from new columns.
- [ ] Admin endpoint extension: `src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs` `PUT /admin/queues/{id}` accepts updated `QueueDto` with `Csat` nested.
- [ ] Tests: queue round-trip with CSAT config populated.

**Phase A acceptance:** migration applies cleanly to fresh Postgres + existing tenant Postgres; back-compat verified; tests pass.

---

## Phase B — CSAT response endpoints (~2d)

### B.1 — `CsatResponseEndpoints.cs` (~1d)

- [ ] `src/Verbara.Platform.Api/Endpoints/CsatResponseEndpoints.cs` (NEW) — 4 endpoints:
  - `POST /api/v1/csat/responses/webchat` (anonymous + WebChat session-token-signed).
  - `POST /api/v1/csat/responses/email` (internal-only via API key).
  - `POST /api/v1/csat/responses/sms` (internal-only).
  - `GET /api/v1/analytics/csat/queues/{queueId}?range=24h` (`SupervisorPlus`).
- [ ] Each persists `SurveyResponse` via `ISurveyResponseStore.SaveAsync`.
- [ ] Each publishes `CsatResponseRecordedEvent` via `IPushEventBus.PublishAsync`.
- [ ] Each writes audit row via `IAuditService.RecordAsync(category="csat", ...)`.
- [ ] HTTP 402 license-gate consistent with v2.2.0 contract (consume Pro's `ILicenseGuard.CanExecuteAsync(LicenseFeature.CsatRunner)` decision).
- [ ] Register endpoints in `Program.cs` MapEndpoints section.

### B.2 — DTOs + JsonContext (~0.5d)

- [ ] `src/Verbara.Platform.Api/Dtos/CsatResponseRequest.cs`, `CsatResponseDto.cs`, `QueueCsatConfigDto.cs`, `CsatTemplateDto.cs`.
- [ ] Register all 5 (+ `CsatResponseRecordedEvent`) in `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs`.

### B.3 — Hub + relay (~0.5d)

- [ ] `src/Verbara.Platform.Api/Hubs/IPlatformHubClient.cs` — add `OnCsatResponseRecorded(CsatResponseRecordedEvent)` typed method.
- [ ] `src/Verbara.Platform.Api/Services/PushToHubRelay.cs` — add new event-type branch routing to `Group("supervisor:{tenantId}")`.
- [ ] Tests: `tests/Verbara.Platform.Api.Tests/CsatResponseEndpointsTests.cs` — 4 endpoint contracts + 402 license-gate + audit row assertion.

**Phase B acceptance:** all 4 endpoints functional; supervisor session receives `OnCsatResponseRecorded` ≤ 2s; integration test passes.

---

## Phase C — Email IMAP gap-fill (~2d)

### C.1 — `ImapInboundPoller` (~1d)

- [ ] `src/Verbara.Platform.Mail/Services/ImapInboundPoller.cs` (NEW, `IHostedService`):
  - Configurable per-tenant IMAP endpoint via `IOptions<ImapPollerOptions>`.
  - Polls every 30s (configurable).
  - Tracks last-processed UID per mailbox.
  - Parses `MimeMessage` via MailKit.
  - Routes to `CsatReplyMailHandler` based on `+token` envelope prefix.
- [ ] `src/Verbara.Platform.Mail/Services/ImapPollerOptions.cs`.
- [ ] Register in DI extension `AddPlatformMail(...)`.

### C.2 — `CsatReplyMailHandler` (~0.5d)

- [ ] `src/Verbara.Platform.Mail/Services/CsatReplyMailHandler.cs` (NEW):
  - Validates HMAC-signed token (7-day TTL).
  - Regex `\b([1-5])\b` against subject first, then plain-text first 200 chars.
  - Falls back to `In-Reply-To` header matching when `+suffix` stripped.
  - Forwards to `POST /api/v1/csat/responses/email` (internal API key auth).
  - Auto-reply thank-you (optional, configurable per tenant).

### C.3 — Tests (~0.5d)

- [ ] `tests/Verbara.Platform.Mail.Tests/ImapInboundPollerTests.cs` — Testcontainers MailHog; dispatch CSAT email → reply → parser → forward.
- [ ] `tests/Verbara.Platform.Mail.Tests/CsatReplyMailHandlerTests.cs` — token validation; regex edge cases; In-Reply-To fallback; auto-reply opt-in.

**Phase C acceptance:** end-to-end IMAP receive → parse → forward → `SurveyResponse` row; idempotency tests pass.

---

## Phase D — SMS correlator (~1d)

- [ ] `src/Verbara.Platform.Channels.Sms/CsatSmsCorrelator.cs` (NEW):
  - Plugs into inbound SMS dispatch path after `SmsWebhookHandler`.
  - Looks up `csat_pending_dispatches WHERE tenant_id=$1 AND channel='sms' AND correlator=$2 AND consumed_at IS NULL AND sent_at > now() - interval '24h'`.
  - If matched + body matches `^\s*[1-5]\s*$` → forward to `POST /api/v1/csat/responses/sms` + mark `consumed_at = now()`.
  - Else fall through to normal conversation routing (don't eat user messages).
- [ ] Register in DI: `AddPlatformChannelsSms(...)`.
- [ ] Tests: `tests/Verbara.Platform.Channels.Sms.Tests/CsatSmsCorrelatorTests.cs` — correlation window logic; collision handling (2 surveys to same phone in 24h → most-recent wins); message that's not a rating falls through; expired dispatches don't match.

**Phase D acceptance:** correlator routes correctly; rating replies captured; non-rating replies pass through.

---

## Phase E — Template store + admin endpoints (~1.5d)

### E.1 — Template store (~0.5d)

- [ ] `src/Verbara.Platform.Surveys/CsatTemplateStore.cs` (NEW) — `ICsatTemplateStore` + Postgres impl.
- [ ] `src/Verbara.Platform.Storage.Postgres/Stores/PostgresCsatTemplateStore.cs`.
- [ ] Migration migrations seed default templates per locale (en-US/es-419/pt-BR) for each channel.

### E.2 — `CsatTemplateProvider` (~0.5d)

- [ ] `src/Verbara.Platform.Api/Services/CsatTemplateProvider.cs` (NEW, implements Pro's `Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider`).
- [ ] Fallback chain: tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US.
- [ ] Register in DI: `services.AddSingleton<ICsatTemplateProvider, CsatTemplateProvider>()`.
- [ ] Update `TenantProvisioningService.cs` to seed default templates on tenant create.

### E.3 — Admin endpoints (~0.5d)

- [ ] `src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs` (NEW) — 4 endpoints:
  - `GET /api/v1/admin/csat/templates`.
  - `PUT /api/v1/admin/csat/templates/{id}`.
  - `DELETE /api/v1/admin/csat/templates/{id}`.
  - `POST /api/v1/admin/csat/templates/{id}/preview-voice` (calls Pro's TTS synth + returns audio URL).
- [ ] All `AdminOnly` policy.
- [ ] Audit pattern matching existing endpoints.
- [ ] Tests: `tests/Verbara.Platform.Api.Tests/CsatTemplateAdminEndpointsTests.cs`.

**Phase E acceptance:** template fallback chain works; admin CRUD + preview functional; TenantProvisioningService seeds defaults.

---

## Phase F — AOT validation + cross-package tests (~1d)

- [ ] Full `dotnet test Verbara.Platform.slnx --filter "Category!=Integration&FullyQualifiedName!~Postgres"` green.
- [ ] Integration tests with Testcontainers Postgres + MailHog: end-to-end migration + IMAP gap-fill.
- [ ] AOT publish: `dotnet publish src/Verbara.Platform.Api -c Release -r linux-x64 /p:PublishAot=true` → 0 trim/AOT warnings.
- [ ] Web Playwright E2E (driven by Web v3.2.0-web track but exercises Platform endpoints): WebChat → rating panel → submit → supervisor dashboard updates.

**Phase F acceptance:** all gates green; AOT clean.

---

## Phase G — Docs + CHANGELOG (~1d)

- [ ] `CHANGELOG.md` `[2.4.0]` section per spec draft.
- [ ] `docs/roadmap.md` — header bump Pro pin `2.5.0-pro` → `2.6.0-pro`; add v2.4.0 row to Shipped table.
- [ ] `docs/operations/csat-runbook.md` (NEW) — turning on CSAT per queue; configuring templates; troubleshooting IMAP / SMS correlation.
- [ ] `docs/manuales/smb/XX-csat-setup.md` (NEW) — operator-facing guide for SMB tier; multi-lingual.
- [ ] Move this plan file from `docs/plans/active/` to `docs/plans/completed/` on ship.

**Phase G acceptance:** docs complete; operator runbook published.

---

## Phase H — Pack + tag + ship (~0.5d)

- [ ] `Directory.Build.props` `<PackageVersion>2.3.0</PackageVersion>` → `2.4.0` (assumes 2.3.0 shipped as Pro 2.5.0-pro consumer release).
- [ ] `Directory.Packages.props` — bump Pro pins `2.5.0-pro` → `2.6.0-pro`.
- [ ] `dotnet nuget locals all --clear && rm -rf ~/.nuget/packages/verbara.sdk.pro*/`.
- [ ] `dotnet restore Verbara.Platform.slnx` + `dotnet build /warnaserror` + `dotnet test`.
- [ ] Commit + push + `git tag -a v2.4.0 -m "..."` + `git push origin v2.4.0`.
- [ ] CI (`release.yml`) auto-publishes signed image to `ghcr.io/verbara/platform/api`.
- [ ] Manual GH Release with CHANGELOG body (if CI's release.yml doesn't auto-create).
- [ ] Cross-repo close-out: update `.project-memory/MEMORY.md` + roadmap per established pattern.

**Phase H acceptance:** v2.4.0 tagged; GH Release published; image pushed to ghcr.io with cosign signature.

---

## Total estimate

| Phase | Days |
|---|---|
| A — Survey domain + Postgres migration | 1.5 |
| B — CSAT response endpoints + Hub | 2 |
| C — Email IMAP gap-fill | 2 |
| D — SMS correlator | 1 |
| E — Template store + admin endpoints | 1.5 |
| F — AOT validation + cross-package | 1 |
| G — Docs + CHANGELOG | 1 |
| H — Pack + tag + ship | 0.5 |
| **Platform-side total** | **~10.5d** (within the 14-16d cross-repo total when factoring parallelism with Pro and Web tracks) |

---

## Risks

1. **Migration on production-sized `survey_responses`** — `ALTER TABLE ADD COLUMN` is metadata-only in Postgres 11+ for nullable columns w/o default. BUT the 2 new indexes scan the table. Mitigation: use `CREATE INDEX CONCURRENTLY` for large tenants; document in `docs/operations/csat-runbook.md`.
2. **IMAP poller reliability + idempotency** — UID-based dedup standard; test rigor on duplicate-receive + EXPUNGE-during-poll + mailbox-rebuild edge cases.
3. **SMS correlation collision (2 surveys to same phone in 24h)** — Mitigated by FIFO + strict expiry; documented as known limitation in operator FAQ.
4. **`ICsatTemplateProvider` cross-repo contract** — Pro 2.6.0-pro must publish the interface; Platform 2.4.0 implements it. Integration test against actual Pro 2.6.0-pro nupkg required (not against stub interface).
5. **Survey admin UI back-compat** — `Verbara.Platform.Web/src/admin/surveys/` UI assumes multi-question surveys. CSAT uses 1 well-known question. Verify UI doesn't break when rendering single-question CSAT survey; if it does, +0.5d Web fix (handled in Web v3.2.0-web track).

---

## Verification

```bash
# Pre-flight
dotnet nuget locals all --clear && rm -rf ~/.nuget/packages/verbara.sdk.pro*/
dotnet restore Verbara.Platform.slnx

# Phase F acceptance
dotnet build Verbara.Platform.slnx -c Release /warnaserror
dotnet test Verbara.Platform.slnx --filter "Category!=Integration&FullyQualifiedName!~Postgres" --no-build -c Release
dotnet build tests/Verbara.Platform.Api.Aot.Probe -c Release
# (or full PublishAot if API csproj toggled aot-compatible)

# Migration smoke against test Postgres
psql -h localhost -U postgres -d verbara_test \
     -f src/Verbara.Platform.Storage.Postgres/Migrations/0XX_SurveyCsatExtensions.sql

# Phase H
git add -A && git commit -m "feat: v2.4.0 — CSAT consumer migration"
git tag -a v2.4.0 -m "..." && git push origin main v2.4.0
```

### Acceptance criteria

- Migration applies + back-compat verified.
- All 9 new endpoints (4 response + 4 admin + 1 analytics) pass contract tests.
- IMAP gap-fill end-to-end + idempotency.
- SMS correlator + fall-through behavior.
- Template fallback chain works.
- HTTP 402 license-gate consistent with v2.2.0 contract.
- AOT publish clean.
- CHANGELOG `[2.4.0]` complete.
- Tag `v2.4.0` + GH Release + ghcr.io signed image.

---

## Execution pause note

Per user direction 2026-05-18: execution paused while Platform-side pending work is completed. When that finishes:

1. Verify pre-conditions (Pro v2.6.0-pro shipped + on GitHub Packages).
2. Coordinate calendar with Pro v2.6.0-pro ship (ideally same week).
3. Start Phase A.
