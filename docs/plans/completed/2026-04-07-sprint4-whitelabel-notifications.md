# Sprint 4: White-Label + Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add tenant branding (logo, colors, locale, subdomain) with 3-tier inheritance and a persistent notification system with role-based routing, SSE delivery, and branded email templates.

**Architecture:** TenantBranding model (1:1 with Tenant) stored in dedicated table, exposed via TenantSettings facade and public endpoint. Notifications use category/severity enums routed to users by role via static NotificationTypeRegistry. Critical notifications trigger branded HTML emails rendered from embedded resource templates using {{placeholder}} substitution. PDF reports enhanced with tenant branding.

**Tech Stack:** .NET 10 Native AOT, Dapper + Npgsql 9, MailKit, QuestPDF, xUnit + FluentAssertions + NSubstitute, System.Reactive (SSE)

**Spec:** `docs/superpowers/specs/2026-04-07-sprint4-whitelabel-notifications-design.md`

---

## File Structure

### New Files (~24)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Platform.Core/Branding/TenantBranding.cs` | Branding model (14 fields) |
| `src/Asterisk.Platform.Core/Branding/ITenantBrandingStore.cs` | Store interface (Get, GetBySubdomain, Upsert) |
| `src/Asterisk.Platform.Core/Notifications/Notification.cs` | Notification model + enums |
| `src/Asterisk.Platform.Core/Notifications/INotificationStore.cs` | Store interface (CRUD + mark read) |
| `src/Asterisk.Platform.Core/Notifications/NotificationTypeRegistry.cs` | Static routing: type → category+severity+roles |
| `src/Asterisk.Platform.Core/Email/BrandingContext.cs` | Branding data record for templates |
| `src/Asterisk.Platform.Core/Email/IEmailTemplateService.cs` | Template renderer interface |
| `src/Asterisk.Platform.Api/Services/Email/EmbeddedEmailTemplateService.cs` | Loads HTML embedded resources + substitution |
| `src/Asterisk.Platform.Api/Services/Email/Templates/_base-layout.html` | Shared email layout (header+footer) |
| `src/Asterisk.Platform.Api/Services/Email/Templates/notification-critical.html` | Critical alert content |
| `src/Asterisk.Platform.Api/Services/Email/Templates/notification-warning.html` | Warning alert content |
| `src/Asterisk.Platform.Api/Services/Email/Templates/scheduled-report.html` | Report delivery content |
| `src/Asterisk.Platform.Api/Services/Email/Templates/gdpr-export-ready.html` | Export ready content |
| `src/Asterisk.Platform.Api/Services/Email/Templates/password-reset.html` | Reset link content |
| `src/Asterisk.Platform.Api/Services/Email/Templates/welcome-user.html` | Welcome content |
| `src/Asterisk.Platform.Api/Services/NotificationService.cs` | Orchestrator: create + route + email + SSE |
| `src/Asterisk.Platform.Api/Endpoints/NotificationEndpoints.cs` | 5 endpoints: list, count, get, mark read, mark all |
| `src/Asterisk.Platform.Api/Endpoints/BrandingEndpoints.cs` | 2 public endpoints: by tenantId, by subdomain |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantBrandingStore.cs` | ConcurrentDictionary impl |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryNotificationStore.cs` | ConcurrentDictionary impl |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantBrandingStore.cs` | Dapper impl |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresNotificationStore.cs` | Dapper impl |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/010_BrandingNotifications.sql` | Both tables + indexes |

### Modified Files (~12)

| File | Change |
|------|--------|
| `src/Asterisk.Platform.Core/Email/EmailMessage.cs` | Add FromName?, FromAddress? |
| `src/Asterisk.Platform.Core/PlatformEventBus.cs` | Add NotificationEvent record |
| `src/Asterisk.Platform.Api/Services/SmtpEmailService.cs` | Use message.FromName/FromAddress override |
| `src/Asterisk.Platform.Api/Services/Reports/PdfReportRenderer.cs` | Branding in header (logo, colors) |
| `src/Asterisk.Platform.Api/Services/Reports/ReportSchedulerService.cs` | Use branded email template |
| `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` | BrandingSettings section in DTO |
| `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs` | BrandingSettings section |
| `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs` | Password reset email |
| `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs` | Subdomain→branding store lookup |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | New DTO registrations |
| `src/Asterisk.Platform.Api/Program.cs` | DI + endpoint mapping |
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register branding + notification stores |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Register branding + notification stores |

### Test Files (~9)

| File | Tests |
|------|-------|
| `tests/Asterisk.Platform.Api.Tests/TenantBrandingStoreTests.cs` | 4 |
| `tests/Asterisk.Platform.Api.Tests/NotificationStoreTests.cs` | 5 |
| `tests/Asterisk.Platform.Api.Tests/NotificationServiceTests.cs` | 6 |
| `tests/Asterisk.Platform.Api.Tests/NotificationEndpointsTests.cs` | 5 |
| `tests/Asterisk.Platform.Api.Tests/BrandingEndpointsTests.cs` | 4 |
| `tests/Asterisk.Platform.Api.Tests/EmailTemplateServiceTests.cs` | 4 |
| `tests/Asterisk.Platform.Api.Tests/BrandingInheritanceTests.cs` | 3 |
| `tests/Asterisk.Platform.Api.Tests/PasswordResetEmailTests.cs` | 2 |
| `tests/Asterisk.Platform.Api.Tests/SubdomainResolutionTests.cs` | 2 |

---

## Task 1: TenantBranding Model + Store Interface + InMemory Store

**Files:**
- Create: `src/Asterisk.Platform.Core/Branding/TenantBranding.cs`
- Create: `src/Asterisk.Platform.Core/Branding/ITenantBrandingStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantBrandingStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/TenantBrandingStoreTests.cs`

- [ ] **Step 1: Create TenantBranding model**

```csharp
// src/Asterisk.Platform.Core/Branding/TenantBranding.cs
namespace Asterisk.Platform.Core.Branding;

public sealed class TenantBranding
{
    public required string TenantId { get; init; }
    public string? DisplayName { get; set; }
    public string? LogoUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? SecondaryColor { get; set; }
    public string? AccentColor { get; set; }
    public string? Locale { get; set; }
    public string? Timezone { get; set; }
    public string? Subdomain { get; set; }
    public string? SupportEmail { get; set; }
    public string? SupportUrl { get; set; }
    public string? EmailFromName { get; set; }
    public string? EmailFromAddress { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Create ITenantBrandingStore interface**

```csharp
// src/Asterisk.Platform.Core/Branding/ITenantBrandingStore.cs
namespace Asterisk.Platform.Core.Branding;

public interface ITenantBrandingStore
{
    ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default);
    ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default);
    ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write failing tests**

```csharp
// tests/Asterisk.Platform.Api.Tests/TenantBrandingStoreTests.cs
namespace Asterisk.Platform.Api.Tests;

public class TenantBrandingStoreTests
{
    private readonly InMemoryTenantBrandingStore _store = new();

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldInsertAndRetrieve_WhenNew()
    {
        var branding = new TenantBranding
        {
            TenantId = "tenant-1",
            DisplayName = "Acme Corp",
            PrimaryColor = "#1E40AF",
            Subdomain = "acme",
        };
        await _store.UpsertAsync(branding);
        var result = await _store.GetAsync("tenant-1");
        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Acme Corp");
        result.PrimaryColor.Should().Be("#1E40AF");
    }

    [Fact]
    public async Task GetBySubdomainAsync_ShouldResolve_WhenSubdomainExists()
    {
        var branding = new TenantBranding
        {
            TenantId = "tenant-2",
            Subdomain = "partner-x",
        };
        await _store.UpsertAsync(branding);
        var result = await _store.GetBySubdomainAsync("partner-x");
        result.Should().NotBeNull();
        result!.TenantId.Should().Be("tenant-2");
    }

    [Fact]
    public async Task UpsertAsync_ShouldUpdate_WhenExists()
    {
        var branding = new TenantBranding { TenantId = "tenant-3", DisplayName = "Old" };
        await _store.UpsertAsync(branding);
        branding.DisplayName = "New";
        await _store.UpsertAsync(branding);
        var result = await _store.GetAsync("tenant-3");
        result!.DisplayName.Should().Be("New");
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantBrandingStoreTests" -v q`
Expected: FAIL (InMemoryTenantBrandingStore does not exist)

- [ ] **Step 5: Implement InMemoryTenantBrandingStore**

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryTenantBrandingStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Core.Branding;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantBrandingStore : ITenantBrandingStore
{
    private readonly ConcurrentDictionary<string, TenantBranding> _store = new();

    public ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        _store.TryGetValue(tenantId, out var branding);
        return ValueTask.FromResult(branding);
    }

    public ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        var branding = _store.Values.FirstOrDefault(b =>
            string.Equals(b.Subdomain, subdomain, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(branding);
    }

    public ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default)
    {
        branding.UpdatedAt = DateTimeOffset.UtcNow;
        _store[branding.TenantId] = branding;
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 6: Register in InMemory DI**

Add to `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` in `AddInMemoryStorage()`:

```csharp
services.AddSingleton<ITenantBrandingStore, InMemoryTenantBrandingStore>();
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantBrandingStoreTests" -v q`
Expected: 4 passed

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Platform.Core/Branding/ src/Asterisk.Platform.Storage.InMemory/InMemoryTenantBrandingStore.cs src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs tests/Asterisk.Platform.Api.Tests/TenantBrandingStoreTests.cs
git commit -m "feat: TenantBranding model + InMemory store"
```

---

## Task 2: Notification Model + Store Interface + InMemory Store

**Files:**
- Create: `src/Asterisk.Platform.Core/Notifications/Notification.cs`
- Create: `src/Asterisk.Platform.Core/Notifications/INotificationStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryNotificationStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/NotificationStoreTests.cs`

- [ ] **Step 1: Create Notification model + enums**

```csharp
// src/Asterisk.Platform.Core/Notifications/Notification.cs
namespace Asterisk.Platform.Core.Notifications;

public sealed class Notification
{
    public required string NotificationId { get; init; }
    public required string TenantId { get; init; }
    public required string? UserId { get; init; }
    public required NotificationCategory Category { get; init; }
    public required NotificationSeverity Severity { get; init; }
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? ActionUrl { get; init; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public enum NotificationCategory { Operational = 0, System = 1, Security = 2, Billing = 3 }

public enum NotificationSeverity { Info = 0, Warning = 1, Critical = 2 }
```

- [ ] **Step 2: Create INotificationStore interface**

```csharp
// src/Asterisk.Platform.Core/Notifications/INotificationStore.cs
namespace Asterisk.Platform.Core.Notifications;

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

- [ ] **Step 3: Write failing tests**

```csharp
// tests/Asterisk.Platform.Api.Tests/NotificationStoreTests.cs
namespace Asterisk.Platform.Api.Tests;

public class NotificationStoreTests
{
    private readonly InMemoryNotificationStore _store = new();

    private Notification CreateNotification(string id, string tenantId = "t1", string userId = "u1",
        NotificationSeverity severity = NotificationSeverity.Info, bool isRead = false) =>
        new()
        {
            NotificationId = id, TenantId = tenantId, UserId = userId,
            Category = NotificationCategory.System, Severity = severity,
            Type = "system.test", Title = "Test", Body = "Test body",
            IsRead = isRead,
        };

    [Fact]
    public async Task SaveAsync_ShouldPersistAndRetrieve()
    {
        var n = CreateNotification("n1");
        await _store.SaveAsync(n);
        var result = await _store.GetAsync("n1");
        result.Should().NotBeNull();
        result!.Title.Should().Be("Test");
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByUserAndTenant()
    {
        await _store.SaveAsync(CreateNotification("n1", userId: "u1"));
        await _store.SaveAsync(CreateNotification("n2", userId: "u2"));
        await _store.SaveAsync(CreateNotification("n3", userId: "u1"));

        var result = await _store.ListAsync("t1", "u1", null, 10, 0);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task CountUnreadAsync_ShouldCountOnlyUnread()
    {
        await _store.SaveAsync(CreateNotification("n1"));
        await _store.SaveAsync(CreateNotification("n2", isRead: true));
        await _store.SaveAsync(CreateNotification("n3"));

        var count = await _store.CountUnreadAsync("t1", "u1");
        count.Should().Be(2);
    }

    [Fact]
    public async Task MarkReadAsync_ShouldSetIsReadAndReadAt()
    {
        await _store.SaveAsync(CreateNotification("n1"));
        await _store.MarkReadAsync("n1");

        var result = await _store.GetAsync("n1");
        result!.IsRead.Should().BeTrue();
        result.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkAllReadAsync_ShouldMarkAllForUser()
    {
        await _store.SaveAsync(CreateNotification("n1", userId: "u1"));
        await _store.SaveAsync(CreateNotification("n2", userId: "u1"));
        await _store.SaveAsync(CreateNotification("n3", userId: "u2"));

        await _store.MarkAllReadAsync("t1", "u1");

        var u1Count = await _store.CountUnreadAsync("t1", "u1");
        var u2Count = await _store.CountUnreadAsync("t1", "u2");
        u1Count.Should().Be(0);
        u2Count.Should().Be(1);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "NotificationStoreTests" -v q`
Expected: FAIL

- [ ] **Step 5: Implement InMemoryNotificationStore**

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryNotificationStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Core.Notifications;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly ConcurrentDictionary<string, Notification> _store = new();

    public ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
    {
        _store.TryGetValue(notificationId, out var n);
        return ValueTask.FromResult(n);
    }

    public ValueTask<IReadOnlyList<Notification>> ListAsync(string tenantId, string userId,
        bool? unreadOnly, int limit, int offset, CancellationToken ct = default)
    {
        var query = _store.Values
            .Where(n => n.TenantId == tenantId && n.UserId == userId);

        if (unreadOnly == true)
            query = query.Where(n => !n.IsRead);

        IReadOnlyList<Notification> result = query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(offset).Take(limit).ToList();

        return ValueTask.FromResult(result);
    }

    public ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var count = _store.Values
            .Count(n => n.TenantId == tenantId && n.UserId == userId && !n.IsRead);
        return ValueTask.FromResult(count);
    }

    public ValueTask SaveAsync(Notification notification, CancellationToken ct = default)
    {
        _store[notification.NotificationId] = notification;
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default)
    {
        if (_store.TryGetValue(notificationId, out var n))
        {
            n.IsRead = true;
            n.ReadAt = DateTimeOffset.UtcNow;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var n in _store.Values.Where(n =>
            n.TenantId == tenantId && n.UserId == userId && !n.IsRead))
        {
            n.IsRead = true;
            n.ReadAt = now;
        }
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 6: Register in InMemory DI**

Add to `AddInMemoryStorage()`:

```csharp
services.AddSingleton<INotificationStore, InMemoryNotificationStore>();
```

- [ ] **Step 7: Run tests, verify pass, commit**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "NotificationStoreTests" -v q`
Expected: 5 passed

```bash
git add src/Asterisk.Platform.Core/Notifications/ src/Asterisk.Platform.Storage.InMemory/InMemoryNotificationStore.cs src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs tests/Asterisk.Platform.Api.Tests/NotificationStoreTests.cs
git commit -m "feat: Notification model + enums + InMemory store"
```

---

## Task 3: PostgresTenantBrandingStore + PostgresNotificationStore + Migration 010

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/010_BrandingNotifications.sql`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantBrandingStore.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresNotificationStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create migration 010**

```sql
-- src/Asterisk.Platform.Storage.Postgres/Migrations/010_BrandingNotifications.sql

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

- [ ] **Step 2: Implement PostgresTenantBrandingStore**

```csharp
// src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantBrandingStore.cs
using Asterisk.Platform.Core.Branding;
using Dapper;
using Npgsql;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantBrandingStore(NpgsqlDataSource dataSource) : ITenantBrandingStore
{
    public async ValueTask<TenantBranding?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BrandingRow>(
            "SELECT * FROM tenant_branding WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToModel();
    }

    public async ValueTask<TenantBranding?> GetBySubdomainAsync(string subdomain, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BrandingRow>(
            "SELECT * FROM tenant_branding WHERE subdomain = @Subdomain",
            new { Subdomain = subdomain });
        return row?.ToModel();
    }

    public async ValueTask UpsertAsync(TenantBranding branding, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO tenant_branding (
                tenant_id, display_name, logo_url, favicon_url,
                primary_color, secondary_color, accent_color,
                locale, timezone, subdomain,
                support_email, support_url, email_from_name, email_from_address,
                created_at, updated_at
            ) VALUES (
                @TenantId, @DisplayName, @LogoUrl, @FaviconUrl,
                @PrimaryColor, @SecondaryColor, @AccentColor,
                @Locale, @Timezone, @Subdomain,
                @SupportEmail, @SupportUrl, @EmailFromName, @EmailFromAddress,
                @CreatedAt, @UpdatedAt
            )
            ON CONFLICT (tenant_id) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                logo_url = EXCLUDED.logo_url,
                favicon_url = EXCLUDED.favicon_url,
                primary_color = EXCLUDED.primary_color,
                secondary_color = EXCLUDED.secondary_color,
                accent_color = EXCLUDED.accent_color,
                locale = EXCLUDED.locale,
                timezone = EXCLUDED.timezone,
                subdomain = EXCLUDED.subdomain,
                support_email = EXCLUDED.support_email,
                support_url = EXCLUDED.support_url,
                email_from_name = EXCLUDED.email_from_name,
                email_from_address = EXCLUDED.email_from_address,
                updated_at = EXCLUDED.updated_at
            """, new
        {
            branding.TenantId, branding.DisplayName, branding.LogoUrl, branding.FaviconUrl,
            branding.PrimaryColor, branding.SecondaryColor, branding.AccentColor,
            branding.Locale, branding.Timezone, branding.Subdomain,
            branding.SupportEmail, branding.SupportUrl, branding.EmailFromName, branding.EmailFromAddress,
            CreatedAt = branding.CreatedAt.UtcDateTime, UpdatedAt = DateTimeOffset.UtcNow.UtcDateTime,
        });
    }

    private sealed class BrandingRow
    {
        public string tenant_id { get; init; } = "";
        public string? display_name { get; init; }
        public string? logo_url { get; init; }
        public string? favicon_url { get; init; }
        public string? primary_color { get; init; }
        public string? secondary_color { get; init; }
        public string? accent_color { get; init; }
        public string? locale { get; init; }
        public string? timezone { get; init; }
        public string? subdomain { get; init; }
        public string? support_email { get; init; }
        public string? support_url { get; init; }
        public string? email_from_name { get; init; }
        public string? email_from_address { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public TenantBranding ToModel() => new()
        {
            TenantId = tenant_id, DisplayName = display_name, LogoUrl = logo_url,
            FaviconUrl = favicon_url, PrimaryColor = primary_color, SecondaryColor = secondary_color,
            AccentColor = accent_color, Locale = locale, Timezone = timezone, Subdomain = subdomain,
            SupportEmail = support_email, SupportUrl = support_url,
            EmailFromName = email_from_name, EmailFromAddress = email_from_address,
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
        };
    }
}
```

- [ ] **Step 3: Implement PostgresNotificationStore**

```csharp
// src/Asterisk.Platform.Storage.Postgres/Stores/PostgresNotificationStore.cs
using Asterisk.Platform.Core.Notifications;
using Dapper;
using Npgsql;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresNotificationStore(NpgsqlDataSource dataSource) : INotificationStore
{
    public async ValueTask<Notification?> GetAsync(string notificationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<NotificationRow>(
            "SELECT * FROM notifications WHERE notification_id = @Id", new { Id = notificationId });
        return row?.ToModel();
    }

    public async ValueTask<IReadOnlyList<Notification>> ListAsync(string tenantId, string userId,
        bool? unreadOnly, int limit, int offset, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var sql = """
            SELECT * FROM notifications
            WHERE tenant_id = @TenantId AND user_id = @UserId
            """;
        if (unreadOnly == true) sql += " AND is_read = false";
        sql += " ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset";

        var rows = await conn.QueryAsync<NotificationRow>(sql,
            new { TenantId = tenantId, UserId = userId, Limit = limit, Offset = offset });
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async ValueTask<int> CountUnreadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM notifications WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            new { TenantId = tenantId, UserId = userId });
    }

    public async ValueTask SaveAsync(Notification notification, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO notifications (notification_id, tenant_id, user_id, category, severity, type, title, body, action_url, is_read, created_at, read_at)
            VALUES (@NotificationId, @TenantId, @UserId, @Category, @Severity, @Type, @Title, @Body, @ActionUrl, @IsRead, @CreatedAt, @ReadAt)
            """, new
        {
            notification.NotificationId, notification.TenantId, notification.UserId,
            Category = (int)notification.Category, Severity = (int)notification.Severity,
            notification.Type, notification.Title, notification.Body, notification.ActionUrl,
            notification.IsRead,
            CreatedAt = notification.CreatedAt.UtcDateTime,
            ReadAt = notification.ReadAt?.UtcDateTime,
        });
    }

    public async ValueTask MarkReadAsync(string notificationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @Now WHERE notification_id = @Id",
            new { Id = notificationId, Now = DateTime.UtcNow });
    }

    public async ValueTask MarkAllReadAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE notifications SET is_read = true, read_at = @Now WHERE tenant_id = @TenantId AND user_id = @UserId AND is_read = false",
            new { TenantId = tenantId, UserId = userId, Now = DateTime.UtcNow });
    }

    private sealed class NotificationRow
    {
        public string notification_id { get; init; } = "";
        public string tenant_id { get; init; } = "";
        public string? user_id { get; init; }
        public int category { get; init; }
        public int severity { get; init; }
        public string type { get; init; } = "";
        public string title { get; init; } = "";
        public string body { get; init; } = "";
        public string? action_url { get; init; }
        public bool is_read { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? read_at { get; init; }

        public Notification ToModel() => new()
        {
            NotificationId = notification_id, TenantId = tenant_id, UserId = user_id,
            Category = (NotificationCategory)category, Severity = (NotificationSeverity)severity,
            Type = type, Title = title, Body = body, ActionUrl = action_url, IsRead = is_read,
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
            ReadAt = read_at is not null ? new DateTimeOffset(read_at.Value, TimeSpan.Zero) : null,
        };
    }
}
```

- [ ] **Step 4: Register in Postgres DI**

Add to `AddPostgresStorage()`:

```csharp
services.AddSingleton<ITenantBrandingStore, PostgresTenantBrandingStore>();
services.AddSingleton<INotificationStore, PostgresNotificationStore>();
```

- [ ] **Step 5: Build, commit**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: 0 warnings, 0 errors

```bash
git add src/Asterisk.Platform.Storage.Postgres/Migrations/010_BrandingNotifications.sql src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantBrandingStore.cs src/Asterisk.Platform.Storage.Postgres/Stores/PostgresNotificationStore.cs src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs
git commit -m "feat: Postgres branding + notification stores + migration 010"
```

---

## Task 4: Email Template System

**Files:**
- Create: `src/Asterisk.Platform.Core/Email/BrandingContext.cs`
- Create: `src/Asterisk.Platform.Core/Email/IEmailTemplateService.cs`
- Create: `src/Asterisk.Platform.Api/Services/Email/EmbeddedEmailTemplateService.cs`
- Create: 7 HTML template files under `src/Asterisk.Platform.Api/Services/Email/Templates/`
- Modify: `src/Asterisk.Platform.Core/Email/EmailMessage.cs` (add FromName, FromAddress)
- Modify: `src/Asterisk.Platform.Api/Services/SmtpEmailService.cs` (per-tenant From)
- Create: `tests/Asterisk.Platform.Api.Tests/EmailTemplateServiceTests.cs`

- [ ] **Step 1: Create BrandingContext record**

```csharp
// src/Asterisk.Platform.Core/Email/BrandingContext.cs
namespace Asterisk.Platform.Core.Email;

public sealed record BrandingContext(
    string CompanyName,
    string? LogoUrl,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string? SupportEmail,
    string? SupportUrl,
    string FromName,
    string FromAddress);
```

- [ ] **Step 2: Create IEmailTemplateService interface**

```csharp
// src/Asterisk.Platform.Core/Email/IEmailTemplateService.cs
namespace Asterisk.Platform.Core.Email;

public interface IEmailTemplateService
{
    string Render(string templateName, BrandingContext branding,
                  IReadOnlyDictionary<string, string> variables);
}
```

- [ ] **Step 3: Add FromName/FromAddress to EmailMessage**

Modify `src/Asterisk.Platform.Core/Email/EmailMessage.cs`:

```csharp
public sealed class EmailMessage
{
    public required IReadOnlyList<EmailRecipient> Recipients { get; init; }
    public required string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyList<EmailAttachment>? Attachments { get; init; }
    public string? FromName { get; init; }
    public string? FromAddress { get; init; }
}
```

- [ ] **Step 4: Update SmtpEmailService to use per-tenant From**

Modify `BuildMimeMessage` in `src/Asterisk.Platform.Api/Services/SmtpEmailService.cs`:

```csharp
private MimeMessage BuildMimeMessage(EmailMessage message)
{
    var mime = new MimeMessage();
    var fromName = message.FromName ?? _options.FromName;
    var fromAddress = message.FromAddress ?? _options.FromAddress;
    mime.From.Add(new MailboxAddress(fromName, fromAddress));
    // ... rest unchanged
```

- [ ] **Step 5: Create 7 HTML email templates**

All templates as embedded resources. Add to `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Services\Email\Templates\*.html" />
</ItemGroup>
```

Create `src/Asterisk.Platform.Api/Services/Email/Templates/_base-layout.html`:
```html
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"/></head>
<body style="margin:0;padding:0;background:#f1f5f9;">
<table width="100%" cellpadding="0" cellspacing="0" style="background:#f1f5f9;padding:20px 0;">
<tr><td align="center">
<table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;">
<tr><td style="background:{{PrimaryColor}};padding:24px;text-align:center;">
<img src="{{LogoUrl}}" alt="{{CompanyName}}" height="40" style="max-width:200px;display:inline-block;" />
</td></tr>
<tr><td style="padding:32px;line-height:1.6;color:#1e293b;font-family:Arial,Helvetica,sans-serif;font-size:14px;">
{{Content}}
</td></tr>
<tr><td style="background:#f8fafc;padding:20px;color:#64748b;font-size:12px;text-align:center;font-family:Arial,Helvetica,sans-serif;">
{{CompanyName}}<br/>{{SupportEmail}} &middot; <a href="{{SupportUrl}}" style="color:#64748b;">{{SupportUrl}}</a>
</td></tr>
</table>
</td></tr>
</table>
</body>
</html>
```

Create `notification-critical.html`:
```html
<h2 style="margin:0 0 16px;color:#dc2626;font-size:18px;">{{Title}}</h2>
<p style="margin:0 0 16px;">{{Body}}</p>
<table cellpadding="0" cellspacing="0"><tr><td style="background:{{PrimaryColor}};border-radius:6px;padding:12px 24px;">
<a href="{{ActionUrl}}" style="color:#ffffff;text-decoration:none;font-weight:bold;font-size:14px;">View Details</a>
</td></tr></table>
```

Create `notification-warning.html`:
```html
<h2 style="margin:0 0 16px;color:#d97706;font-size:18px;">{{Title}}</h2>
<p style="margin:0 0 16px;">{{Body}}</p>
<table cellpadding="0" cellspacing="0"><tr><td style="background:{{PrimaryColor}};border-radius:6px;padding:12px 24px;">
<a href="{{ActionUrl}}" style="color:#ffffff;text-decoration:none;font-weight:bold;font-size:14px;">View Details</a>
</td></tr></table>
```

Create `scheduled-report.html`:
```html
<h2 style="margin:0 0 16px;color:#1e293b;font-size:18px;">{{ReportName}}</h2>
<p style="margin:0 0 8px;">Your scheduled report is ready.</p>
<p style="margin:0 0 16px;color:#64748b;">Period: {{Period}} &middot; Generated: {{GeneratedAt}}</p>
<p style="margin:0;color:#64748b;font-size:12px;">The report is attached to this email.</p>
```

Create `gdpr-export-ready.html`:
```html
<h2 style="margin:0 0 16px;color:#1e293b;font-size:18px;">Data Export Ready</h2>
<p style="margin:0 0 16px;">The data export for contact <strong>{{ContactId}}</strong> is ready for download.</p>
<table cellpadding="0" cellspacing="0"><tr><td style="background:{{PrimaryColor}};border-radius:6px;padding:12px 24px;">
<a href="{{DownloadUrl}}" style="color:#ffffff;text-decoration:none;font-weight:bold;font-size:14px;">Download Export</a>
</td></tr></table>
<p style="margin:16px 0 0;color:#64748b;font-size:12px;">This link expires in 24 hours.</p>
```

Create `password-reset.html`:
```html
<h2 style="margin:0 0 16px;color:#1e293b;font-size:18px;">Password Reset</h2>
<p style="margin:0 0 16px;">A password reset was requested for <strong>{{UserEmail}}</strong>.</p>
<table cellpadding="0" cellspacing="0"><tr><td style="background:{{PrimaryColor}};border-radius:6px;padding:12px 24px;">
<a href="{{ResetLink}}" style="color:#ffffff;text-decoration:none;font-weight:bold;font-size:14px;">Reset Password</a>
</td></tr></table>
<p style="margin:16px 0 0;color:#64748b;font-size:12px;">This link expires in {{ExpiresIn}}. If you did not request this, ignore this email.</p>
```

Create `welcome-user.html`:
```html
<h2 style="margin:0 0 16px;color:#1e293b;font-size:18px;">Welcome to {{CompanyName}}</h2>
<p style="margin:0 0 8px;">Your account has been created.</p>
<p style="margin:0 0 4px;"><strong>Email:</strong> {{UserEmail}}</p>
<p style="margin:0 0 16px;"><strong>Temporary Password:</strong> {{TempPassword}}</p>
<table cellpadding="0" cellspacing="0"><tr><td style="background:{{PrimaryColor}};border-radius:6px;padding:12px 24px;">
<a href="{{LoginUrl}}" style="color:#ffffff;text-decoration:none;font-weight:bold;font-size:14px;">Sign In</a>
</td></tr></table>
<p style="margin:16px 0 0;color:#64748b;font-size:12px;">Please change your password after signing in.</p>
```

- [ ] **Step 6: Implement EmbeddedEmailTemplateService**

```csharp
// src/Asterisk.Platform.Api/Services/Email/EmbeddedEmailTemplateService.cs
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Asterisk.Platform.Core.Email;

namespace Asterisk.Platform.Api.Services.Email;

internal sealed class EmbeddedEmailTemplateService : IEmailTemplateService
{
    private static readonly Assembly Assembly = typeof(EmbeddedEmailTemplateService).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private const string ResourcePrefix = "Asterisk.Platform.Api.Services.Email.Templates.";

    public string Render(string templateName, BrandingContext branding,
                         IReadOnlyDictionary<string, string> variables)
    {
        var layout = LoadTemplate("_base-layout");
        var content = LoadTemplate(templateName);

        var html = layout.Replace("{{Content}}", content);

        // Branding placeholders
        html = html.Replace("{{CompanyName}}", Escape(branding.CompanyName));
        html = html.Replace("{{LogoUrl}}", Escape(branding.LogoUrl ?? ""));
        html = html.Replace("{{PrimaryColor}}", Escape(branding.PrimaryColor));
        html = html.Replace("{{SecondaryColor}}", Escape(branding.SecondaryColor));
        html = html.Replace("{{AccentColor}}", Escape(branding.AccentColor));
        html = html.Replace("{{SupportEmail}}", Escape(branding.SupportEmail ?? ""));
        html = html.Replace("{{SupportUrl}}", Escape(branding.SupportUrl ?? ""));

        // Content-specific variables
        foreach (var (key, value) in variables)
            html = html.Replace($"{{{{{key}}}}}", Escape(value));

        return html;
    }

    private static string LoadTemplate(string name)
    {
        return Cache.GetOrAdd(name, static n =>
        {
            var resourceName = $"{ResourcePrefix}{n}.html";
            using var stream = Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return $"<p>Template '{n}' not found.</p>";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
```

- [ ] **Step 7: Write tests**

```csharp
// tests/Asterisk.Platform.Api.Tests/EmailTemplateServiceTests.cs
namespace Asterisk.Platform.Api.Tests;

public class EmailTemplateServiceTests
{
    private readonly EmbeddedEmailTemplateService _service = new();
    private readonly BrandingContext _branding = new(
        CompanyName: "Acme Corp", LogoUrl: "https://acme.com/logo.png",
        PrimaryColor: "#1E40AF", SecondaryColor: "#64748B", AccentColor: "#0D9488",
        SupportEmail: "help@acme.com", SupportUrl: "https://help.acme.com",
        FromName: "Acme Support", FromAddress: "noreply@acme.com");

    [Fact]
    public void Render_ShouldInjectBranding_WhenTemplateExists()
    {
        var html = _service.Render("notification-critical", _branding,
            new Dictionary<string, string> { ["Title"] = "Test Alert", ["Body"] = "Something happened", ["ActionUrl"] = "https://app.acme.com" });

        html.Should().Contain("Acme Corp");
        html.Should().Contain("#1E40AF");
        html.Should().Contain("Test Alert");
    }

    [Fact]
    public void Render_ShouldIncludeBaseLayout_WhenRendered()
    {
        var html = _service.Render("password-reset", _branding,
            new Dictionary<string, string> { ["UserEmail"] = "john@acme.com", ["ResetLink"] = "https://reset.link", ["ExpiresIn"] = "1 hour" });

        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("help@acme.com");
        html.Should().Contain("john@acme.com");
    }

    [Fact]
    public void Render_ShouldReturnFallback_WhenTemplateNotFound()
    {
        var html = _service.Render("nonexistent", _branding, new Dictionary<string, string>());
        html.Should().Contain("not found");
    }

    [Fact]
    public void Render_ShouldEscapeHtml_WhenVariablesContainSpecialChars()
    {
        var html = _service.Render("notification-critical", _branding,
            new Dictionary<string, string> { ["Title"] = "<script>alert('xss')</script>", ["Body"] = "Safe", ["ActionUrl"] = "#" });

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
    }
}
```

- [ ] **Step 8: Run tests, commit**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "EmailTemplateServiceTests" -v q`
Expected: 4 passed

```bash
git add src/Asterisk.Platform.Core/Email/BrandingContext.cs src/Asterisk.Platform.Core/Email/IEmailTemplateService.cs src/Asterisk.Platform.Core/Email/EmailMessage.cs src/Asterisk.Platform.Api/Services/Email/ src/Asterisk.Platform.Api/Services/SmtpEmailService.cs src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj tests/Asterisk.Platform.Api.Tests/EmailTemplateServiceTests.cs
git commit -m "feat: email template system with 7 branded HTML templates"
```

---

## Task 5: NotificationTypeRegistry + NotificationEvent + NotificationService

**Files:**
- Create: `src/Asterisk.Platform.Core/Notifications/NotificationTypeRegistry.cs`
- Modify: `src/Asterisk.Platform.Core/PlatformEventBus.cs` (add NotificationEvent)
- Create: `src/Asterisk.Platform.Api/Services/NotificationService.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/NotificationServiceTests.cs`

- [ ] **Step 1: Create NotificationTypeRegistry**

```csharp
// src/Asterisk.Platform.Core/Notifications/NotificationTypeRegistry.cs
namespace Asterisk.Platform.Core.Notifications;

public static class NotificationTypeRegistry
{
    public sealed record NotificationTypeInfo(
        string Type, NotificationCategory Category, NotificationSeverity Severity,
        IReadOnlyList<string> TargetRoles);

    private static readonly Dictionary<string, NotificationTypeInfo> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["billing.dunning_escalated"] = new("billing.dunning_escalated", NotificationCategory.Billing, NotificationSeverity.Critical, ["Admin", "SystemAdmin", "PlatformAdmin", "PartnerAdmin", "PartnerBilling"]),
        ["billing.quota_warning"] = new("billing.quota_warning", NotificationCategory.Billing, NotificationSeverity.Warning, ["Admin", "SystemAdmin"]),
        ["billing.quota_exceeded"] = new("billing.quota_exceeded", NotificationCategory.Billing, NotificationSeverity.Critical, ["Admin", "SystemAdmin", "PlatformAdmin"]),
        ["billing.tenant_suspended"] = new("billing.tenant_suspended", NotificationCategory.Billing, NotificationSeverity.Critical, ["Admin", "SystemAdmin", "PlatformAdmin", "PartnerAdmin", "PartnerBilling"]),
        ["billing.tenant_created"] = new("billing.tenant_created", NotificationCategory.System, NotificationSeverity.Info, ["PlatformAdmin", "PartnerAdmin"]),
        ["security.account_locked"] = new("security.account_locked", NotificationCategory.Security, NotificationSeverity.Critical, ["Admin", "SystemAdmin"]),
        ["security.suspicious_login"] = new("security.suspicious_login", NotificationCategory.Security, NotificationSeverity.Warning, ["Admin", "SystemAdmin"]),
        ["system.license_expiring"] = new("system.license_expiring", NotificationCategory.System, NotificationSeverity.Warning, ["Admin", "SystemAdmin", "PlatformAdmin"]),
        ["system.webhook_circuit_open"] = new("system.webhook_circuit_open", NotificationCategory.System, NotificationSeverity.Critical, ["Admin", "SystemAdmin"]),
        ["system.report_failed"] = new("system.report_failed", NotificationCategory.System, NotificationSeverity.Warning, ["Admin", "SystemAdmin"]),
        ["operational.conversation_escalated"] = new("operational.conversation_escalated", NotificationCategory.Operational, NotificationSeverity.Critical, ["Supervisor", "Manager"]),
        ["operational.agent_offline"] = new("operational.agent_offline", NotificationCategory.Operational, NotificationSeverity.Warning, ["Supervisor", "Manager"]),
        ["gdpr.export_completed"] = new("gdpr.export_completed", NotificationCategory.System, NotificationSeverity.Info, ["Admin", "SystemAdmin"]),
        ["gdpr.purge_completed"] = new("gdpr.purge_completed", NotificationCategory.System, NotificationSeverity.Info, ["Admin", "SystemAdmin"]),
    };

    public static NotificationTypeInfo? Get(string type) =>
        Types.GetValueOrDefault(type);

    public static IReadOnlyCollection<string> AllTypes => Types.Keys;
}
```

- [ ] **Step 2: Add NotificationEvent to PlatformEventBus.cs**

Add to the event records section in `src/Asterisk.Platform.Core/PlatformEventBus.cs`:

```csharp
public sealed record NotificationEvent(
    string TenantId, string Type, DateTimeOffset Timestamp,
    string NotificationId, string UserId,
    NotificationCategory Category, NotificationSeverity Severity,
    string Title, string Body, string? ActionUrl
) : PlatformEvent(TenantId, "notification.created", Timestamp);
```

Add the needed `using Asterisk.Platform.Core.Notifications;` import.

- [ ] **Step 3: Implement NotificationService**

```csharp
// src/Asterisk.Platform.Api/Services/NotificationService.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class NotificationService(
    INotificationStore notificationStore,
    IUserRoleStore userRoleStore,
    IUserStore userStore,
    ITenantStore tenantStore,
    ITenantBrandingStore brandingStore,
    PlatformEventBus eventBus,
    IEmailService emailService,
    IEmailTemplateService templateService,
    IOptions<SmtpOptions> smtpOptions,
    ILogger<NotificationService> logger)
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dedupCache = new();

    public async Task CreateAsync(string tenantId, string type, string title, string body,
        string? actionUrl = null, CancellationToken ct = default)
    {
        var typeInfo = NotificationTypeRegistry.Get(type);
        if (typeInfo is null)
        {
            LogUnknownType(type);
            return;
        }

        // Dedup check
        var dedupKey = $"{tenantId}:{type}";
        var now = DateTimeOffset.UtcNow;
        if (_dedupCache.TryGetValue(dedupKey, out var lastSent) && now - lastSent < DedupWindow)
            return;
        _dedupCache[dedupKey] = now;

        // Find target users by role
        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null) return;

        var users = await FindUsersByRolesAsync(tenantId, typeInfo.TargetRoles, ct);

        foreach (var user in users)
        {
            var notification = new Notification
            {
                NotificationId = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                UserId = user.UserId.Value,
                Category = typeInfo.Category,
                Severity = typeInfo.Severity,
                Type = type,
                Title = title,
                Body = body,
                ActionUrl = actionUrl,
            };

            await notificationStore.SaveAsync(notification, ct);

            eventBus.Publish(new NotificationEvent(
                tenantId, "notification.created", now,
                notification.NotificationId, user.UserId.Value,
                typeInfo.Category, typeInfo.Severity, title, body, actionUrl));
        }

        // Critical → send email
        if (typeInfo.Severity == NotificationSeverity.Critical)
            await SendCriticalEmailAsync(tenantId, tenant, title, body, actionUrl, users, ct);

        // Partner cross-tenant propagation
        if (tenant.ParentTenantId is not null && HasPartnerRoles(typeInfo.TargetRoles))
            await CreateAsync(tenant.ParentTenantId, type, title,
                $"[{tenant.Name}] {body}", actionUrl, ct);

        LogCreated(type, tenantId, users.Count);
    }

    private async Task<IReadOnlyList<User>> FindUsersByRolesAsync(
        string tenantId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var allUsers = await userStore.ListAsync(tid, ct);
        var result = new List<User>();

        foreach (var user in allUsers)
        {
            var userRoles = await userRoleStore.GetRolesForUserAsync(tid, user.UserId, ct);
            var roleTemplates = userRoles.Select(r => r.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (roles.Any(r => roleTemplates.Contains(r.ToLowerInvariant().Replace(" ", "_"))))
                result.Add(user);
        }

        return result;
    }

    private static bool HasPartnerRoles(IReadOnlyList<string> roles) =>
        roles.Any(r => r.StartsWith("Partner", StringComparison.OrdinalIgnoreCase));

    private async Task SendCriticalEmailAsync(string tenantId, Tenant tenant,
        string title, string body, string? actionUrl,
        IReadOnlyList<User> users, CancellationToken ct)
    {
        var branding = await BuildBrandingContextAsync(tenantId, tenant, ct);

        var variables = new Dictionary<string, string>
        {
            ["Title"] = title,
            ["Body"] = body,
            ["ActionUrl"] = actionUrl ?? "",
        };

        var html = templateService.Render("notification-critical", branding, variables);

        var recipients = users
            .Where(u => !string.IsNullOrWhiteSpace(u.Email))
            .Select(u => new EmailRecipient(u.Email, u.DisplayName))
            .ToList();

        if (recipients.Count == 0) return;

        var message = new EmailMessage
        {
            Recipients = recipients,
            Subject = $"[Critical] {title}",
            HtmlBody = html,
            TextBody = $"{title}\n\n{body}",
            FromName = branding.FromName,
            FromAddress = branding.FromAddress,
        };

        try
        {
            await emailService.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            LogEmailFailed(title, ex.Message);
        }
    }

    private async Task<BrandingContext> BuildBrandingContextAsync(
        string tenantId, Tenant tenant, CancellationToken ct)
    {
        var branding = await brandingStore.GetAsync(tenantId, ct);
        TenantBranding? parentBranding = null;

        if (branding is null && tenant.ParentTenantId is not null)
            parentBranding = await brandingStore.GetAsync(tenant.ParentTenantId, ct);

        var defaults = smtpOptions.Value;

        return new BrandingContext(
            CompanyName: branding?.DisplayName ?? parentBranding?.DisplayName ?? tenant.Name,
            LogoUrl: branding?.LogoUrl ?? parentBranding?.LogoUrl,
            PrimaryColor: branding?.PrimaryColor ?? parentBranding?.PrimaryColor ?? "#1E40AF",
            SecondaryColor: branding?.SecondaryColor ?? parentBranding?.SecondaryColor ?? "#64748B",
            AccentColor: branding?.AccentColor ?? parentBranding?.AccentColor ?? "#0D9488",
            SupportEmail: branding?.SupportEmail ?? parentBranding?.SupportEmail,
            SupportUrl: branding?.SupportUrl ?? parentBranding?.SupportUrl,
            FromName: branding?.EmailFromName ?? parentBranding?.EmailFromName ?? defaults.FromName,
            FromAddress: branding?.EmailFromAddress ?? parentBranding?.EmailFromAddress ?? defaults.FromAddress);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown notification type: {Type}")]
    private partial void LogUnknownType(string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification created: type={Type} tenant={TenantId} users={UserCount}")]
    private partial void LogCreated(string type, string tenantId, int userCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send critical email: title={Title} error={Error}")]
    private partial void LogEmailFailed(string title, string error);
}
```

- [ ] **Step 4: Write NotificationService tests**

```csharp
// tests/Asterisk.Platform.Api.Tests/NotificationServiceTests.cs
// Tests verify: role routing, dedup, critical→email, partner cross-tenant, severity filtering
// Use NSubstitute mocks for stores, verify Save/Publish/SendAsync calls
// 6 tests total — see spec Section 6 for coverage matrix
```

The test file should test:
1. `CreateAsync_ShouldRouteToCorrectRoles_WhenTypeIsBillingDunning` — verify Admin+SystemAdmin users get notifications
2. `CreateAsync_ShouldDedup_WhenSameTypeWithin5Minutes` — second call within window is skipped
3. `CreateAsync_ShouldSendEmail_WhenSeverityIsCritical` — verify IEmailService.SendAsync called
4. `CreateAsync_ShouldNotSendEmail_WhenSeverityIsInfo` — verify IEmailService.SendAsync NOT called
5. `CreateAsync_ShouldPropagateToPartner_WhenCustomerHasParent` — verify recursive call creates in parent tenant
6. `CreateAsync_ShouldSkip_WhenTypeIsUnknown` — verify nothing saved

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "NotificationServiceTests" -v q`
Expected: 6 passed

```bash
git add src/Asterisk.Platform.Core/Notifications/NotificationTypeRegistry.cs src/Asterisk.Platform.Core/PlatformEventBus.cs src/Asterisk.Platform.Api/Services/NotificationService.cs tests/Asterisk.Platform.Api.Tests/NotificationServiceTests.cs
git commit -m "feat: NotificationService with type registry + role routing + dedup + email"
```

---

## Task 6: BrandingEndpoints (Public)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/BrandingEndpoints.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/BrandingEndpointsTests.cs`

- [ ] **Step 1: Implement BrandingEndpoints**

```csharp
// src/Asterisk.Platform.Api/Endpoints/BrandingEndpoints.cs
using Asterisk.Platform.Core.Branding;

namespace Asterisk.Platform.Api.Endpoints;

internal static class BrandingEndpoints
{
    internal sealed record PublicBrandingDto(
        string? DisplayName, string? LogoUrl, string? FaviconUrl,
        string? PrimaryColor, string? SecondaryColor, string? AccentColor,
        string? Locale, string? Timezone);

    internal static RouteGroupBuilder MapBrandingEndpoints(this RouteGroupBuilder group)
    {
        var branding = group.MapGroup("/branding")
            .WithTags("Branding");

        branding.MapGet("/{tenantId}", GetByTenantId);
        branding.MapGet("/by-subdomain/{subdomain}", GetBySubdomain);

        return group;
    }

    private static async Task<IResult> GetByTenantId(
        string tenantId,
        [FromServices] ITenantBrandingStore store,
        CancellationToken ct)
    {
        var branding = await store.GetAsync(tenantId, ct);
        if (branding is null) return Results.NotFound();
        return Results.Ok(ToPublicDto(branding));
    }

    private static async Task<IResult> GetBySubdomain(
        string subdomain,
        [FromServices] ITenantBrandingStore store,
        CancellationToken ct)
    {
        var branding = await store.GetBySubdomainAsync(subdomain, ct);
        if (branding is null) return Results.NotFound();
        return Results.Ok(ToPublicDto(branding));
    }

    private static PublicBrandingDto ToPublicDto(TenantBranding b) => new(
        b.DisplayName, b.LogoUrl, b.FaviconUrl,
        b.PrimaryColor, b.SecondaryColor, b.AccentColor,
        b.Locale, b.Timezone);
}
```

Note: These are **public endpoints** (no `.RequireAuthorization()`). The frontend needs branding before login.

- [ ] **Step 2: Write tests (4 tests)**

Tests: GetByTenantId returns DTO, GetBySubdomain resolves, 404 for unknown, only public fields exposed (no SupportEmail, EmailFromAddress).

- [ ] **Step 3: Run tests, commit**

```bash
git commit -m "feat: public branding endpoints (no auth)"
```

---

## Task 7: NotificationEndpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/NotificationEndpoints.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/NotificationEndpointsTests.cs`

- [ ] **Step 1: Implement NotificationEndpoints**

```csharp
// src/Asterisk.Platform.Api/Endpoints/NotificationEndpoints.cs
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Api.Endpoints.Shared;

namespace Asterisk.Platform.Api.Endpoints;

internal static class NotificationEndpoints
{
    internal sealed record NotificationDto(
        string NotificationId, string Type, string Category, string Severity,
        string Title, string Body, string? ActionUrl, bool IsRead,
        DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    internal sealed record UnreadCountDto(int Count);

    internal static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        var notifications = group.MapGroup("/notifications")
            .RequireAuthorization("Authenticated")
            .WithTags("Notifications");

        notifications.MapGet("/", ListNotifications);
        notifications.MapGet("/unread-count", GetUnreadCount);
        notifications.MapGet("/{id}", GetNotification);
        notifications.MapPut("/{id}/read", MarkRead);
        notifications.MapPut("/read-all", MarkAllRead);

        return group;
    }

    private static async Task<IResult> ListNotifications(
        HttpContext context,
        [FromServices] INotificationStore store,
        [FromQuery] bool? unreadOnly,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null) return Results.Unauthorized();

        var items = await store.ListAsync(tenantId, userId, unreadOnly, limit ?? 50, offset ?? 0, ct);
        return Results.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetUnreadCount(
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null) return Results.Unauthorized();

        var count = await store.CountUnreadAsync(tenantId, userId, ct);
        return Results.Ok(new UnreadCountDto(count));
    }

    private static async Task<IResult> GetNotification(
        string id,
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null) return Results.Unauthorized();

        var n = await store.GetAsync(id, ct);
        if (n is null || n.TenantId != tenantId || n.UserId != userId)
            return Results.NotFound();
        return Results.Ok(ToDto(n));
    }

    private static async Task<IResult> MarkRead(
        string id,
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null) return Results.Unauthorized();

        var n = await store.GetAsync(id, ct);
        if (n is null || n.TenantId != tenantId || n.UserId != userId)
            return Results.NotFound();

        await store.MarkReadAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkAllRead(
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null) return Results.Unauthorized();

        await store.MarkAllReadAsync(tenantId, userId, ct);
        return Results.NoContent();
    }

    private static (string? tenantId, string? userId) ExtractClaims(HttpContext context)
    {
        var tenantId = context.User.FindFirst("tid")?.Value ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("user_id")?.Value;
        return (tenantId, userId);
    }

    private static NotificationDto ToDto(Notification n) => new(
        n.NotificationId, n.Type, n.Category.ToString(), n.Severity.ToString(),
        n.Title, n.Body, n.ActionUrl, n.IsRead, n.CreatedAt, n.ReadAt);
}
```

- [ ] **Step 2: Write tests (5 tests)**

Tests: List paged, unread count, mark read, mark all read, ownership (user can't see other user's notifications).

- [ ] **Step 3: Run tests, commit**

```bash
git commit -m "feat: notification endpoints (list, count, read, read-all)"
```

---

## Task 8: TenantSettings Branding Section + BrandingInheritance

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/BrandingInheritanceTests.cs`

- [ ] **Step 1: Add BrandingSettingsDto**

Add to `TenantSettingsEndpoints.cs`:

```csharp
internal sealed record BrandingSettingsDto(
    string? DisplayName, string? LogoUrl, string? FaviconUrl,
    string? PrimaryColor, string? SecondaryColor, string? AccentColor,
    string? Locale, string? Timezone, string? Subdomain,
    string? SupportEmail, string? SupportUrl,
    string? EmailFromName, string? EmailFromAddress);

internal sealed record UpdateBrandingSettingsDto(
    string? DisplayName, string? LogoUrl, string? FaviconUrl,
    string? PrimaryColor, string? SecondaryColor, string? AccentColor,
    string? Locale, string? Timezone,
    string? SupportEmail, string? SupportUrl,
    string? EmailFromName, string? EmailFromAddress);
// Note: Subdomain NOT in update — AdminOnly cannot set subdomain (PlatformAdminOnly does)
```

- [ ] **Step 2: Add Branding to TenantSettingsDto**

Extend `TenantSettingsDto` with a `BrandingSettingsDto? Branding` property.

- [ ] **Step 3: Update BuildSettingsDto to include Branding**

In `BuildSettingsDto()`, add:

```csharp
var branding = await brandingStore.GetAsync(tenantId, ct);

// 3-tier inheritance for display
TenantBranding? parentBranding = null;
if (tenant.ParentTenantId is not null)
    parentBranding = await brandingStore.GetAsync(tenant.ParentTenantId, ct);

var brandingDto = new BrandingSettingsDto(
    DisplayName: branding?.DisplayName ?? parentBranding?.DisplayName,
    LogoUrl: branding?.LogoUrl ?? parentBranding?.LogoUrl,
    // ... same pattern for all fields
    Subdomain: branding?.Subdomain);
```

- [ ] **Step 4: Update ApplyUpdates for Branding**

In `ApplyUpdates()`, handle `UpdateBrandingSettingsDto`:

```csharp
if (request.Branding is not null)
{
    var existing = await brandingStore.GetAsync(tenantId, ct) ?? new TenantBranding { TenantId = tenantId };
    // Apply non-null fields from request.Branding to existing
    // AdminOnly: cannot write Subdomain (stripped before this point)
    await brandingStore.UpsertAsync(existing, ct);
}
```

- [ ] **Step 5: ManagementTenantSettingsEndpoints: allow Subdomain**

In the management version of `UpdateSettings`, do NOT strip Subdomain — PlatformAdminOnly can set it.

Add an `UpdateManagementBrandingSettingsDto` that includes `Subdomain?`.

- [ ] **Step 6: Write BrandingInheritanceTests (3 tests)**

```csharp
// tests/Asterisk.Platform.Api.Tests/BrandingInheritanceTests.cs
// Tests:
// 1. Customer inherits Partner branding when own is null
// 2. Partner inherits Platform defaults when own is null
// 3. Full chain: Customer has partial overrides, rest inherited from Partner
```

- [ ] **Step 7: Run tests, commit**

```bash
git commit -m "feat: branding section in TenantSettings facade with 3-tier inheritance"
```

---

## Task 9: TenantResolutionMiddleware Subdomain Update

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/SubdomainResolutionTests.cs`

- [ ] **Step 1: Update subdomain resolution**

In `TenantResolutionMiddleware.cs`, modify the subdomain resolution section:

```csharp
// Current: uses subdomain directly as tenantId
// Updated: lookup in ITenantBrandingStore first, fallback to direct

if (!string.IsNullOrWhiteSpace(subdomain))
{
    var brandingStore = context.RequestServices.GetService<ITenantBrandingStore>();
    if (brandingStore is not null)
    {
        var branding = await brandingStore.GetBySubdomainAsync(subdomain, context.RequestAborted);
        if (branding is not null)
        {
            tenantId = branding.TenantId;
        }
    }

    // Fallback: use subdomain directly as tenantId
    tenantId ??= subdomain;
}
```

- [ ] **Step 2: Write tests (2 tests)**

```csharp
// tests/Asterisk.Platform.Api.Tests/SubdomainResolutionTests.cs
// 1. Subdomain_ShouldResolveTenantId_WhenBrandingStoreHasMatch
// 2. Subdomain_ShouldFallbackToDirectTenantId_WhenNoBrandingMatch
```

- [ ] **Step 3: Run tests, commit**

```bash
git commit -m "feat: subdomain resolution via branding store with fallback"
```

---

## Task 10: AuthEndpoints Password Reset + Welcome Email

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PasswordResetEmailTests.cs`

- [ ] **Step 1: Implement password reset email in ForgotPassword**

Replace the comment `// In production, send email with resetToken.` with:

```csharp
// Send password reset email
try
{
    var brandingStore = context.RequestServices.GetRequiredService<ITenantBrandingStore>();
    var emailTemplateService = context.RequestServices.GetRequiredService<IEmailTemplateService>();
    var emailService = context.RequestServices.GetRequiredService<IEmailService>();
    var smtpOpts = context.RequestServices.GetRequiredService<IOptions<SmtpOptions>>().Value;

    var tenantBranding = await brandingStore.GetAsync(forgotTenantId!, ct);
    var tenantStore = context.RequestServices.GetRequiredService<ITenantStore>();
    var tenant = await tenantStore.GetAsync(forgotTenantId!, ct);

    var brandingContext = new BrandingContext(
        CompanyName: tenantBranding?.DisplayName ?? tenant?.Name ?? "Platform",
        LogoUrl: tenantBranding?.LogoUrl,
        PrimaryColor: tenantBranding?.PrimaryColor ?? "#1E40AF",
        SecondaryColor: tenantBranding?.SecondaryColor ?? "#64748B",
        AccentColor: tenantBranding?.AccentColor ?? "#0D9488",
        SupportEmail: tenantBranding?.SupportEmail,
        SupportUrl: tenantBranding?.SupportUrl,
        FromName: tenantBranding?.EmailFromName ?? smtpOpts.FromName,
        FromAddress: tenantBranding?.EmailFromAddress ?? smtpOpts.FromAddress);

    var resetLink = $"{context.Request.Scheme}://{context.Request.Host}/reset-password?token={resetToken}";
    var variables = new Dictionary<string, string>
    {
        ["UserEmail"] = body.Email,
        ["ResetLink"] = resetLink,
        ["ExpiresIn"] = "1 hour",
    };

    var html = emailTemplateService.Render("password-reset", brandingContext, variables);
    var message = new EmailMessage
    {
        Recipients = [new EmailRecipient(body.Email, user.DisplayName)],
        Subject = "Password Reset Request",
        HtmlBody = html,
        TextBody = $"Reset your password: {resetLink}\nThis link expires in 1 hour.",
        FromName = brandingContext.FromName,
        FromAddress = brandingContext.FromAddress,
    };
    await emailService.SendAsync(message, ct);
}
catch
{
    // Silently fail — don't reveal email existence
}
```

- [ ] **Step 2: Write tests (2 tests)**

```csharp
// tests/Asterisk.Platform.Api.Tests/PasswordResetEmailTests.cs
// 1. ForgotPassword_ShouldSendEmail_WhenUserExists
// 2. ForgotPassword_ShouldContainResetLink_WhenEmailSent
```

- [ ] **Step 3: Run tests, commit**

```bash
git commit -m "feat: password reset email with branded template"
```

---

## Task 11: ReportSchedulerService + PdfReportRenderer Branding + GdprExportService Email

**Files:**
- Modify: `src/Asterisk.Platform.Api/Services/Reports/ReportSchedulerService.cs`
- Modify: `src/Asterisk.Platform.Api/Services/Reports/PdfReportRenderer.cs`
- Modify: `src/Asterisk.Platform.Api/Endpoints/GdprEndpoints.cs`

- [ ] **Step 1: Update ReportSchedulerService to use branded email**

Replace the plaintext email construction with:

```csharp
// Load branding
var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
var templateService = scope.ServiceProvider.GetRequiredService<IEmailTemplateService>();
var tenantBranding = await brandingStore.GetAsync(report.TenantId, ct);

var brandingContext = BuildBrandingContext(tenantBranding, report.TenantId);

var variables = new Dictionary<string, string>
{
    ["ReportName"] = report.Name,
    ["Period"] = $"{report.From:yyyy-MM-dd} — {report.To:yyyy-MM-dd}",
    ["GeneratedAt"] = now.ToString("yyyy-MM-dd HH:mm"),
};

var html = templateService.Render("scheduled-report", brandingContext, variables);

var message = new EmailMessage
{
    Recipients = recipients,
    Subject = $"[Report] {report.Name} — {now:yyyy-MM-dd}",
    HtmlBody = html,
    TextBody = $"Please find the {report.ReportType} report attached.",
    FromName = brandingContext.FromName,
    FromAddress = brandingContext.FromAddress,
    Attachments = [new EmailAttachment(filename, renderer.ContentType, fileBytes)],
};
```

- [ ] **Step 2: Update PdfReportRenderer with branding**

Add `BrandingContext?` parameter to `RenderAsync` or pass via `ReportData`:

```csharp
// In BuildHeader, use branding colors instead of hardcoded PdfColors.Blue.Darken3
// If branding.LogoUrl is provided and can be fetched, insert as Image
// Otherwise use CompanyName as text header
```

The key changes:
- Replace `PdfColors.Blue.Darken3` with parsed `branding.PrimaryColor` (using `QuestPDF.Helpers.Colors.FromHex()`)
- Replace "Tenant: {name}" with `branding.CompanyName`

- [ ] **Step 3: Add GDPR export email notification**

In `GdprEndpoints.cs`, after the export completes successfully, trigger a notification:

```csharp
// After export returns result:
var notificationService = context.RequestServices.GetService<NotificationService>();
if (notificationService is not null)
{
    await notificationService.CreateAsync(tenantId, "gdpr.export_completed",
        "Data Export Ready",
        $"Data export for contact {contactId} is ready for download.",
        $"/admin/gdpr/export?contactId={contactId}", ct);
}
```

Also send a branded email using `gdpr-export-ready` template to the requesting user.

- [ ] **Step 4: Build, run all tests, commit**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: all passing

```bash
git commit -m "feat: branded email + PDF reports + GDPR export notification"
```

---

## Task 12: Program.cs Wiring + ApiJsonContext

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Register services in Program.cs**

```csharp
// After email service registration:
builder.Services.AddSingleton<Asterisk.Platform.Core.Email.IEmailTemplateService,
    Asterisk.Platform.Api.Services.Email.EmbeddedEmailTemplateService>();
builder.Services.AddSingleton<Asterisk.Platform.Api.Services.NotificationService>();
```

- [ ] **Step 2: Map endpoints in Program.cs**

```csharp
// After existing endpoint mappings:
v1.MapNotificationEndpoints();
v1.MapBrandingEndpoints();
```

- [ ] **Step 3: Add JsonSerializable attributes to ApiJsonContext**

```csharp
// Notifications
[JsonSerializable(typeof(NotificationEndpoints.NotificationDto))]
[JsonSerializable(typeof(List<NotificationEndpoints.NotificationDto>))]
[JsonSerializable(typeof(NotificationEndpoints.UnreadCountDto))]
// Branding
[JsonSerializable(typeof(BrandingEndpoints.PublicBrandingDto))]
[JsonSerializable(typeof(BrandingSettingsDto))]
[JsonSerializable(typeof(UpdateBrandingSettingsDto))]
// Enums
[JsonSerializable(typeof(NotificationCategory))]
[JsonSerializable(typeof(NotificationSeverity))]
```

- [ ] **Step 4: Build full solution, run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: 0 warnings, ~1507 tests passing

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: wire Sprint 4 endpoints + DI + serialization"
```

---

## Task 13: CLAUDE.md + Memory Updates

**Files:**
- Modify: `CLAUDE.md` (test count, endpoint groups, Sprint 4 section)

- [ ] **Step 1: Update CLAUDE.md**

Update test count (1472 → ~1507), endpoint groups (53 → 55), add Sprint 4 completion section with summary of deliverables.

- [ ] **Step 2: Commit**

```bash
git commit -m "docs: update CLAUDE.md with Sprint 4 completion"
```

---

## Verification

```bash
# Full build
dotnet build Asterisk.Platform.slnx

# Full test suite
dotnet test Asterisk.Platform.slnx -v q

# Expected: ~1507 tests, 0 warnings, 0 errors

# Quick functional test
cd src/Asterisk.Platform.Api && dotnet run &
sleep 5

# Public branding (no auth)
curl -s http://localhost:5000/api/v1/branding/demo | jq .

# Notifications (requires auth token)
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/v1/notifications | jq .
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/v1/notifications/unread-count | jq .

kill %1
```
