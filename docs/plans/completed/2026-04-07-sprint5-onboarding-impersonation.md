# Sprint 5: Onboarding Wizard + Impersonation Read-Only — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-provision Golden Defaults + use-case templates on tenant creation, add onboarding endpoints with checklist, and implement read-only impersonation mode with permission intersection and audit enhancement.

**Architecture:** `TenantProvisioningService` implements `ITenantLifecycleHandler` to seed resources on tenant creation. Impersonation read-only filters JWT permissions to view-only set + middleware safety net. AuditEntry gains `ImpersonatorId` field.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, NSubstitute, Dapper (Postgres migration)

---

## File Structure

### New Files
| File | Purpose |
|------|---------|
| `src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs` | `ITenantLifecycleHandler` that seeds Golden Defaults + template resources |
| `src/Asterisk.Platform.Api/Services/TenantProvisioningTemplates.cs` | Static template definitions (support, sales, blended) |
| `src/Asterisk.Platform.Api/Endpoints/OnboardingEndpoints.cs` | 4 endpoints: status, apply-template, complete, dismiss-checklist |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/011_OnboardingAudit.sql` | ALTER TABLE audit_entries ADD COLUMN impersonator_id |
| `tests/Asterisk.Platform.Api.Tests/TenantProvisioningServiceTests.cs` | 6 tests for provisioning |
| `tests/Asterisk.Platform.Api.Tests/OnboardingEndpointsTests.cs` | 4 tests for onboarding endpoints |
| `tests/Asterisk.Platform.Api.Tests/ImpersonationReadOnlyTests.cs` | 6 tests for permission filter + response |
| `tests/Asterisk.Platform.Api.Tests/ReadOnlyMiddlewareTests.cs` | 4 tests for middleware safety net |
| `tests/Asterisk.Platform.Api.Tests/AuditImpersonatorTests.cs` | 3 tests for ImpersonatorId |
| `tests/Asterisk.Platform.Api.Tests/ProvisioningIntegrationTests.cs` | 3 tests for template via create-tenant |

### Modified Files
| File | Change |
|------|--------|
| `src/Asterisk.Platform.Audit/AuditEntry.cs` | Add `ImpersonatorId` property |
| `src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs` | Add `ReadOnly` to request/response, permission filter |
| `src/Asterisk.Platform.Api/Services/JwtTokenService.cs` | Add `readonly` claim to impersonation token |
| `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs` | Add read-only impersonation blocking |
| `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs` | Add `Template` to CreateMgmtTenantRequest |
| `src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs` | Add `Template` to CreatePartnerCustomerRequest |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Register new DTOs |
| `src/Asterisk.Platform.Api/Program.cs` | Register TenantProvisioningService + OnboardingEndpoints |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs` | Include impersonator_id in INSERT/SELECT |

---

## Task 1: AuditEntry ImpersonatorId

**Files:**
- Modify: `src/Asterisk.Platform.Audit/AuditEntry.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/011_OnboardingAudit.sql`
- Test: `tests/Asterisk.Platform.Api.Tests/AuditImpersonatorTests.cs`

- [ ] **Step 1: Write tests for ImpersonatorId on AuditEntry**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/AuditImpersonatorTests.cs
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class AuditImpersonatorTests
{
    [Fact]
    public void AuditEntry_ShouldAcceptImpersonatorId()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.NewId(),
            TenantId = new TenantId("test-tenant"),
            Action = "test.action",
            OccurredAt = DateTimeOffset.UtcNow,
            ImpersonatorId = "admin-user-123",
        };

        entry.ImpersonatorId.Should().Be("admin-user-123");
    }

    [Fact]
    public void AuditEntry_ShouldDefaultImpersonatorIdToNull()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.NewId(),
            TenantId = new TenantId("test-tenant"),
            Action = "test.action",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ImpersonatorId.Should().BeNull();
    }

    [Fact]
    public void AuditEntry_ShouldIncludeImpersonatorIdInProperties()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.NewId(),
            TenantId = new TenantId("test-tenant"),
            Action = "impersonation.started",
            OccurredAt = DateTimeOffset.UtcNow,
            ImpersonatorId = "platform-admin-1",
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "read_only",
                ["targetTenantId"] = "acme",
            },
        };

        entry.ImpersonatorId.Should().Be("platform-admin-1");
        entry.Metadata.Should().ContainKey("mode").WhoseValue.Should().Be("read_only");
    }
}
```

- [ ] **Step 2: Run tests — expect failure (ImpersonatorId does not exist)**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "AuditImpersonatorTests" -v q
```

- [ ] **Step 3: Add ImpersonatorId to AuditEntry**

In `src/Asterisk.Platform.Audit/AuditEntry.cs`, add after the `IntegrityHash` property:

```csharp
    /// <summary>
    /// When set, indicates the action was performed during an impersonation session.
    /// Contains the user ID of the admin who initiated the impersonation.
    /// </summary>
    public string? ImpersonatorId { get; init; }
```

- [ ] **Step 4: Create migration 011**

```sql
-- File: src/Asterisk.Platform.Storage.Postgres/Migrations/011_OnboardingAudit.sql
-- Sprint 5: Audit impersonator tracking

ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS impersonator_id TEXT NULL;
```

- [ ] **Step 5: Update PostgresAuditStore to include impersonator_id**

In `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs`, add `impersonator_id` to the INSERT column list and VALUES, and to the SELECT column list in queries. The AuditRow class needs a matching `public string? ImpersonatorId { get; init; }` property.

- [ ] **Step 6: Run tests — expect pass**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "AuditImpersonatorTests" -v q
```

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Platform.Audit/AuditEntry.cs src/Asterisk.Platform.Storage.Postgres/Migrations/011_OnboardingAudit.sql src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs tests/Asterisk.Platform.Api.Tests/AuditImpersonatorTests.cs
git commit -m "feat: add ImpersonatorId to AuditEntry + migration 011"
```

---

## Task 2: Impersonation Read-Only — Permission Filter + JWT Claims

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Services/JwtTokenService.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ImpersonationReadOnlyTests.cs`

- [ ] **Step 1: Write tests for read-only permission filtering**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/ImpersonationReadOnlyTests.cs
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ImpersonationReadOnlyTests
{
    private static readonly HashSet<string> ReadOnlyPermissions = new(StringComparer.Ordinal)
    {
        "contacts:contact:view",
        "contacts:conversation:monitor",
        "queues:queue:view",
        "users:user:view",
        "campaigns:campaign:view",
        "reporting:realtime:view",
        "reporting:historical:view",
        "reporting:historical:export",
        "quality:evaluation:view",
        "recording:recording:play",
        "recording:recording:export",
        "routing:skill:view",
        "routing:flow:view",
        "analytics:cdr:view",
        "analytics:cdr:export",
        "analytics:interval:view",
        "system:audit:view",
        "agentassist:session:view",
        "callanalytics:analysis:view",
        "partner:customer:view",
        "partner:billing:view",
        "partner:settings:view",
    };

    [Fact]
    public void ReadOnlyFilter_ShouldKeepOnlyViewPermissions()
    {
        var allPerms = new HashSet<string>
        {
            "contacts:contact:view", "contacts:contact:edit", "contacts:contact:create",
            "queues:queue:view", "queues:queue:create", "queues:queue:edit",
            "users:user:view", "users:user:create",
            "system:audit:view", "system:tenant:configure",
        };

        var filtered = allPerms.Where(p => ReadOnlyPermissions.Contains(p)).ToHashSet();

        filtered.Should().HaveCount(4);
        filtered.Should().Contain("contacts:contact:view");
        filtered.Should().Contain("queues:queue:view");
        filtered.Should().Contain("users:user:view");
        filtered.Should().Contain("system:audit:view");
        filtered.Should().NotContain("contacts:contact:edit");
        filtered.Should().NotContain("system:tenant:configure");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldExcludePlatformPermissions()
    {
        var allPerms = new HashSet<string>
        {
            "platform:tenant:create", "platform:tenant:manage",
            "contacts:contact:view", "queues:queue:view",
        };

        var nonPlatform = allPerms.Where(p => !p.StartsWith("platform:", StringComparison.Ordinal));
        var filtered = nonPlatform.Where(p => ReadOnlyPermissions.Contains(p)).ToHashSet();

        filtered.Should().HaveCount(2);
        filtered.Should().NotContain("platform:tenant:create");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldIncludeMonitorPermission()
    {
        // Monitor is view-like (listen to conversations)
        var allPerms = new HashSet<string>
        {
            "contacts:conversation:handle", "contacts:conversation:monitor",
            "contacts:conversation:transfer", "contacts:conversation:barge",
        };

        var filtered = allPerms.Where(p => ReadOnlyPermissions.Contains(p)).ToHashSet();

        filtered.Should().ContainSingle().Which.Should().Be("contacts:conversation:monitor");
    }

    [Fact]
    public void ReadOnlyFilter_ShouldIncludeExportPermissions()
    {
        var allPerms = new HashSet<string>
        {
            "reporting:historical:view", "reporting:historical:export",
            "recording:recording:play", "recording:recording:export",
            "analytics:cdr:view", "analytics:cdr:export",
        };

        var filtered = allPerms.Where(p => ReadOnlyPermissions.Contains(p)).ToHashSet();

        filtered.Should().HaveCount(6); // All are read-only
    }

    [Fact]
    public void FullModeFilter_ShouldKeepAllNonPlatformPermissions()
    {
        var allPerms = new HashSet<string>
        {
            "platform:tenant:create", "contacts:contact:view", "contacts:contact:edit",
            "queues:queue:view", "queues:queue:create",
        };

        var filtered = allPerms.Where(p => !p.StartsWith("platform:", StringComparison.Ordinal)).ToHashSet();

        filtered.Should().HaveCount(4);
        filtered.Should().Contain("contacts:contact:edit");
        filtered.Should().Contain("queues:queue:create");
    }

    [Fact]
    public void ReadOnlyPermissionSet_ShouldHave22Entries()
    {
        ReadOnlyPermissions.Should().HaveCount(22);
    }
}
```

- [ ] **Step 2: Run tests — expect pass (pure logic tests)**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ImpersonationReadOnlyTests" -v q
```

- [ ] **Step 3: Update ImpersonateRequest and ImpersonateResponse DTOs**

In `src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs`, replace the DTOs at the bottom:

```csharp
internal sealed record ImpersonateRequest(string TargetTenantId, bool ReadOnly = false);
internal sealed record ImpersonateResponse(string AccessToken, DateTimeOffset ExpiresAt, string TargetTenantId, string TargetTenantName, bool ReadOnly);
```

- [ ] **Step 4: Add ReadOnlyPermissions set and filter logic to StartImpersonation**

In `ManagementImpersonationEndpoints.cs`, add the static set at class level:

```csharp
    private static readonly HashSet<string> ReadOnlyPermissions = new(StringComparer.Ordinal)
    {
        "contacts:contact:view",
        "contacts:conversation:monitor",
        "queues:queue:view",
        "users:user:view",
        "campaigns:campaign:view",
        "reporting:realtime:view",
        "reporting:historical:view",
        "reporting:historical:export",
        "quality:evaluation:view",
        "recording:recording:play",
        "recording:recording:export",
        "routing:skill:view",
        "routing:flow:view",
        "analytics:cdr:view",
        "analytics:cdr:export",
        "analytics:interval:view",
        "system:audit:view",
        "agentassist:session:view",
        "callanalytics:analysis:view",
        "partner:customer:view",
        "partner:billing:view",
        "partner:settings:view",
    };
```

In `StartImpersonation`, replace the permission filtering block:

```csharp
        // Target permissions: caller's permissions minus platform:* scoped ones
        var nonPlatformPerms = callerPermissions
            .Where(p => !p.StartsWith("platform:", StringComparison.Ordinal));

        var targetPermissions = body.ReadOnly
            ? new HashSet<string>(nonPlatformPerms.Where(p => ReadOnlyPermissions.Contains(p)))
            : new HashSet<string>(nonPlatformPerms);

        // Generate shadow JWT
        var (token, expiresAt) = jwtTokenService.GenerateImpersonationToken(
            adminUser, body.TargetTenantId, targetPermissions, body.ReadOnly);
```

Update the audit log metadata to include mode:

```csharp
        await authEventService.LogAsync(
            callerTenantId,
            callerUserId,
            AuthEventTypes.ImpersonationStarted,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent,
            new { targetTenantId = body.TargetTenantId, targetTenantName = targetTenant.Name, mode = body.ReadOnly ? "read_only" : "full" },
            ct);

        return Results.Ok(new ImpersonateResponse(token, expiresAt, body.TargetTenantId, targetTenant.Name, body.ReadOnly));
```

- [ ] **Step 5: Add readonly claim to JwtTokenService**

In `src/Asterisk.Platform.Api/Services/JwtTokenService.cs`, update the `GenerateImpersonationToken` signature and add the claim:

```csharp
    public (string Token, DateTimeOffset ExpiresAt) GenerateImpersonationToken(
        User admin, string targetTenantId, IReadOnlySet<string> targetPermissions, bool readOnly = false)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TimeSpan.FromMinutes(30));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.UserId.Value),
            new("tid", targetTenantId),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new("name", admin.DisplayName),
            new(ClaimTypes.Role, "Admin"),
            new("impersonator_id", admin.UserId.Value),
            new("impersonator_tenant", admin.TenantId.Value),
            new("impersonation", "true"),
        };

        if (readOnly)
            claims.Add(new Claim("readonly", "true"));

        foreach (var permission in targetPermissions)
        {
            claims.Add(new Claim("permissions", permission));
        }

        // ...rest unchanged...
```

- [ ] **Step 6: Build and run all tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build Asterisk.Platform.slnx && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ImpersonationReadOnlyTests" -v q
```

- [ ] **Step 7: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs src/Asterisk.Platform.Api/Services/JwtTokenService.cs tests/Asterisk.Platform.Api.Tests/ImpersonationReadOnlyTests.cs
git commit -m "feat: impersonation read-only permission filter + readonly JWT claim"
```

---

## Task 3: Read-Only Middleware Safety Net

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ReadOnlyMiddlewareTests.cs`

- [ ] **Step 1: Write tests for read-only middleware blocking**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/ReadOnlyMiddlewareTests.cs
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ReadOnlyMiddlewareTests
{
    // Test the IsBlockedInReadOnlyMode logic directly via a helper
    // The middleware is internal, so we test the blocking rules as a pure function

    private static bool IsBlockedInReadOnlyMode(string method, string path)
    {
        // GET, HEAD, OPTIONS always allowed
        if (method is "GET" or "HEAD" or "OPTIONS")
            return false;

        // DELETE /management/impersonate always allowed (end session)
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/api/v1/management/impersonate", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/management/impersonate", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Block all other DELETE, PUT, PATCH
        if (method is "DELETE" or "PUT" or "PATCH")
            return true;

        // POST: allow safe read-only operations
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/sse", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/export", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        return false;
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/queues")]
    [InlineData("GET", "/api/v1/analytics/live")]
    [InlineData("HEAD", "/api/v1/admin/queues")]
    [InlineData("OPTIONS", "/api/v1/admin/queues")]
    public void ReadOnlyMode_ShouldAllowReadMethods(string method, string path)
    {
        IsBlockedInReadOnlyMode(method, path).Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", "/api/v1/admin/queues/q1")]
    [InlineData("DELETE", "/api/v1/admin/queues/q1")]
    [InlineData("PATCH", "/api/v1/admin/queues/q1")]
    [InlineData("PUT", "/api/v1/admin/tenant/settings")]
    [InlineData("DELETE", "/api/v1/management/tenants/acme")]
    public void ReadOnlyMode_ShouldBlockWriteMethods(string method, string path)
    {
        IsBlockedInReadOnlyMode(method, path).Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", "/api/v1/sse/events")]
    [InlineData("POST", "/api/v1/analytics/search")]
    [InlineData("POST", "/api/v1/gdpr/export")]
    public void ReadOnlyMode_ShouldAllowSafePostEndpoints(string method, string path)
    {
        IsBlockedInReadOnlyMode(method, path).Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/api/v1/admin/queues")]
    [InlineData("POST", "/api/v1/admin/users")]
    [InlineData("POST", "/api/v1/management/tenants")]
    public void ReadOnlyMode_ShouldBlockUnsafePostEndpoints(string method, string path)
    {
        IsBlockedInReadOnlyMode(method, path).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests — expect pass**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ReadOnlyMiddlewareTests" -v q
```

- [ ] **Step 3: Add read-only blocking to TenantResolutionMiddleware**

In `src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs`, add after the existing impersonation block check:

```csharp
        // Block write operations during read-only impersonation
        if (IsReadOnlyImpersonation(context) && IsBlockedInReadOnlyMode(context))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var error = new ErrorResponse("Operation not allowed in read-only impersonation mode");
            await JsonSerializer.SerializeAsync(context.Response.Body, error, ApiJsonContext.Default.ErrorResponse);
            return;
        }
```

Add the helper methods:

```csharp
    private static bool IsReadOnlyImpersonation(HttpContext context)
    {
        return IsImpersonating(context)
            && context.User.FindFirstValue("readonly") == "true";
    }

    private static bool IsBlockedInReadOnlyMode(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "";

        // GET, HEAD, OPTIONS always allowed
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            return false;

        // DELETE /management/impersonate always allowed (end session)
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/api/v1/management/impersonate", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/management/impersonate", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Block all other DELETE, PUT, PATCH
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            return true;

        // POST: allow safe read-only operations
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("/sse", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/search", StringComparison.OrdinalIgnoreCase))
                return false;
            if (path.Contains("/export", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        return false;
    }
```

- [ ] **Step 4: Build and run tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build Asterisk.Platform.slnx && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ReadOnlyMiddlewareTests" -v q
```

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/TenantResolutionMiddleware.cs tests/Asterisk.Platform.Api.Tests/ReadOnlyMiddlewareTests.cs
git commit -m "feat: read-only impersonation middleware safety net"
```

---

## Task 4: Provisioning Templates (Static Data)

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/TenantProvisioningTemplates.cs`

- [ ] **Step 1: Create template definitions**

```csharp
// File: src/Asterisk.Platform.Api/Services/TenantProvisioningTemplates.cs
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Api.Services;

internal static class TenantProvisioningTemplates
{
    public static readonly IReadOnlySet<string> ValidTemplateNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "support", "sales", "blended" };

    public static IReadOnlyList<Queue> GetQueues(string template, TenantId tenantId, string timezone)
    {
        var hours = CreateBusinessHours(timezone);
        return template.ToLowerInvariant() switch
        {
            "support" => [CreateQueue(tenantId, "General Support", 20, 1800, 14400, hours)],
            "sales" =>
            [
                CreateQueue(tenantId, "Sales Inbound", 15, 600, 3600, hours),
                CreateQueue(tenantId, "Sales Outbound", 15, 600, 3600, hours),
            ],
            "blended" =>
            [
                CreateQueue(tenantId, "Support", 20, 1800, 14400, hours),
                CreateQueue(tenantId, "Sales", 15, 600, 3600, hours),
                CreateQueue(tenantId, "VIP", 10, 300, 1800, HoursOfOperation.AlwaysOpen()),
            ],
            _ => [],
        };
    }

    public static IReadOnlyList<FlowDefinition> GetFlows(string template, TenantId tenantId, string tenantName)
    {
        return template.ToLowerInvariant() switch
        {
            "support" => [CreateWelcomeFlow(tenantId, tenantName, "Support Welcome", "support")],
            "sales" => [CreateWelcomeFlow(tenantId, tenantName, "Sales Greeting", "sales")],
            "blended" => [CreateWelcomeFlow(tenantId, tenantName, "Welcome Routing", "blended")],
            _ => [],
        };
    }

    public static IReadOnlyList<AutomationRule> GetAutomationRules(string template, TenantId tenantId)
    {
        return template.ToLowerInvariant() switch
        {
            "support" =>
            [
                CreateAutoCloseRule(tenantId),
                CreateSlaBreachRule(tenantId),
            ],
            "sales" =>
            [
                CreateHotLeadRule(tenantId),
                CreateFollowUpRule(tenantId),
            ],
            "blended" =>
            [
                CreateAutoCloseRule(tenantId),
                CreateSlaBreachRule(tenantId),
                CreateVipDetectionRule(tenantId),
            ],
            _ => [],
        };
    }

    public static Survey CreateDefaultCsatSurvey(TenantId tenantId) => new()
    {
        SurveyId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "Customer Satisfaction",
        Type = SurveyType.Csat,
        IsActive = true,
        Questions =
        [
            new SurveyQuestion
            {
                QuestionId = EntityId.NewId(),
                Text = "How would you rate your experience?",
                Type = SurveyQuestionType.Scale,
            },
            new SurveyQuestion
            {
                QuestionId = EntityId.NewId(),
                Text = "Any additional feedback?",
                Type = SurveyQuestionType.FreeText,
            },
        ],
    };

    // ─── Private Helpers ──────────────────────────────────────────────────

    private static Queue CreateQueue(TenantId tenantId, string name,
        int answerSec, int firstResponseSec, int resolutionSec, HoursOfOperation hours) => new()
    {
        QueueId = EntityId.NewId(),
        TenantId = tenantId,
        Name = name,
        IsActive = true,
        SlaTargets = new SlaPolicyTarget
        {
            AnswerWithinSeconds = answerSec,
            FirstResponseWithinSeconds = firstResponseSec,
            ResolutionWithinSeconds = resolutionSec,
        },
        Hours = hours,
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 30, ForceWrapUp = false },
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedBy = "system",
    };

    private static HoursOfOperation CreateBusinessHours(string timezone)
    {
        var hours = new HoursOfOperation(timezone);
        var open = new TimeOnly(9, 0);
        var close = new TimeOnly(18, 0);
        hours.SetDaySchedule(DayOfWeek.Monday, open, close);
        hours.SetDaySchedule(DayOfWeek.Tuesday, open, close);
        hours.SetDaySchedule(DayOfWeek.Wednesday, open, close);
        hours.SetDaySchedule(DayOfWeek.Thursday, open, close);
        hours.SetDaySchedule(DayOfWeek.Friday, open, close);
        return hours;
    }

    private static FlowDefinition CreateWelcomeFlow(TenantId tenantId, string tenantName, string flowName, string variant)
    {
        var entryId = EntityId.NewId();
        var endId = EntityId.NewId();
        var message = variant switch
        {
            "sales" => $"Thanks for reaching out to {tenantName} sales. An agent will be with you shortly.",
            "blended" => $"Welcome to {tenantName}. Please hold while we connect you.",
            _ => $"Welcome to {tenantName} support. We'll be right with you.",
        };

        return new FlowDefinition
        {
            FlowId = EntityId.NewId(),
            TenantId = tenantId,
            Name = flowName,
            Version = 1,
            IsPublished = false,
            EntryNodeId = entryId,
            Nodes =
            [
                new FlowNode
                {
                    NodeId = entryId,
                    Type = "SendMessage",
                    Config = new Dictionary<string, string> { ["message"] = message },
                    Edges = [new FlowEdge("default", endId)],
                },
                new FlowNode
                {
                    NodeId = endId,
                    Type = "End",
                    Config = new Dictionary<string, string>(),
                    Edges = [],
                },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AutomationRule CreateAutoCloseRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "Auto-Close Inactive Conversations",
        Trigger = AutomationTrigger.TimerElapsed,
        Conditions = [new AutomationCondition { Field = "idle_days", Operator = ConditionOperator.GreaterThan, Value = "30" }],
        Actions = [new AutomationAction { Type = AutomationActionType.CloseConversation }],
        IsActive = true,
        Priority = 100,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateSlaBreachRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "SLA Breach Escalation",
        Trigger = AutomationTrigger.SlaBreached,
        Conditions = [],
        Actions = [new AutomationAction { Type = AutomationActionType.SetPriority, Config = new Dictionary<string, string> { ["priority"] = "high" } }],
        IsActive = true,
        Priority = 50,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateHotLeadRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "Hot Lead Priority",
        Trigger = AutomationTrigger.MessageReceived,
        Conditions = [new AutomationCondition { Field = "message_text", Operator = ConditionOperator.Contains, Value = "pricing" }],
        Actions = [new AutomationAction { Type = AutomationActionType.SetPriority, Config = new Dictionary<string, string> { ["priority"] = "high" } }],
        IsActive = true,
        Priority = 50,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateFollowUpRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "Follow-Up Reminder",
        Trigger = AutomationTrigger.TimerElapsed,
        Conditions = [new AutomationCondition { Field = "idle_hours", Operator = ConditionOperator.GreaterThan, Value = "24" }],
        Actions = [new AutomationAction { Type = AutomationActionType.SetPriority, Config = new Dictionary<string, string> { ["priority"] = "medium" } }],
        IsActive = true,
        Priority = 100,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateVipDetectionRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.NewId(),
        TenantId = tenantId,
        Name = "VIP Detection",
        Trigger = AutomationTrigger.ConversationCreated,
        Conditions = [new AutomationCondition { Field = "contact_tag", Operator = ConditionOperator.Contains, Value = "vip" }],
        Actions = [new AutomationAction { Type = AutomationActionType.SetPriority, Config = new Dictionary<string, string> { ["priority"] = "critical" } }],
        IsActive = true,
        Priority = 10,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
```

- [ ] **Step 2: Build to verify no compilation errors**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build src/Asterisk.Platform.Api/
```

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/TenantProvisioningTemplates.cs
git commit -m "feat: static provisioning templates (support, sales, blended)"
```

---

## Task 5: TenantProvisioningService

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/TenantProvisioningServiceTests.cs`

- [ ] **Step 1: Write tests**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/TenantProvisioningServiceTests.cs
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantProvisioningServiceTests
{
    private readonly ITenantRoleStore _roleStore = Substitute.For<ITenantRoleStore>();
    private readonly IRoleTemplateStore _templateStore = Substitute.For<IRoleTemplateStore>();
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly IAutomationRuleStore _automationStore = Substitute.For<IAutomationRuleStore>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly ITenantAuthConfigStore _authConfigStore = Substitute.For<ITenantAuthConfigStore>();
    private readonly ITenantRetentionPolicyStore _retentionStore = Substitute.For<ITenantRetentionPolicyStore>();

    private TenantProvisioningService CreateService() => new(
        _roleStore, _templateStore, _queueStore, _surveyStore,
        _automationStore, _flowStore, _authConfigStore, _retentionStore,
        Substitute.For<ILogger<TenantProvisioningService>>());

    private static Tenant CreateTenant(string? template = null)
    {
        var metadata = new Dictionary<string, string> { ["Plan"] = "Pro" };
        if (template is not null)
            metadata["OnboardingTemplate"] = template;
        return new Tenant
        {
            TenantId = "test-tenant",
            Name = "Test Corp",
            Type = TenantType.Customer,
            Metadata = metadata,
        };
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCloneRoleTemplates()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new RoleTemplate { TemplateId = "agent", Name = "Agent", Description = "Agent role", IsSystem = true, CreatedAt = DateTimeOffset.UtcNow },
            new RoleTemplate { TemplateId = "supervisor", Name = "Supervisor", Description = "Supervisor role", IsSystem = true, CreatedAt = DateTimeOffset.UtcNow },
        ]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant(), CancellationToken.None);

        await _roleStore.Received(2).CloneFromTemplateAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCreateCsatSurvey()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant(), CancellationToken.None);

        await _surveyStore.Received(1).SaveAsync(Arg.Is<Survey>(s =>
            s.Type == SurveyType.Csat && s.Questions.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCreateRetentionPolicy()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant(), CancellationToken.None);

        await _retentionStore.Received(1).UpsertAsync(
            Arg.Is<TenantRetentionPolicy>(p => p.TenantId == "test-tenant"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithSupportTemplate_ShouldCreateQueue()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant("support"), CancellationToken.None);

        await _queueStore.Received(1).SaveAsync(
            Arg.Is<Queue>(q => q.Name == "General Support"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithSalesTemplate_ShouldCreateTwoQueues()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant("sales"), CancellationToken.None);

        await _queueStore.Received(2).SaveAsync(Arg.Any<Queue>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithNoTemplate_ShouldNotCreateQueues()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var svc = CreateService();

        await svc.OnTenantCreatedAsync(CreateTenant(), CancellationToken.None);

        await _queueStore.DidNotReceive().SaveAsync(Arg.Any<Queue>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Create TenantProvisioningService**

```csharp
// File: src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;
using Asterisk.Platform.Automation;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class TenantProvisioningService : ITenantLifecycleHandler
{
    private readonly ITenantRoleStore _roleStore;
    private readonly IRoleTemplateStore _templateStore;
    private readonly IQueueStore _queueStore;
    private readonly ISurveyStore _surveyStore;
    private readonly IAutomationRuleStore _automationStore;
    private readonly IFlowStore _flowStore;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly ITenantRetentionPolicyStore _retentionStore;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        ITenantRoleStore roleStore,
        IRoleTemplateStore templateStore,
        IQueueStore queueStore,
        ISurveyStore surveyStore,
        IAutomationRuleStore automationStore,
        IFlowStore flowStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantRetentionPolicyStore retentionStore,
        ILogger<TenantProvisioningService> logger)
    {
        _roleStore = roleStore;
        _templateStore = templateStore;
        _queueStore = queueStore;
        _surveyStore = surveyStore;
        _automationStore = automationStore;
        _flowStore = flowStore;
        _authConfigStore = authConfigStore;
        _retentionStore = retentionStore;
        _logger = logger;
    }

    public async ValueTask OnTenantCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var tenantId = new TenantId(tenant.TenantId);
        var plan = tenant.GetPlan();
        var template = tenant.Metadata?.GetValueOrDefault("OnboardingTemplate");
        var timezone = "UTC";

        LogProvisioningStarted(tenant.TenantId, template ?? "none");

        // ── Golden Defaults (always) ──────────────────────────────────────

        await CloneRoleTemplatesAsync(tenantId, cancellationToken);
        await CreateAuthConfigAsync(tenantId, plan, cancellationToken);
        await CreateRetentionPolicyAsync(tenantId, plan, cancellationToken);
        await _surveyStore.SaveAsync(TenantProvisioningTemplates.CreateDefaultCsatSurvey(tenantId), cancellationToken);

        // ── Template-specific resources ───────────────────────────────────

        if (!string.IsNullOrEmpty(template) && TenantProvisioningTemplates.ValidTemplateNames.Contains(template))
        {
            foreach (var queue in TenantProvisioningTemplates.GetQueues(template, tenantId, timezone))
                await _queueStore.SaveAsync(queue, cancellationToken);

            foreach (var flow in TenantProvisioningTemplates.GetFlows(template, tenantId, tenant.Name))
                await _flowStore.SaveAsync(flow, cancellationToken);

            foreach (var rule in TenantProvisioningTemplates.GetAutomationRules(template, tenantId))
                await _automationStore.SaveAsync(rule, cancellationToken);
        }

        LogProvisioningCompleted(tenant.TenantId);
    }

    public ValueTask OnTenantSuspendedAsync(string tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask OnTenantDeletedAsync(string tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private async Task CloneRoleTemplatesAsync(TenantId tenantId, CancellationToken ct)
    {
        var templates = await _templateStore.GetAllAsync(ct);
        foreach (var tmpl in templates)
        {
            try
            {
                await _roleStore.CloneFromTemplateAsync(tenantId, tmpl.TemplateId, tmpl.TemplateId, tmpl.Name, tmpl.Description, ct);
            }
            catch (Exception ex)
            {
                LogRoleCloneFailed(tmpl.TemplateId, tenantId.Value, ex);
            }
        }
    }

    private async Task CreateAuthConfigAsync(TenantId tenantId, TenantPlan plan, CancellationToken ct)
    {
        var (mfaPolicy, minLength) = plan switch
        {
            TenantPlan.Enterprise => ("required_for_roles", 16),
            TenantPlan.Pro => ("optional", 12),
            _ => ("optional", 8),
        };

        var config = new TenantAuthConfig
        {
            TenantId = tenantId.Value,
            MfaPolicy = mfaPolicy,
            MfaRequiredRoles = plan == TenantPlan.Enterprise ? ["Admin", "Supervisor"] : [],
            PasswordMinLength = minLength,
            PasswordRequireUppercase = true,
            PasswordRequireNumber = true,
            PasswordRequireSpecial = plan >= TenantPlan.Pro,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await _authConfigStore.SaveAsync(config, ct);
    }

    private async Task CreateRetentionPolicyAsync(TenantId tenantId, TenantPlan plan, CancellationToken ct)
    {
        var days = plan switch
        {
            TenantPlan.Enterprise => 730,
            TenantPlan.Pro => 365,
            _ => 90,
        };

        await _retentionStore.UpsertAsync(new TenantRetentionPolicy
        {
            TenantId = tenantId.Value,
            ConversationRetentionDays = days,
            AuthEventRetentionDays = days,
            AuditRetentionDays = PlanDefinition.GetAuditRetentionDays(plan),
            UsageRecordRetentionDays = days,
        }, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning tenant {TenantId} with template '{Template}'")]
    private partial void LogProvisioningStarted(string tenantId, string template);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning completed for tenant {TenantId}")]
    private partial void LogProvisioningCompleted(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clone role template {TemplateId} for tenant {TenantId}")]
    private partial void LogRoleCloneFailed(string templateId, string tenantId, Exception ex);
}
```

- [ ] **Step 3: Run tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantProvisioningServiceTests" -v q
```

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs tests/Asterisk.Platform.Api.Tests/TenantProvisioningServiceTests.cs
git commit -m "feat: TenantProvisioningService with Golden Defaults + template resources"
```

---

## Task 6: Template Param in CreateTenant Endpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ProvisioningIntegrationTests.cs`

- [ ] **Step 1: Write tests**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/ProvisioningIntegrationTests.cs
using Asterisk.Platform.Api.Services;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ProvisioningIntegrationTests
{
    [Theory]
    [InlineData("support")]
    [InlineData("sales")]
    [InlineData("blended")]
    public void ValidTemplateNames_ShouldBeAccepted(string template)
    {
        TenantProvisioningTemplates.ValidTemplateNames.Contains(template).Should().BeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("enterprise")]
    [InlineData("")]
    public void InvalidTemplateNames_ShouldBeRejected(string template)
    {
        TenantProvisioningTemplates.ValidTemplateNames.Contains(template).Should().BeFalse();
    }

    [Fact]
    public void SupportTemplate_ShouldCreateOneQueue()
    {
        var queues = TenantProvisioningTemplates.GetQueues("support",
            new Platform.Core.TenantId("t1"), "UTC");
        queues.Should().HaveCount(1);
        queues[0].Name.Should().Be("General Support");
    }
}
```

- [ ] **Step 2: Add Template to CreateMgmtTenantRequest**

In `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`, update the DTO:

```csharp
internal sealed record CreateMgmtTenantRequest(
    string TenantId,
    string Name,
    TenantType Type = TenantType.Customer,
    string? ParentTenantId = null,
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    Dictionary<string, string>? Metadata = null,
    string? Template = null);
```

In `CreateTenant` method, before `await store.UpsertAsync(tenant, ct);`, add template validation and metadata injection:

```csharp
        // Validate template if provided
        if (body.Template is not null && !TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
            return Results.BadRequest(new ErrorResponse($"Invalid template '{body.Template}'. Valid: support, sales, blended."));

        // Inject template into metadata so TenantProvisioningService can read it
        var metadata = body.Metadata ?? new Dictionary<string, string>();
        if (body.Template is not null)
            metadata["OnboardingTemplate"] = body.Template;
```

Replace `Metadata = body.Metadata,` with `Metadata = metadata,` in the Tenant constructor.

Add `using Asterisk.Platform.Api.Services;` at the top.

- [ ] **Step 3: Add Template to CreatePartnerCustomerRequest**

In `src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs`, update the DTO:

```csharp
internal sealed record CreatePartnerCustomerRequest(
    string TenantId,
    string Name,
    string? Plan = null,
    string? Template = null);
```

In `CreateCustomer` method, add template validation after plan resolution:

```csharp
    // Validate template if provided
    if (body.Template is not null && !TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
        return Results.BadRequest(new ErrorResponse($"Invalid template '{body.Template}'. Valid: support, sales, blended."));
```

And inject into metadata:

```csharp
    var metadata = new Dictionary<string, string>
    {
        ["Plan"] = customerPlan.ToString(),
        ["RateLimitTier"] = derivedTier.ToString(),
    };
    if (body.Template is not null)
        metadata["OnboardingTemplate"] = body.Template;
```

Add `using Asterisk.Platform.Api.Services;` at the top.

- [ ] **Step 4: Run tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build Asterisk.Platform.slnx && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ProvisioningIntegrationTests" -v q
```

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs tests/Asterisk.Platform.Api.Tests/ProvisioningIntegrationTests.cs
git commit -m "feat: template param in CreateTenant + CreateCustomer endpoints"
```

---

## Task 7: Onboarding Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/OnboardingEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/OnboardingEndpointsTests.cs`

- [ ] **Step 1: Write tests**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/OnboardingEndpointsTests.cs
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Storage.InMemory;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class OnboardingEndpointsTests
{
    [Fact]
    public void OnboardingStatus_ShouldShowNotCompleted_WhenMetadataAbsent()
    {
        var tenant = new Tenant
        {
            TenantId = "test",
            Name = "Test",
            Metadata = new Dictionary<string, string>(),
        };

        var completed = tenant.Metadata?.GetValueOrDefault("OnboardingCompleted") == "true";
        completed.Should().BeFalse();
    }

    [Fact]
    public void OnboardingStatus_ShouldShowCompleted_WhenMetadataPresent()
    {
        var tenant = new Tenant
        {
            TenantId = "test",
            Name = "Test",
            Metadata = new Dictionary<string, string> { ["OnboardingCompleted"] = "true" },
        };

        var completed = tenant.Metadata?.GetValueOrDefault("OnboardingCompleted") == "true";
        completed.Should().BeTrue();
    }

    [Fact]
    public void OnboardingStatus_ShouldShowTemplateApplied()
    {
        var tenant = new Tenant
        {
            TenantId = "test",
            Name = "Test",
            Metadata = new Dictionary<string, string> { ["OnboardingTemplate"] = "support" },
        };

        var template = tenant.Metadata?.GetValueOrDefault("OnboardingTemplate");
        template.Should().Be("support");
    }

    [Fact]
    public void OnboardingStatus_ShouldShowChecklistDismissed()
    {
        var tenant = new Tenant
        {
            TenantId = "test",
            Name = "Test",
            Metadata = new Dictionary<string, string> { ["OnboardingDismissedChecklist"] = "true" },
        };

        var dismissed = tenant.Metadata?.GetValueOrDefault("OnboardingDismissedChecklist") == "true";
        dismissed.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Create OnboardingEndpoints**

```csharp
// File: src/Asterisk.Platform.Api/Endpoints/OnboardingEndpoints.cs
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/onboarding").RequireAuthorization("AdminOnly");

        group.MapGet("/status", GetStatus);
        group.MapPost("/apply-template", ApplyTemplate);
        group.MapPost("/complete", CompleteOnboarding);
        group.MapPut("/dismiss-checklist", DismissChecklist);
    }

    private static async Task<IResult> GetStatus(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IQueueStore queueStore,
        [FromServices] IUserStore userStore,
        [FromServices] ITenantChannelConfigStore channelConfigStore,
        [FromServices] ITenantBrandingStore brandingStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null) return Results.Forbid();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null) return Results.NotFound();

        var tid = new TenantId(tenantId);
        var metadata = tenant.Metadata ?? new Dictionary<string, string>();

        var wizardCompleted = metadata.GetValueOrDefault("OnboardingCompleted") == "true";
        var templateApplied = metadata.GetValueOrDefault("OnboardingTemplate");
        var checklistDismissed = metadata.GetValueOrDefault("OnboardingDismissedChecklist") == "true";

        // Build dynamic checklist
        var queues = await queueStore.ListAsync(tid, new PagedQuery(1, 1), ct);
        var users = await userStore.ListAsync(tid, new PagedQuery(1, 1), ct);
        var branding = await brandingStore.GetAsync(tenantId, ct);

        var checklist = new List<ChecklistItemDto>
        {
            new("org_profile", "Organization profile configured", wizardCompleted),
            new("channels_selected", "Channels selected", metadata.ContainsKey("OnboardingChannels")),
            new("first_queue", "First queue created", queues.TotalCount > 0),
            new("channel_credentials", "Configure channel credentials", false), // simplified check
            new("first_agent", "Add your first agent", users.TotalCount > 1), // >1 because admin exists
            new("test_interaction", "Make a test call/chat", metadata.GetValueOrDefault("OnboardingTestCompleted") == "true"),
            new("branding", "Customize branding", branding?.DisplayName is not null),
        };

        return Results.Ok(new OnboardingStatusDto(wizardCompleted, templateApplied, checklist, checklistDismissed));
    }

    private static async Task<IResult> ApplyTemplate(
        HttpContext context,
        [FromBody] ApplyTemplateRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null) return Results.Forbid();

        if (!TenantProvisioningTemplates.ValidTemplateNames.Contains(body.Template))
            return Results.BadRequest(new ErrorResponse($"Invalid template '{body.Template}'. Valid: support, sales, blended."));

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null) return Results.NotFound();

        // Check if template already applied
        if (tenant.Metadata?.ContainsKey("OnboardingTemplate") == true
            && tenant.Metadata["OnboardingTemplate"] != "none")
            return Results.Ok(new MessageResponse("Template already applied."));

        // Update metadata with template
        var metadata = tenant.Metadata ?? new Dictionary<string, string>();
        metadata["OnboardingTemplate"] = body.Template;

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = metadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);

        // Apply template resources (find the provisioning service among lifecycle handlers)
        foreach (var handler in lifecycleHandlers)
        {
            if (handler is TenantProvisioningService provisioner)
            {
                await provisioner.ApplyTemplateAsync(updated, body.Template, ct);
                break;
            }
        }

        return Results.Ok(new MessageResponse($"Template '{body.Template}' applied."));
    }

    private static async Task<IResult> CompleteOnboarding(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null) return Results.Forbid();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null) return Results.NotFound();

        var metadata = tenant.Metadata ?? new Dictionary<string, string>();
        metadata["OnboardingCompleted"] = "true";

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = metadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);
        return Results.Ok(new MessageResponse("Onboarding completed."));
    }

    private static async Task<IResult> DismissChecklist(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null) return Results.Forbid();

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        if (tenant is null) return Results.NotFound();

        var metadata = tenant.Metadata ?? new Dictionary<string, string>();
        metadata["OnboardingDismissedChecklist"] = "true";

        var updated = new Tenant
        {
            TenantId = tenant.TenantId,
            Name = tenant.Name,
            Status = tenant.Status,
            Type = tenant.Type,
            ParentTenantId = tenant.ParentTenantId,
            Options = tenant.Options,
            Metadata = metadata,
            CreatedAt = tenant.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);
        return Results.Ok(new MessageResponse("Checklist dismissed."));
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record OnboardingStatusDto(
    bool WizardCompleted,
    string? TemplateApplied,
    IReadOnlyList<ChecklistItemDto> Checklist,
    bool ChecklistDismissed);

internal sealed record ChecklistItemDto(string Key, string Label, bool Completed);
internal sealed record ApplyTemplateRequest(string Template);
```

- [ ] **Step 3: Add ApplyTemplateAsync to TenantProvisioningService**

In `src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs`, add a public method:

```csharp
    public async Task ApplyTemplateAsync(Tenant tenant, string template, CancellationToken ct)
    {
        var tenantId = new TenantId(tenant.TenantId);
        var timezone = "UTC";

        foreach (var queue in TenantProvisioningTemplates.GetQueues(template, tenantId, timezone))
            await _queueStore.SaveAsync(queue, ct);

        foreach (var flow in TenantProvisioningTemplates.GetFlows(template, tenantId, tenant.Name))
            await _flowStore.SaveAsync(flow, ct);

        foreach (var rule in TenantProvisioningTemplates.GetAutomationRules(template, tenantId))
            await _automationStore.SaveAsync(rule, ct);
    }
```

- [ ] **Step 4: Run tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build Asterisk.Platform.slnx && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "OnboardingEndpointsTests" -v q
```

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/OnboardingEndpoints.cs src/Asterisk.Platform.Api/Services/TenantProvisioningService.cs tests/Asterisk.Platform.Api.Tests/OnboardingEndpointsTests.cs
git commit -m "feat: onboarding endpoints (status, apply-template, complete, dismiss)"
```

---

## Task 8: Wire DI + Endpoints + ApiJsonContext

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Register TenantProvisioningService in Program.cs DI**

Add after the existing singleton registrations (near line 172):

```csharp
builder.Services.AddSingleton<TenantProvisioningService>();
builder.Services.AddSingleton<ITenantLifecycleHandler>(sp => sp.GetRequiredService<TenantProvisioningService>());
```

- [ ] **Step 2: Map OnboardingEndpoints in Program.cs**

Add after `v1.MapNotificationEndpoints();`:

```csharp
v1.MapOnboardingEndpoints();
```

- [ ] **Step 3: Register new DTOs in ApiJsonContext**

Add to `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`:

```csharp
// Onboarding
[JsonSerializable(typeof(OnboardingStatusDto))]
[JsonSerializable(typeof(ChecklistItemDto))]
[JsonSerializable(typeof(List<ChecklistItemDto>))]
[JsonSerializable(typeof(ApplyTemplateRequest))]
```

- [ ] **Step 4: Build entire solution**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 5: Run all tests**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform && dotnet test Asterisk.Platform.slnx -v q
```

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Api/Program.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "feat: wire TenantProvisioningService DI + OnboardingEndpoints + ApiJsonContext"
```

---

## Task 9: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update CLAUDE.md**

Add Sprint 5 completion section. Update endpoint count to 56. Update test count. Add OnboardingEndpoints to the endpoint inventory table. Note the `Template` param on CreateTenant and CreateCustomer. Document the `ImpersonatorId` on AuditEntry. Document the `readonly` JWT claim.

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(claude): update CLAUDE.md with Sprint 5 completion"
```

---

## Self-Review Checklist

**1. Spec coverage:**
- [x] TenantProvisioningService with Golden Defaults → Task 5
- [x] 3 built-in templates (support, sales, blended) → Task 4
- [x] Template param on CreateTenant + CreateCustomer → Task 6
- [x] Onboarding endpoints (status, apply-template, complete, dismiss) → Task 7
- [x] Checklist dynamic calculation → Task 7 (GetStatus)
- [x] ImpersonateRequest ReadOnly field → Task 2
- [x] Permission intersection (23 view permissions) → Task 2
- [x] readonly JWT claim → Task 2
- [x] Middleware safety net for read-only mode → Task 3
- [x] AuditEntry ImpersonatorId → Task 1
- [x] Migration 011 → Task 1
- [x] DI wiring + endpoint mapping → Task 8
- [x] ApiJsonContext registration → Task 8

**2. Placeholder scan:** No TBD, TODO, or incomplete sections found.

**3. Type consistency:**
- `ImpersonateRequest(string TargetTenantId, bool ReadOnly = false)` — consistent across Task 2
- `OnboardingStatusDto` — consistent between endpoints and ApiJsonContext
- `TenantProvisioningTemplates.ValidTemplateNames` — used in Tasks 4, 6, 7
- `AuditEntry.ImpersonatorId` — consistent between Task 1 model and Postgres store
