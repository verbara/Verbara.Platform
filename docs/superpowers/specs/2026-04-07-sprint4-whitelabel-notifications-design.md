# Sprint 4: White-Label + Notifications — Design Spec

**Date:** 2026-04-07
**Sprint:** v1.4.0 Sprint 4
**Decisions:** #4 (White-Label) + #6 (Notifications)
**Depends on:** Sprint 0 (security), Sprint 1 (suspension + settings), Sprint 2 (features + dunning), Sprint 3 (partner portal)

---

## Goal

Enable tenant-level branding (logo, colors, locale, subdomain) with 3-tier inheritance (Customer → Partner → Platform) and a persistent in-app notification system with role-based routing, dual delivery (SSE + email for critical), and branded transactional email templates.

## Architecture

White-label uses a dedicated `TenantBranding` model (1:1 with Tenant) stored in its own table, exposed via the TenantSettings facade and a public endpoint for pre-login branding. Subdomain resolution is enhanced to look up branding store. Notifications use a `Notification` model with category/severity, routed to users by role via a hardcoded `NotificationTypeRegistry`. Critical notifications trigger branded emails. Six HTML email templates use embedded resources with `{{placeholder}}` substitution (reusing Platform.Flows TemplateRenderer pattern). PDF reports are enhanced with tenant branding (logo, colors).

## Non-Goals (Deferred)

### v1.5.0
- User notification preferences per category/channel (opt-in/out per user)
- Quiet hours for notifications (suppress non-critical during off-hours)
- Notification digests (batched email summaries)
- SLA breach alerting (real-time notifications when SLA targets are missed)
- Idle agent detection notifications (supervisor alert when agent idle > threshold)
- Font family customization in TenantBranding
- LoginBackgroundUrl in TenantBranding
- MFA recovery codes email backup
- Invoice email with line items (requires Scriban for loops)

### v2.0
- Custom domains with Let's Encrypt + reverse proxy
- Custom CSS injection in TenantBranding (with sanitization)
- Push notifications (mobile/PWA) + SMS notification channel
- Slack/Teams notification integrations
- Admin-configurable notification rules (Genesys-style)
- Notification escalation chains + AI-driven alerts + auto-resolve

---

## Section 1: Data Models

### TenantBranding (Platform.Core)

```csharp
public sealed class TenantBranding
{
    public required string TenantId { get; init; }
    public string? DisplayName { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? PrimaryColor { get; set; }       // Hex: #1E40AF
    public string? SecondaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string? Locale { get; set; }              // es-CO, en-US
    public string? Timezone { get; set; }             // IANA: America/Bogota
    public string? Subdomain { get; set; }            // Unique, for tenant resolution
    public string? SupportEmail { get; set; }
    public string? SupportUrl { get; set; }
    public string? EmailFromName { get; set; }
    public string? EmailFromAddress { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### ITenantBrandingStore (Platform.Core)

```csharp
public interface ITenantBrandingStore
{
    ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default);
    ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default);
    ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default);
}
```

### Notification (Platform.Core)

```csharp
public sealed class Notification
{
    public required string NotificationId { get; init; }
    public required string TenantId { get; init; }
    public required string? UserId { get; init; }       // null = broadcast to role
    public required NotificationCategory Category { get; init; }
    public required NotificationSeverity Severity { get; init; }
    public required string Type { get; init; }           // e.g. billing.dunning_escalated
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? ActionUrl { get; init; }              // Deep link in UI
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; set; }
}

public enum NotificationCategory { Operational = 0, System = 1, Security = 2, Billing = 3 }
public enum NotificationSeverity { Info = 0, Warning = 1, Critical = 2 }
```

### INotificationStore (Platform.Core)

```csharp
public interface INotificationStore
{
    ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Notification>> ListAsync(string tenantId, string userId,
        bool? unreadOnly, int limit, int offset, CancellationToken ct = default);
    ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default);
    ValueTask SaveAsync(Notification notification, CancellationToken ct = default);
    ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default);
    ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default);
}
```

### Branding Inheritance (3-Tier Cascade)

```
Customer branding field ?? Partner branding field ?? Platform SystemSettings default
```

When a TenantBranding field is null for a Customer, it inherits from the Parent tenant's (Partner) branding. If the Partner's field is also null, it falls back to Platform SystemSettings (PlatformName, DefaultLanguage, DefaultTimezone). Inheritance is resolved at read time by the TenantSettings facade, not stored.

---

## Section 2: Notification Routing & Processing

### NotificationTypeRegistry (static)

14 notification types with hardcoded routing:

| Type | Category | Severity | Target Roles |
|------|----------|----------|-------------|
| `billing.dunning_escalated` | Billing | Critical | Admin, SystemAdmin, PlatformAdmin, PartnerAdmin, PartnerBilling |
| `billing.quota_warning` | Billing | Warning | Admin, SystemAdmin |
| `billing.quota_exceeded` | Billing | Critical | Admin, SystemAdmin, PlatformAdmin |
| `billing.tenant_suspended` | Billing | Critical | Admin, SystemAdmin, PlatformAdmin, PartnerAdmin, PartnerBilling |
| `billing.tenant_created` | System | Info | PlatformAdmin, PartnerAdmin |
| `security.account_locked` | Security | Critical | Admin, SystemAdmin |
| `security.suspicious_login` | Security | Warning | Admin, SystemAdmin |
| `system.license_expiring` | System | Warning | Admin, SystemAdmin, PlatformAdmin |
| `system.webhook_circuit_open` | System | Critical | Admin, SystemAdmin |
| `system.report_failed` | System | Warning | Admin, SystemAdmin |
| `operational.conversation_escalated` | Operational | Critical | Supervisor, Manager |
| `operational.agent_offline` | Operational | Warning | Supervisor, Manager |
| `gdpr.export_completed` | System | Info | Admin, SystemAdmin |
| `gdpr.purge_completed` | System | Info | Admin, SystemAdmin |

### Role-Based Routing Matrix

| Role | Operational | System | Security | Billing | Rationale |
|------|:-----------:|:------:|:--------:|:-------:|-----------|
| Agent | - | - | - | - | Flow state. Degradation via passive UI banner |
| Supervisor | **yes** | - | - | - | Can act: barge, whisper, reassign, manage queues |
| QA | - | - | - | - | Batch evaluator. No real-time operations |
| Manager | **yes** | **yes** | - | - | Can act: campaigns, agent roster. System for webhook/report failures |
| Admin | **yes** | **yes** | **yes** | **yes** | Can act on everything within tenant |
| System Admin | **yes** | **yes** | **yes** | **yes** | Can act on infra + auth config |
| Platform Admin | - | **yes** | **yes** | **yes** | Cross-tenant. Operational only via impersonation |
| API | - | - | - | - | Machine role, no notifications |
| Partner Admin | - | - | - | **yes** | Billing/lifecycle of child customers only |
| Partner Billing | - | - | - | **yes** (view) | Views invoices, needs dunning awareness |
| Partner Viewer | - | - | - | - | Read-only, no actionable notifications |

### NotificationService Flow

```
CreateNotificationAsync(tenantId, type, title, body, actionUrl?)
  │
  ├── 1. Lookup type in NotificationTypeRegistry → category, severity, targetRoles
  ├── 2. Dedup check: same (tenantId, type) within 5min → skip
  ├── 3. Load users in tenant with any of targetRoles (IUserRoleStore)
  ├── 4. For each user:
  │      ├── Create Notification record
  │      ├── Save to INotificationStore
  │      └── Publish NotificationEvent to PlatformEventBus (→ SSE)
  ├── 5. If severity == Critical:
  │      ├── Load TenantBranding → build BrandingContext (with inheritance)
  │      ├── Render notification-critical email template
  │      └── Send email via IEmailService (per-tenant From address)
  └── 6. Partner cross-tenant propagation:
         ├── If notification type targets Partner roles AND tenant has ParentTenantId:
         ├── Load parent tenant (Partner)
         └── Create notification copy in Partner tenant for PartnerAdmin/PartnerBilling
```

### Partner Cross-Tenant Notifications

When a Customer tenant event triggers a notification targeting Partner roles (dunning_escalated, tenant_suspended, tenant_created):

1. Notification created in Customer tenant → Customer Admin sees it
2. Copy created in Partner tenant → PartnerAdmin/PartnerBilling see it
3. If notification also targets PlatformAdmin → copy in host tenant

Uses `Tenant.ParentTenantId` to resolve the chain.

### SSE Integration

New event type on PlatformEventBus:

```csharp
public sealed record NotificationEvent(
    string TenantId, string Type, DateTimeOffset Timestamp,
    string NotificationId, string UserId,
    NotificationCategory Category, NotificationSeverity Severity,
    string Title, string Body, string? ActionUrl
) : PlatformEvent(TenantId, "notification.created", Timestamp);
```

Frontend listens for `notification.created` events on SSE stream → shows toast + updates bell icon badge.

---

## Section 3: Email Template System

### Architecture

```
Platform.Core/Email/
  IEmailTemplateService.cs          ← Interface
  BrandingContext.cs                 ← Record with branding data for templates

Platform.Api/Services/Email/
  EmbeddedEmailTemplateService.cs   ← Implementation: loads embedded HTML + placeholder substitution
  Templates/
    _base-layout.html               ← Shared layout (header logo + footer support)
    notification-critical.html      ← Content for severity=critical emails
    notification-warning.html       ← Content for severity=warning emails
    scheduled-report.html           ← Replaces current plaintext report delivery
    gdpr-export-ready.html          ← Export completed notification
    password-reset.html             ← Reset link with token
    welcome-user.html               ← Initial credentials
```

### BrandingContext

```csharp
public sealed record BrandingContext(
    string CompanyName,          // TenantBranding.DisplayName ?? Tenant.Name
    string? LogoUrl,             // TenantBranding.LogoUrl
    string PrimaryColor,         // TenantBranding.PrimaryColor ?? "#1E40AF"
    string SecondaryColor,       // TenantBranding.SecondaryColor ?? "#64748B"
    string AccentColor,          // TenantBranding.AccentColor ?? "#0D9488"
    string? SupportEmail,        // TenantBranding.SupportEmail
    string? SupportUrl,          // TenantBranding.SupportUrl
    string FromName,             // TenantBranding.EmailFromName ?? SmtpOptions.FromName
    string FromAddress           // TenantBranding.EmailFromAddress ?? SmtpOptions.FromAddress
);
```

Built with 3-tier inheritance: Customer → Partner → Platform defaults.

### IEmailTemplateService

```csharp
public interface IEmailTemplateService
{
    string Render(string templateName, BrandingContext branding,
                  IReadOnlyDictionary<string, string> variables);
}
```

### Rendering Process

```
1. Load _base-layout.html from embedded resources (cached after first read)
2. Load {templateName}.html from embedded resources (cached)
3. Replace {{Content}} in base layout with content template
4. Replace branding placeholders: {{CompanyName}}, {{LogoUrl}}, {{PrimaryColor}}, etc.
5. Replace content-specific variables: {{Title}}, {{Body}}, {{ActionUrl}}, etc.
6. Return final HTML string
```

Uses `{{variable}}` pattern from Platform.Flows/TemplateRenderer. Single-pass scan, AOT-safe. No external template engine dependencies.

### Base Layout Structure

```html
<!-- _base-layout.html -->
<table width="600" style="margin:0 auto; font-family:Arial,sans-serif;">
  <!-- Header -->
  <tr><td style="background:{{PrimaryColor}}; padding:20px; text-align:center;">
    <img src="{{LogoUrl}}" alt="{{CompanyName}}" height="40"
         style="max-width:200px;" />
  </td></tr>

  <!-- Content block -->
  <tr><td style="padding:30px; line-height:1.6; color:#1e293b;">
    {{Content}}
  </td></tr>

  <!-- Footer -->
  <tr><td style="background:#f8fafc; padding:20px; color:#64748b; font-size:12px; text-align:center;">
    {{CompanyName}}<br/>
    {{SupportEmail}} · {{SupportUrl}}
  </td></tr>
</table>
```

Table-based layout (Outlook compatible). Inline CSS. No dark mode engineering — defensive design with off-white backgrounds and transparent PNGs.

### Email Template Integration Points

| Caller | Template | Trigger |
|--------|----------|---------|
| NotificationService | notification-critical | Critical notification email |
| NotificationService | notification-warning | Included for future use. Sprint 4: only Critical triggers email automatically. Warning template available for manual/future use. |
| ReportSchedulerService | scheduled-report | Replaces current plaintext report delivery |
| GdprExportService | gdpr-export-ready | New: email when export completes |
| AuthEndpoints.ForgotPassword | password-reset | Closes gap: implements password reset email |
| User creation flow | welcome-user | New: initial credentials email |

### Per-Tenant From Address

EmailMessage extended with optional From override:

```csharp
public sealed class EmailMessage
{
    public required IReadOnlyList<EmailRecipient> Recipients { get; init; }
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<EmailAttachment>? Attachments { get; init; }
    public string? FromName { get; init; }        // NEW: override SmtpOptions.FromName
    public string? FromAddress { get; init; }     // NEW: override SmtpOptions.FromAddress
}
```

SmtpEmailService uses `message.FromName ?? _options.FromName` when building MimeMessage.

### PDF Report Branding

PdfReportRenderer.BuildHeader() enhanced:
- Logo image in header via QuestPDF `.Image(bytes)` method
- `PrimaryColor` replaces hardcoded `PdfColors.Blue.Darken3`
- `CompanyName` replaces generic "Tenant: {name}" label

ReportData extended with optional `BrandingContext`.

---

## Section 4: Endpoints

### Notification Endpoints (5 new, 1 file)

File: `NotificationEndpoints.cs`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/notifications` | Authenticated | List for caller (query: unreadOnly, limit, offset) |
| GET | `/notifications/unread-count` | Authenticated | Unread count for bell icon badge |
| GET | `/notifications/{id}` | Authenticated | Single notification detail |
| PUT | `/notifications/{id}/read` | Authenticated | Mark as read |
| PUT | `/notifications/read-all` | Authenticated | Mark all as read |

No AdminOnly or PlatformAdminOnly required — each user only sees their own notifications (filtered by userId from JWT). Ownership is implicit.

### Public Branding Endpoints (2 new, 1 file)

File: `BrandingEndpoints.cs`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/branding/{tenantId}` | None | Public branding for login page / webchat widget |
| GET | `/branding/by-subdomain/{subdomain}` | None | Resolve subdomain → branding |

Public (no auth) because frontend needs branding BEFORE login. Only visual fields exposed:

```csharp
public sealed record PublicBrandingDto(
    string? DisplayName, string? LogoUrl, string? FaviconUrl,
    string? PrimaryColor, string? SecondaryColor, string? AccentColor,
    string? Locale, string? Timezone);
```

Does NOT expose SupportEmail, EmailFromAddress, or internal fields.

### TenantSettings Facade Extension

**AdminOnly** (`TenantSettingsEndpoints.cs`):
- GET/PUT `/admin/tenant/settings` → `BrandingSettings` section added to TenantSettingsDto
- Admin can write: all branding fields EXCEPT Subdomain
- Subdomain is read-only for Admin (PlatformAdminOnly assigns it)

**PlatformAdminOnly** (`ManagementTenantSettingsEndpoints.cs`):
- GET/PUT `/management/tenants/{id}/settings` → `BrandingSettings` section added
- Platform Admin can write all branding fields INCLUDING Subdomain

### Password Reset Email Integration

`AuthEndpoints.ForgotPassword` modified:

```
POST /auth/forgot-password
  ├── 1. Validate email exists (existing)
  ├── 2. Generate reset token (existing)
  ├── 3. NEW: Load TenantBranding → build BrandingContext
  ├── 4. NEW: Render password-reset template
  ├── 5. NEW: Send email via IEmailService
  └── 6. Return "If the email exists, a reset link has been sent" (existing)
```

### TenantResolutionMiddleware Update

Subdomain resolution enhanced:

```
Current:  subdomain → used directly as tenantId
Updated:  subdomain → ITenantBrandingStore.GetBySubdomainAsync(subdomain) → tenantId
Fallback: if no branding match → try subdomain as tenantId directly (backward compat)
```

### Endpoint Count

53 → 55 (2 new groups: NotificationEndpoints, BrandingEndpoints)

### No New Permissions

Notifications use existing roles for routing. Notification endpoints are `Authenticated` (filtered by userId). Public branding endpoints require no auth. Branding admin goes through existing TenantSettings facade (AdminOnly / PlatformAdminOnly).

---

## Section 5: Storage & Migrations

### InMemory Stores (2 new)

**InMemoryTenantBrandingStore:**
- `ConcurrentDictionary<string, TenantBranding>` keyed by TenantId
- `GetBySubdomainAsync`: LINQ scan filtering by Subdomain (acceptable for dev/test)

**InMemoryNotificationStore:**
- `ConcurrentDictionary<string, Notification>` keyed by NotificationId
- `ListAsync`: LINQ filter by TenantId + UserId + unreadOnly, OrderByDescending CreatedAt, skip/take
- `CountUnreadAsync`: LINQ count where IsRead == false
- `MarkAllReadAsync`: LINQ filter + set IsRead=true, ReadAt=now

Both registered as singletons in `AddInMemoryStorage()`.

### Postgres Stores (2 new)

**PostgresTenantBrandingStore:**
- Class-based row type with `{get; init;}` (Dapper + Npgsql 9)
- `GetAsync`: SELECT by tenant_id
- `GetBySubdomainAsync`: SELECT by subdomain
- `UpsertAsync`: INSERT ... ON CONFLICT (tenant_id) DO UPDATE

**PostgresNotificationStore:**
- Class-based row type with `{get; init;}`
- `ListAsync`: SELECT with tenant_id + user_id filter + optional is_read + ORDER BY created_at DESC + LIMIT/OFFSET
- `CountUnreadAsync`: SELECT COUNT(*) with is_read = false
- `MarkAllReadAsync`: UPDATE SET is_read=true, read_at=now WHERE tenant_id + user_id + is_read=false

Both registered as singletons in `AddPostgresStorage()`.

### Migration 010: Branding & Notifications

```sql
-- tenant_branding (1:1 with tenants)
CREATE TABLE IF NOT EXISTS tenant_branding (
    tenant_id          TEXT PRIMARY KEY REFERENCES tenants(tenant_id),
    display_name       TEXT,
    logo_url           TEXT,
    favicon_url        TEXT,
    primary_color      TEXT,
    secondary_color    TEXT,
    accent_color       TEXT,
    locale             TEXT,
    timezone           TEXT,
    subdomain          TEXT,
    support_email      TEXT,
    support_url        TEXT,
    email_from_name    TEXT,
    email_from_address TEXT,
    created_at         TIMESTAMPTZ NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_tenant_branding_subdomain
    ON tenant_branding (subdomain) WHERE subdomain IS NOT NULL;

-- notifications
CREATE TABLE IF NOT EXISTS notifications (
    notification_id  TEXT PRIMARY KEY,
    tenant_id        TEXT NOT NULL,
    user_id          TEXT,
    category         INTEGER NOT NULL,
    severity         INTEGER NOT NULL,
    type             TEXT NOT NULL,
    title            TEXT NOT NULL,
    body             TEXT NOT NULL,
    action_url       TEXT,
    is_read          BOOLEAN NOT NULL DEFAULT false,
    created_at       TIMESTAMPTZ NOT NULL,
    read_at          TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_notifications_user_unread
    ON notifications (tenant_id, user_id, created_at DESC)
    WHERE is_read = false;

CREATE INDEX IF NOT EXISTS ix_notifications_tenant_type_dedup
    ON notifications (tenant_id, type, created_at DESC);
```

### DI & Wiring

```csharp
// In AddInMemoryStorage():
services.AddSingleton<ITenantBrandingStore, InMemoryTenantBrandingStore>();
services.AddSingleton<INotificationStore, InMemoryNotificationStore>();

// In AddPostgresStorage():
services.AddSingleton<ITenantBrandingStore, PostgresTenantBrandingStore>();
services.AddSingleton<INotificationStore, PostgresNotificationStore>();

// In Program.cs:
services.AddSingleton<IEmailTemplateService, EmbeddedEmailTemplateService>();
services.AddSingleton<NotificationService>();

// Endpoint mapping:
v1.MapNotificationEndpoints();
v1.MapBrandingEndpoints();
```

---

## Section 6: Testing (~35 new)

| Test File | Count | Coverage |
|-----------|-------|----------|
| `TenantBrandingStoreTests` | 4 | CRUD + GetBySubdomain + upsert update + null subdomain |
| `NotificationStoreTests` | 5 | Save + List + CountUnread + MarkRead + MarkAllRead |
| `NotificationServiceTests` | 6 | Create by type + role routing + dedup 5min + critical→email + partner cross-tenant + severity filter |
| `NotificationEndpointsTests` | 5 | List paged + unread count + mark read + mark all + ownership |
| `BrandingEndpointsTests` | 4 | Public get by tenantId + get by subdomain + 404 + only public fields |
| `EmailTemplateServiceTests` | 4 | Render with branding + base layout injection + missing template fallback + all placeholders replaced |
| `BrandingInheritanceTests` | 3 | Customer inherits Partner + Partner inherits Platform + partial overrides |
| `PasswordResetEmailTests` | 2 | ForgotPassword sends email + email contains reset link |
| `SubdomainResolutionTests` | 2 | Subdomain→tenantId via branding store + fallback to direct tenantId |

Expected test count: 1472 → ~1507.

---

## File Inventory

| Category | New Files | Modified Files |
|----------|-----------|----------------|
| Models/Interfaces | 4 (TenantBranding, ITenantBrandingStore, Notification, INotificationStore) | 0 |
| Enums | 2 (NotificationCategory, NotificationSeverity) | 0 |
| Services | 3 (NotificationService, EmbeddedEmailTemplateService, BrandingContext+IEmailTemplateService) | 2 (ReportSchedulerService, GdprExportService) |
| Registry | 1 (NotificationTypeRegistry) | 0 |
| Endpoints | 2 (NotificationEndpoints, BrandingEndpoints) | 3 (TenantSettingsEndpoints, ManagementTenantSettingsEndpoints, AuthEndpoints) |
| Email Templates | 7 (_base-layout + 6 content) | 0 |
| Storage InMemory | 2 (InMemoryTenantBrandingStore, InMemoryNotificationStore) | 1 (ServiceCollectionExtensions) |
| Storage Postgres | 2 (PostgresTenantBrandingStore, PostgresNotificationStore) + migration 010 | 1 (ServiceCollectionExtensions) |
| Middleware | 0 | 1 (TenantResolutionMiddleware) |
| Email | 0 | 2 (EmailMessage, SmtpEmailService) |
| PDF | 0 | 1 (PdfReportRenderer) |
| Serialization | 0 | 1 (ApiJsonContext) |
| Program.cs | 0 | 1 |
| Tests | 9 new test files | 0 |
| **Total** | **~24 new** | **~12 modified** |
