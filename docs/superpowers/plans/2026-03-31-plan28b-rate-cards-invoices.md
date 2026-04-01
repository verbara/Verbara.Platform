# Plan 28B: Rate Cards + Invoice Generation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add rate card pricing configuration and invoice generation to Asterisk.Platform.Billing, enabling per-tenant billing based on metered usage.

**Architecture:** Extends the existing Platform.Billing package (from Plan 28A) with 3 new domain models (RateCard, Invoice, InvoiceLineItem), 2 new store interfaces, 1 invoice generation service with flat-rate + tiered pricing calculations. InMemory and Postgres store implementations follow established patterns. RateCard.Rates and Invoice.LineItems stored as JSONB in Postgres.

**Tech Stack:** .NET 10, Dapper, Npgsql, xUnit, FluentAssertions, NSubstitute, AOT-safe JSON (source-generated JsonSerializerContext)

**Depends on:** Plan 28A complete (UsageRecord, UsageSummary, IUsageRecordStore, TenantQuota all exist in Platform.Billing)

---

## File Map

### New Files (11)

| File | Responsibility |
|------|---------------|
| `src/Asterisk.Platform.Billing/RateCard.cs` | RateCard, RateEntry, RateTier domain models |
| `src/Asterisk.Platform.Billing/Invoice.cs` | Invoice, InvoiceLineItem, InvoiceStatus domain models |
| `src/Asterisk.Platform.Billing/IRateCardStore.cs` | Persistence interface for rate cards |
| `src/Asterisk.Platform.Billing/IInvoiceStore.cs` | Persistence interface for invoices |
| `src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs` | Service interface for invoice generation |
| `src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs` | Invoice generation: flat-rate + tiered pricing logic |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryRateCardStore.cs` | ConcurrentDictionary-backed rate card store |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs` | ConcurrentDictionary-backed invoice store |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/003_RateCardsInvoices.sql` | DDL for rate_cards and invoices tables |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresRateCardStore.cs` | Dapper/Npgsql rate card store with JSONB rates |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresInvoiceStore.cs` | Dapper/Npgsql invoice store with JSONB line_items |

### Modified Files (4)

| File | Change |
|------|--------|
| `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs` | Register IInvoiceGenerationService → DefaultInvoiceGenerationService |
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register IRateCardStore + IInvoiceStore |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Register IRateCardStore + IInvoiceStore |
| `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs` | Add JsonSerializable attributes for RateEntry, RateTier, InvoiceLineItem lists |

### Test Files (5)

| File | Tests |
|------|-------|
| `tests/Asterisk.Platform.Billing.Tests/RateCardTests.cs` | Model property tests, tiered rate card construction |
| `tests/Asterisk.Platform.Billing.Tests/InvoiceTests.cs` | Model property tests, status enum, line item construction |
| `tests/Asterisk.Platform.Billing.Tests/DefaultInvoiceGenerationServiceTests.cs` | Flat-rate, tiered, included quantity, no rate card, empty usage |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryRateCardStoreTests.cs` | CRUD operations, active rate card lookup, multi-tenant isolation |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryInvoiceStoreTests.cs` | Save/get/list/update status, pagination, multi-tenant isolation |

---

## Tasks

### Task 1: RateCard Domain Models

**Files:**
- Create: `src/Asterisk.Platform.Billing/RateCard.cs`
- Test: `tests/Asterisk.Platform.Billing.Tests/RateCardTests.cs`

- [ ] **Step 1: Create RateCard.cs with RateCard, RateEntry, RateTier**

```csharp
// src/Asterisk.Platform.Billing/RateCard.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Pricing configuration for a tenant — maps usage types to unit prices.
/// </summary>
public sealed class RateCard : ITenantScoped
{
    public required EntityId RateCardId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public required IReadOnlyList<RateEntry> Rates { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// A single pricing line within a rate card — one per usage type.
/// </summary>
public sealed class RateEntry
{
    public required UsageType UsageType { get; init; }
    public required decimal UnitPrice { get; init; }
    public decimal IncludedQuantity { get; init; }
    public IReadOnlyList<RateTier>? Tiers { get; init; }
}

/// <summary>
/// Volume-based pricing tier — applies when usage exceeds FromQuantity.
/// </summary>
public sealed class RateTier
{
    public required decimal FromQuantity { get; init; }
    public decimal? ToQuantity { get; init; }
    public required decimal UnitPrice { get; init; }
}
```

- [ ] **Step 2: Write RateCard model tests**

```csharp
// tests/Asterisk.Platform.Billing.Tests/RateCardTests.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class RateCardTests
{
    [Fact]
    public void RateCard_ShouldExposeAllProperties_WhenConstructed()
    {
        var id = EntityId.New();
        var tenantId = new TenantId("t1");
        var effectiveFrom = DateTimeOffset.UtcNow;
        var rates = new List<RateEntry>
        {
            new() { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
        };

        var card = new RateCard
        {
            RateCardId = id,
            TenantId = tenantId,
            Name = "Standard",
            Currency = "USD",
            EffectiveFrom = effectiveFrom,
            Rates = rates,
            IsDefault = true,
        };

        card.RateCardId.Should().Be(id);
        card.TenantId.Should().Be(tenantId);
        card.Name.Should().Be("Standard");
        card.Currency.Should().Be("USD");
        card.EffectiveFrom.Should().Be(effectiveFrom);
        card.EffectiveTo.Should().BeNull();
        card.Rates.Should().HaveCount(1);
        card.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void RateCard_ShouldImplementITenantScoped()
    {
#pragma warning disable CA1859
        ITenantScoped scoped = new RateCard
        {
            RateCardId = EntityId.New(),
            TenantId = new TenantId("t1"),
            Name = "Test",
            Currency = "USD",
            EffectiveFrom = DateTimeOffset.UtcNow,
            Rates = [],
        };
#pragma warning restore CA1859

        scoped.TenantId.Should().Be(new TenantId("t1"));
    }

    [Fact]
    public void RateEntry_ShouldSupportTieredPricing()
    {
        var entry = new RateEntry
        {
            UsageType = UsageType.VoiceInbound,
            UnitPrice = 0.10m,
            IncludedQuantity = 100m,
            Tiers = new List<RateTier>
            {
                new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                new() { FromQuantity = 100m, ToQuantity = 500m, UnitPrice = 0.08m },
                new() { FromQuantity = 500m, ToQuantity = null, UnitPrice = 0.05m },
            },
        };

        entry.Tiers.Should().HaveCount(3);
        entry.Tiers![2].ToQuantity.Should().BeNull();
    }

    [Fact]
    public void RateEntry_ShouldDefaultIncludedQuantityToZero()
    {
        var entry = new RateEntry
        {
            UsageType = UsageType.SmsOutbound,
            UnitPrice = 0.02m,
        };

        entry.IncludedQuantity.Should().Be(0m);
        entry.Tiers.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "RateCardTests" -v q`
Expected: 4 passed

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Billing/RateCard.cs tests/Asterisk.Platform.Billing.Tests/RateCardTests.cs
git commit -m "feat(billing): add RateCard, RateEntry, RateTier domain models"
```

---

### Task 2: Invoice Domain Models

**Files:**
- Create: `src/Asterisk.Platform.Billing/Invoice.cs`
- Test: `tests/Asterisk.Platform.Billing.Tests/InvoiceTests.cs`

- [ ] **Step 1: Create Invoice.cs with Invoice, InvoiceLineItem, InvoiceStatus**

```csharp
// src/Asterisk.Platform.Billing/Invoice.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Generated billing document for a tenant's usage within a period.
/// </summary>
public sealed class Invoice : ITenantScoped
{
    public required EntityId InvoiceId { get; init; }
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<InvoiceLineItem> LineItems { get; init; }
    public required decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public required DateTimeOffset GeneratedAt { get; init; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>
/// Lifecycle status of an invoice.
/// </summary>
public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    Void,
}

/// <summary>
/// A single line within an invoice — one per usage type with pricing breakdown.
/// </summary>
public sealed class InvoiceLineItem
{
    public required UsageType UsageType { get; init; }
    public required string Description { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal Amount { get; init; }
    public decimal IncludedQuantity { get; init; }
    public decimal OverageQuantity { get; init; }
}
```

- [ ] **Step 2: Write Invoice model tests**

```csharp
// tests/Asterisk.Platform.Billing.Tests/InvoiceTests.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class InvoiceTests
{
    [Fact]
    public void Invoice_ShouldExposeAllProperties_WhenConstructed()
    {
        var id = EntityId.New();
        var tenantId = new TenantId("t1");
        var now = DateTimeOffset.UtcNow;
        var lineItems = new List<InvoiceLineItem>
        {
            new()
            {
                UsageType = UsageType.VoiceInbound,
                Description = "Voice Inbound",
                Quantity = 100m,
                UnitPrice = 0.05m,
                Amount = 5.00m,
            },
        };

        var invoice = new Invoice
        {
            InvoiceId = id,
            TenantId = tenantId,
            PeriodStart = now.AddDays(-30),
            PeriodEnd = now,
            Currency = "USD",
            LineItems = lineItems,
            Subtotal = 5.00m,
            Tax = 0.50m,
            Total = 5.50m,
            GeneratedAt = now,
        };

        invoice.InvoiceId.Should().Be(id);
        invoice.TenantId.Should().Be(tenantId);
        invoice.Currency.Should().Be("USD");
        invoice.LineItems.Should().HaveCount(1);
        invoice.Subtotal.Should().Be(5.00m);
        invoice.Tax.Should().Be(0.50m);
        invoice.Total.Should().Be(5.50m);
        invoice.Status.Should().Be(InvoiceStatus.Draft);
        invoice.IssuedAt.Should().BeNull();
        invoice.PaidAt.Should().BeNull();
    }

    [Fact]
    public void Invoice_ShouldImplementITenantScoped()
    {
#pragma warning disable CA1859
        ITenantScoped scoped = new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = new TenantId("t1"),
            PeriodStart = DateTimeOffset.UtcNow,
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
#pragma warning restore CA1859

        scoped.TenantId.Should().Be(new TenantId("t1"));
    }

    [Fact]
    public void InvoiceStatus_ShouldHaveFourValues()
    {
        Enum.GetValues<InvoiceStatus>().Should().HaveCount(4);
        ((int)InvoiceStatus.Draft).Should().Be(0);
        ((int)InvoiceStatus.Issued).Should().Be(1);
        ((int)InvoiceStatus.Paid).Should().Be(2);
        ((int)InvoiceStatus.Void).Should().Be(3);
    }

    [Fact]
    public void InvoiceLineItem_ShouldDefaultIncludedAndOverageToZero()
    {
        var item = new InvoiceLineItem
        {
            UsageType = UsageType.SmsOutbound,
            Description = "SMS Outbound",
            Quantity = 50m,
            UnitPrice = 0.02m,
            Amount = 1.00m,
        };

        item.IncludedQuantity.Should().Be(0m);
        item.OverageQuantity.Should().Be(0m);
    }

    [Fact]
    public void Invoice_ShouldAllowStatusTransitions()
    {
        var invoice = new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = new TenantId("t1"),
            PeriodStart = DateTimeOffset.UtcNow,
            PeriodEnd = DateTimeOffset.UtcNow,
            Currency = "USD",
            LineItems = [],
            Subtotal = 0m,
            Total = 0m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        invoice.Status = InvoiceStatus.Issued;
        invoice.IssuedAt = DateTimeOffset.UtcNow;
        invoice.Status.Should().Be(InvoiceStatus.Issued);
        invoice.IssuedAt.Should().NotBeNull();

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = DateTimeOffset.UtcNow;
        invoice.Status.Should().Be(InvoiceStatus.Paid);
        invoice.PaidAt.Should().NotBeNull();
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "InvoiceTests" -v q`
Expected: 5 passed

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Billing/Invoice.cs tests/Asterisk.Platform.Billing.Tests/InvoiceTests.cs
git commit -m "feat(billing): add Invoice, InvoiceLineItem, InvoiceStatus domain models"
```

---

### Task 3: Store Interfaces

**Files:**
- Create: `src/Asterisk.Platform.Billing/IRateCardStore.cs`
- Create: `src/Asterisk.Platform.Billing/IInvoiceStore.cs`

- [ ] **Step 1: Create IRateCardStore.cs**

```csharp
// src/Asterisk.Platform.Billing/IRateCardStore.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for rate card configurations.
/// </summary>
public interface IRateCardStore
{
    /// <summary>Creates or updates a rate card.</summary>
    Task SaveAsync(RateCard rateCard, CancellationToken ct);

    /// <summary>Returns a rate card by its ID within a tenant.</summary>
    Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);

    /// <summary>Returns the active rate card for a tenant at a given point in time.</summary>
    Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct);

    /// <summary>Lists all rate cards for a tenant.</summary>
    Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Deletes a rate card by its ID within a tenant.</summary>
    Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct);
}
```

- [ ] **Step 2: Create IInvoiceStore.cs**

```csharp
// src/Asterisk.Platform.Billing/IInvoiceStore.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for generated invoices.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>Persists a new invoice.</summary>
    Task SaveAsync(Invoice invoice, CancellationToken ct);

    /// <summary>Returns an invoice by its ID within a tenant.</summary>
    Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct);

    /// <summary>Lists invoices for a tenant with pagination.</summary>
    Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct);

    /// <summary>Updates the status and optional timestamps on an existing invoice.</summary>
    Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct);
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/Asterisk.Platform.Billing/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Billing/IRateCardStore.cs src/Asterisk.Platform.Billing/IInvoiceStore.cs
git commit -m "feat(billing): add IRateCardStore and IInvoiceStore interfaces"
```

---

### Task 4: IInvoiceGenerationService Interface

**Files:**
- Create: `src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs`

- [ ] **Step 1: Create IInvoiceGenerationService.cs**

```csharp
// src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Generates invoices by combining usage summaries with rate card pricing.
/// </summary>
public interface IInvoiceGenerationService
{
    /// <summary>
    /// Generates an invoice for a tenant's usage within the specified period.
    /// Applies the active rate card to calculate line items and totals.
    /// </summary>
    Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Asterisk.Platform.Billing/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Billing/IInvoiceGenerationService.cs
git commit -m "feat(billing): add IInvoiceGenerationService interface"
```

---

### Task 5: DefaultInvoiceGenerationService

**Files:**
- Create: `src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs`
- Test: `tests/Asterisk.Platform.Billing.Tests/DefaultInvoiceGenerationServiceTests.cs`

- [ ] **Step 1: Write tests for invoice generation**

```csharp
// tests/Asterisk.Platform.Billing.Tests/DefaultInvoiceGenerationServiceTests.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class DefaultInvoiceGenerationServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset PeriodStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    private static (DefaultInvoiceGenerationService Service, IRateCardStore RateCardStore, IUsageRecordStore UsageStore, IClock Clock) Build()
    {
        var rateCardStore = Substitute.For<IRateCardStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultInvoiceGenerationService(rateCardStore, usageStore, clock);
        return (service, rateCardStore, usageStore, clock);
    }

    private static RateCard MakeFlatRateCard(params RateEntry[] rates) => new()
    {
        RateCardId = EntityId.New(),
        TenantId = Tenant1,
        Name = "Standard",
        Currency = "USD",
        EffectiveFrom = PeriodStart.AddMonths(-1),
        Rates = rates.ToList(),
        IsDefault = true,
    };

    [Fact]
    public async Task GenerateAsync_ShouldThrowInvalidOperation_WhenNoActiveRateCard()
    {
        var (service, rateCardStore, _, _) = Build();
        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((RateCard?)null);

        var act = () => service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rate card*");
    }

    [Fact]
    public async Task GenerateAsync_ShouldReturnEmptyInvoice_WhenNoUsage()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>());

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.TenantId.Should().Be(Tenant1);
        invoice.Currency.Should().Be("USD");
        invoice.LineItems.Should().BeEmpty();
        invoice.Subtotal.Should().Be(0m);
        invoice.Total.Should().Be(0m);
        invoice.Status.Should().Be(InvoiceStatus.Draft);
    }

    [Fact]
    public async Task GenerateAsync_ShouldCalculateFlatRate_WhenNoTiers()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound,
                    TotalQuantity = 200m,
                    RecordCount = 50,
                    LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.LineItems.Should().HaveCount(1);
        var line = invoice.LineItems[0];
        line.UsageType.Should().Be(UsageType.VoiceInbound);
        line.Quantity.Should().Be(200m);
        line.UnitPrice.Should().Be(0.05m);
        line.Amount.Should().Be(10.00m); // 200 × 0.05
        line.IncludedQuantity.Should().Be(0m);
        line.OverageQuantity.Should().Be(200m);
        invoice.Subtotal.Should().Be(10.00m);
        invoice.Total.Should().Be(10.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldSubtractIncludedQuantity_WhenConfigured()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m, IncludedQuantity = 100m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound,
                    TotalQuantity = 250m,
                    RecordCount = 250,
                    LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems[0];
        line.Quantity.Should().Be(250m);
        line.IncludedQuantity.Should().Be(100m);
        line.OverageQuantity.Should().Be(150m);
        line.Amount.Should().Be(3.00m); // (250 - 100) × 0.02
    }

    [Fact]
    public async Task GenerateAsync_ShouldReturnZeroAmount_WhenUsageBelowIncluded()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m, IncludedQuantity = 500m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound,
                    TotalQuantity = 80m,
                    RecordCount = 80,
                    LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        var line = invoice.LineItems[0];
        line.OverageQuantity.Should().Be(0m);
        line.Amount.Should().Be(0m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyTieredPricing_WhenTiersConfigured()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry
            {
                UsageType = UsageType.VoiceInbound,
                UnitPrice = 0.10m, // ignored when tiers exist
                Tiers = new List<RateTier>
                {
                    new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                    new() { FromQuantity = 100m, ToQuantity = 500m, UnitPrice = 0.08m },
                    new() { FromQuantity = 500m, ToQuantity = null, UnitPrice = 0.05m },
                },
            });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1,
                    PeriodStart = PeriodStart,
                    PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound,
                    TotalQuantity = 350m,
                    RecordCount = 100,
                    LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        // Tier 1: min(100, 350) - 0 = 100 × $0.10 = $10.00
        // Tier 2: min(500, 350) - 100 = 250 × $0.08 = $20.00
        // Tier 3: skipped (350 < 500)
        // Total = $30.00
        var line = invoice.LineItems[0];
        line.Quantity.Should().Be(350m);
        line.Amount.Should().Be(30.00m);
        invoice.Subtotal.Should().Be(30.00m);
        invoice.Total.Should().Be(30.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldHandleMultipleUsageTypes()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
            new RateEntry { UsageType = UsageType.SmsOutbound, UnitPrice = 0.02m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 100m,
                    RecordCount = 25, LastUpdatedAt = FixedNow,
                },
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 50m,
                    RecordCount = 50, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        invoice.LineItems.Should().HaveCount(2);
        invoice.Subtotal.Should().Be(6.00m); // (100 × 0.05) + (50 × 0.02)
        invoice.Total.Should().Be(6.00m);
    }

    [Fact]
    public async Task GenerateAsync_ShouldSkipUsageTypes_WhenNotInRateCard()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        // Rate card only covers VoiceInbound
        var rateCard = MakeFlatRateCard(
            new RateEntry { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        // Usage includes VoiceInbound + SmsOutbound (not in rate card)
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 100m,
                    RecordCount = 25, LastUpdatedAt = FixedNow,
                },
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.SmsOutbound, TotalQuantity = 50m,
                    RecordCount = 50, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        // Only VoiceInbound should appear — SmsOutbound has no rate
        invoice.LineItems.Should().HaveCount(1);
        invoice.LineItems[0].UsageType.Should().Be(UsageType.VoiceInbound);
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyTieredPricing_WithUnboundedLastTier()
    {
        var (service, rateCardStore, usageStore, _) = Build();
        var rateCard = MakeFlatRateCard(
            new RateEntry
            {
                UsageType = UsageType.VoiceInbound,
                UnitPrice = 0m,
                Tiers = new List<RateTier>
                {
                    new() { FromQuantity = 0m, ToQuantity = 100m, UnitPrice = 0.10m },
                    new() { FromQuantity = 100m, ToQuantity = null, UnitPrice = 0.05m },
                },
            });

        rateCardStore.GetActiveAsync(Tenant1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(rateCard);
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new List<UsageSummary>
            {
                new()
                {
                    TenantId = Tenant1, PeriodStart = PeriodStart, PeriodEnd = PeriodEnd,
                    UsageType = UsageType.VoiceInbound, TotalQuantity = 600m,
                    RecordCount = 200, LastUpdatedAt = FixedNow,
                },
            });

        var invoice = await service.GenerateAsync(Tenant1, PeriodStart, PeriodEnd, CancellationToken.None);

        // Tier 1: 100 × $0.10 = $10.00
        // Tier 2: 500 × $0.05 = $25.00
        // Total = $35.00
        invoice.LineItems[0].Amount.Should().Be(35.00m);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultInvoiceGenerationServiceTests" -v q`
Expected: FAIL — `DefaultInvoiceGenerationService` does not exist

- [ ] **Step 3: Implement DefaultInvoiceGenerationService**

```csharp
// src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Generates invoices by applying rate card pricing to usage summaries.
/// Supports flat-rate pricing (with optional included quantities) and tiered pricing.
/// </summary>
public sealed class DefaultInvoiceGenerationService : IInvoiceGenerationService
{
    private readonly IRateCardStore _rateCardStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;

    public DefaultInvoiceGenerationService(IRateCardStore rateCardStore, IUsageRecordStore usageStore, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(rateCardStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        _rateCardStore = rateCardStore;
        _usageStore = usageStore;
        _clock = clock;
    }

    public async Task<Invoice> GenerateAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var rateCard = await _rateCardStore.GetActiveAsync(tenantId, periodStart, ct)
            ?? throw new InvalidOperationException($"No active rate card found for tenant '{tenantId.Value}'.");

        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);
        var summaryByType = summaries.ToDictionary(s => s.UsageType);

        var lineItems = new List<InvoiceLineItem>();

        foreach (var rate in rateCard.Rates)
        {
            if (!summaryByType.TryGetValue(rate.UsageType, out var summary))
                continue;

            var lineItem = rate.Tiers is { Count: > 0 }
                ? CalculateTieredLineItem(rate, summary)
                : CalculateFlatLineItem(rate, summary);

            lineItems.Add(lineItem);
        }

        var subtotal = lineItems.Sum(li => li.Amount);

        return new Invoice
        {
            InvoiceId = EntityId.New(),
            TenantId = tenantId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Currency = rateCard.Currency,
            LineItems = lineItems,
            Subtotal = subtotal,
            Tax = 0m,
            Total = subtotal,
            GeneratedAt = _clock.UtcNow,
        };
    }

    private static InvoiceLineItem CalculateFlatLineItem(RateEntry rate, UsageSummary summary)
    {
        var overage = Math.Max(0m, summary.TotalQuantity - rate.IncludedQuantity);
        var amount = overage * rate.UnitPrice;

        return new InvoiceLineItem
        {
            UsageType = rate.UsageType,
            Description = rate.UsageType.ToString(),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.UnitPrice,
            Amount = amount,
            IncludedQuantity = rate.IncludedQuantity,
            OverageQuantity = overage,
        };
    }

    private static InvoiceLineItem CalculateTieredLineItem(RateEntry rate, UsageSummary summary)
    {
        var remaining = summary.TotalQuantity;
        var totalAmount = 0m;

        foreach (var tier in rate.Tiers!)
        {
            if (remaining <= 0m)
                break;

            var tierCeiling = tier.ToQuantity ?? decimal.MaxValue;
            var tierWidth = tierCeiling - tier.FromQuantity;
            var quantityInTier = Math.Min(remaining, tierWidth);

            totalAmount += quantityInTier * tier.UnitPrice;
            remaining -= quantityInTier;
        }

        return new InvoiceLineItem
        {
            UsageType = rate.UsageType,
            Description = rate.UsageType.ToString(),
            Quantity = summary.TotalQuantity,
            UnitPrice = rate.Tiers![0].UnitPrice,
            Amount = totalAmount,
            IncludedQuantity = 0m,
            OverageQuantity = summary.TotalQuantity,
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultInvoiceGenerationServiceTests" -v q`
Expected: 9 passed

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Billing/DefaultInvoiceGenerationService.cs tests/Asterisk.Platform.Billing.Tests/DefaultInvoiceGenerationServiceTests.cs
git commit -m "feat(billing): add DefaultInvoiceGenerationService with flat-rate and tiered pricing"
```

---

### Task 6: InMemoryRateCardStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryRateCardStore.cs`
- Test: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryRateCardStoreTests.cs`

- [ ] **Step 1: Write InMemoryRateCardStore tests**

```csharp
// tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryRateCardStoreTests.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryRateCardStoreTests
{
    private readonly InMemoryRateCardStore _store = new();
    private static readonly TenantId Tenant1 = new("t1");
    private static readonly TenantId Tenant2 = new("t2");

    private static RateCard MakeRateCard(TenantId tenantId, DateTimeOffset effectiveFrom, bool isDefault = false, DateTimeOffset? effectiveTo = null)
        => new()
        {
            RateCardId = EntityId.New(),
            TenantId = tenantId,
            Name = "Test Card",
            Currency = "USD",
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Rates = new List<RateEntry>
            {
                new() { UsageType = UsageType.VoiceInbound, UnitPrice = 0.05m },
            },
            IsDefault = isDefault,
        };

    [Fact]
    public async Task SaveAsync_ShouldPersist_AndGetByIdAsync_ShouldRetrieve()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(card.RateCardId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldNotCrossTenants()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant2, card.RateCardId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyTenantCards()
    {
        await _store.SaveAsync(MakeRateCard(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeRateCard(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeRateCard(Tenant2, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, CancellationToken.None);
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveCard()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        await _store.DeleteAsync(Tenant1, card.RateCardId, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnCardEffectiveAtDate()
    {
        var asOf = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(card.RateCardId);
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnNull_WhenNoActiveCard()
    {
        var asOf = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldExcludeExpiredCards()
    {
        var asOf = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var card = MakeRateCard(Tenant1,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            effectiveTo: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(card, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAsync_ShouldReturnMostRecentActive_WhenMultipleExist()
    {
        var asOf = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero);
        var older = MakeRateCard(Tenant1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = MakeRateCard(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(older, CancellationToken.None);
        await _store.SaveAsync(newer, CancellationToken.None);

        var result = await _store.GetActiveAsync(Tenant1, asOf, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RateCardId.Should().Be(newer.RateCardId);
    }

    [Fact]
    public async Task SaveAsync_ShouldOverwriteExistingCard()
    {
        var card = MakeRateCard(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(card, CancellationToken.None);

        var updated = new RateCard
        {
            RateCardId = card.RateCardId,
            TenantId = Tenant1,
            Name = "Updated",
            Currency = "EUR",
            EffectiveFrom = card.EffectiveFrom,
            Rates = card.Rates,
        };
        await _store.SaveAsync(updated, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, card.RateCardId, CancellationToken.None);
        result!.Name.Should().Be("Updated");
        result.Currency.Should().Be("EUR");

        var list = await _store.ListAsync(Tenant1, CancellationToken.None);
        list.Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Implement InMemoryRateCardStore**

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryRateCardStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryRateCardStore : IRateCardStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), RateCard> _cards = new();

    public Task SaveAsync(RateCard rateCard, CancellationToken ct)
    {
        _cards[(rateCard.TenantId, rateCard.RateCardId)] = rateCard;
        return Task.CompletedTask;
    }

    public Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        _cards.TryGetValue((tenantId, rateCardId), out var card);
        return Task.FromResult(card);
    }

    public Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct)
    {
        var active = _cards.Values
            .Where(c => c.TenantId == tenantId
                && c.EffectiveFrom <= asOf
                && (c.EffectiveTo == null || c.EffectiveTo > asOf))
            .OrderByDescending(c => c.EffectiveFrom)
            .FirstOrDefault();

        return Task.FromResult(active);
    }

    public Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<RateCard> result = _cards.Values
            .Where(c => c.TenantId == tenantId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        _cards.TryRemove((tenantId, rateCardId), out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryRateCardStoreTests" -v q`
Expected: 10 passed

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Storage.InMemory/InMemoryRateCardStore.cs tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryRateCardStoreTests.cs
git commit -m "feat(billing): add InMemoryRateCardStore with CRUD and active-card lookup"
```

---

### Task 7: InMemoryInvoiceStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs`
- Test: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryInvoiceStoreTests.cs`

- [ ] **Step 1: Write InMemoryInvoiceStore tests**

```csharp
// tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryInvoiceStoreTests.cs
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public class InMemoryInvoiceStoreTests
{
    private readonly InMemoryInvoiceStore _store = new();
    private static readonly TenantId Tenant1 = new("t1");
    private static readonly TenantId Tenant2 = new("t2");

    private static Invoice MakeInvoice(TenantId tenantId, DateTimeOffset periodStart)
        => new()
        {
            InvoiceId = EntityId.New(),
            TenantId = tenantId,
            PeriodStart = periodStart,
            PeriodEnd = periodStart.AddMonths(1),
            Currency = "USD",
            LineItems = new List<InvoiceLineItem>
            {
                new()
                {
                    UsageType = UsageType.VoiceInbound,
                    Description = "Voice Inbound",
                    Quantity = 100m,
                    UnitPrice = 0.05m,
                    Amount = 5.00m,
                },
            },
            Subtotal = 5.00m,
            Total = 5.00m,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task SaveAsync_ShouldPersist_AndGetByIdAsync_ShouldRetrieve()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.InvoiceId.Should().Be(invoice.InvoiceId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var result = await _store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ShouldNotCrossTenants()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant2, invoice.InvoiceId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnPaginatedResults()
    {
        for (var i = 0; i < 5; i++)
            await _store.SaveAsync(MakeInvoice(Tenant1, DateTimeOffset.UtcNow.AddMonths(-i)), CancellationToken.None);

        var page1 = await _store.ListAsync(Tenant1, 1, 2, CancellationToken.None);
        var page2 = await _store.ListAsync(Tenant1, 2, 2, CancellationToken.None);
        var page3 = await _store.ListAsync(Tenant1, 3, 2, CancellationToken.None);

        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
        page3.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_ShouldOrderByPeriodStartDescending()
    {
        var jan = MakeInvoice(Tenant1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var mar = MakeInvoice(Tenant1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var feb = MakeInvoice(Tenant1, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        await _store.SaveAsync(jan, CancellationToken.None);
        await _store.SaveAsync(mar, CancellationToken.None);
        await _store.SaveAsync(feb, CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, 1, 10, CancellationToken.None);

        result[0].PeriodStart.Month.Should().Be(3);
        result[1].PeriodStart.Month.Should().Be(2);
        result[2].PeriodStart.Month.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldNotCrossTenants()
    {
        await _store.SaveAsync(MakeInvoice(Tenant1, DateTimeOffset.UtcNow), CancellationToken.None);
        await _store.SaveAsync(MakeInvoice(Tenant2, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await _store.ListAsync(Tenant1, 1, 10, CancellationToken.None);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldChangeStatus()
    {
        var invoice = MakeInvoice(Tenant1, DateTimeOffset.UtcNow);
        await _store.SaveAsync(invoice, CancellationToken.None);

        await _store.UpdateStatusAsync(Tenant1, invoice.InvoiceId, InvoiceStatus.Issued, CancellationToken.None);

        var result = await _store.GetByIdAsync(Tenant1, invoice.InvoiceId, CancellationToken.None);
        result!.Status.Should().Be(InvoiceStatus.Issued);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldBeNoOp_WhenNotFound()
    {
        // Should not throw
        await _store.UpdateStatusAsync(Tenant1, EntityId.New(), InvoiceStatus.Void, CancellationToken.None);
    }
}
```

- [ ] **Step 2: Implement InMemoryInvoiceStore**

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryInvoiceStore : IInvoiceStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Invoice> _invoices = new();

    public Task SaveAsync(Invoice invoice, CancellationToken ct)
    {
        _invoices[(invoice.TenantId, invoice.InvoiceId)] = invoice;
        return Task.CompletedTask;
    }

    public Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct)
    {
        _invoices.TryGetValue((tenantId, invoiceId), out var invoice);
        return Task.FromResult(invoice);
    }

    public Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        IReadOnlyList<Invoice> result = _invoices.Values
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.PeriodStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(result);
    }

    public Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct)
    {
        if (_invoices.TryGetValue((tenantId, invoiceId), out var invoice))
            invoice.Status = status;

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryInvoiceStoreTests" -v q`
Expected: 8 passed

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryInvoiceStoreTests.cs
git commit -m "feat(billing): add InMemoryInvoiceStore with pagination and status updates"
```

---

### Task 8: PostgresJsonContext + SQL Migration

**Files:**
- Modify: `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/003_RateCardsInvoices.sql`

- [ ] **Step 1: Add JsonSerializable attributes for billing types**

Add these attributes to the `PostgresJsonContext` class in `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs`, after the existing `[JsonSerializable(typeof(List<SurveyAnswer>))]` line:

```csharp
[JsonSerializable(typeof(List<Asterisk.Platform.Billing.RateEntry>))]
[JsonSerializable(typeof(IReadOnlyList<Asterisk.Platform.Billing.RateEntry>))]
[JsonSerializable(typeof(List<Asterisk.Platform.Billing.RateTier>))]
[JsonSerializable(typeof(IReadOnlyList<Asterisk.Platform.Billing.RateTier>))]
[JsonSerializable(typeof(List<Asterisk.Platform.Billing.InvoiceLineItem>))]
[JsonSerializable(typeof(IReadOnlyList<Asterisk.Platform.Billing.InvoiceLineItem>))]
```

Also add at the top of the file:

```csharp
using Asterisk.Platform.Billing;
```

- [ ] **Step 2: Create 003_RateCardsInvoices.sql migration**

```sql
-- 003_RateCardsInvoices.sql — Rate card pricing and invoice tables

CREATE TABLE IF NOT EXISTS rate_cards (
    rate_card_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    currency TEXT NOT NULL DEFAULT 'USD',
    effective_from TIMESTAMPTZ NOT NULL,
    effective_to TIMESTAMPTZ,
    rates JSONB NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ratecard_tenant ON rate_cards (tenant_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS invoices (
    invoice_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    currency TEXT NOT NULL,
    line_items JSONB NOT NULL,
    subtotal NUMERIC(18,2) NOT NULL,
    tax NUMERIC(18,2) NOT NULL DEFAULT 0,
    total NUMERIC(18,2) NOT NULL,
    status SMALLINT NOT NULL DEFAULT 0,
    generated_at TIMESTAMPTZ NOT NULL,
    issued_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_invoice_tenant ON invoices (tenant_id, period_start DESC);
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs src/Asterisk.Platform.Storage.Postgres/Migrations/003_RateCardsInvoices.sql
git commit -m "feat(billing): add PostgresJsonContext types and 003 migration for rate cards and invoices"
```

---

### Task 9: PostgresRateCardStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresRateCardStore.cs`

- [ ] **Step 1: Implement PostgresRateCardStore**

```csharp
// src/Asterisk.Platform.Storage.Postgres/Stores/PostgresRateCardStore.cs
using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

public sealed class PostgresRateCardStore : IRateCardStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRateCardStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(RateCard rateCard, CancellationToken ct)
    {
        var ratesJson = JsonSerializer.Serialize(rateCard.Rates.ToList(), PostgresJson.Ctx.ListRateEntry);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO rate_cards (rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default) " +
            "VALUES (@RateCardId, @TenantId, @Name, @Currency, @EffectiveFrom, @EffectiveTo, @Rates::jsonb, @IsDefault) " +
            "ON CONFLICT (rate_card_id) DO UPDATE SET " +
            "name = EXCLUDED.name, currency = EXCLUDED.currency, effective_from = EXCLUDED.effective_from, " +
            "effective_to = EXCLUDED.effective_to, rates = EXCLUDED.rates, is_default = EXCLUDED.is_default",
            new
            {
                RateCardId = rateCard.RateCardId.Value,
                TenantId = rateCard.TenantId.Value,
                rateCard.Name,
                rateCard.Currency,
                rateCard.EffectiveFrom,
                EffectiveTo = rateCard.EffectiveTo,
                Rates = ratesJson,
                rateCard.IsDefault,
            });
    }

    public async Task<RateCard?> GetByIdAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RateCardRow?>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            new { TenantId = tenantId.Value, RateCardId = rateCardId.Value });

        return row?.ToRateCard();
    }

    public async Task<RateCard?> GetActiveAsync(TenantId tenantId, DateTimeOffset asOf, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RateCardRow?>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId AND effective_from <= @AsOf " +
            "AND (effective_to IS NULL OR effective_to > @AsOf) " +
            "ORDER BY effective_from DESC LIMIT 1",
            new { TenantId = tenantId.Value, AsOf = asOf });

        return row?.ToRateCard();
    }

    public async Task<IReadOnlyList<RateCard>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RateCardRow>(
            "SELECT rate_card_id, tenant_id, name, currency, effective_from, effective_to, rates, is_default " +
            "FROM rate_cards WHERE tenant_id = @TenantId ORDER BY effective_from DESC",
            new { TenantId = tenantId.Value });

        return rows.Select(r => r.ToRateCard()).ToList();
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId rateCardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM rate_cards WHERE tenant_id = @TenantId AND rate_card_id = @RateCardId",
            new { TenantId = tenantId.Value, RateCardId = rateCardId.Value });
    }

    private sealed record RateCardRow(
        string rate_card_id,
        string tenant_id,
        string name,
        string currency,
        DateTimeOffset effective_from,
        DateTimeOffset? effective_to,
        string rates,
        bool is_default)
    {
        public RateCard ToRateCard() => new()
        {
            RateCardId = new EntityId(rate_card_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            Currency = currency,
            EffectiveFrom = effective_from,
            EffectiveTo = effective_to,
            Rates = JsonSerializer.Deserialize(rates, PostgresJson.Ctx.ListRateEntry) ?? [],
            IsDefault = is_default,
        };
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Stores/PostgresRateCardStore.cs
git commit -m "feat(billing): add PostgresRateCardStore with UPSERT and active-card query"
```

---

### Task 10: PostgresInvoiceStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresInvoiceStore.cs`

- [ ] **Step 1: Implement PostgresInvoiceStore**

```csharp
// src/Asterisk.Platform.Storage.Postgres/Stores/PostgresInvoiceStore.cs
using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

public sealed class PostgresInvoiceStore : IInvoiceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresInvoiceStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(Invoice invoice, CancellationToken ct)
    {
        var lineItemsJson = JsonSerializer.Serialize(invoice.LineItems.ToList(), PostgresJson.Ctx.ListInvoiceLineItem);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO invoices (invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at) " +
            "VALUES (@InvoiceId, @TenantId, @PeriodStart, @PeriodEnd, @Currency, @LineItems::jsonb, @Subtotal, @Tax, @Total, @Status, @GeneratedAt, @IssuedAt, @PaidAt)",
            new
            {
                InvoiceId = invoice.InvoiceId.Value,
                TenantId = invoice.TenantId.Value,
                invoice.PeriodStart,
                invoice.PeriodEnd,
                invoice.Currency,
                LineItems = lineItemsJson,
                invoice.Subtotal,
                invoice.Tax,
                invoice.Total,
                Status = (short)invoice.Status,
                invoice.GeneratedAt,
                invoice.IssuedAt,
                invoice.PaidAt,
            });
    }

    public async Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InvoiceRow?>(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            new { TenantId = tenantId.Value, InvoiceId = invoiceId.Value });

        return row?.ToInvoice();
    }

    public async Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<InvoiceRow>(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId ORDER BY period_start DESC " +
            "LIMIT @PageSize OFFSET @Offset",
            new { TenantId = tenantId.Value, PageSize = pageSize, Offset = (page - 1) * pageSize });

        return rows.Select(r => r.ToInvoice()).ToList();
    }

    public async Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE invoices SET status = @Status, " +
            "issued_at = CASE WHEN @Status = 1 THEN NOW() ELSE issued_at END, " +
            "paid_at = CASE WHEN @Status = 2 THEN NOW() ELSE paid_at END " +
            "WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            new { TenantId = tenantId.Value, InvoiceId = invoiceId.Value, Status = (short)status });
    }

    private sealed record InvoiceRow(
        string invoice_id,
        string tenant_id,
        DateTimeOffset period_start,
        DateTimeOffset period_end,
        string currency,
        string line_items,
        decimal subtotal,
        decimal tax,
        decimal total,
        short status,
        DateTimeOffset generated_at,
        DateTimeOffset? issued_at,
        DateTimeOffset? paid_at)
    {
        public Invoice ToInvoice() => new()
        {
            InvoiceId = new EntityId(invoice_id),
            TenantId = new TenantId(tenant_id),
            PeriodStart = period_start,
            PeriodEnd = period_end,
            Currency = currency,
            LineItems = JsonSerializer.Deserialize(line_items, PostgresJson.Ctx.ListInvoiceLineItem) ?? [],
            Subtotal = subtotal,
            Tax = tax,
            Total = total,
            Status = (InvoiceStatus)status,
            GeneratedAt = generated_at,
            IssuedAt = issued_at,
            PaidAt = paid_at,
        };
    }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Stores/PostgresInvoiceStore.cs
git commit -m "feat(billing): add PostgresInvoiceStore with JSONB line items and status transitions"
```

---

### Task 11: DI Registration (All Three ServiceCollectionExtensions)

**Files:**
- Modify: `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add IInvoiceGenerationService to Billing DI**

In `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs`, add the service registration. The complete file should be:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Billing;

/// <summary>
/// DI registration extensions for Platform.Billing services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMeteringService"/>, <see cref="IQuotaEnforcementService"/>, and <see cref="IInvoiceGenerationService"/>.
    /// Store implementations (<see cref="IUsageRecordStore"/>, <see cref="ITenantQuotaStore"/>,
    /// <see cref="IRateCardStore"/>, <see cref="IInvoiceStore"/>) must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformBilling(this IServiceCollection services)
    {
        services.AddSingleton<IMeteringService, DefaultMeteringService>();
        services.AddSingleton<IQuotaEnforcementService, DefaultQuotaEnforcementService>();
        services.AddSingleton<IInvoiceGenerationService, DefaultInvoiceGenerationService>();
        return services;
    }
}
```

- [ ] **Step 2: Add IRateCardStore + IInvoiceStore to InMemory DI**

In `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`, add two lines in the `// Billing` section:

```csharp
        // Billing
        services.AddSingleton<IUsageRecordStore, InMemoryUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, InMemoryTenantQuotaStore>();
        services.AddSingleton<IRateCardStore, InMemoryRateCardStore>();
        services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
```

- [ ] **Step 3: Add IRateCardStore + IInvoiceStore to Postgres DI**

In `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`, add two lines in the `// Billing` section:

```csharp
        // Billing
        services.AddSingleton<IUsageRecordStore, PostgresUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, PostgresTenantQuotaStore>();
        services.AddSingleton<IRateCardStore, PostgresRateCardStore>();
        services.AddSingleton<IInvoiceStore, PostgresInvoiceStore>();
```

- [ ] **Step 4: Build entire solution**

Run: `dotnet build Asterisk.Platform.slnx -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 5: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass (previous + new tests from Tasks 1-7)

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs
git commit -m "feat(billing): register rate card and invoice stores in DI (Billing, InMemory, Postgres)"
```

---

### Task 12: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update test counts in CLAUDE.md**

Update these values:
- Platform.Billing tests: `22` → new count (22 existing + 4 RateCard + 5 Invoice + 9 InvoiceGenerationService + 10 InMemoryRateCard + 8 InMemoryInvoice = **58**)
- Storage.InMemory tests: `54` → `54` (InMemory store tests are in their own test project, counted separately — verify actual count)
- Total test count: update from `1104` to new total (verify with `dotnet test` output)

Update the package table entry for Platform.Billing:
```
| Platform.Billing | Metering engine, quota enforcement, rate cards, invoice generation, DI | XX |
```

- [ ] **Step 2: Add Plan 28B section**

Add after the Plan 28A section:

```markdown
## Plan 28B: Rate Cards + Invoice Generation -- COMPLETE (2026-03-31)

**Spec:** `docs/superpowers/specs/2026-03-31-v120-monetization-ready-design.md` (Sub-project B)
**Plan:** `docs/superpowers/plans/2026-03-31-plan28b-rate-cards-invoices.md`

Extends Platform.Billing package with pricing and invoicing:
1. **Domain Models** -- RateCard (with RateEntry + RateTier), Invoice (with InvoiceLineItem), InvoiceStatus enum
2. **Store Interfaces** -- IRateCardStore (CRUD + active lookup), IInvoiceStore (CRUD + pagination + status transitions)
3. **Invoice Generation** -- DefaultInvoiceGenerationService with flat-rate pricing (included quantities, overage) and tiered pricing
4. **InMemory Storage** -- InMemoryRateCardStore, InMemoryInvoiceStore
5. **Postgres Storage** -- PostgresRateCardStore (UPSERT, JSONB rates), PostgresInvoiceStore (JSONB line_items, status CASE), 003_RateCardsInvoices.sql migration
6. **DI Wiring** -- IInvoiceGenerationService in AddPlatformBilling(), stores in both storage packages
```

Update the v1.2.0 section:
```markdown
- **Sub-project A:** Metering Engine + Quota Enforcement -- COMPLETE (Plan 28A)
- **Sub-project B:** Rate Cards + Invoice Generation -- COMPLETE (Plan 28B)
- **Sub-project C:** Management API + Usage Dashboard (~14 files, ~20 tests)
- **Sub-project D:** E2E Tests for Billing (~4 files, ~25 tests)
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with Plan 28B completion and test counts"
```

---

## Summary

| Task | Description | New Tests | Files |
|------|-------------|-----------|-------|
| 1 | RateCard, RateEntry, RateTier models | 4 | 2 |
| 2 | Invoice, InvoiceLineItem, InvoiceStatus models | 5 | 2 |
| 3 | IRateCardStore + IInvoiceStore interfaces | 0 | 2 |
| 4 | IInvoiceGenerationService interface | 0 | 1 |
| 5 | DefaultInvoiceGenerationService (flat + tiered) | 9 | 2 |
| 6 | InMemoryRateCardStore | 10 | 2 |
| 7 | InMemoryInvoiceStore | 8 | 2 |
| 8 | PostgresJsonContext + 003 migration | 0 | 2 |
| 9 | PostgresRateCardStore | 0 | 1 |
| 10 | PostgresInvoiceStore | 0 | 1 |
| 11 | DI registration (3 files) | 0 | 3 |
| 12 | CLAUDE.md update | 0 | 1 |
| **Total** | | **36** | **21** |

**Batching recommendation (FCM):**
- **Phase A** (Tasks 1-4): Foundation — models + interfaces. All independent, batch together.
- **Phase B** (Task 5): Critical — invoice generation service with complex pricing logic. Individual focused subagent.
- **Phase C** (Tasks 6-7): InMemory stores. Independent, batch together.
- **Phase D** (Tasks 8-10): Postgres — migration + both stores. Batch together.
- **Phase E** (Tasks 11-12): Integration — DI wiring + docs. Batch together.
