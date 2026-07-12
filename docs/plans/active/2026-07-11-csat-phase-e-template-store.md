# Plan: csat-runner Phase E (tasks 5.1–5.4) — template store + ICsatTemplateProvider + admin endpoints

Scope: STRICT tasks 5.1–5.4 of `openspec/changes/csat-runner/tasks.md`. No Phase E2/F/G/H, no `dotnet pack`.

## 5.1 — Template store
- `src/Verbara.Platform.Surveys/CsatTemplateStore.cs` (NEW): `ICsatTemplateStore` + `CsatTemplateEntry`
  domain record (distinct name from Pro's resolved `CsatTemplate`) + `CsatDefaultTemplates` static seed
  set (en-US / es-419 / pt-BR × {voice,email,sms}, `IsDefault=true`, global i.e. tenant-agnostic bodies).
- `src/Verbara.Platform.Storage.Postgres/Stores/PostgresCsatTemplateStore.cs` (NEW): Npgsql facade over
  `csat_templates`; class row + `static Map` + explicit `NpgsqlDbType` on nullable `subject`/`updated_at`.
  Register `AddSingleton<ICsatTemplateStore, PostgresCsatTemplateStore>()` in `AddPostgresStorage`.
- `src/Verbara.Platform.Storage.InMemory/InMemoryCsatTemplateStore.cs` (NEW) + register in
  `AddInMemoryStorage` (Testing env store — mirrors the ISurveyStore convention; enables container-free tests).

## 5.2 — Provider
- `src/Verbara.Platform.Api/Services/CsatTemplateProvider.cs` (NEW): implements
  `Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider`. Fallback chain tenant-locale → tenant-default-locale
  → global-default-locale → global-default-en-US. Maps store `CsatTemplateEntry` → Pro `CsatTemplate`
  (`Subject`, `Body`, `Locale`=resolved). `AddSingleton<ICsatTemplateProvider, CsatTemplateProvider>()` in Program.cs.

## 5.3 — Provisioning seed
- `TenantProvisioningService`: inject `ICsatTemplateStore`, add `CreateDefaultCsatTemplatesAsync` called from
  `OnTenantCreatedAsync` (golden defaults, next to `CreateDefaultSurveyAsync`). Seed from `CsatDefaultTemplates`.

## 5.4 — Admin endpoints
- `src/Verbara.Platform.Api/Endpoints/CsatTemplateAdminEndpoints.cs` (NEW): `GET/PUT/DELETE
  /api/v1/admin/csat/templates/{id}` + list `GET /` + `POST /{id}/preview-voice`. All `AdminOnly` + audit.
  Register in Program.cs (`v1.MapCsatTemplateAdminEndpoints()`).
- `preview-voice`: Pro voice/TTS is DEFERRED (no ITtsSynthesizer, CsatVoiceOptions intentionally absent).
  Return HTTP 501 + RFC 9457 ProblemDetails ("voice preview deferred"). Endpoint shape present, no fake.
- New DTOs (`UpsertCsatTemplateRequest`, `VoicePreviewDeferredDto`) registered in `ApiJsonContext`.
- `tests/Verbara.Platform.Api.Tests/CsatTemplateAdminEndpointsTests.cs` (NEW): upsert+readback, list,
  delete, fallback via provider, AdminOnly 403 (NonAdminAuthenticatedApiFactory), preview-voice 501.

## Gate
0-warning Release build, green tests, `openspec validate --all --strict`, commit on `feat/csat-runner`.
