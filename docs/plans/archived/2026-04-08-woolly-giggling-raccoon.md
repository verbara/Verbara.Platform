# Sprint 6: Microservice Extraction — PDF Renderer + Mail Service

## Context

Platform.Api has `IsAotCompatible=false` solely because of 3 libraries: QuestPDF, ScottPlot, MailKit. All 25/28 source projects are already AOT-compatible. Zero reflection in own code. Extracting these into standalone microservices:
1. Unblocks NativeAOT for Platform.Api (startup ~2s JIT → ~200ms AOT, 50% memory reduction)
2. Isolates CPU-intensive PDF rendering from real-time API responsiveness
3. Creates a reusable email service with Microsoft 365 Graph integration

This is a 4-phase sprint with 2 new projects, 2 Dockerfiles, and enabling AOT on Platform.Api.

## Architecture Overview

```
┌─────────────────┐     HTTP      ┌──────────────────────┐
│  Platform.Api    │─────────────→│  Platform.Renderer    │
│  (NativeAOT)    │              │  (QuestPDF+ScottPlot) │
│                  │              │  :5010                │
│  HttpPdf...      │              └──────────────────────┘
│  HttpEmail...    │     HTTP      ┌──────────────────────┐
│                  │─────────────→│  Platform.Mail        │
│                  │              │  (MailKit+Graph)      │
│                  │              │  :5020                │
└─────────────────┘              └──────────────────────┘
```

- **Monorepo**: Both services in Asterisk.Platform.slnx, share Platform.Core models
- **Communication**: HTTP via IHttpClientFactory named clients
- **Security**: `X-Service-Key` header for internal calls
- **Database**: PostgreSQL `mail` schema for OAuth tokens (same Postgres instance)

---

## Phase 1: Platform.Renderer (PDF Generation Microservice)

### 1.1 Create project structure

**New files:**
- `src/Asterisk.Platform.Renderer/Asterisk.Platform.Renderer.csproj`
- `src/Asterisk.Platform.Renderer/Program.cs`
- `src/Asterisk.Platform.Renderer/RenderEndpoint.cs`
- `src/Asterisk.Platform.Renderer/Dockerfile.renderer`

**csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <IsAotCompatible>false</IsAotCompatible>
    <EnableTrimAnalyzer>false</EnableTrimAnalyzer>
    <EnableAotAnalyzer>false</EnableAotAnalyzer>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Platform.Core\Asterisk.Platform.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="QuestPDF" />
    <PackageReference Include="ScottPlot" />
  </ItemGroup>
</Project>
```

**Program.cs:** Minimal API host, single endpoint, health check, concurrency semaphore (max 3).

**RenderEndpoint.cs:** `POST /api/v1/render?format=pdf|csv` — accepts `ReportData` JSON, returns rendered bytes.

**Dockerfile.renderer:** Standard dotnet SDK build → aspnet runtime, port 5010, curl for healthcheck.

### 1.2 Move renderers from Platform.Api

- **Copy** `PdfReportRenderer.cs` → `src/Asterisk.Platform.Renderer/PdfReportRenderer.cs`
- **Copy** `CsvReportRenderer.cs` → `src/Asterisk.Platform.Renderer/CsvReportRenderer.cs`
- Keep CsvReportRenderer also in Platform.Api (it's AOT-safe, no external libs)
- Add `RendererJsonContext` for AOT-safe serialization of `ReportData`

### 1.3 Create HttpPdfReportRenderer in Platform.Api

**New file:** `src/Asterisk.Platform.Api/Services/Reports/HttpPdfReportRenderer.cs`

```csharp
internal sealed class HttpPdfReportRenderer(IHttpClientFactory factory) : IReportRenderer
{
    public string ContentType => "application/pdf";
    public string FileExtension => "pdf";

    public async ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct)
    {
        using var client = factory.CreateClient("renderer");
        using var response = await client.PostAsJsonAsync("/api/v1/render?format=pdf", data, ApiJsonContext.Default.ReportData, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
```

### 1.4 Wire DI in Platform.Api Program.cs

```csharp
// Replace:
//   AddKeyedSingleton<IReportRenderer, PdfReportRenderer>("pdf");
// With:
builder.Services.AddHttpClient("renderer", c => {
    c.BaseAddress = new Uri(config["Services:Renderer:BaseUrl"] ?? "http://renderer:5010");
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.Add("X-Service-Key", config["Services:ServiceKey"]);
});
builder.Services.AddKeyedSingleton<IReportRenderer, HttpPdfReportRenderer>("pdf");
```

### 1.5 Add to solution and Docker

- Add project to `Asterisk.Platform.slnx`
- Add `renderer` service to `docker-compose.full.yml` and `docker/demo/docker-compose.demo.yml`
- Add `Services__Renderer__BaseUrl` env var to platform-api service
- Add `Services__ServiceKey` shared secret env var

### 1.6 Remove QuestPDF + ScottPlot from Platform.Api

- Delete `src/Asterisk.Platform.Api/Services/Reports/PdfReportRenderer.cs`
- Remove `<PackageReference Include="QuestPDF" />` and `<PackageReference Include="ScottPlot" />` from Api.csproj

### 1.7 Tests

- Add `tests/Asterisk.Platform.Renderer.Tests/` with render endpoint integration tests
- Update existing Api tests that reference PdfReportRenderer to use HttpPdfReportRenderer (mock IHttpClientFactory)

**Verification:** `dotnet build` + `dotnet test` pass with 0 warnings. Docker compose up, trigger manual report, PDF received via email.

---

## Phase 2: Platform.Mail (Email + Microsoft 365 Microservice)

### 2.1 Create project structure

**New files:**
- `src/Asterisk.Platform.Mail/Asterisk.Platform.Mail.csproj`
- `src/Asterisk.Platform.Mail/Program.cs`
- `src/Asterisk.Platform.Mail/Endpoints/SendEndpoint.cs`
- `src/Asterisk.Platform.Mail/Endpoints/TemplateEndpoint.cs`
- `src/Asterisk.Platform.Mail/Endpoints/MicrosoftAuthEndpoints.cs`
- `src/Asterisk.Platform.Mail/Endpoints/MailboxEndpoints.cs`
- `src/Asterisk.Platform.Mail/Services/SmtpSender.cs` (moved from Api)
- `src/Asterisk.Platform.Mail/Services/TemplateRenderer.cs` (moved from Api)
- `src/Asterisk.Platform.Mail/Services/GraphMailboxService.cs` (new)
- `src/Asterisk.Platform.Mail/Services/TokenStore.cs` (new)
- `src/Asterisk.Platform.Mail/Services/TokenRefreshService.cs` (new BackgroundService)
- `src/Asterisk.Platform.Mail/Templates/*.html` (moved from Api)
- `src/Asterisk.Platform.Mail/Migrations/001_MailSchema.sql`
- `src/Asterisk.Platform.Mail/Dockerfile.mail`

**csproj dependencies:**
- `Platform.Core` (shared models: EmailMessage, BrandingContext, SmtpOptions)
- `MailKit` + `MimeKit` (SMTP sending)
- `Microsoft.Graph` (Graph API client)
- `Azure.Identity` (Azure AD token exchange)
- `Npgsql` + `Dapper` (token store)

### 2.2 Internal Email Surface (replaces SmtpEmailService)

**`POST /api/v1/send`** — accepts `EmailMessage`, sends via SMTP, returns 202.
- Validates `X-Service-Key` header
- Retry logic (2 attempts, 30s delay) — same as current SmtpEmailService
- SmtpOptions from configuration

**`POST /api/v1/render-template`** — accepts template name + BrandingContext + variables, returns rendered HTML.
- EmbeddedEmailTemplateService moves here with all 6 HTML templates
- Returns `{ "html": "..." }`

### 2.3 Microsoft 365 Graph Integration

**PostgreSQL Migration (001_MailSchema.sql):**
```sql
CREATE SCHEMA IF NOT EXISTS mail;

CREATE TABLE mail.oauth_tokens (
    id             TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,
    tenant_id      TEXT NOT NULL,
    user_id        TEXT NOT NULL,
    provider       TEXT NOT NULL DEFAULT 'microsoft',
    access_token   TEXT NOT NULL,
    refresh_token  TEXT NOT NULL,
    expires_at     TIMESTAMPTZ NOT NULL,
    scopes         TEXT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, user_id, provider)
);
```

**OAuth Endpoints (minimal API, no Razor):**
- `GET /auth/microsoft/signin?tenantId={tid}&userId={uid}&returnUrl={url}` → builds Azure AD auth URL with PKCE (S256), stores state in DataProtection-encrypted cookie (5min TTL), returns 302 redirect
- `GET /auth/microsoft/callback?code={code}&state={state}` → validates state cookie, exchanges code for tokens via Azure AD token endpoint, stores in `mail.oauth_tokens`, redirects to `returnUrl`
- `POST /auth/microsoft/signout` → revokes tokens, deletes from store

**Token Management:**
- `TokenStore.cs` — Dapper CRUD for `mail.oauth_tokens` (GetAsync, UpsertAsync, DeleteAsync, GetExpiringAsync)
- `TokenRefreshService.cs` — BackgroundService, every 5 min, refreshes tokens expiring within 10 min via Azure AD `/oauth2/v2.0/token` refresh_token grant

**Graph Mailbox Endpoints (7 operations):**
- `GET /api/v1/mailbox/messages?unreadOnly=true&top=25&skip=0` — list messages
- `POST /api/v1/mailbox/messages/send` — send new email
- `POST /api/v1/mailbox/messages/{id}/reply` — reply to message
- `POST /api/v1/mailbox/messages/{id}/forward` — forward message
- `DELETE /api/v1/mailbox/messages/{id}` — delete message
- `PATCH /api/v1/mailbox/messages/{id}/read` — mark as read
- `GET /api/v1/mailbox/messages/{id}/attachments` — get attachments

All mailbox endpoints:
- Require `Authorization: Bearer {jwt}` header (Platform.Api JWT forwarded)
- Extract `tid` + `sub` claims to lookup the user's Graph token from `mail.oauth_tokens`
- Use `Microsoft.Graph.GraphServiceClient` with the user's access token
- Return 401 if no Graph token linked, 403 if token expired and refresh failed

### 2.4 Create HttpEmailService in Platform.Api

**New file:** `src/Asterisk.Platform.Api/Services/HttpEmailService.cs`

```csharp
internal sealed class HttpEmailService(IHttpClientFactory factory) : IEmailService
{
    public async ValueTask SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = factory.CreateClient("mail");
        using var response = await client.PostAsJsonAsync("/api/v1/send", message, ApiJsonContext.Default.EmailMessage, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

**New file:** `src/Asterisk.Platform.Api/Services/HttpEmailTemplateService.cs`

Calls `POST /api/v1/render-template` on the Mail service.

### 2.5 Wire DI in Platform.Api Program.cs

```csharp
// Replace:
//   AddSingleton<IEmailService, SmtpEmailService>();
//   AddSingleton<IEmailTemplateService, EmbeddedEmailTemplateService>();
// With:
builder.Services.AddHttpClient("mail", c => {
    c.BaseAddress = new Uri(config["Services:Mail:BaseUrl"] ?? "http://mail:5020");
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("X-Service-Key", config["Services:ServiceKey"]);
});
builder.Services.AddSingleton<IEmailService, HttpEmailService>();
builder.Services.AddSingleton<IEmailTemplateService, HttpEmailTemplateService>();
```

### 2.6 Graph Proxy Endpoints in Platform.Api

Add `MicrosoftMailEndpoints.cs` in Platform.Api that proxies mailbox requests to Platform.Mail:
- `GET /api/v1/mail/messages` → forwards to Mail service with JWT
- Same for all 7 operations
- Protected by `Authenticated` policy
- Alternative: expose Platform.Mail directly to frontend (simpler but less consistent)

**Recommendation:** Proxy through Platform.Api for consistent auth, rate limiting, and audit.

### 2.7 Remove MailKit from Platform.Api

- Delete `SmtpEmailService.cs`, `EmbeddedEmailTemplateService.cs`, `Services/Email/Templates/`
- Remove `<PackageReference Include="MailKit" />` from Api.csproj
- Remove `<EmbeddedResource Include="Services\Email\Templates\*.html" />` from Api.csproj

### 2.8 Add to solution and Docker

- Add projects to `Asterisk.Platform.slnx`
- Add `mail` service to compose files
- Add env vars: `Services__Mail__BaseUrl`, Microsoft config (`Microsoft__ClientId`, etc.)
- Mail service depends on `postgres` (healthy)

### 2.9 Tests

- `tests/Asterisk.Platform.Mail.Tests/` — SMTP send, template render, token CRUD, Graph mock tests
- Update Api tests: mock IHttpClientFactory for HttpEmailService/HttpEmailTemplateService

**Verification:** `dotnet build` + `dotnet test` pass. Docker compose up: password reset sends email, scheduled report sends email, notifications work.

---

## Phase 3: AOT Activation on Platform.Api

### 3.1 Enable AOT analyzers

In `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj`:
```xml
<IsAotCompatible>true</IsAotCompatible>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
```

### 3.2 Fix any AOT warnings

- Run `dotnet build` with analyzers on
- Fix any warnings (likely: JsonSerializer calls needing context, any remaining reflection)
- Ensure `ApiJsonContext` covers all new DTOs (HttpPdfReportRenderer request/response, HttpEmailService request/response)

### 3.3 NativeAOT publish

Update Dockerfile to:
```dockerfile
RUN dotnet publish src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj \
    -c Release -p:PublishAot=true -o /app
```

Runtime image changes from `mcr.microsoft.com/dotnet/aspnet:10.0` to a minimal base (or `dotnet/runtime-deps:10.0`).

### 3.4 Validate

- All 1546+ tests pass
- Docker compose full stack: all services healthy
- Startup time < 500ms (vs ~2s JIT)
- Memory baseline reduction measurable
- All 56 endpoint groups functional

---

## Phase 4: Docker Compose Integration

### New services in docker-compose.full.yml

```yaml
renderer:
  build:
    context: ..
    dockerfile: src/Asterisk.Platform.Renderer/Dockerfile.renderer
  environment:
    ASPNETCORE_URLS: http://+:5010
    Services__ServiceKey: ${SERVICE_KEY:-platform_internal_secret}
  healthcheck:
    test: ["CMD-SHELL", "curl -sf http://localhost:5010/health || exit 1"]
    interval: 30s
    timeout: 10s
    retries: 3
    start_period: 10s
  deploy:
    resources:
      limits:
        memory: 512M

mail:
  build:
    context: ..
    dockerfile: src/Asterisk.Platform.Mail/Dockerfile.mail
  environment:
    ASPNETCORE_URLS: http://+:5020
    ConnectionStrings__Postgres: Host=postgres;Database=platform;Username=platform;Password=${POSTGRES_PASSWORD}
    Smtp__Host: ${SMTP_HOST:-localhost}
    Smtp__Port: ${SMTP_PORT:-587}
    Services__ServiceKey: ${SERVICE_KEY:-platform_internal_secret}
    Microsoft__ClientId: ${MS_CLIENT_ID:-}
    Microsoft__TenantId: ${MS_TENANT_ID:-common}
  depends_on:
    postgres:
      condition: service_healthy
  healthcheck:
    test: ["CMD-SHELL", "curl -sf http://localhost:5020/health || exit 1"]
    interval: 30s
    timeout: 10s
    retries: 3
    start_period: 10s
```

### platform-api service changes

```yaml
platform-api:
  environment:
    Services__Renderer__BaseUrl: http://renderer:5010
    Services__Mail__BaseUrl: http://mail:5020
    Services__ServiceKey: ${SERVICE_KEY:-platform_internal_secret}
  depends_on:
    renderer:
      condition: service_healthy
    mail:
      condition: service_healthy
```

---

## Critical Files Summary

### Files to CREATE
| File | Purpose |
|------|---------|
| `src/Asterisk.Platform.Renderer/Asterisk.Platform.Renderer.csproj` | Renderer project |
| `src/Asterisk.Platform.Renderer/Program.cs` | Minimal API host |
| `src/Asterisk.Platform.Renderer/RenderEndpoint.cs` | POST /api/v1/render |
| `src/Asterisk.Platform.Renderer/PdfReportRenderer.cs` | Moved from Api |
| `src/Asterisk.Platform.Renderer/CsvReportRenderer.cs` | Moved from Api |
| `src/Asterisk.Platform.Renderer/RendererJsonContext.cs` | AOT serialization |
| `src/Asterisk.Platform.Renderer/Dockerfile.renderer` | Docker image |
| `src/Asterisk.Platform.Mail/Asterisk.Platform.Mail.csproj` | Mail project |
| `src/Asterisk.Platform.Mail/Program.cs` | Minimal API host |
| `src/Asterisk.Platform.Mail/Endpoints/SendEndpoint.cs` | SMTP send |
| `src/Asterisk.Platform.Mail/Endpoints/TemplateEndpoint.cs` | Template render |
| `src/Asterisk.Platform.Mail/Endpoints/MicrosoftAuthEndpoints.cs` | OAuth flow |
| `src/Asterisk.Platform.Mail/Endpoints/MailboxEndpoints.cs` | Graph API |
| `src/Asterisk.Platform.Mail/Services/SmtpSender.cs` | Moved from Api |
| `src/Asterisk.Platform.Mail/Services/TemplateRenderer.cs` | Moved from Api |
| `src/Asterisk.Platform.Mail/Services/GraphMailboxService.cs` | Graph client |
| `src/Asterisk.Platform.Mail/Services/TokenStore.cs` | Postgres token CRUD |
| `src/Asterisk.Platform.Mail/Services/TokenRefreshService.cs` | Auto-renewal |
| `src/Asterisk.Platform.Mail/Templates/*.html` | 6 email templates |
| `src/Asterisk.Platform.Mail/Migrations/001_MailSchema.sql` | DB schema |
| `src/Asterisk.Platform.Mail/Dockerfile.mail` | Docker image |
| `src/Asterisk.Platform.Mail/MailJsonContext.cs` | AOT serialization |
| `src/Asterisk.Platform.Api/Services/Reports/HttpPdfReportRenderer.cs` | HTTP proxy to Renderer |
| `src/Asterisk.Platform.Api/Services/HttpEmailService.cs` | HTTP proxy to Mail |
| `src/Asterisk.Platform.Api/Services/HttpEmailTemplateService.cs` | HTTP proxy to Mail |
| `src/Asterisk.Platform.Api/Endpoints/MicrosoftMailEndpoints.cs` | Graph proxy endpoints |
| `tests/Asterisk.Platform.Renderer.Tests/` | Renderer tests |
| `tests/Asterisk.Platform.Mail.Tests/` | Mail service tests |

### Files to MODIFY
| File | Change |
|------|--------|
| `Asterisk.Platform.slnx` | Add 4 new projects |
| `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj` | Remove QuestPDF/ScottPlot/MailKit, enable AOT |
| `src/Asterisk.Platform.Api/Program.cs` | Replace DI: HTTP clients + proxy implementations |
| `src/Asterisk.Platform.Api/ApiJsonContext.cs` | Add DTOs for HTTP calls |
| `docker/docker-compose.full.yml` | Add renderer + mail services |
| `docker/demo/docker-compose.demo.yml` | Add renderer + mail services |
| `docker/docker-compose.production.yml` | Add renderer + mail services |
| `Dockerfile` | Switch to PublishAot (Phase 3) |

### Files to DELETE (from Platform.Api)
| File | Reason |
|------|--------|
| `Services/Reports/PdfReportRenderer.cs` | Moved to Renderer |
| `Services/SmtpEmailService.cs` | Moved to Mail |
| `Services/Email/EmbeddedEmailTemplateService.cs` | Moved to Mail |
| `Services/Email/Templates/*.html` | Moved to Mail |

### Existing files to REUSE
| File | Purpose |
|------|---------|
| `Platform.Core/Reports/IReportRenderer.cs` | Shared interface (no changes) |
| `Platform.Core/Email/IEmailService.cs` | Shared interface (no changes) |
| `Platform.Core/Email/IEmailTemplateService.cs` | Shared interface (no changes) |
| `Platform.Core/Email/EmailMessage.cs` | Shared model (no changes) |
| `Platform.Core/Email/BrandingContext.cs` | Shared model (no changes) |
| `Platform.Core/Email/SmtpOptions.cs` | Shared config (no changes) |
| `Platform.Core/Reports/ReportData.cs` | Shared model (no changes) |

---

## Verification Plan

1. **Build:** `dotnet build Asterisk.Platform.slnx` — 0 errors, 0 warnings
2. **Tests:** `dotnet test Asterisk.Platform.slnx` — all pass (1546+ existing + new)
3. **Docker Full Stack:** `docker compose -f docker/docker-compose.full.yml up`
   - All services healthy (platform-api, renderer, mail, postgres, redis, web)
   - `curl http://localhost:5000/health` → 200
   - `curl http://localhost:5010/health` → 200
   - `curl http://localhost:5020/health` → 200
4. **PDF Render:** Create scheduled report via API, trigger manual run, verify PDF received via email
5. **Email Send:** Trigger password reset, verify email received
6. **Notifications:** Trigger critical notification (e.g., quota exceeded), verify email
7. **AOT Startup:** Measure Platform.Api startup time (target < 500ms)
8. **Graph API (if Azure AD configured):** Link Microsoft account, list messages, send test email

## Execution Strategy

Use Subagent-Driven Development with FCM batching:
- **Phase A (Foundation):** Renderer project + Mail project scaffolding — batch
- **Phase B (Critical):** OAuth flow, Graph service, token management — individual focused subagents
- **Phase C (Integration):** DI wiring, Docker compose, HTTP proxies — batch
- **Phase D (AOT):** Enable analyzers, fix warnings, NativeAOT publish — individual
