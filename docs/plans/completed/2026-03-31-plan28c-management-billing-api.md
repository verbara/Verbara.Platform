# Management Billing API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 12 management endpoints for rate card CRUD, invoice management, usage queries, and quota management — all PlatformAdminOnly.

**Architecture:** Single endpoint file `ManagementBillingEndpoints.cs` with 3 route groups (`/api/management/rate-cards`, `/api/management/invoices`, `/api/management/tenants/{tenantId}`). Extends `IUsageRecordStore` with `ListAsync` for paginated usage record queries. Tests use `PlatformAdminApiFactory` with store seeding via DI container.

**Tech Stack:** .NET 10, ASP.NET Minimal API, xUnit, FluentAssertions, Dapper/Npgsql

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project C, lines 289-322)

---

## File Structure

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `src/Asterisk.Platform.Billing/IUsageRecordStore.cs` | Add `ListAsync` method |
| Modify | `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs` | Implement `ListAsync` |
| Modify | `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs` | Implement `ListAsync` |
| Create | `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` | 12 endpoints + DTOs + mapping |
| Modify | `src/Asterisk.Platform.Api/Program.cs` | Wire `MapManagementBillingEndpoints()` |
| Modify | `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs` | Add `ListAsync` tests |
| Create | `tests/Asterisk.Platform.Api.Tests/ManagementBillingEndpointTests.cs` | 18 endpoint integration tests |
| Modify | `CLAUDE.md` | Update test counts, add Plan 28C section |

## Endpoint Inventory

| # | Method | Route | Handler | Store/Service |
|---|--------|-------|---------|---------------|
| 1 | GET | `/api/management/rate-cards?tenantId=` | ListRateCards | IRateCardStore.ListAsync |
| 2 | POST | `/api/management/rate-cards?tenantId=` | CreateRateCard | IRateCardStore.SaveAsync |
| 3 | PUT | `/api/management/rate-cards/{id}?tenantId=` | UpdateRateCard | IRateCardStore.GetByIdAsync + SaveAsync |
| 4 | DELETE | `/api/management/rate-cards/{id}?tenantId=` | DeleteRateCard | IRateCardStore.DeleteAsync |
| 5 | GET | `/api/management/invoices?tenantId=&page=&pageSize=` | ListInvoices | IInvoiceStore.ListAsync |
| 6 | POST | `/api/management/invoices/generate?tenantId=` | GenerateInvoice | IInvoiceGenerationService + IInvoiceStore.SaveAsync |
| 7 | GET | `/api/management/invoices/{id}?tenantId=` | GetInvoice | IInvoiceStore.GetByIdAsync |
| 8 | POST | `/api/management/invoices/{id}/issue?tenantId=` | IssueInvoice | IInvoiceStore.UpdateStatusAsync |
| 9 | GET | `/api/management/tenants/{tenantId}/usage?from=&until=` | GetUsageSummary | IUsageRecordStore.GetSummaryAsync |
| 10 | GET | `/api/management/tenants/{tenantId}/usage/details?from=&until=&type=&page=&pageSize=` | GetUsageDetails | IUsageRecordStore.ListAsync |
| 11 | GET | `/api/management/tenants/{tenantId}/quota` | GetQuotaStatus | IQuotaEnforcementService.GetQuotaStatusAsync |
| 12 | PUT | `/api/management/tenants/{tenantId}/quota` | UpdateQuota | ITenantQuotaStore.UpsertAsync |

---

## Task 1: Extend IUsageRecordStore with ListAsync

**Files:**
- Modify: `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs`
- Modify: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs`

**Context:** The Management API needs a `GET /usage/details` endpoint that returns paginated individual `UsageRecord` objects (not summaries). The current `IUsageRecordStore` only has `GetSummaryAsync` and `GetSummaryByTypeAsync`. We need a `ListAsync` method with date range, optional type filter, and pagination.

- [ ] **Step 1: Add ListAsync to IUsageRecordStore**

In `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`, add this method to the interface:

```csharp
/// <summary>Returns paginated individual usage records for a tenant within a date range, optionally filtered by type.</summary>
Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct);
```

The complete interface after the change:

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for usage records and aggregated summaries.
/// </summary>
public interface IUsageRecordStore
{
    /// <summary>Persists a single usage record.</summary>
    Task SaveAsync(UsageRecord record, CancellationToken ct);

    /// <summary>Persists a batch of usage records.</summary>
    Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);

    /// <summary>Returns aggregated summaries for a tenant within a date range, grouped by UsageType.</summary>
    Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Returns the aggregated summary for a specific usage type within a date range.</summary>
    Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Returns paginated individual usage records for a tenant within a date range, optionally filtered by type.</summary>
    Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct);
}
```

- [ ] **Step 2: Implement ListAsync in InMemoryUsageRecordStore**

In `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs`, add this method:

```csharp
public Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct)
{
    var filtered = GetTenantRecords(tenantId)
        .Where(r => r.RecordedAt >= from && r.RecordedAt < until);

    if (type is not null)
        filtered = filtered.Where(r => r.UsageType == type.Value);

    IReadOnlyList<UsageRecord> result = filtered
        .OrderByDescending(r => r.RecordedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToList();

    return Task.FromResult(result);
}
```

- [ ] **Step 3: Implement ListAsync in PostgresUsageRecordStore**

In `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs`, add this method and a new `RecordRow` type:

After the existing `SummaryRow` record at the bottom of the class, add:

```csharp
private sealed record RecordRow(
    string record_id, string tenant_id, short usage_type, decimal quantity, short unit,
    string? channel, string? reference_id, DateTimeOffset recorded_at, string? metadata);
```

Add the implementation method:

```csharp
public async Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);

    var sql = "SELECT record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata " +
              "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @Until";

    if (type is not null)
        sql += " AND usage_type = @UsageType";

    sql += " ORDER BY recorded_at DESC LIMIT @Limit OFFSET @Offset";

    var rows = await conn.QueryAsync<RecordRow>(sql, new
    {
        TenantId = tenantId.Value,
        From = from,
        Until = until,
        UsageType = type is not null ? (short)type.Value : (short)0,
        Limit = pageSize,
        Offset = (page - 1) * pageSize,
    });

    return rows.Select(r => new UsageRecord
    {
        RecordId = EntityId.From(r.record_id),
        TenantId = new TenantId(r.tenant_id),
        UsageType = (UsageType)r.usage_type,
        Quantity = r.quantity,
        Unit = (UsageUnit)r.unit,
        Channel = r.channel,
        ReferenceId = r.reference_id,
        RecordedAt = r.recorded_at,
        Metadata = r.metadata != null
            ? JsonSerializer.Deserialize(r.metadata, PostgresJson.Ctx.DictionaryStringString)
            : null,
    }).ToList();
}
```

- [ ] **Step 4: Write tests for InMemoryUsageRecordStore.ListAsync**

In `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs`, add these tests at the end of the class:

```csharp
[Fact]
public async Task ListAsync_ShouldReturnPaginatedRecords()
{
    var store = new InMemoryUsageRecordStore();
    for (int i = 0; i < 5; i++)
        await store.SaveAsync(MakeRecord(quantity: i + 1, recordedAt: BaseTime.AddHours(i)), CancellationToken.None);

    var page1 = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 3, CancellationToken.None);
    var page2 = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 2, 3, CancellationToken.None);

    page1.Should().HaveCount(3);
    page2.Should().HaveCount(2);
    // Ordered by RecordedAt DESC, so first page has most recent
    page1[0].Quantity.Should().Be(5m);
    page1[2].Quantity.Should().Be(3m);
}

[Fact]
public async Task ListAsync_ShouldFilterByType()
{
    var store = new InMemoryUsageRecordStore();
    await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 10m), CancellationToken.None);
    await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, quantity: 3m, unit: UsageUnit.Segments), CancellationToken.None);
    await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 5m, recordedAt: BaseTime.AddHours(1)), CancellationToken.None);

    var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), UsageType.VoiceInbound, 1, 50, CancellationToken.None);

    result.Should().HaveCount(2);
    result.Should().OnlyContain(r => r.UsageType == UsageType.VoiceInbound);
}

[Fact]
public async Task ListAsync_ShouldFilterByDateRange()
{
    var store = new InMemoryUsageRecordStore();
    await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(-1)), CancellationToken.None);
    await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(5), quantity: 8m), CancellationToken.None);
    await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddMonths(2)), CancellationToken.None);

    var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 50, CancellationToken.None);

    result.Should().HaveCount(1);
    result[0].Quantity.Should().Be(8m);
}

[Fact]
public async Task ListAsync_ShouldIsolateTenants()
{
    var store = new InMemoryUsageRecordStore();
    await store.SaveAsync(MakeRecord(tenantId: Tenant1, quantity: 10m), CancellationToken.None);
    await store.SaveAsync(MakeRecord(tenantId: Tenant2, quantity: 20m), CancellationToken.None);

    var result = await store.ListAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), null, 1, 50, CancellationToken.None);

    result.Should().HaveCount(1);
    result[0].Quantity.Should().Be(10m);
}
```

- [ ] **Step 5: Verify build and tests pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryUsageRecordStore" -v q`

Expected: 12 tests pass (8 existing + 4 new), 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Billing/IUsageRecordStore.cs \
        src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs \
        src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs \
        tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs
git commit -m "feat(billing): add ListAsync to IUsageRecordStore for paginated record queries"
```

---

## Task 2: ManagementBillingEndpoints — Complete Endpoint File

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs`

**Context:** This single file implements all 12 management billing endpoints with PlatformAdminOnly auth. It follows the exact pattern from `ManagementTenantEndpoints.cs`: static methods returning `Task<IResult>`, `[FromQuery]`/`[FromBody]`/`[FromServices]` attributes, sealed record DTOs at the bottom. The endpoints call into billing stores/services that already exist from Plans 28A and 28B.

**Key conventions:**
- `TenantId` is created via `new TenantId(string)` (public constructor)
- `EntityId` is created via `EntityId.From(string)` (private constructor, static factory)
- Enums in DTOs use string representation (e.g., `"VoiceInbound"`)
- Enum parsing via `Enum.Parse<T>(string)` — platform admin only so input is trusted

- [ ] **Step 1: Create ManagementBillingEndpoints.cs**

Create `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` with this complete content:

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementBillingEndpoints
{
    public static void MapManagementBillingEndpoints(this IEndpointRouteBuilder app)
    {
        // Rate Cards
        var rc = app.MapGroup("/api/management/rate-cards").RequireAuthorization("PlatformAdminOnly");
        rc.MapGet("/", ListRateCards);
        rc.MapPost("/", CreateRateCard);
        rc.MapPut("/{id}", UpdateRateCard);
        rc.MapDelete("/{id}", DeleteRateCard);

        // Invoices
        var inv = app.MapGroup("/api/management/invoices").RequireAuthorization("PlatformAdminOnly");
        inv.MapGet("/", ListInvoices);
        inv.MapPost("/generate", GenerateInvoice);
        inv.MapGet("/{id}", GetInvoice);
        inv.MapPost("/{id}/issue", IssueInvoice);

        // Usage & Quotas (per-tenant)
        var tb = app.MapGroup("/api/management/tenants/{tenantId}").RequireAuthorization("PlatformAdminOnly");
        tb.MapGet("/usage", GetUsageSummary);
        tb.MapGet("/usage/details", GetUsageDetails);
        tb.MapGet("/quota", GetQuotaStatus);
        tb.MapPut("/quota", UpdateQuota);
    }

    // ─── Rate Card Handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> ListRateCards(
        [FromQuery] string tenantId,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var cards = await store.ListAsync(new TenantId(tenantId), ct);
        return Results.Ok(cards.Select(MapRateCardToDto).ToList());
    }

    private static async Task<IResult> CreateRateCard(
        [FromQuery] string tenantId,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var rateCard = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId(tenantId),
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            IsDefault = body.IsDefault,
            Rates = body.Rates.Select(MapDtoToRateEntry).ToList(),
        };

        await store.SaveAsync(rateCard, ct);
        return Results.Created($"/api/management/rate-cards/{rateCard.RateCardId.Value}", MapRateCardToDto(rateCard));
    }

    private static async Task<IResult> UpdateRateCard(
        string id,
        [FromQuery] string tenantId,
        [FromBody] CreateRateCardRequest body,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var existing = await store.GetByIdAsync(tid, EntityId.From(id), ct);
        if (existing is null)
            return Results.NotFound();

        var updated = new RateCard
        {
            RateCardId = existing.RateCardId,
            TenantId = existing.TenantId,
            Name = body.Name,
            Currency = body.Currency,
            EffectiveFrom = body.EffectiveFrom,
            EffectiveTo = body.EffectiveTo,
            IsDefault = body.IsDefault,
            Rates = body.Rates.Select(MapDtoToRateEntry).ToList(),
        };

        await store.SaveAsync(updated, ct);
        return Results.Ok(MapRateCardToDto(updated));
    }

    private static async Task<IResult> DeleteRateCard(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IRateCardStore store,
        CancellationToken ct)
    {
        await store.DeleteAsync(new TenantId(tenantId), EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── Invoice Handlers ────────────────────────────────────────────────────────

    private static async Task<IResult> ListInvoices(
        [FromQuery] string tenantId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoices = await store.ListAsync(new TenantId(tenantId), page ?? 1, pageSize ?? 20, ct);
        return Results.Ok(invoices.Select(MapInvoiceToDto).ToList());
    }

    private static async Task<IResult> GenerateInvoice(
        [FromQuery] string tenantId,
        [FromBody] GenerateInvoiceRequest body,
        [FromServices] IInvoiceGenerationService generator,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        Invoice invoice;
        try
        {
            invoice = await generator.GenerateAsync(new TenantId(tenantId), body.PeriodStart, body.PeriodEnd, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        await store.SaveAsync(invoice, ct);
        return Results.Created($"/api/management/invoices/{invoice.InvoiceId.Value}", MapInvoiceToDto(invoice));
    }

    private static async Task<IResult> GetInvoice(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoice = await store.GetByIdAsync(new TenantId(tenantId), EntityId.From(id), ct);
        return invoice is null ? Results.NotFound() : Results.Ok(MapInvoiceToDto(invoice));
    }

    private static async Task<IResult> IssueInvoice(
        string id,
        [FromQuery] string tenantId,
        [FromServices] IInvoiceStore store,
        CancellationToken ct)
    {
        var invoice = await store.GetByIdAsync(new TenantId(tenantId), EntityId.From(id), ct);
        if (invoice is null)
            return Results.NotFound();

        await store.UpdateStatusAsync(new TenantId(tenantId), EntityId.From(id), InvoiceStatus.Issued, ct);
        return Results.Ok(new { invoiceId = id, status = "Issued" });
    }

    // ─── Usage Handlers ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetUsageSummary(
        string tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromServices] IUsageRecordStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var effectiveFrom = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var effectiveUntil = until ?? now;

        var summaries = await store.GetSummaryAsync(new TenantId(tenantId), effectiveFrom, effectiveUntil, ct);
        return Results.Ok(summaries.Select(MapSummaryToDto).ToList());
    }

    private static async Task<IResult> GetUsageDetails(
        string tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] string? type,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] IUsageRecordStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var effectiveFrom = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var effectiveUntil = until ?? now;

        UsageType? typeFilter = !string.IsNullOrEmpty(type) ? Enum.Parse<UsageType>(type) : null;

        var records = await store.ListAsync(new TenantId(tenantId), effectiveFrom, effectiveUntil, typeFilter, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(records.Select(MapRecordToDto).ToList());
    }

    // ─── Quota Handlers ──────────────────────────────────────────────────────────

    private static async Task<IResult> GetQuotaStatus(
        string tenantId,
        [FromServices] IQuotaEnforcementService service,
        CancellationToken ct)
    {
        var status = await service.GetQuotaStatusAsync(new TenantId(tenantId), ct);

        return Results.Ok(new QuotaStatusDto(
            status.TenantId.Value,
            status.Quota is not null ? MapQuotaToDto(status.Quota) : null,
            status.CurrentUsage.Select(MapSummaryToDto).ToList()));
    }

    private static async Task<IResult> UpdateQuota(
        string tenantId,
        [FromBody] UpdateQuotaRequest body,
        [FromServices] ITenantQuotaStore store,
        CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var existing = await store.GetAsync(tid, ct);

        var quota = new TenantQuota
        {
            TenantId = tid,
            MaxConcurrentChannels = body.MaxConcurrentChannels ?? existing?.MaxConcurrentChannels ?? 100,
            MaxActiveCampaigns = body.MaxActiveCampaigns ?? existing?.MaxActiveCampaigns ?? 10,
            MaxMonthlyVoiceMinutes = body.MaxMonthlyVoiceMinutes ?? existing?.MaxMonthlyVoiceMinutes,
            MaxMonthlyMessages = body.MaxMonthlyMessages ?? existing?.MaxMonthlyMessages,
            MaxStorageBytes = body.MaxStorageBytes ?? existing?.MaxStorageBytes,
            MaxActiveAgents = body.MaxActiveAgents ?? existing?.MaxActiveAgents,
            QuotaAction = body.QuotaAction is not null
                ? Enum.Parse<QuotaAction>(body.QuotaAction)
                : existing?.QuotaAction ?? QuotaAction.Warn,
        };

        await store.UpsertAsync(quota, ct);
        return Results.Ok(MapQuotaToDto(quota));
    }

    // ─── Mapping Helpers ─────────────────────────────────────────────────────────

    private static RateCardDto MapRateCardToDto(RateCard rc) => new(
        rc.RateCardId.Value, rc.TenantId.Value, rc.Name, rc.Currency,
        rc.EffectiveFrom, rc.EffectiveTo, rc.IsDefault,
        rc.Rates.Select(MapRateEntryToDto).ToList());

    private static RateEntryDto MapRateEntryToDto(RateEntry re) => new(
        re.UsageType.ToString(), re.UnitPrice, re.IncludedQuantity,
        re.Tiers?.Select(t => new RateTierDto(t.FromQuantity, t.ToQuantity, t.UnitPrice)).ToList());

    private static RateEntry MapDtoToRateEntry(RateEntryDto dto) => new()
    {
        UsageType = Enum.Parse<UsageType>(dto.UsageType),
        UnitPrice = dto.UnitPrice,
        IncludedQuantity = dto.IncludedQuantity,
        Tiers = dto.Tiers?.Select(t => new RateTier
        {
            FromQuantity = t.FromQuantity,
            ToQuantity = t.ToQuantity,
            UnitPrice = t.UnitPrice,
        }).ToList(),
    };

    private static InvoiceDto MapInvoiceToDto(Invoice inv) => new(
        inv.InvoiceId.Value, inv.TenantId.Value, inv.PeriodStart, inv.PeriodEnd,
        inv.Currency, inv.LineItems.Select(MapLineItemToDto).ToList(),
        inv.Subtotal, inv.Tax, inv.Total, inv.Status.ToString(),
        inv.GeneratedAt, inv.IssuedAt, inv.PaidAt);

    private static InvoiceLineItemDto MapLineItemToDto(InvoiceLineItem li) => new(
        li.UsageType.ToString(), li.Description, li.Quantity, li.UnitPrice,
        li.Amount, li.IncludedQuantity, li.OverageQuantity);

    private static UsageSummaryDto MapSummaryToDto(UsageSummary s) => new(
        s.UsageType.ToString(), s.TotalQuantity, s.RecordCount,
        s.PeriodStart, s.PeriodEnd, s.LastUpdatedAt);

    private static UsageRecordDto MapRecordToDto(UsageRecord r) => new(
        r.RecordId.Value, r.UsageType.ToString(), r.Quantity, r.Unit.ToString(),
        r.Channel, r.ReferenceId, r.RecordedAt);

    private static QuotaDto MapQuotaToDto(TenantQuota q) => new(
        q.MaxConcurrentChannels, q.MaxActiveCampaigns,
        q.MaxMonthlyVoiceMinutes, q.MaxMonthlyMessages,
        q.MaxStorageBytes, q.MaxActiveAgents, q.QuotaAction.ToString());
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

// Rate Cards
internal sealed record RateCardDto(
    string RateCardId, string TenantId, string Name, string Currency,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo,
    bool IsDefault, IReadOnlyList<RateEntryDto> Rates);

internal sealed record RateEntryDto(
    string UsageType, decimal UnitPrice, decimal IncludedQuantity,
    IReadOnlyList<RateTierDto>? Tiers);

internal sealed record RateTierDto(decimal FromQuantity, decimal? ToQuantity, decimal UnitPrice);

internal sealed record CreateRateCardRequest(
    string Name, string Currency, DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo, bool IsDefault, IReadOnlyList<RateEntryDto> Rates);

// Invoices
internal sealed record InvoiceDto(
    string InvoiceId, string TenantId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    string Currency, IReadOnlyList<InvoiceLineItemDto> LineItems,
    decimal Subtotal, decimal Tax, decimal Total,
    string Status, DateTimeOffset GeneratedAt, DateTimeOffset? IssuedAt, DateTimeOffset? PaidAt);

internal sealed record InvoiceLineItemDto(
    string UsageType, string Description, decimal Quantity, decimal UnitPrice,
    decimal Amount, decimal IncludedQuantity, decimal OverageQuantity);

internal sealed record GenerateInvoiceRequest(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd);

// Usage
internal sealed record UsageSummaryDto(
    string UsageType, decimal TotalQuantity, int RecordCount,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, DateTimeOffset LastUpdatedAt);

internal sealed record UsageRecordDto(
    string RecordId, string UsageType, decimal Quantity, string Unit,
    string? Channel, string? ReferenceId, DateTimeOffset RecordedAt);

// Quotas
internal sealed record QuotaStatusDto(
    string TenantId, QuotaDto? Quota, IReadOnlyList<UsageSummaryDto> CurrentUsage);

internal sealed record QuotaDto(
    int MaxConcurrentChannels, int MaxActiveCampaigns,
    long? MaxMonthlyVoiceMinutes, long? MaxMonthlyMessages,
    long? MaxStorageBytes, int? MaxActiveAgents, string QuotaAction);

internal sealed record UpdateQuotaRequest(
    int? MaxConcurrentChannels = null, int? MaxActiveCampaigns = null,
    long? MaxMonthlyVoiceMinutes = null, long? MaxMonthlyMessages = null,
    long? MaxStorageBytes = null, int? MaxActiveAgents = null, string? QuotaAction = null);
```

- [ ] **Step 2: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs
git commit -m "feat(billing): add ManagementBillingEndpoints with 12 management endpoints"
```

---

## Task 3: Wire Endpoints in Program.cs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs:316`

**Context:** Add the new endpoint mapping call after the existing management endpoint mappings. The line `app.MapUsersMeEndpoint();` is currently the last endpoint mapping at line 316. Add the new mapping before or after the existing management group.

- [ ] **Step 1: Add MapManagementBillingEndpoints to Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, find the line:

```csharp
app.MapUsersMeEndpoint();
```

Add after it:

```csharp
app.MapManagementBillingEndpoints();
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Asterisk.Platform.slnx`

Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Program.cs
git commit -m "feat(billing): wire ManagementBillingEndpoints in Program.cs"
```

---

## Task 4: Management Billing Endpoint Tests

**Files:**
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementBillingEndpointTests.cs`

**Context:** Integration tests using `PlatformAdminApiFactory` (which provides a platform admin client with management API key). The factory already seeds a host tenant with id `"platform"`. Tests create child tenants via the management API, then seed billing data via DI container stores. The test class implements `IClassFixture<PlatformAdminApiFactory>`.

**Key patterns from existing tests:**
- `IClassFixture<PlatformAdminApiFactory>` for shared factory
- `factory.CreatePlatformAdminClient()` for authenticated client
- `factory.Services.GetRequiredService<T>()` for store access
- `PostAsJsonAsync` for POST/PUT with JSON body
- `ReadAsStringAsync()` + `Should().Contain()` for response assertions
- `PlatformApiFactory` for unauthenticated client (auth denial tests)
- Constants: `PlatformAdminApiFactory.HostTenantId` = `"platform"`

- [ ] **Step 1: Create ManagementBillingEndpointTests.cs**

Create `tests/Asterisk.Platform.Api.Tests/ManagementBillingEndpointTests.cs` with this complete content:

```csharp
using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementBillingEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;
    private readonly PlatformAdminApiFactory _factory;
    private const string TestTenantId = "billing-test-tenant";

    public ManagementBillingEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();

        // Ensure test tenant exists
        _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = TestTenantId,
            name = "Billing Test Tenant",
            type = 2, // Customer
        }).GetAwaiter().GetResult();
    }

    // ─── Rate Card Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRateCards_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/rate-cards?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateRateCard_ShouldReturnCreated()
    {
        var response = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Standard Plan",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = true,
            rates = new[]
            {
                new
                {
                    usageType = "VoiceInbound",
                    unitPrice = 0.015m,
                    includedQuantity = 100m,
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Standard Plan");
        body.Should().Contain("VoiceInbound");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldReturnOk()
    {
        // Create first
        var createResponse = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Update Test",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.01m, includedQuantity = 0m } },
        });
        var created = await createResponse.Content.ReadAsStringAsync();
        var rateCardId = System.Text.Json.JsonDocument.Parse(created).RootElement.GetProperty("rateCardId").GetString();

        // Update
        var response = await _client.PutAsJsonAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}", new
        {
            name = "Updated Plan",
            currency = "EUR",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = new[] { new { usageType = "SmsOutbound", unitPrice = 0.02m, includedQuantity = 50m } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Updated Plan");
        body.Should().Contain("EUR");
    }

    [Fact]
    public async Task UpdateRateCard_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.PutAsJsonAsync($"/api/management/rate-cards/nonexistent?tenantId={TestTenantId}", new
        {
            name = "Ghost",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = Array.Empty<object>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteRateCard_ShouldReturnNoContent()
    {
        var createResponse = await _client.PostAsJsonAsync($"/api/management/rate-cards?tenantId={TestTenantId}", new
        {
            name = "Delete Me",
            currency = "USD",
            effectiveFrom = DateTimeOffset.UtcNow,
            isDefault = false,
            rates = Array.Empty<object>(),
        });
        var created = await createResponse.Content.ReadAsStringAsync();
        var rateCardId = System.Text.Json.JsonDocument.Parse(created).RootElement.GetProperty("rateCardId").GetString();

        var response = await _client.DeleteAsync($"/api/management/rate-cards/{rateCardId}?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Invoice Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListInvoices_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/invoices?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GenerateInvoice_ShouldReturnCreated()
    {
        // Seed a rate card first
        var tid = new TenantId(TestTenantId);
        var rateCardStore = _factory.Services.GetRequiredService<IRateCardStore>();
        await rateCardStore.SaveAsync(new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = tid,
            Name = "Invoice Gen Test",
            Currency = "USD",
            EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsDefault = true,
            Rates = [new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.01m, IncludedQuantity = 0m }],
        }, CancellationToken.None);

        // Seed usage
        var usageStore = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.VoiceInbound,
            Quantity = 100m,
            Unit = UsageUnit.Minutes,
            RecordedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        var response = await _client.PostAsJsonAsync($"/api/management/invoices/generate?tenantId={TestTenantId}", new
        {
            periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            periodEnd = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VoiceInbound");
    }

    [Fact]
    public async Task GenerateInvoice_ShouldReturnBadRequest_WhenNoRateCard()
    {
        var response = await _client.PostAsJsonAsync("/api/management/invoices/generate?tenantId=no-rate-card-tenant", new
        {
            periodStart = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            periodEnd = new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("No active rate card");
    }

    [Fact]
    public async Task GetInvoice_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.GetAsync($"/api/management/invoices/nonexistent?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueInvoice_ShouldReturnOk()
    {
        // Create a draft invoice via store
        var tid = new TenantId(TestTenantId);
        var invoiceId = EntityId.New();
        var invoiceStore = _factory.Services.GetRequiredService<IInvoiceStore>();
        await invoiceStore.SaveAsync(new Invoice
        {
            InvoiceId = invoiceId,
            TenantId = tid,
            PeriodStart = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Tax = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var response = await _client.PostAsync($"/api/management/invoices/{invoiceId.Value}/issue?tenantId={TestTenantId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Issued");
    }

    [Fact]
    public async Task IssueInvoice_ShouldReturnNotFound_WhenMissing()
    {
        var response = await _client.PostAsync($"/api/management/invoices/nonexistent/issue?tenantId={TestTenantId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Usage Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsageSummary_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/tenants/{TestTenantId}/usage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsageDetails_ShouldReturnOk()
    {
        var response = await _client.GetAsync(
            $"/api/management/tenants/{TestTenantId}/usage/details?from=2026-01-01T00:00:00Z&until=2026-12-31T23:59:59Z");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsageDetails_ShouldFilterByType()
    {
        // Seed some data
        var tid = new TenantId("usage-filter-test");
        var usageStore = _factory.Services.GetRequiredService<IUsageRecordStore>();
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.VoiceInbound,
            Quantity = 10m,
            Unit = UsageUnit.Minutes,
            RecordedAt = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        await usageStore.SaveAsync(new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tid,
            UsageType = UsageType.SmsOutbound,
            Quantity = 5m,
            Unit = UsageUnit.Segments,
            RecordedAt = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        // Ensure tenant exists
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "usage-filter-test",
            name = "Usage Filter Test",
            type = 2,
        });

        var response = await _client.GetAsync(
            "/api/management/tenants/usage-filter-test/usage/details?from=2026-06-01T00:00:00Z&until=2026-07-01T00:00:00Z&type=VoiceInbound");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VoiceInbound");
        body.Should().NotContain("SmsOutbound");
    }

    // ─── Quota Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuotaStatus_ShouldReturnOk()
    {
        var response = await _client.GetAsync($"/api/management/tenants/{TestTenantId}/quota");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(TestTenantId);
    }

    [Fact]
    public async Task UpdateQuota_ShouldReturnOk()
    {
        var response = await _client.PutAsJsonAsync($"/api/management/tenants/{TestTenantId}/quota", new
        {
            maxConcurrentChannels = 200,
            maxActiveCampaigns = 20,
            maxMonthlyVoiceMinutes = 10000L,
            quotaAction = "SoftBlock",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("200");
        body.Should().Contain("SoftBlock");
    }

    // ─── Auth Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_ShouldRequirePlatformAdmin()
    {
        using var factory = new PlatformApiFactory();
        var anonClient = factory.CreateClient();

        var response = await anonClient.GetAsync($"/api/management/rate-cards?tenantId={TestTenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListInvoices_ShouldSupportPagination()
    {
        var response = await _client.GetAsync($"/api/management/invoices?tenantId={TestTenantId}&page=1&pageSize=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Verify all tests pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ManagementBillingEndpoint" -v q`

Expected: 18 tests pass, 0 failures.

- [ ] **Step 3: Run full solution tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`

Expected: All tests pass (1,140 existing + 4 store tests + 18 endpoint tests = 1,162 total), 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add tests/Asterisk.Platform.Api.Tests/ManagementBillingEndpointTests.cs
git commit -m "test(billing): add 18 integration tests for management billing endpoints"
```

---

## Task 5: Update CLAUDE.md and Documentation

**Files:**
- Modify: `CLAUDE.md`

**Context:** Update the project documentation to reflect the new endpoints and test counts.

- [ ] **Step 1: Update CLAUDE.md**

In `CLAUDE.md`, make these changes:

1. **Update test count in header:** Change `1140 tests` to `1162 tests` (or actual count after Task 4)

2. **Update Platform.Billing row:** Change test count from `40` to `40` (no new billing package tests, the 4 new tests are in Storage.InMemory.Tests and the 18 in Api.Tests)

3. **Update Storage.InMemory row:** Change test count from `82` to `86`

4. **Update Platform.Api row:** Change test count from `283` to `301`

5. **Update Endpoint Inventory:** Add `ManagementBillingEndpoints` to the Management category:
```
| Management | ManagementTenantEndpoints, ManagementSystemEndpoints, ManagementClusterEndpoints, ManagementApiKeyEndpoints, ManagementBillingEndpoints, SetupEndpoints |
```

6. **Update endpoint group count:** Change `41 endpoint groups, 41 files` to `42 endpoint groups, 42 files` and `42` in the Endpoint Inventory heading.

7. **Add Plan 28C section** after the Plan 28B section:

```markdown
## Plan 28C: Management Billing API -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project C)
**Plan:** `docs/superpowers/plans/2026-03-31-plan28c-management-billing-api.md`

Management API for billing administration (PlatformAdminOnly):
1. **Rate Card CRUD** -- List, Create, Update, Delete rate cards per tenant
2. **Invoice Management** -- List, Generate, Get, Issue invoices per tenant
3. **Usage Queries** -- Summary and detailed usage records with date range and type filters
4. **Quota Management** -- View quota status with current usage, update tenant quotas
5. **Store Extension** -- Added ListAsync to IUsageRecordStore (InMemory + Postgres) for paginated record queries
```

8. **Mark Sub-project C as COMPLETE** in the v1.2.0 section:
```markdown
- **Sub-project C: Management API + Usage Dashboard** -- COMPLETE (Plan 28C)
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with Plan 28C completion and updated counts"
```

---

## Notes

### Out of Scope (for separate plans)
- **Frontend pages** (`/admin/tenants/{id}/usage`, `/admin/billing/rate-cards`, `/admin/billing/invoices`) — these are in the `Asterisk.Platform.Web` repo
- **Analytics filtering** (adding `?tenantId=` to existing CDR/intervals/dashboard endpoints) — touches Pro SDK-dependent endpoints, separate enhancement
- **E2E tests** — Sub-project D, separate plan

### FCM Batching Strategy
- **Phase A (Foundation):** Task 1 — store extension with ListAsync
- **Phase B (Endpoints):** Tasks 2 + 3 — endpoint file + wiring (sequential)
- **Phase C (Tests):** Task 4 — integration tests
- **Phase D (Docs):** Task 5 — CLAUDE.md update
