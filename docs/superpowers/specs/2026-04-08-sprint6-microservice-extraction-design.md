# Sprint 6: Microservice Extraction — PDF Renderer + Mail Service

## Problem

Platform.Api has `IsAotCompatible=false` because of QuestPDF, ScottPlot, and MailKit. These 3 libraries are the only AOT blockers — 25/28 source projects already pass AOT analysis, zero reflection in own code. Additionally, PDF rendering is CPU-intensive and email delivery has different scaling characteristics than the API hot path.

## Solution

Extract into 2 standalone microservices within the monorepo:

1. **Asterisk.Platform.Renderer** — Stateless PDF/CSV rendering (QuestPDF + ScottPlot)
2. **Asterisk.Platform.Mail** — SMTP sending (MailKit) + Microsoft 365 Graph API integration

Platform.Api calls both via `IHttpClientFactory` named clients. After extraction, Platform.Api becomes NativeAOT-publishable.

## Architecture

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

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Monorepo vs separate repos | Monorepo | Shared Platform.Core models, unified CI, small team |
| Inter-service auth | `X-Service-Key` header | Internal Docker network, no IP validation complexity |
| Token storage | PostgreSQL `mail` schema | Ecosystem is 100% Postgres, no MySQL |
| OAuth callbacks | Minimal API endpoints | Zero Razor Pages in codebase, 3 endpoints suffice |
| CSV renderer | Keep in Platform.Api | AOT-safe, no external libs, no value as HTTP call |
| Template rendering | Move to Mail service | Templates tightly coupled to email sending |

## Microservice 1: Platform.Renderer

### API

```
POST /api/v1/render?format=pdf|csv
  Request: ReportData (JSON)
  Response: rendered bytes (application/pdf or text/csv)
  Headers: X-Service-Key (required)

GET /health
  Response: { "status": "healthy", "activeConcurrency": N, "maxConcurrency": 3 }
```

### Implementation

- Port 5010, no database, no persistence
- Concurrency semaphore (max 3) moved from ReportSchedulerService
- Existing `PdfReportRenderer` and `CsvReportRenderer` move here verbatim
- `RendererJsonContext` for AOT-safe JSON deserialization of `ReportData`

### Platform.Api Changes

- New `HttpPdfReportRenderer : IReportRenderer` calls renderer via HTTP
- DI: `AddHttpClient("renderer")` + `AddKeyedSingleton<IReportRenderer, HttpPdfReportRenderer>("pdf")`
- Remove `QuestPDF` and `ScottPlot` package references

## Microservice 2: Platform.Mail

### Internal API (replaces SmtpEmailService)

```
POST /api/v1/send
  Request: EmailMessage (JSON)
  Response: 202 Accepted
  Headers: X-Service-Key (required)

POST /api/v1/render-template
  Request: { templateName, branding: BrandingContext, variables: {} }
  Response: { html: "..." }
  Headers: X-Service-Key (required)
```

### Microsoft 365 Graph API

#### OAuth 2.0 Endpoints
```
GET  /auth/microsoft/signin?tenantId={tid}&userId={uid}&returnUrl={url}
  → 302 redirect to Azure AD with PKCE (S256)
  → State in DataProtection-encrypted cookie (5min TTL)

GET  /auth/microsoft/callback?code={code}&state={state}
  → Exchange code for tokens → store in PostgreSQL → redirect to returnUrl

POST /auth/microsoft/signout
  → Revoke tokens → delete from store
```

#### Mailbox Operations (7 endpoints)
```
GET    /api/v1/mailbox/messages?unreadOnly=true&top=25&skip=0
POST   /api/v1/mailbox/messages/send
POST   /api/v1/mailbox/messages/{id}/reply
POST   /api/v1/mailbox/messages/{id}/forward
DELETE /api/v1/mailbox/messages/{id}
PATCH  /api/v1/mailbox/messages/{id}/read
GET    /api/v1/mailbox/messages/{id}/attachments
```

All require `Authorization: Bearer {jwt}` (Platform.Api JWT forwarded). Extract `tid` + `sub` to lookup user's Graph token.

### Token Management

PostgreSQL schema `mail`:
```sql
CREATE TABLE mail.oauth_tokens (
    id             TEXT PRIMARY KEY,
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

`TokenRefreshService` (BackgroundService): every 5 min, refreshes tokens expiring within 10 min.

### Platform.Api Changes

- New `HttpEmailService : IEmailService` calls mail service via HTTP
- New `HttpEmailTemplateService : IEmailTemplateService` calls mail service
- New `MicrosoftMailEndpoints` proxies Graph operations to mail service
- DI: `AddHttpClient("mail")` + singleton registrations
- Remove `MailKit` package reference
- Delete `SmtpEmailService`, `EmbeddedEmailTemplateService`, `Templates/`

## AOT Activation (after both extractions)

With QuestPDF, ScottPlot, and MailKit removed from Platform.Api:
1. Set `IsAotCompatible=true`, enable all analyzers
2. Fix any remaining warnings
3. Switch Dockerfile to `dotnet publish -p:PublishAot=true`
4. Runtime image: `dotnet/runtime-deps:10.0`

Expected: startup < 500ms (vs ~2s JIT), ~50% memory reduction.

## Docker Integration

Both microservices added to all compose files:
- `renderer`: port 5010, no DB dependency, 512M memory limit
- `mail`: port 5020, depends on postgres (healthy)
- `platform-api`: env vars for service URLs + shared `X-Service-Key`

## Relationship to Channel.Email

**Completely separate.** Channel.Email is the omnichannel email connector (inbound MIME parsing, outbound conversation replies, threading). Platform.Mail is system notifications + personal Microsoft 365 mailbox. Different purposes, different data flows. Future convergence possible (Graph as email channel backend) but not in scope.

## Multi-Tenancy

- **Renderer:** Receives tenant branding via `ReportData.PrimaryColor`. No data persistence.
- **Mail:** Token store keyed by `(tenant_id, user_id, provider)`. Graph operations scoped to authenticated user's mailbox (inherent tenant isolation).

## Implementation Phases

1. **Phase 1:** Platform.Renderer — scaffold, move renderers, create HTTP proxy, wire DI, Docker
2. **Phase 2:** Platform.Mail — scaffold, move SMTP/templates, Graph integration, token store, Docker
3. **Phase 3:** AOT Activation — enable analyzers, fix warnings, NativeAOT publish
4. **Phase 4:** Docker Compose — add both services to all compose files, verify full stack
