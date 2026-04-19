# Sprint 3: Partner Portal + Partner Billing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable Partner tenants to manage their own customers, define markup pricing, generate invoices, and track revenue through a dedicated `/partner/*` API surface.

**Architecture:** 19 new endpoints under `/partner/*` with `PartnerAdminOnly` auth handler, 8 new `partner:*` permissions, 3 role templates, `PartnerRevenueRecord` model for margin tracking. Reuses existing RateCard/Invoice/Usage infrastructure. IInvoiceGenerationService gets an overload accepting an explicit RateCard for Partner-based invoice generation.

**Tech Stack:** .NET 10, ASP.NET Minimal APIs, Dapper (Postgres), ConcurrentDictionary (InMemory), xUnit + FluentAssertions + NSubstitute

**Spec:** `docs/superpowers/specs/2026-04-07-sprint3-partner-portal-billing-design.md`

---

## File Structure

### New Files (16)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Platform.Billing/PartnerRevenueRecord.cs` | Revenue snapshot model |
| `src/Asterisk.Platform.Billing/IPartnerRevenueStore.cs` | Store interface |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryPartnerRevenueStore.cs` | In-memory store implementation |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPartnerRevenueStore.cs` | Postgres store implementation |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/009_PartnerRevenue.sql` | Migration for partner_revenue table |
| `src/Asterisk.Platform.Api/Auth/PartnerAdminRequirement.cs` | Authorization requirement |
| `src/Asterisk.Platform.Api/Auth/PartnerAdminAuthorizationHandler.cs` | Authorization handler |
| `src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs` | 8 partner customer endpoints |
| `src/Asterisk.Platform.Api/Endpoints/PartnerBillingEndpoints.cs` | 7 partner billing endpoints |
| `src/Asterisk.Platform.Api/Endpoints/PartnerRevenueEndpoints.cs` | 2 revenue dashboard endpoints |
| `src/Asterisk.Platform.Api/Endpoints/PartnerSettingsEndpoints.cs` | 2 partner settings endpoints |
| `tests/Asterisk.Platform.Api.Tests/PartnerAdminAuthorizationHandlerTests.cs` | Auth handler tests |
| `tests/Asterisk.Platform.Api.Tests/PartnerCustomerEndpointsTests.cs` | Customer CRUD tests |
| `tests/Asterisk.Platform.Api.Tests/PartnerBillingEndpointsTests.cs` | Billing endpoint tests |
| `tests/Asterisk.Platform.Api.Tests/PartnerRevenueEndpointsTests.cs` | Revenue endpoint tests |
| `tests/Asterisk.Platform.Api.Tests/PartnerSettingsEndpointsTests.cs` | Settings endpoint tests |

### Modified Files (8)

| File | Changes |
|------|---------|
| `src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs` | Add overload with explicit RateCard |
| `src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs` | Implement the overload |
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register IPartnerRevenueStore |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Register IPartnerRevenueStore |
| `src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs` | Add 8 partner permissions |
| `src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs` | Add 3 partner role templates |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Register new DTOs |
| `src/Asterisk.Platform.Api/Program.cs` | Auth policy + endpoint mapping |

---

### Task 1: PartnerRevenueRecord + IPartnerRevenueStore + InMemoryPartnerRevenueStore

**Files:**
- Create: `src/Asterisk.Platform.Billing/PartnerRevenueRecord.cs`
- Create: `src/Asterisk.Platform.Billing/IPartnerRevenueStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryPartnerRevenueStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create PartnerRevenueRecord model**

```csharp
// src/Asterisk.Platform.Billing/PartnerRevenueRecord.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public sealed class PartnerRevenueRecord
{
    public required EntityId RevenueId { get; init; }
    public required TenantId PartnerTenantId { get; init; }
    public required TenantId CustomerTenantId { get; init; }
    public required EntityId InvoiceId { get; init; }
    public required decimal GrossAmount { get; init; }
    public required decimal PlatformCost { get; init; }
    public required decimal PartnerMargin { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

- [ ] **Step 2: Create IPartnerRevenueStore interface**

```csharp
// src/Asterisk.Platform.Billing/IPartnerRevenueStore.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public interface IPartnerRevenueStore
{
    ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);
    ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create InMemoryPartnerRevenueStore**

Follow the pattern from `InMemoryInvoiceStore`: `ConcurrentDictionary` keyed by `EntityId`, LINQ filtering.

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryPartnerRevenueStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryPartnerRevenueStore : IPartnerRevenueStore
{
    private readonly ConcurrentDictionary<string, PartnerRevenueRecord> _records = new();

    public ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct)
    {
        var record = _records.Values
            .FirstOrDefault(r => r.PartnerTenantId == partnerId && r.InvoiceId == invoiceId);
        return ValueTask.FromResult(record);
    }

    public ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = _records.Values
            .Where(r => r.PartnerTenantId == partnerId);

        if (from.HasValue)
            query = query.Where(r => r.PeriodEnd >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.PeriodStart <= to.Value);

        IReadOnlyList<PartnerRevenueRecord> result = query
            .OrderByDescending(r => r.PeriodStart)
            .ToList();
        return ValueTask.FromResult(result);
    }

    public ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct)
    {
        _records[record.RevenueId.Value] = record;
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Register InMemoryPartnerRevenueStore in DI**

Add to `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` in the `AddInMemoryStorage()` method, in the Billing section (near `InMemoryDunningStore`):

```csharp
services.AddSingleton<IPartnerRevenueStore, InMemoryPartnerRevenueStore>();
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Billing/PartnerRevenueRecord.cs \
        src/Asterisk.Platform.Billing/IPartnerRevenueStore.cs \
        src/Asterisk.Platform.Storage.InMemory/InMemoryPartnerRevenueStore.cs \
        src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs
git commit -m "feat: add PartnerRevenueRecord model, store interface, and InMemory implementation"
```

---

### Task 2: PostgresPartnerRevenueStore + Migration 009

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/009_PartnerRevenue.sql`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPartnerRevenueStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create migration 009**

```sql
-- src/Asterisk.Platform.Storage.Postgres/Migrations/009_PartnerRevenue.sql
CREATE TABLE IF NOT EXISTS partner_revenue (
    revenue_id       TEXT PRIMARY KEY,
    partner_tenant_id TEXT NOT NULL,
    customer_tenant_id TEXT NOT NULL,
    invoice_id       TEXT NOT NULL,
    gross_amount     NUMERIC(18,4) NOT NULL,
    platform_cost    NUMERIC(18,4) NOT NULL,
    partner_margin   NUMERIC(18,4) NOT NULL,
    period_start     TIMESTAMPTZ NOT NULL,
    period_end       TIMESTAMPTZ NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_partner_period
    ON partner_revenue (partner_tenant_id, period_start);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_invoice
    ON partner_revenue (invoice_id);
```

- [ ] **Step 2: Create PostgresPartnerRevenueStore**

Follow pattern from `PostgresInvoiceStore`: class-based row type with `{get; init;}`, Dapper queries, singleton.

```csharp
// src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPartnerRevenueStore.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Dapper;
using Npgsql;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPartnerRevenueStore : IPartnerRevenueStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPartnerRevenueStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<PartnerRevenueRow>(
            "SELECT * FROM partner_revenue WHERE partner_tenant_id = @PartnerId AND invoice_id = @InvoiceId",
            new { PartnerId = partnerId.Value, InvoiceId = invoiceId.Value });
        return row?.ToRecord();
    }

    public async ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(TenantId partnerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var sql = "SELECT * FROM partner_revenue WHERE partner_tenant_id = @PartnerId";
        if (from.HasValue) sql += " AND period_end >= @From";
        if (to.HasValue) sql += " AND period_start <= @To";
        sql += " ORDER BY period_start DESC";

        var rows = await conn.QueryAsync<PartnerRevenueRow>(sql, new
        {
            PartnerId = partnerId.Value,
            From = from?.UtcDateTime,
            To = to?.UtcDateTime,
        });
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            """
            INSERT INTO partner_revenue (revenue_id, partner_tenant_id, customer_tenant_id, invoice_id,
                gross_amount, platform_cost, partner_margin, period_start, period_end, created_at)
            VALUES (@RevenueId, @PartnerTenantId, @CustomerTenantId, @InvoiceId,
                @GrossAmount, @PlatformCost, @PartnerMargin, @PeriodStart, @PeriodEnd, @CreatedAt)
            ON CONFLICT (revenue_id) DO UPDATE SET
                gross_amount = EXCLUDED.gross_amount,
                platform_cost = EXCLUDED.platform_cost,
                partner_margin = EXCLUDED.partner_margin
            """,
            new
            {
                RevenueId = record.RevenueId.Value,
                PartnerTenantId = record.PartnerTenantId.Value,
                CustomerTenantId = record.CustomerTenantId.Value,
                InvoiceId = record.InvoiceId.Value,
                record.GrossAmount,
                record.PlatformCost,
                record.PartnerMargin,
                PeriodStart = record.PeriodStart.UtcDateTime,
                PeriodEnd = record.PeriodEnd.UtcDateTime,
                CreatedAt = record.CreatedAt.UtcDateTime,
            });
    }

    // Dapper row type (class-based, {get; init;} — Npgsql 9 requirement)
    private sealed class PartnerRevenueRow
    {
        public string Revenue_Id { get; init; } = "";
        public string Partner_Tenant_Id { get; init; } = "";
        public string Customer_Tenant_Id { get; init; } = "";
        public string Invoice_Id { get; init; } = "";
        public decimal Gross_Amount { get; init; }
        public decimal Platform_Cost { get; init; }
        public decimal Partner_Margin { get; init; }
        public DateTime Period_Start { get; init; }
        public DateTime Period_End { get; init; }
        public DateTime Created_At { get; init; }

        public PartnerRevenueRecord ToRecord() => new()
        {
            RevenueId = EntityId.From(Revenue_Id),
            PartnerTenantId = new TenantId(Partner_Tenant_Id),
            CustomerTenantId = new TenantId(Customer_Tenant_Id),
            InvoiceId = EntityId.From(Invoice_Id),
            GrossAmount = Gross_Amount,
            PlatformCost = Platform_Cost,
            PartnerMargin = Partner_Margin,
            PeriodStart = new DateTimeOffset(Period_Start, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(Period_End, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(Created_At, TimeSpan.Zero),
        };
    }
}
```

**Note:** Check existing Postgres store row types for the exact column naming convention (snake_case with underscore-separated property names for Dapper mapping). Adapt the row type property names to match what Dapper auto-maps from Postgres snake_case columns. Some stores use `DefaultTypeMap.MatchNamesWithUnderscores` — check `ServiceCollectionExtensions.cs` for Postgres to see if this is configured globally.

- [ ] **Step 3: Register PostgresPartnerRevenueStore in DI**

Add to `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` in `AddPostgresStorage()`, near the other billing stores:

```csharp
services.AddSingleton<IPartnerRevenueStore>(sp =>
    new PostgresPartnerRevenueStore(sp.GetRequiredService<NpgsqlDataSource>()));
```

- [ ] **Step 4: Add migration to EnsureSchemaAsync**

Find the `EnsureSchemaAsync` or migration runner method in the Postgres storage package and add `009_PartnerRevenue.sql` to the migration list. Follow the pattern of how `008` and earlier migrations are loaded and executed.

- [ ] **Step 5: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Migrations/009_PartnerRevenue.sql \
        src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPartnerRevenueStore.cs \
        src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs
git commit -m "feat: add PostgresPartnerRevenueStore with migration 009"
```

---

### Task 3: IInvoiceGenerationService Overload

**Files:**
- Modify: `src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs`
- Modify: `src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs`

- [ ] **Step 1: Add overload to IInvoiceGenerationService**

Read the current file, then add a second method that accepts an explicit `RateCard`:

```csharp
// Add to IInvoiceGenerationService:
Task<Invoice> GenerateWithRateCardAsync(TenantId tenantId, RateCard rateCard, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
```

This overload uses the provided RateCard instead of looking up the tenant's own rate card.

- [ ] **Step 2: Implement in DefaultInvoiceGenerationService**

Read the current `GenerateAsync` implementation. The new `GenerateWithRateCardAsync` should be nearly identical but skip the `IRateCardStore.GetActiveAsync()` call and use the passed `rateCard` directly. Refactor to share the common logic (usage loading + line item calculation) via a private method.

```csharp
// In DefaultInvoiceGenerationService:

public Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
{
    // Existing: loads tenant's own rate card, then calls shared logic
}

public async Task<Invoice> GenerateWithRateCardAsync(TenantId tenantId, RateCard rateCard, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
{
    var summaries = await _meteringService.GetCurrentPeriodSummaryAsync(tenantId, ct);
    return BuildInvoice(tenantId, rateCard, summaries, periodStart, periodEnd);
}

// Extract shared logic into:
private Invoice BuildInvoice(TenantId tenantId, RateCard rateCard, IReadOnlyList<UsageSummary> summaries, DateTimeOffset periodStart, DateTimeOffset periodEnd)
{
    // Same line-item calculation logic as before
}
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: All 1446 tests pass, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs \
        src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs
git commit -m "feat: add GenerateWithRateCardAsync overload for partner invoice generation"
```

---

### Task 4: PartnerAdminRequirement + PartnerAdminAuthorizationHandler

**Files:**
- Create: `src/Asterisk.Platform.Api/Auth/PartnerAdminRequirement.cs`
- Create: `src/Asterisk.Platform.Api/Auth/PartnerAdminAuthorizationHandler.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Create PartnerAdminRequirement**

Follow `PlatformAdminRequirement` pattern:

```csharp
// src/Asterisk.Platform.Api/Auth/PartnerAdminRequirement.cs
using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PartnerAdminRequirement : IAuthorizationRequirement
{
    public string? Permission { get; }

    public PartnerAdminRequirement(string? permission = null)
    {
        Permission = permission;
    }
}
```

- [ ] **Step 2: Create PartnerAdminAuthorizationHandler**

Follow `PlatformAdminAuthorizationHandler` pattern but validate `TenantType.Partner` instead of Platform/Partner:

```csharp
// src/Asterisk.Platform.Api/Auth/PartnerAdminAuthorizationHandler.cs
using System.Security.Claims;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;

namespace Asterisk.Platform.Api.Auth;

internal sealed class PartnerAdminAuthorizationHandler : AuthorizationHandler<PartnerAdminRequirement>
{
    private readonly ITenantStore _tenantStore;
    private readonly PermissionResolver _resolver;

    public PartnerAdminAuthorizationHandler(ITenantStore tenantStore, PermissionResolver resolver)
    {
        _tenantStore = tenantStore;
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PartnerAdminRequirement requirement)
    {
        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim))
            return;

        var tenant = await _tenantStore.GetAsync(tenantIdClaim);
        if (tenant is null || tenant.Type != TenantType.Partner)
            return;

        // Partner must be in an operational status
        if (tenant.Status is TenantStatus.Suspended or TenantStatus.Deleted or TenantStatus.PendingDeletion)
            return;

        // If a specific permission is required, check it
        if (requirement.Permission is not null)
        {
            var userIdClaim = context.User.FindFirst("user_id")?.Value
                ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
                return;

            var tenantId = new TenantId(tenantIdClaim);
            var userId = EntityId.From(userIdClaim);
            var permissions = await _resolver.ResolveAsync(tenantId, userId, CancellationToken.None);
            if (!PermissionResolver.HasPermission(permissions, requirement.Permission))
                return;
        }

        context.Succeed(requirement);
    }
}
```

- [ ] **Step 3: Register in Program.cs**

Add to `src/Asterisk.Platform.Api/Program.cs` in the authorization section (after `PlatformAdminOnly` policy, around line 327):

```csharp
options.AddPolicy("PartnerAdminOnly", p =>
    p.AddRequirements(new PartnerAdminRequirement()));
```

And register the handler (after `PlatformAdminAuthorizationHandler`, around line 333):

```csharp
builder.Services.AddSingleton<IAuthorizationHandler, PartnerAdminAuthorizationHandler>();
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Auth/PartnerAdminRequirement.cs \
        src/Asterisk.Platform.Api/Auth/PartnerAdminAuthorizationHandler.cs \
        src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: add PartnerAdminOnly authorization handler and policy"
```

---

### Task 5: Permission + RoleTemplate Seeding

**Files:**
- Modify: `src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs`

- [ ] **Step 1: Add 8 partner permissions to PermissionSeeder**

Add a new `// ── partner (8) ──` section after the `// ── platform (8) ──` section in `GetPermissions()`:

```csharp
// ── partner (8) ──
yield return P("partner:customer:view", "partner", "customer", "view",
    "View partner's child customer tenants");
yield return P("partner:customer:create", "partner", "customer", "create",
    "Create customer tenants under this partner",
    ["partner:customer:view"]);
yield return P("partner:customer:manage", "partner", "customer", "manage",
    "Edit settings, suspend, and activate child customers",
    ["partner:customer:view"]);
yield return P("partner:customer:delete", "partner", "customer", "delete",
    "Delete child customer tenants (soft delete)",
    ["partner:customer:manage", "partner:customer:view"]);
yield return P("partner:billing:view", "partner", "billing", "view",
    "View invoices, usage, and revenue for child customers");
yield return P("partner:billing:manage", "partner", "billing", "manage",
    "Create rate cards, generate invoices, manage quotas",
    ["partner:billing:view"]);
yield return P("partner:settings:view", "partner", "settings", "view",
    "View partner's own tenant settings");
yield return P("partner:settings:manage", "partner", "settings", "manage",
    "Edit partner's own operational and auth settings",
    ["partner:settings:view"]);
```

- [ ] **Step 2: Add partner permissions to AllPermissions() in RoleTemplateSeeder**

Add the 8 partner permission IDs to the `AllPermissions()` method:

```csharp
// Add after the platform permissions:
"partner:customer:view", "partner:customer:create",
"partner:customer:manage", "partner:customer:delete",
"partner:billing:view", "partner:billing:manage",
"partner:settings:view", "partner:settings:manage",
```

- [ ] **Step 3: Add 3 partner role templates to RoleTemplateSeeder**

Add to `GetTemplates()` after the Platform Admin template:

```csharp
// ── Partner Admin ──
yield return (
    new TemplateRow("partner_admin", "Partner Admin", "Full partner portal access for managing child customers and billing"),
    [
        "partner:customer:view", "partner:customer:create",
        "partner:customer:manage", "partner:customer:delete",
        "partner:billing:view", "partner:billing:manage",
        "partner:settings:view", "partner:settings:manage",
    ]);

// ── Partner Billing ──
yield return (
    new TemplateRow("partner_billing", "Partner Billing", "Partner billing and revenue access without customer management"),
    [
        "partner:customer:view",
        "partner:billing:view", "partner:billing:manage",
    ]);

// ── Partner Viewer ──
yield return (
    new TemplateRow("partner_viewer", "Partner Viewer", "Read-only access to partner portal"),
    [
        "partner:customer:view",
        "partner:billing:view",
        "partner:settings:view",
    ]);
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs \
        src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs
git commit -m "feat: seed 8 partner permissions and 3 partner role templates"
```

---

### Task 6: PartnerCustomerEndpoints (8 endpoints)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs`

This file contains 8 endpoints: 6 customer CRUD + 2 customer settings. All require `PartnerAdminOnly` policy + specific `partner:*` permission.

- [ ] **Step 1: Create PartnerCustomerEndpoints with DTOs and all 8 endpoints**

Read these files for patterns before implementing:
- `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs` — ownership validation, tenant CRUD pattern
- `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` — `BuildSettingsDto()` and `ApplyUpdates()` shared methods
- `src/Asterisk.Platform.Api/Endpoints/Shared/MessageResponse.cs` — response DTO pattern

```csharp
// src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs
using System.Security.Claims;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerCustomerEndpoints
{
    // ── DTOs ──

    internal sealed record PartnerCustomerDto(
        string TenantId,
        string Name,
        string Status,
        string Plan,
        DateTimeOffset CreatedAt);

    internal sealed record CreatePartnerCustomerRequest(
        string TenantId,
        string Name,
        string? Plan);

    internal sealed record UpdatePartnerCustomerRequest(
        string? Name,
        int? MaxConcurrentChannels,
        int? MaxActiveCampaigns);

    // ── Mapping ──

    public static RouteGroupBuilder MapPartnerCustomerEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner")
            .WithTags("Partner - Customers")
            .RequireAuthorization("PartnerAdminOnly");

        group.MapGet("/customers", ListCustomers)
            .RequireAuthorization("partner:customer:view");

        group.MapPost("/customers", CreateCustomer)
            .RequireAuthorization("partner:customer:create");

        group.MapGet("/customers/{customerId}", GetCustomer)
            .RequireAuthorization("partner:customer:view");

        group.MapPut("/customers/{customerId}", UpdateCustomer)
            .RequireAuthorization("partner:customer:manage");

        group.MapPost("/customers/{customerId}/suspend", SuspendCustomer)
            .RequireAuthorization("partner:customer:manage");

        group.MapPost("/customers/{customerId}/activate", ActivateCustomer)
            .RequireAuthorization("partner:customer:manage");

        group.MapGet("/customers/{customerId}/settings", GetCustomerSettings)
            .RequireAuthorization("partner:customer:manage");

        group.MapPut("/customers/{customerId}/settings", UpdateCustomerSettings)
            .RequireAuthorization("partner:customer:manage");

        return group;
    }

    // ── Handlers ──
    // Implement each handler following these patterns:
    //
    // 1. Extract callerTenantId from JWT: context.User.FindFirst("tid")?.Value
    // 2. For operations on a specific customer:
    //    var customer = await tenantStore.GetAsync(customerId, ct);
    //    if (customer is null || customer.ParentTenantId != callerTenantId)
    //        return Results.NotFound();
    // 3. For CreateCustomer:
    //    - Force Type = TenantType.Customer, ParentTenantId = callerTenantId
    //    - Validate plan hierarchy ceiling: customer plan <= partner plan
    //    - Partner must be Active to create children
    //    - Use tenantStore.UpsertAsync() to save
    // 4. For GetCustomerSettings/UpdateCustomerSettings:
    //    - Reuse TenantSettingsEndpoints.BuildSettingsDto() for GET
    //    - Reuse TenantSettingsEndpoints.ApplyUpdates() for PUT
    //    - Strip Quotas and RateLimitTier from Partner writes (only Platform can set these)
    //    - Enforce plan hierarchy ceiling on plan changes

    private static async Task<IResult> ListCustomers(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var children = await tenantStore.GetChildrenAsync(callerTenantId, ct);
        var filtered = children.Where(c => c.Type == TenantType.Customer);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<TenantStatus>(status, true, out var s))
            filtered = filtered.Where(c => c.Status == s);

        var result = filtered.Select(c => new PartnerCustomerDto(
            c.TenantId, c.Name, c.Status.ToString(), c.GetPlan().ToString(), c.CreatedAt)).ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateCustomer(
        HttpContext context,
        [FromBody] CreatePartnerCustomerRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] FeatureGateCache featureGateCache,
        [FromServices] TenantTierCache tierCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var partner = await tenantStore.GetAsync(callerTenantId, ct);
        if (partner is null || partner.Status != TenantStatus.Active)
            return Results.Problem("Partner must be Active to create customers.", statusCode: 400);

        // Validate tenant ID not taken
        var existing = await tenantStore.GetAsync(body.TenantId, ct);
        if (existing is not null)
            return Results.Problem("Tenant ID already exists.", statusCode: 409);

        // Plan hierarchy ceiling
        var partnerPlan = partner.GetPlan();
        var customerPlan = TenantPlan.Starter;
        if (body.Plan is not null && Enum.TryParse<TenantPlan>(body.Plan, true, out var requested))
        {
            if (requested > partnerPlan)
                return Results.Problem($"Customer plan cannot exceed partner plan ({partnerPlan}).", statusCode: 400);
            customerPlan = requested;
        }

        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string> { ["Plan"] = customerPlan.ToString() };
        var tier = PlanDefinition.GetDefaultTier(customerPlan);
        metadata["RateLimitTier"] = tier.ToString();

        var tenant = new Tenant
        {
            TenantId = body.TenantId,
            Name = body.Name,
            Type = TenantType.Customer,
            ParentTenantId = callerTenantId,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = metadata,
        };

        await tenantStore.UpsertAsync(tenant, ct);
        return Results.Created($"/api/v1/partner/customers/{tenant.TenantId}",
            new PartnerCustomerDto(tenant.TenantId, tenant.Name, tenant.Status.ToString(),
                customerPlan.ToString(), tenant.CreatedAt));
    }

    private static async Task<IResult> GetCustomer(
        HttpContext context,
        string customerId,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        return Results.Ok(new PartnerCustomerDto(
            customer.TenantId, customer.Name, customer.Status.ToString(),
            customer.GetPlan().ToString(), customer.CreatedAt));
    }

    private static async Task<IResult> UpdateCustomer(
        HttpContext context,
        string customerId,
        [FromBody] UpdatePartnerCustomerRequest body,
        [FromServices] ITenantStore tenantStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        // Apply updates: name, options
        var options = customer.Options;
        if (body.MaxConcurrentChannels.HasValue)
            options.MaxConcurrentChannels = body.MaxConcurrentChannels.Value;
        if (body.MaxActiveCampaigns.HasValue)
            options.MaxActiveCampaigns = body.MaxActiveCampaigns.Value;

        var updated = new Tenant
        {
            TenantId = customer.TenantId,
            Name = body.Name ?? customer.Name,
            Type = customer.Type,
            ParentTenantId = customer.ParentTenantId,
            Status = customer.Status,
            Options = options,
            Metadata = customer.Metadata,
            CreatedAt = customer.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await tenantStore.UpsertAsync(updated, ct);
        return Results.Ok(new MessageResponse("Customer updated"));
    }

    private static async Task<IResult> SuspendCustomer(
        HttpContext context,
        string customerId,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        if (customer.Status == TenantStatus.Suspended)
            return Results.Ok(new MessageResponse("Customer already suspended"));

        await tenantStore.UpdateStatusAsync(customerId, TenantStatus.Suspended, ct);
        tierCache.Remove(customerId);
        featureGateCache.Remove(customerId);
        return Results.Ok(new MessageResponse("Customer suspended"));
    }

    private static async Task<IResult> ActivateCustomer(
        HttpContext context,
        string customerId,
        [FromServices] ITenantStore tenantStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        if (customer.Status == TenantStatus.Active)
            return Results.Ok(new MessageResponse("Customer already active"));

        await tenantStore.UpdateStatusAsync(customerId, TenantStatus.Active, ct);
        tierCache.Remove(customerId);
        featureGateCache.Remove(customerId);
        return Results.Ok(new MessageResponse("Customer activated"));
    }

    private static async Task<IResult> GetCustomerSettings(
        HttpContext context,
        string customerId,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var tid = new TenantId(customerId);
        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            customer, tid, authConfigStore, quotaStore, retentionStore, addOnStore,
            dunningStore, featureGateService, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdateCustomerSettings(
        HttpContext context,
        string customerId,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        // Partner can write: Operational, Auth, Plan, AddOns
        // Partner CANNOT write: Quotas, RateLimitTier (strip them)
        var partnerRestricted = body with { Quotas = null, RateLimitTier = null };

        // Enforce plan hierarchy ceiling
        if (partnerRestricted.Plan is not null)
        {
            var partner = await tenantStore.GetAsync(callerTenantId, ct);
            var partnerPlan = partner?.GetPlan() ?? TenantPlan.Starter;
            if (Enum.TryParse<TenantPlan>(partnerRestricted.Plan, true, out var requestedPlan) && requestedPlan > partnerPlan)
                return Results.Problem($"Customer plan cannot exceed partner plan ({partnerPlan}).", statusCode: 400);
        }

        var tid = new TenantId(customerId);
        await TenantSettingsEndpoints.ApplyUpdates(
            partnerRestricted, customer, tid, tenantStore, authConfigStore,
            quotaStore, retentionStore, addOnStore, tierCache, featureGateCache, ct);

        return Results.Ok(new MessageResponse("Customer settings updated"));
    }
}
```

**Important implementation notes:**
- The `UpdateTenantSettingsRequest` type is already defined in `TenantSettingsEndpoints.cs`. Reuse it.
- `BuildSettingsDto()` and `ApplyUpdates()` are `internal static` methods on `TenantSettingsEndpoints`. Verify their exact signatures by reading the file — the parameter list may have changed since Sprint 2. Adapt the calls accordingly.
- If `BuildSettingsDto`/`ApplyUpdates` are not accessible (private), either make them `internal` or extract to a shared helper.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/PartnerCustomerEndpoints.cs
git commit -m "feat: add PartnerCustomerEndpoints with 8 customer management endpoints"
```

---

### Task 7: PartnerBillingEndpoints (7 endpoints)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/PartnerBillingEndpoints.cs`

Contains 7 endpoints: 4 rate card CRUD + customer invoices list + invoice generate + customer usage.

- [ ] **Step 1: Create PartnerBillingEndpoints with all 7 endpoints**

Read `ManagementBillingEndpoints.cs` for the exact DTO patterns (RateCardDto, InvoiceDto, UsageSummaryDto). Reuse them where possible.

```csharp
// src/Asterisk.Platform.Api/Endpoints/PartnerBillingEndpoints.cs
using System.Security.Claims;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerBillingEndpoints
{
    // ── DTOs (Partner-specific) ──

    internal sealed record GenerateInvoiceResponse(
        InvoiceDto Invoice,
        PartnerRevenueSnapshotDto Revenue);

    internal sealed record PartnerRevenueSnapshotDto(
        decimal GrossAmount,
        decimal PlatformCost,
        decimal PartnerMargin);

    // Reuse InvoiceDto, RateCardDto, UsageSummaryDto from ManagementBillingEndpoints
    // (if they are internal to that file, extract them to Shared/ or duplicate)

    // ── Mapping ──

    public static RouteGroupBuilder MapPartnerBillingEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner")
            .WithTags("Partner - Billing")
            .RequireAuthorization("PartnerAdminOnly");

        // Rate Cards (Partner's own)
        group.MapGet("/rate-cards", ListRateCards)
            .RequireAuthorization("partner:billing:manage");
        group.MapPost("/rate-cards", CreateRateCard)
            .RequireAuthorization("partner:billing:manage");
        group.MapPut("/rate-cards/{rateCardId}", UpdateRateCard)
            .RequireAuthorization("partner:billing:manage");
        group.MapDelete("/rate-cards/{rateCardId}", DeleteRateCard)
            .RequireAuthorization("partner:billing:manage");

        // Customer Invoices
        group.MapGet("/customers/{customerId}/invoices", ListCustomerInvoices)
            .RequireAuthorization("partner:billing:view");
        group.MapPost("/customers/{customerId}/invoices/generate", GenerateCustomerInvoice)
            .RequireAuthorization("partner:billing:manage");

        // Customer Usage
        group.MapGet("/customers/{customerId}/usage", GetCustomerUsage)
            .RequireAuthorization("partner:billing:view");

        return group;
    }

    // ── Handlers ──
    // Key patterns:
    //
    // Rate Cards: Partner's rate cards use partnerId as TenantId.
    //   var callerTenantId = context.User.FindFirst("tid")?.Value;
    //   var partnerTid = new TenantId(callerTenantId);
    //   await rateCardStore.ListAsync(partnerTid, ct);
    //
    // Customer operations: validate ownership first.
    //   var customer = await tenantStore.GetAsync(customerId, ct);
    //   if (customer?.ParentTenantId != callerTenantId) return Results.NotFound();
    //
    // Invoice generation with markup:
    //   1. Load Partner's active RateCard
    //   2. Generate invoice using GenerateWithRateCardAsync(customerTid, partnerRateCard, from, to, ct)
    //   3. Load Platform base RateCard (host tenant, IsDefault=true)
    //   4. Calculate platform cost using same usage but base rates
    //   5. Create PartnerRevenueRecord
    //   6. Return invoice + revenue snapshot

    private static async Task<IResult> ListRateCards(
        HttpContext context,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var cards = await rateCardStore.ListAsync(new TenantId(callerTenantId), ct);
        return Results.Ok(cards);
    }

    private static async Task<IResult> CreateRateCard(
        HttpContext context,
        [FromBody] RateCard body,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        // Force TenantId to caller's (Partner's) tenant
        var card = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId(callerTenantId),
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            Rates = body.Rates,
            IsDefault = body.IsDefault,
        };
        await rateCardStore.SaveAsync(card, ct);
        return Results.Created($"/api/v1/partner/rate-cards/{card.RateCardId.Value}", card);
    }

    private static async Task<IResult> UpdateRateCard(
        HttpContext context,
        string rateCardId,
        [FromBody] RateCard body,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var tid = new TenantId(callerTenantId);
        var existing = await rateCardStore.GetByIdAsync(tid, EntityId.From(rateCardId), ct);
        if (existing is null) return Results.NotFound();

        var updated = new RateCard
        {
            RateCardId = existing.RateCardId,
            TenantId = tid,
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            Rates = body.Rates,
            IsDefault = body.IsDefault,
        };
        await rateCardStore.SaveAsync(updated, ct);
        return Results.Ok(new MessageResponse("Rate card updated"));
    }

    private static async Task<IResult> DeleteRateCard(
        HttpContext context,
        string rateCardId,
        [FromServices] IRateCardStore rateCardStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var tid = new TenantId(callerTenantId);
        var existing = await rateCardStore.GetByIdAsync(tid, EntityId.From(rateCardId), ct);
        if (existing is null) return Results.NotFound();

        await rateCardStore.DeleteAsync(tid, EntityId.From(rateCardId), ct);
        return Results.Ok(new MessageResponse("Rate card deleted"));
    }

    private static async Task<IResult> ListCustomerInvoices(
        HttpContext context,
        string customerId,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IInvoiceStore invoiceStore,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var invoices = await invoiceStore.ListAsync(new TenantId(customerId), page, pageSize, ct);
        return Results.Ok(invoices);
    }

    private static async Task<IResult> GenerateCustomerInvoice(
        HttpContext context,
        string customerId,
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IRateCardStore rateCardStore,
        [FromServices] IInvoiceGenerationService invoiceService,
        [FromServices] IPartnerRevenueStore revenueStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var partnerTid = new TenantId(callerTenantId);
        var customerTid = new TenantId(customerId);

        // 1. Load Partner's active rate card
        var partnerRateCard = await rateCardStore.GetActiveAsync(partnerTid, DateTimeOffset.UtcNow, ct);
        if (partnerRateCard is null)
            return Results.Problem("No active rate card configured for this partner.", statusCode: 400);

        // 2. Generate invoice using Partner's rate card
        var invoice = await invoiceService.GenerateWithRateCardAsync(customerTid, partnerRateCard, from, to, ct);

        // 3. Load Platform base rate card (host tenant, IsDefault=true)
        var hostTenant = await tenantStore.GetHostTenantAsync(ct);
        if (hostTenant is null)
            return Results.Problem("Platform host tenant not found.", statusCode: 500);

        var baseRateCard = await rateCardStore.GetActiveAsync(new TenantId(hostTenant.TenantId), DateTimeOffset.UtcNow, ct);
        if (baseRateCard is null)
            return Results.Problem("No platform base rate card configured.", statusCode: 400);

        // 4. Calculate platform cost (same usage, base rates)
        var baseInvoice = await invoiceService.GenerateWithRateCardAsync(customerTid, baseRateCard, from, to, ct);
        var platformCost = baseInvoice.Total;

        // 5. Create PartnerRevenueRecord
        var revenue = new PartnerRevenueRecord
        {
            RevenueId = EntityId.New(),
            PartnerTenantId = partnerTid,
            CustomerTenantId = customerTid,
            InvoiceId = invoice.InvoiceId,
            GrossAmount = invoice.Total,
            PlatformCost = platformCost,
            PartnerMargin = invoice.Total - platformCost,
            PeriodStart = from,
            PeriodEnd = to,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await revenueStore.UpsertAsync(revenue, ct);

        // 6. Save the actual invoice (Partner-priced one)
        // Note: baseInvoice is NOT saved — it's only used for cost calculation

        return Results.Ok(new GenerateInvoiceResponse(
            new InvoiceDto(invoice),
            new PartnerRevenueSnapshotDto(revenue.GrossAmount, revenue.PlatformCost, revenue.PartnerMargin)));
    }

    private static async Task<IResult> GetCustomerUsage(
        HttpContext context,
        string customerId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IMeteringService meteringService,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var customer = await tenantStore.GetAsync(customerId, ct);
        if (customer is null || customer.ParentTenantId != callerTenantId)
            return Results.NotFound();

        var summaries = await meteringService.GetCurrentPeriodSummaryAsync(new TenantId(customerId), ct);
        return Results.Ok(summaries);
    }
}
```

**Implementation notes:**
- `InvoiceDto` may be defined in `ManagementBillingEndpoints`. If it's internal to that file, either make it accessible or define a local version. Check its constructor pattern (may take an `Invoice` and map properties).
- The `GenerateCustomerInvoice` handler generates TWO invoices internally (one at partner rates, one at base rates). Only the partner-priced one is saved. The base-rates one is discarded after calculating `PlatformCost`. This is intentional — it's simpler than extracting a separate cost calculator.
- The `baseInvoice` should NOT be saved to the invoice store. Verify that `GenerateWithRateCardAsync` does not auto-save — if it does, you'll need to either skip saving in the overload or delete the base invoice after calculation.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/PartnerBillingEndpoints.cs
git commit -m "feat: add PartnerBillingEndpoints with rate cards and invoice generation"
```

---

### Task 8: PartnerRevenueEndpoints + PartnerSettingsEndpoints (4 endpoints)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/PartnerRevenueEndpoints.cs`
- Create: `src/Asterisk.Platform.Api/Endpoints/PartnerSettingsEndpoints.cs`

- [ ] **Step 1: Create PartnerRevenueEndpoints (2 endpoints)**

```csharp
// src/Asterisk.Platform.Api/Endpoints/PartnerRevenueEndpoints.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerRevenueEndpoints
{
    internal sealed record PartnerRevenueSummaryDto(
        decimal TotalGross,
        decimal TotalPlatformCost,
        decimal TotalMargin,
        int CustomerCount,
        int InvoiceCount);

    internal sealed record PartnerRevenueDto(
        string RevenueId,
        string CustomerTenantId,
        string InvoiceId,
        decimal GrossAmount,
        decimal PlatformCost,
        decimal PartnerMargin,
        DateTimeOffset PeriodStart,
        DateTimeOffset PeriodEnd);

    public static RouteGroupBuilder MapPartnerRevenueEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner/revenue")
            .WithTags("Partner - Revenue")
            .RequireAuthorization("PartnerAdminOnly")
            .RequireAuthorization("partner:billing:view");

        group.MapGet("/", GetRevenueSummary);
        group.MapGet("/details", GetRevenueDetails);

        return group;
    }

    private static async Task<IResult> GetRevenueSummary(
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] IPartnerRevenueStore revenueStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var records = await revenueStore.ListAsync(new TenantId(callerTenantId), from, to, ct);

        var summary = new PartnerRevenueSummaryDto(
            TotalGross: records.Sum(r => r.GrossAmount),
            TotalPlatformCost: records.Sum(r => r.PlatformCost),
            TotalMargin: records.Sum(r => r.PartnerMargin),
            CustomerCount: records.Select(r => r.CustomerTenantId).Distinct().Count(),
            InvoiceCount: records.Count);

        return Results.Ok(summary);
    }

    private static async Task<IResult> GetRevenueDetails(
        HttpContext context,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] IPartnerRevenueStore revenueStore,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var records = await revenueStore.ListAsync(new TenantId(callerTenantId), from, to, ct);

        var result = records.Select(r => new PartnerRevenueDto(
            r.RevenueId.Value, r.CustomerTenantId.Value, r.InvoiceId.Value,
            r.GrossAmount, r.PlatformCost, r.PartnerMargin,
            r.PeriodStart, r.PeriodEnd)).ToList();

        return Results.Ok(result);
    }
}
```

- [ ] **Step 2: Create PartnerSettingsEndpoints (2 endpoints)**

```csharp
// src/Asterisk.Platform.Api/Endpoints/PartnerSettingsEndpoints.cs
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PartnerSettingsEndpoints
{
    public static RouteGroupBuilder MapPartnerSettingsEndpoints(this RouteGroupBuilder app)
    {
        var group = app.MapGroup("/partner/settings")
            .WithTags("Partner - Settings")
            .RequireAuthorization("PartnerAdminOnly");

        group.MapGet("/", GetPartnerSettings)
            .RequireAuthorization("partner:settings:view");

        group.MapPut("/", UpdatePartnerSettings)
            .RequireAuthorization("partner:settings:manage");

        return group;
    }

    private static async Task<IResult> GetPartnerSettings(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] IDunningStore dunningStore,
        [FromServices] IFeatureGateService featureGateService,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var tenant = await tenantStore.GetAsync(callerTenantId, ct);
        if (tenant is null) return Results.NotFound();

        var tid = new TenantId(callerTenantId);
        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            tenant, tid, authConfigStore, quotaStore, retentionStore, addOnStore,
            dunningStore, featureGateService, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> UpdatePartnerSettings(
        HttpContext context,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] ITenantAddOnStore addOnStore,
        [FromServices] TenantTierCache tierCache,
        [FromServices] FeatureGateCache featureGateCache,
        CancellationToken ct)
    {
        var callerTenantId = context.User.FindFirst("tid")?.Value;
        if (callerTenantId is null) return Results.Forbid();

        var tenant = await tenantStore.GetAsync(callerTenantId, ct);
        if (tenant is null) return Results.NotFound();

        // Partner can only write Operational + Auth (own settings)
        // Cannot change own Plan, Quotas, RateLimitTier, AddOns (Platform controls these)
        var restricted = body with { Plan = null, Quotas = null, RateLimitTier = null, AddOns = null };

        var tid = new TenantId(callerTenantId);
        await TenantSettingsEndpoints.ApplyUpdates(
            restricted, tenant, tid, tenantStore, authConfigStore,
            quotaStore, retentionStore, addOnStore, tierCache, featureGateCache, ct);

        return Results.Ok(new MessageResponse("Partner settings updated"));
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Success, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/PartnerRevenueEndpoints.cs \
        src/Asterisk.Platform.Api/Endpoints/PartnerSettingsEndpoints.cs
git commit -m "feat: add PartnerRevenueEndpoints and PartnerSettingsEndpoints"
```

---

### Task 9: ApiJsonContext + Program.cs Endpoint Wiring

**Files:**
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Register new DTOs in ApiJsonContext**

Add `[JsonSerializable]` attributes for all new Partner DTOs:

```csharp
// Partner Customer
[JsonSerializable(typeof(PartnerCustomerEndpoints.PartnerCustomerDto))]
[JsonSerializable(typeof(List<PartnerCustomerEndpoints.PartnerCustomerDto>))]
[JsonSerializable(typeof(PartnerCustomerEndpoints.CreatePartnerCustomerRequest))]
[JsonSerializable(typeof(PartnerCustomerEndpoints.UpdatePartnerCustomerRequest))]

// Partner Billing
[JsonSerializable(typeof(PartnerBillingEndpoints.GenerateInvoiceResponse))]
[JsonSerializable(typeof(PartnerBillingEndpoints.PartnerRevenueSnapshotDto))]

// Partner Revenue
[JsonSerializable(typeof(PartnerRevenueEndpoints.PartnerRevenueSummaryDto))]
[JsonSerializable(typeof(PartnerRevenueEndpoints.PartnerRevenueDto))]
[JsonSerializable(typeof(List<PartnerRevenueEndpoints.PartnerRevenueDto>))]
```

- [ ] **Step 2: Wire endpoint mappings in Program.cs**

Add to the endpoint mapping section (after `v1.MapManagementTenantSettingsEndpoints();`, around line 463):

```csharp
v1.MapPartnerCustomerEndpoints();
v1.MapPartnerBillingEndpoints();
v1.MapPartnerRevenueEndpoints();
v1.MapPartnerSettingsEndpoints();
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs \
        src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: register Partner DTOs in ApiJsonContext and wire endpoints in Program.cs"
```

---

### Task 10: Tests (~30 new tests)

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/PartnerAdminAuthorizationHandlerTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PartnerCustomerEndpointsTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PartnerBillingEndpointsTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PartnerRevenueEndpointsTests.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PartnerSettingsEndpointsTests.cs`
- Modify: `tests/Asterisk.Platform.Api.Tests/AuthenticatedPlatformApiFactory.cs` (add SeedPartnerTenant helper)

- [ ] **Step 1: Add SeedPartnerTenant helper to AuthenticatedPlatformApiFactory**

Add alongside `SeedEnterpriseFeatureGate`:

```csharp
internal static async Task<string> SeedPartnerTenant(IServiceProvider services, string partnerId, string parentTenantId)
{
    var tenantStore = services.GetRequiredService<ITenantStore>();
    var now = DateTimeOffset.UtcNow;
    var metadata = new Dictionary<string, string>
    {
        ["Plan"] = TenantPlan.Enterprise.ToString(),
        ["RateLimitTier"] = RateLimitTier.Enterprise.ToString(),
    };

    await tenantStore.UpsertAsync(new Tenant
    {
        TenantId = partnerId,
        Name = "Test Partner",
        Type = TenantType.Partner,
        ParentTenantId = parentTenantId,
        Status = TenantStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
        Metadata = metadata,
    }, CancellationToken.None);

    SeedEnterpriseFeatureGate(services, partnerId);
    return partnerId;
}
```

- [ ] **Step 2: Create PartnerAdminAuthorizationHandlerTests (4 tests)**

```csharp
// tests/Asterisk.Platform.Api.Tests/PartnerAdminAuthorizationHandlerTests.cs
using System.Security.Claims;
using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public class PartnerAdminAuthorizationHandlerTests
{
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly PermissionResolver _resolver = Substitute.For<PermissionResolver>();

    // Note: PermissionResolver may not be substitutable if not virtual/interface.
    // In that case, create a real PermissionResolver with mocked dependencies.
    // Check the constructor of PermissionResolver to determine the right approach.

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenCallerIsPartnerTenant()
    {
        // Arrange: tenant is Partner + Active
        _tenantStore.GetAsync("partner-1").Returns(new Tenant
        {
            TenantId = "partner-1", Name = "Partner", Type = TenantType.Partner,
            Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var handler = new PartnerAdminAuthorizationHandler(_tenantStore, _resolver);
        var requirement = new PartnerAdminRequirement();
        var user = CreateUser("partner-1", "user-1", "Admin");
        var authContext = new AuthorizationHandlerContext([requirement], user, null);

        // Act
        await handler.HandleAsync(authContext);

        // Assert
        authContext.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenCallerIsCustomerTenant()
    {
        _tenantStore.GetAsync("customer-1").Returns(new Tenant
        {
            TenantId = "customer-1", Name = "Customer", Type = TenantType.Customer,
            Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var handler = new PartnerAdminAuthorizationHandler(_tenantStore, _resolver);
        var requirement = new PartnerAdminRequirement();
        var user = CreateUser("customer-1", "user-1", "Admin");
        var authContext = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(authContext);

        authContext.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenCallerIsPlatformTenant()
    {
        _tenantStore.GetAsync("platform").Returns(new Tenant
        {
            TenantId = "platform", Name = "Platform", Type = TenantType.Platform,
            Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var handler = new PartnerAdminAuthorizationHandler(_tenantStore, _resolver);
        var requirement = new PartnerAdminRequirement();
        var user = CreateUser("platform", "user-1", "Admin");
        var authContext = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(authContext);

        authContext.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenPartnerIsSuspended()
    {
        _tenantStore.GetAsync("partner-1").Returns(new Tenant
        {
            TenantId = "partner-1", Name = "Partner", Type = TenantType.Partner,
            Status = TenantStatus.Suspended, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var handler = new PartnerAdminAuthorizationHandler(_tenantStore, _resolver);
        var requirement = new PartnerAdminRequirement();
        var user = CreateUser("partner-1", "user-1", "Admin");
        var authContext = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(authContext);

        authContext.HasSucceeded.Should().BeFalse();
    }

    private static ClaimsPrincipal CreateUser(string tenantId, string userId, string role)
    {
        var claims = new[]
        {
            new Claim("tid", tenantId),
            new Claim("sub", userId),
            new Claim("role", role),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
```

- [ ] **Step 3: Create PartnerCustomerEndpointsTests (8 tests)**

These are integration tests using a test factory. Follow the pattern from `ManagementTenantEndpointTests.cs`:
- Use a factory that seeds a Partner tenant with admin auth
- Test CRUD operations against `/api/v1/partner/customers`
- Test ownership validation (cannot access another partner's customers)
- Test hierarchy ceiling (cannot assign plan > partner plan)
- Test status changes (suspend/activate)

```csharp
// Test names:
// ListCustomers_ShouldReturnOnlyOwnChildren
// CreateCustomer_ShouldSucceed_WhenValidRequest
// CreateCustomer_ShouldReject_WhenPlanExceedsPartnerPlan
// GetCustomer_ShouldReturn404_WhenNotOwnChild
// UpdateCustomer_ShouldSucceed_WhenOwnChild
// SuspendCustomer_ShouldChangeStatus
// ActivateCustomer_ShouldRestoreActive
// GetCustomerSettings_ShouldReturnFacade
```

Create a `PartnerApiFactory` that:
1. Seeds a Platform tenant (host)
2. Seeds a Partner tenant under Platform
3. Seeds a Customer tenant under Partner
4. Creates an API key for the Partner tenant
5. Returns a client authenticated as the Partner admin

Follow the pattern from `PlatformAdminApiFactory` but for Partner context.

- [ ] **Step 4: Create PartnerBillingEndpointsTests (6 tests)**

```csharp
// Test names:
// ListRateCards_ShouldReturnPartnerCards
// CreateRateCard_ShouldSucceed
// UpdateRateCard_ShouldSucceed
// DeleteRateCard_ShouldSucceed
// GenerateCustomerInvoice_ShouldCreateRevenueRecord
// GetCustomerUsage_ShouldReturnSummary
```

The `GenerateCustomerInvoice` test needs to:
1. Seed usage records for the customer
2. Create a Partner rate card
3. Create a Platform base rate card (host tenant)
4. Call generate
5. Verify invoice was created AND PartnerRevenueRecord was created with correct margin

- [ ] **Step 5: Create PartnerRevenueEndpointsTests (4 tests)**

```csharp
// Test names:
// GetRevenueSummary_ShouldAggregateMargins
// GetRevenueSummary_ShouldReturnEmpty_WhenNoRecords
// GetRevenueDetails_ShouldReturnAllRecords
// GetRevenueDetails_ShouldFilterByDateRange
```

- [ ] **Step 6: Create PartnerSettingsEndpointsTests (4 tests)**

```csharp
// Test names:
// GetPartnerSettings_ShouldReturnOwnSettings
// UpdatePartnerSettings_ShouldUpdateOperational
// UpdatePartnerSettings_ShouldStripPlanAndQuotas
// UpdatePartnerSettings_ShouldReturn403_WhenNotPartner
```

- [ ] **Step 7: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass (1446 + ~30 new = ~1476), 0 warnings

- [ ] **Step 8: Commit**

```bash
git add tests/Asterisk.Platform.Api.Tests/PartnerAdminAuthorizationHandlerTests.cs \
        tests/Asterisk.Platform.Api.Tests/PartnerCustomerEndpointsTests.cs \
        tests/Asterisk.Platform.Api.Tests/PartnerBillingEndpointsTests.cs \
        tests/Asterisk.Platform.Api.Tests/PartnerRevenueEndpointsTests.cs \
        tests/Asterisk.Platform.Api.Tests/PartnerSettingsEndpointsTests.cs \
        tests/Asterisk.Platform.Api.Tests/AuthenticatedPlatformApiFactory.cs
git commit -m "test: add 30 partner portal tests with PartnerApiFactory helper"
```

---

## Verification

After all tasks complete:

```bash
# Full build + test
dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx

# Expected: ~1476 tests, 0 failures, 0 warnings

# Verify endpoint count
grep -r "MapPartner" src/Asterisk.Platform.Api/Program.cs
# Expected: 4 lines (Customer, Billing, Revenue, Settings)

# Verify permission count
grep -c "partner:" src/Asterisk.Platform.Storage.Postgres/Seeds/PermissionSeeder.cs
# Expected: 8

# Verify role template count
grep -c "partner_" src/Asterisk.Platform.Storage.Postgres/Seeds/RoleTemplateSeeder.cs
# Expected: 3 (partner_admin, partner_billing, partner_viewer)
```

## Summary

| Metric | Value |
|--------|-------|
| Tasks | 10 |
| New files | 16 |
| Modified files | 8 |
| New endpoints | 19 |
| New permissions | 8 |
| New role templates | 3 |
| New tests | ~30 |
| Expected total tests | ~1476 |
