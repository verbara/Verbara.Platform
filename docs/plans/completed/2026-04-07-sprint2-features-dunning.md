# Sprint 2: Feature Flags + Billing-Lifecycle Dunning — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-tenant plans (Starter/Pro/Enterprise) with feature gating, add-ons, hierarchical inheritance, and automatic billing-lifecycle dunning with progressive degradation.

**Architecture:** TenantPlan stored in Tenant.Metadata drives feature gates (endpoint filter) and rate limit tier (derivation with override). DunningService (IHostedService) monitors overdue invoices and progressively escalates tenant status through Warning → Degraded → Suspended → PendingDeletion. Three new TenantStatus values in Sdk.Pro, all other changes in Platform.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, NSubstitute. AOT-first (no reflection). ConcurrentDictionary caches. BackgroundService for dunning.

**Spec:** `docs/superpowers/specs/2026-04-07-sprint2-features-dunning-design.md`

---

## File Structure

### New Files (14)
| File | Responsibility |
|------|---------------|
| `src/Asterisk.Platform.Core/TenantPlan.cs` | Plan enum (Starter/Pro/Enterprise) |
| `src/Asterisk.Platform.Core/PlanFeature.cs` | Feature flag enum (13 features) |
| `src/Asterisk.Platform.Core/PlanDefinition.cs` | Static plan→features/limits mapping |
| `src/Asterisk.Platform.Core/TenantAddOn.cs` | Add-on entity |
| `src/Asterisk.Platform.Core/ITenantAddOnStore.cs` | Add-on store interface |
| `src/Asterisk.Platform.Core/IFeatureGateService.cs` | Feature gate service interface |
| `src/Asterisk.Platform.Billing/PaymentStatus.cs` | Payment status enum |
| `src/Asterisk.Platform.Billing/DunningConfig.cs` | Dunning timing configuration |
| `src/Asterisk.Platform.Billing/DunningRecord.cs` | Dunning record entity |
| `src/Asterisk.Platform.Billing/IDunningStore.cs` | Dunning store interface |
| `src/Asterisk.Platform.Billing/DunningService.cs` | Background dunning worker |
| `src/Asterisk.Platform.Api/Services/FeatureGateCache.cs` | Feature resolution cache |
| `src/Asterisk.Platform.Api/Services/DefaultFeatureGateService.cs` | Feature gate implementation |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAddOnStore.cs` | In-memory add-on store |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryDunningStore.cs` | In-memory dunning store |

### Modified Files (12)
| File | Change |
|------|--------|
| `Asterisk.Sdk.Pro.MultiTenant/TenantStatus.cs` | Add Warning, Degraded, PendingDeletion |
| `src/Asterisk.Platform.Core/TenantExtensions.cs` | Add GetPlan/SetPlan helpers |
| `src/Asterisk.Platform.Billing/Invoice.cs` | Add PaymentStatus + DueDate properties |
| `src/Asterisk.Platform.Billing/IInvoiceStore.cs` | Add ListByStatusAsync method |
| `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj` | Add Hosting.Abstractions package |
| `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs` | Register DunningService config |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs` | Implement ListByStatusAsync |
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register new stores |
| `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs` | Handle Warning/Degraded/PendingDeletion + populate FeatureGateCache |
| `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` | Add Plan/Features/AddOns/Dunning to DTOs |
| `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs` | Plan/AddOn write support + hierarchy validation |
| `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` | Dunning endpoints + payment resolution |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Register new DTOs |
| `src/Asterisk.Platform.Api/Program.cs` | Register services, apply feature gates to endpoint groups |

### New Test Files (6)
| File | Tests |
|------|-------|
| `tests/Asterisk.Platform.Core.Tests/PlanDefinitionTests.cs` | Plan→features mapping, limits |
| `tests/Asterisk.Platform.Billing.Tests/DunningServiceTests.cs` | Detection, escalation, resolution |
| `tests/Asterisk.Platform.Api.Tests/FeatureGateServiceTests.cs` | Feature resolution, degraded, hierarchy |
| `tests/Asterisk.Platform.Api.Tests/PlanFeatureFilterTests.cs` | Filter 403/pass/bypass |

### Modified Test Files (2)
| File | Change |
|------|--------|
| `tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs` | Add GetPlan/SetPlan tests |
| `tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs` | Add Warning/Degraded/PendingDeletion tests |

---

## Task 1: Sdk.Pro TenantStatus Enum Expansion

**Files:**
- Modify: `/media/Data/Source/Verbara/Asterisk.Sdk.Pro/src/Asterisk.Sdk.Pro.MultiTenant/TenantStatus.cs`

- [ ] **Step 1: Update TenantStatus enum with 3 new values**

Existing values keep their ordinal positions for backward compatibility:

```csharp
namespace Asterisk.Sdk.Pro.MultiTenant;

/// <summary>Lifecycle status of a tenant.</summary>
public enum TenantStatus
{
    /// <summary>Tenant is operational and can originate calls.</summary>
    Active = 0,

    /// <summary>Tenant is temporarily suspended; new calls are blocked.</summary>
    Suspended = 1,

    /// <summary>Tenant has been deleted; data may be retained for audit.</summary>
    Deleted = 2,

    /// <summary>Payment overdue; tenant operational with warning banner.</summary>
    Warning = 3,

    /// <summary>Payment severely overdue; premium features disabled, Starter-only.</summary>
    Degraded = 4,

    /// <summary>Pending data deletion due to prolonged non-payment.</summary>
    PendingDeletion = 5,
}
```

- [ ] **Step 2: Build and test Sdk.Pro**

Run:
```sh
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet build
dotnet test
```

Fix any switch/pattern-match exhaustiveness warnings (add `default` or `_` cases if needed). TreatWarningsAsErrors is ON.

- [ ] **Step 3: Pack to local NuGet feed**

```sh
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
dotnet pack -c Release -o /media/Data/Source/Verbara/local-nuget-feed/
```

- [ ] **Step 4: Restore in Platform**

```sh
cd /media/Data/Source/Verbara/Asterisk.Platform
rm -rf ~/.nuget/packages/asterisk.sdk.pro*
dotnet restore Asterisk.Platform.slnx
dotnet build Asterisk.Platform.slnx
```

Fix any exhaustiveness warnings in Platform. The main one will be in `TenantStatusMiddleware.cs` — the existing `default:` case already handles unknown values (pass-through), so this should build cleanly.

- [ ] **Step 5: Commit**

```sh
cd /media/Data/Source/Verbara/Asterisk.Sdk.Pro
git add -A && git commit -m "feat: add Warning, Degraded, PendingDeletion to TenantStatus enum"
```

---

## Task 2: Platform.Core — TenantPlan, PlanFeature, PlanDefinition

**Files:**
- Create: `src/Asterisk.Platform.Core/TenantPlan.cs`
- Create: `src/Asterisk.Platform.Core/PlanFeature.cs`
- Create: `src/Asterisk.Platform.Core/PlanDefinition.cs`
- Create: `tests/Asterisk.Platform.Core.Tests/PlanDefinitionTests.cs`

- [ ] **Step 1: Write tests for PlanDefinition**

```csharp
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Core.Tests;

public sealed class PlanDefinitionTests
{
    [Fact]
    public void GetFeatures_ShouldReturnEmpty_WhenStarter()
    {
        PlanDefinition.GetFeatures(TenantPlan.Starter).Should().BeEmpty();
    }

    [Fact]
    public void GetFeatures_ShouldReturn8Features_WhenPro()
    {
        var features = PlanDefinition.GetFeatures(TenantPlan.Pro);
        features.Should().HaveCount(8);
        features.Should().Contain(PlanFeature.Dialer);
        features.Should().Contain(PlanFeature.Flows);
        features.Should().Contain(PlanFeature.Recordings);
    }

    [Fact]
    public void GetFeatures_ShouldReturnAll13Features_WhenEnterprise()
    {
        var all = Enum.GetValues<PlanFeature>().ToHashSet();
        PlanDefinition.GetFeatures(TenantPlan.Enterprise).Should().BeEquivalentTo(all);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnStandard_WhenStarter()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Starter).Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnProfessional_WhenPro()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Pro).Should().Be(RateLimitTier.Professional);
    }

    [Fact]
    public void GetDefaultTier_ShouldReturnEnterprise_WhenEnterprise()
    {
        PlanDefinition.GetDefaultTier(TenantPlan.Enterprise).Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn3_WhenStarter()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Starter).Should().Be(3);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn7_WhenPro()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Pro).Should().Be(7);
    }

    [Fact]
    public void GetMaxChannels_ShouldReturn11_WhenEnterprise()
    {
        PlanDefinition.GetMaxChannels(TenantPlan.Enterprise).Should().Be(11);
    }

    [Fact]
    public void GetMaxWebhookSubscriptions_ShouldReturn0_WhenStarter()
    {
        PlanDefinition.GetMaxWebhookSubscriptions(TenantPlan.Starter).Should().Be(0);
    }

    [Fact]
    public void StarterFeatures_ShouldNotContainDialer()
    {
        PlanDefinition.GetFeatures(TenantPlan.Starter).Should().NotContain(PlanFeature.Dialer);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```sh
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet test tests/Asterisk.Platform.Core.Tests/ --filter "PlanDefinitionTests"
```

Expected: compilation failure (types don't exist yet).

- [ ] **Step 3: Create TenantPlan enum**

Create `src/Asterisk.Platform.Core/TenantPlan.cs`:

```csharp
namespace Asterisk.Platform.Core;

public enum TenantPlan
{
    Starter = 0,
    Pro = 1,
    Enterprise = 2,
}
```

- [ ] **Step 4: Create PlanFeature enum**

Create `src/Asterisk.Platform.Core/PlanFeature.cs`:

```csharp
namespace Asterisk.Platform.Core;

public enum PlanFeature
{
    Dialer,
    BotBasic,
    BotAdvanced,
    AgentAssist,
    CallAnalytics,
    AnalyticsExport,
    Flows,
    Webhooks,
    OidcSso,
    ScheduledReports,
    KnowledgeBase,
    Recordings,
    RecordingTranscription,
}
```

- [ ] **Step 5: Create PlanDefinition static class**

Create `src/Asterisk.Platform.Core/PlanDefinition.cs`:

```csharp
namespace Asterisk.Platform.Core;

public static class PlanDefinition
{
    private static readonly IReadOnlySet<PlanFeature> StarterFeatures =
        new HashSet<PlanFeature>().AsReadOnly();

    private static readonly IReadOnlySet<PlanFeature> ProFeatures = new HashSet<PlanFeature>
    {
        PlanFeature.Dialer,
        PlanFeature.BotBasic,
        PlanFeature.AnalyticsExport,
        PlanFeature.Flows,
        PlanFeature.Webhooks,
        PlanFeature.ScheduledReports,
        PlanFeature.KnowledgeBase,
        PlanFeature.Recordings,
    }.AsReadOnly();

    private static readonly IReadOnlySet<PlanFeature> EnterpriseFeatures =
        new HashSet<PlanFeature>(Enum.GetValues<PlanFeature>()).AsReadOnly();

    public static IReadOnlySet<PlanFeature> GetFeatures(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => ProFeatures,
        TenantPlan.Enterprise => EnterpriseFeatures,
        _ => StarterFeatures,
    };

    public static RateLimitTier GetDefaultTier(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => RateLimitTier.Professional,
        TenantPlan.Enterprise => RateLimitTier.Enterprise,
        _ => RateLimitTier.Standard,
    };

    public static int GetMaxChannels(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 7,
        TenantPlan.Enterprise => 11,
        _ => 3,
    };

    public static int GetAuditRetentionDays(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 30,
        TenantPlan.Enterprise => 90,
        _ => 7,
    };

    public static int GetMaxWebhookSubscriptions(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 5,
        TenantPlan.Enterprise => int.MaxValue,
        _ => 0,
    };

    public static int GetMaxScheduledReports(TenantPlan plan) => plan switch
    {
        TenantPlan.Pro => 5,
        TenantPlan.Enterprise => int.MaxValue,
        _ => 0,
    };
}
```

- [ ] **Step 6: Run tests — verify they pass**

```sh
cd /media/Data/Source/Verbara/Asterisk.Platform
dotnet test tests/Asterisk.Platform.Core.Tests/ --filter "PlanDefinitionTests"
```

Expected: 11 tests pass.

- [ ] **Step 7: Build full solution to check for warnings**

```sh
dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 8: Commit**

```sh
git add src/Asterisk.Platform.Core/TenantPlan.cs src/Asterisk.Platform.Core/PlanFeature.cs src/Asterisk.Platform.Core/PlanDefinition.cs tests/Asterisk.Platform.Core.Tests/PlanDefinitionTests.cs
git commit -m "feat: add TenantPlan, PlanFeature enums and PlanDefinition mapping"
```

---

## Task 3: Platform.Core — TenantExtensions, TenantAddOn, Interfaces

**Files:**
- Modify: `src/Asterisk.Platform.Core/TenantExtensions.cs`
- Create: `src/Asterisk.Platform.Core/TenantAddOn.cs`
- Create: `src/Asterisk.Platform.Core/ITenantAddOnStore.cs`
- Create: `src/Asterisk.Platform.Core/IFeatureGateService.cs`
- Modify: `tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs`

- [ ] **Step 1: Add GetPlan/SetPlan tests to TenantExtensionsTests**

Append to `tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs`:

```csharp
[Fact]
public void GetPlan_ShouldReturnStarter_WhenMetadataNull()
{
    var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
    tenant.GetPlan().Should().Be(TenantPlan.Starter);
}

[Fact]
public void GetPlan_ShouldReturnStarter_WhenKeyMissing()
{
    var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = new() };
    tenant.GetPlan().Should().Be(TenantPlan.Starter);
}

[Fact]
public void GetPlan_ShouldReturnPlan_WhenMetadataSet()
{
    var tenant = new Tenant
    {
        TenantId = "t1", Name = "T1",
        Metadata = new() { ["Plan"] = "Enterprise" },
    };
    tenant.GetPlan().Should().Be(TenantPlan.Enterprise);
}

[Fact]
public void SetPlan_ShouldSetMetadata()
{
    var tenant = new Tenant { TenantId = "t1", Name = "T1" };
    tenant.SetPlan(TenantPlan.Pro);
    tenant.Metadata.Should().ContainKey("Plan").WhoseValue.Should().Be("Pro");
}

[Fact]
public void SetPlan_ShouldCreateMetadata_WhenNull()
{
    var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
    tenant.SetPlan(TenantPlan.Enterprise);
    tenant.Metadata.Should().NotBeNull();
    tenant.GetPlan().Should().Be(TenantPlan.Enterprise);
}
```

- [ ] **Step 2: Add GetPlan/SetPlan to TenantExtensions**

Add to `src/Asterisk.Platform.Core/TenantExtensions.cs` after the existing methods:

```csharp
private const string PlanKey = "Plan";

public static TenantPlan GetPlan(this Tenant tenant)
    => tenant.Metadata?.GetValueOrDefault(PlanKey) is string s
        && Enum.TryParse<TenantPlan>(s, out var plan) ? plan : TenantPlan.Starter;

public static void SetPlan(this Tenant tenant, TenantPlan plan)
{
    tenant.Metadata ??= new();
    tenant.Metadata[PlanKey] = plan.ToString();
}
```

- [ ] **Step 3: Create TenantAddOn**

Create `src/Asterisk.Platform.Core/TenantAddOn.cs`:

```csharp
namespace Asterisk.Platform.Core;

public sealed class TenantAddOn
{
    public required string TenantId { get; init; }
    public required PlanFeature Feature { get; init; }
    public required DateTimeOffset EnabledAt { get; init; }
}
```

- [ ] **Step 4: Create ITenantAddOnStore**

Create `src/Asterisk.Platform.Core/ITenantAddOnStore.cs`:

```csharp
namespace Asterisk.Platform.Core;

public interface ITenantAddOnStore
{
    Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default);
}
```

- [ ] **Step 5: Create IFeatureGateService**

Create `src/Asterisk.Platform.Core/IFeatureGateService.cs`:

```csharp
namespace Asterisk.Platform.Core;

public interface IFeatureGateService
{
    bool IsFeatureEnabled(string tenantId, PlanFeature feature);
    IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId);
    int GetMaxChannels(string tenantId);
    int GetAuditRetentionDays(string tenantId);
    int GetMaxWebhookSubscriptions(string tenantId);
    int GetMaxScheduledReports(string tenantId);
}
```

- [ ] **Step 6: Run tests**

```sh
dotnet test tests/Asterisk.Platform.Core.Tests/ --filter "TenantExtensionsTests"
```

Expected: 9 tests pass (4 existing + 5 new).

- [ ] **Step 7: Build full solution**

```sh
dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 8: Commit**

```sh
git add src/Asterisk.Platform.Core/TenantExtensions.cs src/Asterisk.Platform.Core/TenantAddOn.cs src/Asterisk.Platform.Core/ITenantAddOnStore.cs src/Asterisk.Platform.Core/IFeatureGateService.cs tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs
git commit -m "feat: add GetPlan/SetPlan, TenantAddOn, ITenantAddOnStore, IFeatureGateService"
```

---

## Task 4: Platform.Billing — Dunning Domain Models

**Files:**
- Create: `src/Asterisk.Platform.Billing/PaymentStatus.cs`
- Create: `src/Asterisk.Platform.Billing/DunningConfig.cs`
- Create: `src/Asterisk.Platform.Billing/DunningRecord.cs`
- Create: `src/Asterisk.Platform.Billing/IDunningStore.cs`
- Modify: `src/Asterisk.Platform.Billing/Invoice.cs`
- Modify: `src/Asterisk.Platform.Billing/IInvoiceStore.cs`
- Modify: `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj`

- [ ] **Step 1: Create PaymentStatus enum**

Create `src/Asterisk.Platform.Billing/PaymentStatus.cs`:

```csharp
namespace Asterisk.Platform.Billing;

public enum PaymentStatus
{
    Current,
    Overdue,
    Delinquent,
    WrittenOff,
}
```

- [ ] **Step 2: Add PaymentStatus and DueDate to Invoice**

In `src/Asterisk.Platform.Billing/Invoice.cs`, add after the `PaidAt` property:

```csharp
public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Current;
public DateTimeOffset? DueDate { get; set; }
```

- [ ] **Step 3: Add ListByStatusAsync to IInvoiceStore**

Read `src/Asterisk.Platform.Billing/IInvoiceStore.cs` and add this method to the interface:

```csharp
Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default);
```

- [ ] **Step 4: Create DunningConfig**

Create `src/Asterisk.Platform.Billing/DunningConfig.cs`:

```csharp
namespace Asterisk.Platform.Billing;

public sealed class DunningConfig
{
    public int WarningDays { get; init; }
    public int DegradedDays { get; init; } = 7;
    public int SuspendedDays { get; init; } = 14;
    public int PendingDeletionDays { get; init; } = 30;
    public int CheckIntervalHours { get; init; } = 6;
}
```

- [ ] **Step 5: Create DunningRecord**

Create `src/Asterisk.Platform.Billing/DunningRecord.cs`:

```csharp
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Billing;

public sealed class DunningRecord
{
    public required string DunningId { get; init; }
    public required string TenantId { get; init; }
    public required string InvoiceId { get; init; }
    public TenantStatus CurrentStage { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool IsPaused { get; set; }
    public bool IsActive { get; set; } = true;
}
```

- [ ] **Step 6: Create IDunningStore**

Create `src/Asterisk.Platform.Billing/IDunningStore.cs`:

```csharp
namespace Asterisk.Platform.Billing;

public interface IDunningStore
{
    Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default);
    Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default);
    Task UpsertAsync(DunningRecord record, CancellationToken ct = default);
}
```

- [ ] **Step 7: Add Microsoft.Extensions.Hosting.Abstractions to Billing.csproj**

In `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj`, add to the `<ItemGroup>` with PackageReference:

```xml
<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

Check `Directory.Packages.props` at the solution root — if `Microsoft.Extensions.Hosting.Abstractions` is not listed, add it with the same version as other `Microsoft.Extensions.*` packages.

- [ ] **Step 8: Build to verify compilation**

```sh
dotnet build Asterisk.Platform.slnx
```

This will fail because `InMemoryInvoiceStore` and `PostgresInvoiceStore` don't implement `ListByStatusAsync` yet. That's expected — Task 6 will fix InMemory, and Postgres needs a stub too. For now, add a quick stub to the Postgres store if it exists:

Read `src/Asterisk.Platform.Storage.Postgres/` to find the `PostgresInvoiceStore`. Add a `ListByStatusAsync` implementation following the existing pattern (use Dapper query with `WHERE status = @Status`). If the Postgres store uses string status values:

```csharp
public async Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default)
{
    using var conn = await _dataSource.OpenConnectionAsync(ct);
    var rows = await conn.QueryAsync<InvoiceRow>(
        "SELECT * FROM invoices WHERE status = @Status",
        new { Status = status.ToString() });
    return rows.Select(MapToInvoice).ToList();
}
```

Adapt to match the exact patterns in the existing file (column names, row type, mapping method).

- [ ] **Step 9: Commit**

```sh
git add src/Asterisk.Platform.Billing/ src/Asterisk.Platform.Storage.Postgres/
git commit -m "feat: add PaymentStatus, DunningRecord, IDunningStore, Invoice DueDate"
```

---

## Task 5: Platform.Billing — DunningService

**Files:**
- Create: `src/Asterisk.Platform.Billing/DunningService.cs`
- Modify: `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs`
- Create: `tests/Asterisk.Platform.Billing.Tests/DunningServiceTests.cs`

- [ ] **Step 1: Write DunningService tests**

Create `tests/Asterisk.Platform.Billing.Tests/DunningServiceTests.cs`:

```csharp
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Asterisk.Platform.Billing.Tests;

public sealed class DunningServiceTests
{
    private readonly IInvoiceStore _invoiceStore = Substitute.For<IInvoiceStore>();
    private readonly IDunningStore _dunningStore = Substitute.For<IDunningStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DunningConfig _config = new() { WarningDays = 0, DegradedDays = 7, SuspendedDays = 14, PendingDeletionDays = 30 };

    private DunningService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_invoiceStore);
        services.AddSingleton(_dunningStore);
        services.AddSingleton(_tenantStore);
        services.AddSingleton(_clock);
        services.AddSingleton<IEnumerable<ITenantLifecycleHandler>>(Array.Empty<ITenantLifecycleHandler>());
        var sp = services.BuildServiceProvider();

        return new DunningService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DunningService>.Instance,
            Options.Create(_config));
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldCreateDunningRecord_WhenInvoiceOverdue()
    {
        var now = new DateTimeOffset(2026, 4, 7, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var invoice = new Invoice
        {
            InvoiceId = new EntityId("inv-1"),
            TenantId = new TenantId("acme"),
            PeriodStart = now.AddDays(-30),
            PeriodEnd = now.AddDays(-1),
            Currency = "USD",
            LineItems = Array.Empty<InvoiceLineItem>(),
            Subtotal = 100m,
            Total = 100m,
            Status = InvoiceStatus.Issued,
            DueDate = now.AddDays(-1),
            GeneratedAt = now.AddDays(-30),
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(new[] { invoice });
        _dunningStore.GetByInvoiceAsync("inv-1", Arg.Any<CancellationToken>())
            .Returns((DunningRecord?)null);
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Active });
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DunningRecord>());

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        await _dunningStore.Received(1).UpsertAsync(
            Arg.Is<DunningRecord>(r => r.TenantId == "acme" && r.CurrentStage == TenantStatus.Warning),
            Arg.Any<CancellationToken>());
        await _tenantStore.Received(1).UpdateStatusAsync("acme", TenantStatus.Warning, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToDegraded_WhenPast7Days()
    {
        var now = new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var record = new DunningRecord
        {
            DunningId = "d-1",
            TenantId = "acme",
            InvoiceId = "inv-1",
            CurrentStage = TenantStatus.Warning,
            StartedAt = now.AddDays(-8),
            IsActive = true,
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Invoice>());
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { record });

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        record.CurrentStage.Should().Be(TenantStatus.Degraded);
        await _tenantStore.Received(1).UpdateStatusAsync("acme", TenantStatus.Degraded, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToSuspended_WhenPast14Days()
    {
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var record = new DunningRecord
        {
            DunningId = "d-1",
            TenantId = "acme",
            InvoiceId = "inv-1",
            CurrentStage = TenantStatus.Degraded,
            StartedAt = now.AddDays(-15),
            IsActive = true,
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Invoice>());
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { record });

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        record.CurrentStage.Should().Be(TenantStatus.Suspended);
        await _tenantStore.Received(1).UpdateStatusAsync("acme", TenantStatus.Suspended, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldEscalateToPendingDeletion_WhenPast30Days()
    {
        var now = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var record = new DunningRecord
        {
            DunningId = "d-1",
            TenantId = "acme",
            InvoiceId = "inv-1",
            CurrentStage = TenantStatus.Suspended,
            StartedAt = now.AddDays(-31),
            IsActive = true,
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Invoice>());
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { record });

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        record.CurrentStage.Should().Be(TenantStatus.PendingDeletion);
        await _tenantStore.Received(1).UpdateStatusAsync("acme", TenantStatus.PendingDeletion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldSkipPausedRecords()
    {
        var now = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var record = new DunningRecord
        {
            DunningId = "d-1",
            TenantId = "acme",
            InvoiceId = "inv-1",
            CurrentStage = TenantStatus.Warning,
            StartedAt = now.AddDays(-31),
            IsActive = true,
            IsPaused = true,
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Invoice>());
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { record });

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        record.CurrentStage.Should().Be(TenantStatus.Warning);
        await _tenantStore.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<TenantStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDunningCycle_ShouldNotBlockOnSingleTenantFailure()
    {
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        var failRecord = new DunningRecord
        {
            DunningId = "d-1", TenantId = "fail-tenant", InvoiceId = "inv-1",
            CurrentStage = TenantStatus.Warning, StartedAt = now.AddDays(-15), IsActive = true,
        };
        var okRecord = new DunningRecord
        {
            DunningId = "d-2", TenantId = "ok-tenant", InvoiceId = "inv-2",
            CurrentStage = TenantStatus.Warning, StartedAt = now.AddDays(-15), IsActive = true,
        };

        _invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Invoice>());
        _dunningStore.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { failRecord, okRecord });
        _tenantStore.UpdateStatusAsync("fail-tenant", Arg.Any<TenantStatus>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var service = CreateService();
        await service.ProcessDunningCycleAsync(CancellationToken.None);

        // ok-tenant should still be processed despite fail-tenant throwing
        await _tenantStore.Received(1).UpdateStatusAsync("ok-tenant", TenantStatus.Suspended, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Create DunningService**

Create `src/Asterisk.Platform.Billing/DunningService.cs`:

```csharp
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Billing;

public sealed class DunningService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DunningService> _logger;
    private readonly DunningConfig _config;

    public DunningService(
        IServiceScopeFactory scopeFactory,
        ILogger<DunningService> logger,
        IOptions<DunningConfig> config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDunningCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Dunning cycle failed");
            }

            await Task.Delay(TimeSpan.FromHours(_config.CheckIntervalHours), stoppingToken);
        }
    }

    internal async Task ProcessDunningCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var invoiceStore = sp.GetRequiredService<IInvoiceStore>();
        var dunningStore = sp.GetRequiredService<IDunningStore>();
        var tenantStore = sp.GetRequiredService<ITenantStore>();
        var lifecycleHandlers = sp.GetServices<ITenantLifecycleHandler>();
        var clock = sp.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        // Phase 1: Detect new overdue invoices
        var issuedInvoices = await invoiceStore.ListByStatusAsync(InvoiceStatus.Issued, ct);
        foreach (var invoice in issuedInvoices)
        {
            if (invoice.DueDate is null || invoice.DueDate >= now)
                continue;

            var existing = await dunningStore.GetByInvoiceAsync(invoice.InvoiceId.Value, ct);
            if (existing is not null)
                continue;

            try
            {
                var record = new DunningRecord
                {
                    DunningId = EntityId.New().Value,
                    TenantId = invoice.TenantId.Value,
                    InvoiceId = invoice.InvoiceId.Value,
                    CurrentStage = TenantStatus.Warning,
                    StartedAt = invoice.DueDate.Value,
                };

                invoice.PaymentStatus = PaymentStatus.Overdue;
                await invoiceStore.SaveAsync(invoice, ct);
                await dunningStore.UpsertAsync(record, ct);
                await tenantStore.UpdateStatusAsync(invoice.TenantId.Value, TenantStatus.Warning, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to create dunning for invoice {InvoiceId}", invoice.InvoiceId.Value);
            }
        }

        // Phase 2: Escalate existing records
        var activeRecords = await dunningStore.ListActiveAsync(ct);
        foreach (var record in activeRecords)
        {
            if (record.IsPaused)
                continue;

            try
            {
                var days = (now - record.StartedAt).TotalDays;
                TenantStatus? newStage = null;
                PaymentStatus? newPayment = null;

                if (days >= _config.PendingDeletionDays && record.CurrentStage != TenantStatus.PendingDeletion)
                {
                    newStage = TenantStatus.PendingDeletion;
                    newPayment = PaymentStatus.WrittenOff;
                }
                else if (days >= _config.SuspendedDays && record.CurrentStage is TenantStatus.Warning or TenantStatus.Degraded)
                {
                    newStage = TenantStatus.Suspended;
                    newPayment = PaymentStatus.Delinquent;
                }
                else if (days >= _config.DegradedDays && record.CurrentStage == TenantStatus.Warning)
                {
                    newStage = TenantStatus.Degraded;
                }

                if (newStage is null)
                    continue;

                record.CurrentStage = newStage.Value;
                record.EscalatedAt = now;
                await dunningStore.UpsertAsync(record, ct);
                await tenantStore.UpdateStatusAsync(record.TenantId, newStage.Value, ct);

                if (newPayment is not null)
                {
                    var invoice = await invoiceStore.GetByIdAsync(new EntityId(record.InvoiceId), ct);
                    if (invoice is not null)
                    {
                        invoice.PaymentStatus = newPayment.Value;
                        await invoiceStore.SaveAsync(invoice, ct);
                    }
                }

                // Dispatch lifecycle handler for Suspended (cleans up Realtime rows)
                if (newStage == TenantStatus.Suspended)
                {
                    foreach (var handler in lifecycleHandlers)
                    {
                        try { await handler.OnTenantSuspendedAsync(record.TenantId, ct); }
                        catch (Exception hex)
                        {
                            _logger.LogWarning(hex, "Lifecycle handler failed during dunning suspension for {TenantId}", record.TenantId);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Dunning escalation failed for tenant {TenantId}", record.TenantId);
            }
        }
    }
}
```

- [ ] **Step 3: Run tests**

```sh
dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DunningServiceTests"
```

Expected: 6 tests pass.

- [ ] **Step 4: Build full solution**

```sh
dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 5: Commit**

```sh
git add src/Asterisk.Platform.Billing/DunningService.cs tests/Asterisk.Platform.Billing.Tests/DunningServiceTests.cs
git commit -m "feat: add DunningService background worker with escalation logic"
```

---

## Task 6: Platform.Storage.InMemory — AddOn + Dunning Stores

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAddOnStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryDunningStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create InMemoryTenantAddOnStore**

Create `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAddOnStore.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantAddOnStore : ITenantAddOnStore
{
    private readonly ConcurrentDictionary<(string TenantId, PlanFeature Feature), TenantAddOn> _store = new();

    public Task<IReadOnlyList<TenantAddOn>> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var result = _store.Values
            .Where(a => a.TenantId == tenantId)
            .ToList();
        return Task.FromResult<IReadOnlyList<TenantAddOn>>(result);
    }

    public Task UpsertAsync(TenantAddOn addOn, CancellationToken ct = default)
    {
        _store[(addOn.TenantId, addOn.Feature)] = addOn;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, PlanFeature feature, CancellationToken ct = default)
    {
        _store.TryRemove((tenantId, feature), out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Create InMemoryDunningStore**

Create `src/Asterisk.Platform.Storage.InMemory/InMemoryDunningStore.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryDunningStore : IDunningStore
{
    private readonly ConcurrentDictionary<string, DunningRecord> _store = new();

    public Task<DunningRecord?> GetActiveAsync(string tenantId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(r => r.TenantId == tenantId && r.IsActive);
        return Task.FromResult(result);
    }

    public Task<DunningRecord?> GetByInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        var result = _store.Values.FirstOrDefault(r => r.InvoiceId == invoiceId && r.IsActive);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DunningRecord>> ListActiveAsync(CancellationToken ct = default)
    {
        var result = _store.Values.Where(r => r.IsActive).ToList();
        return Task.FromResult<IReadOnlyList<DunningRecord>>(result);
    }

    public Task UpsertAsync(DunningRecord record, CancellationToken ct = default)
    {
        _store[record.DunningId] = record;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Add ListByStatusAsync to InMemoryInvoiceStore**

Read `src/Asterisk.Platform.Storage.InMemory/InMemoryInvoiceStore.cs` and add this method:

```csharp
public Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default)
{
    var result = _store.Values
        .Where(i => i.Status == status)
        .ToList();
    return Task.FromResult<IReadOnlyList<Invoice>>(result);
}
```

- [ ] **Step 4: Register new stores in AddInMemoryStorage()**

In `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`, add after the existing billing store registrations:

```csharp
services.AddSingleton<ITenantAddOnStore, InMemoryTenantAddOnStore>();
services.AddSingleton<IDunningStore, InMemoryDunningStore>();
```

Make sure to add the required `using` statements at the top of the file:
```csharp
using Asterisk.Platform.Core;  // for ITenantAddOnStore (if not already present)
```

- [ ] **Step 5: Build full solution**

```sh
dotnet build Asterisk.Platform.slnx
```

- [ ] **Step 6: Run all tests**

```sh
dotnet test Asterisk.Platform.slnx
```

All existing + new tests should pass.

- [ ] **Step 7: Commit**

```sh
git add src/Asterisk.Platform.Storage.InMemory/
git commit -m "feat: add InMemoryTenantAddOnStore, InMemoryDunningStore, ListByStatusAsync"
```

---

## Task 7: Platform.Api — FeatureGateCache + DefaultFeatureGateService

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/FeatureGateCache.cs`
- Create: `src/Asterisk.Platform.Api/Services/DefaultFeatureGateService.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/FeatureGateServiceTests.cs`

- [ ] **Step 1: Write FeatureGateService tests**

Create `tests/Asterisk.Platform.Api.Tests/FeatureGateServiceTests.cs`:

```csharp
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class FeatureGateServiceTests
{
    private readonly FeatureGateCache _cache = new();

    private DefaultFeatureGateService CreateService() => new(_cache);

    [Fact]
    public void IsFeatureEnabled_ShouldReturnFalse_WhenStarterAndDialer()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Starter,
            PlanDefinition.GetFeatures(TenantPlan.Starter),
            3, 7, 0, 0));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeFalse();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnTrue_WhenProAndDialer()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Pro,
            PlanDefinition.GetFeatures(TenantPlan.Pro),
            7, 30, 5, 5));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeTrue();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnTrue_WhenStarterWithAddOn()
    {
        var features = new HashSet<PlanFeature>(PlanDefinition.GetFeatures(TenantPlan.Starter))
        {
            PlanFeature.Dialer,
        };
        _cache.Set("t1", new ResolvedFeatures(TenantPlan.Starter, features.AsReadOnly(), 3, 7, 0, 0));

        var service = CreateService();
        service.IsFeatureEnabled("t1", PlanFeature.Dialer).Should().BeTrue();
    }

    [Fact]
    public void IsFeatureEnabled_ShouldReturnFalse_WhenNotInCache()
    {
        var service = CreateService();
        service.IsFeatureEnabled("unknown", PlanFeature.Dialer).Should().BeFalse();
    }

    [Fact]
    public void GetMaxChannels_ShouldReturnCachedValue()
    {
        _cache.Set("t1", new ResolvedFeatures(
            TenantPlan.Pro,
            PlanDefinition.GetFeatures(TenantPlan.Pro),
            7, 30, 5, 5));

        var service = CreateService();
        service.GetMaxChannels("t1").Should().Be(7);
    }

    [Fact]
    public void GetEnabledFeatures_ShouldReturnEmpty_WhenNotInCache()
    {
        var service = CreateService();
        service.GetEnabledFeatures("unknown").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Create FeatureGateCache**

Create `src/Asterisk.Platform.Api/Services/FeatureGateCache.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

internal sealed record ResolvedFeatures(
    TenantPlan EffectivePlan,
    IReadOnlySet<PlanFeature> Features,
    int MaxChannels,
    int AuditRetentionDays,
    int MaxWebhookSubscriptions,
    int MaxScheduledReports);

internal sealed class FeatureGateCache
{
    private readonly ConcurrentDictionary<string, ResolvedFeatures> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedFeatures? Get(string tenantId)
        => _cache.GetValueOrDefault(tenantId);

    public void Set(string tenantId, ResolvedFeatures features)
        => _cache[tenantId] = features;

    public void Remove(string tenantId)
        => _cache.TryRemove(tenantId, out _);
}
```

- [ ] **Step 3: Create DefaultFeatureGateService**

Create `src/Asterisk.Platform.Api/Services/DefaultFeatureGateService.cs`:

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

internal sealed class DefaultFeatureGateService : IFeatureGateService
{
    private static readonly IReadOnlySet<PlanFeature> EmptyFeatures = new HashSet<PlanFeature>().AsReadOnly();

    private readonly FeatureGateCache _cache;

    public DefaultFeatureGateService(FeatureGateCache cache)
    {
        _cache = cache;
    }

    public bool IsFeatureEnabled(string tenantId, PlanFeature feature)
        => _cache.Get(tenantId)?.Features.Contains(feature) ?? false;

    public IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId)
        => _cache.Get(tenantId)?.Features ?? EmptyFeatures;

    public int GetMaxChannels(string tenantId)
        => _cache.Get(tenantId)?.MaxChannels ?? PlanDefinition.GetMaxChannels(TenantPlan.Starter);

    public int GetAuditRetentionDays(string tenantId)
        => _cache.Get(tenantId)?.AuditRetentionDays ?? PlanDefinition.GetAuditRetentionDays(TenantPlan.Starter);

    public int GetMaxWebhookSubscriptions(string tenantId)
        => _cache.Get(tenantId)?.MaxWebhookSubscriptions ?? PlanDefinition.GetMaxWebhookSubscriptions(TenantPlan.Starter);

    public int GetMaxScheduledReports(string tenantId)
        => _cache.Get(tenantId)?.MaxScheduledReports ?? PlanDefinition.GetMaxScheduledReports(TenantPlan.Starter);
}
```

- [ ] **Step 4: Run tests**

```sh
dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "FeatureGateServiceTests"
```

Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```sh
git add src/Asterisk.Platform.Api/Services/FeatureGateCache.cs src/Asterisk.Platform.Api/Services/DefaultFeatureGateService.cs tests/Asterisk.Platform.Api.Tests/FeatureGateServiceTests.cs
git commit -m "feat: add FeatureGateCache and DefaultFeatureGateService"
```

---

## Task 8: Platform.Api — TenantStatusMiddleware Expansion + RequirePlanFeature Filter

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/PlanFeatureFilterTests.cs`
- Modify: `tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs`

- [ ] **Step 1: Write expanded middleware tests**

Add to `tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs`:

First, update `CreateServiceProvider()` to also provide `FeatureGateCache` and `ITenantAddOnStore`:

```csharp
private readonly FeatureGateCache _featureGateCache = new();
private readonly ITenantAddOnStore _addOnStore = Substitute.For<ITenantAddOnStore>();
```

Update `CreateServiceProvider()`:
```csharp
private IServiceProvider CreateServiceProvider()
{
    var sp = Substitute.For<IServiceProvider>();
    sp.GetService(typeof(ITenantStore)).Returns(_tenantStore);
    sp.GetService(typeof(TenantTierCache)).Returns(_tierCache);
    sp.GetService(typeof(FeatureGateCache)).Returns(_featureGateCache);
    sp.GetService(typeof(ITenantAddOnStore)).Returns(_addOnStore);
    return sp;
}
```

Add the `using` statements:
```csharp
using Asterisk.Platform.Api.Services;
```

Default the add-on store to return empty:
```csharp
// In constructor or field initializer, add:
// _addOnStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<TenantAddOn>());
// OR set it up in each test that needs it.
```

Add new tests:

```csharp
[Fact]
public async Task Invoke_ShouldAddWarningHeader_WhenTenantWarning()
{
    _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Warning });
    _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<TenantAddOn>());
    var middleware = CreateMiddleware();
    var context = CreateContext("acme");

    await middleware.InvokeAsync(context);

    _nextCalled.Should().BeTrue();
    context.Response.Headers["X-Tenant-Warning"].ToString().Should().Be("payment_overdue");
}

[Fact]
public async Task Invoke_ShouldAddWarningHeaderAndForceSarter_WhenTenantDegraded()
{
    _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(new Tenant
        {
            TenantId = "acme", Name = "ACME", Status = TenantStatus.Degraded,
            Metadata = new() { ["Plan"] = "Enterprise" },
        });
    _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<TenantAddOn>());
    var middleware = CreateMiddleware();
    var context = CreateContext("acme");

    await middleware.InvokeAsync(context);

    _nextCalled.Should().BeTrue();
    context.Response.Headers["X-Tenant-Warning"].ToString().Should().Be("payment_overdue");
    // Feature gate cache should have Starter features (forced)
    _featureGateCache.Get("acme")!.EffectivePlan.Should().Be(TenantPlan.Starter);
}

[Fact]
public async Task Invoke_ShouldReturn403_WhenTenantPendingDeletion()
{
    _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.PendingDeletion });
    var middleware = CreateMiddleware();
    var context = CreateContext("acme");

    await middleware.InvokeAsync(context);

    _nextCalled.Should().BeFalse();
    context.Response.StatusCode.Should().Be(403);
}

[Fact]
public async Task Invoke_ShouldPopulateFeatureGateCache_WhenTenantActive()
{
    _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(new Tenant
        {
            TenantId = "acme", Name = "ACME", Status = TenantStatus.Active,
            Metadata = new() { ["Plan"] = "Pro" },
        });
    _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<TenantAddOn>());
    var middleware = CreateMiddleware();
    var context = CreateContext("acme");

    await middleware.InvokeAsync(context);

    var resolved = _featureGateCache.Get("acme");
    resolved.Should().NotBeNull();
    resolved!.EffectivePlan.Should().Be(TenantPlan.Pro);
    resolved.Features.Should().Contain(PlanFeature.Dialer);
}
```

- [ ] **Step 2: Expand TenantStatusMiddleware**

Replace the contents of `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs`:

```csharp
using System.Text.Json;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Middleware;

internal sealed class TenantStatusMiddleware
{
    private readonly RequestDelegate _next;

    public TenantStatusMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.TryGetValue("TenantId", out var tenantIdObj) || tenantIdObj is not TenantId tenantId)
        {
            await _next(context);
            return;
        }

        var tenantStore = context.RequestServices.GetRequiredService<ITenantStore>();
        var tenant = await tenantStore.GetAsync(tenantId.Value, context.RequestAborted);

        if (tenant is null)
        {
            await _next(context);
            return;
        }

        switch (tenant.Status)
        {
            case TenantStatus.Suspended:
                await WriteError(context, 403, "tenant_suspended",
                    "Tenant Suspended",
                    "This tenant account has been suspended. Contact your administrator.");
                return;

            case TenantStatus.PendingDeletion:
                await WriteError(context, 403, "tenant_pending_deletion",
                    "Tenant Pending Deletion",
                    "This tenant account is pending deletion due to prolonged non-payment. Contact your administrator immediately.");
                return;

            case TenantStatus.Deleted:
                await WriteError(context, 404, "tenant_not_found", "Not Found", "Not found");
                return;

            case TenantStatus.Warning:
            case TenantStatus.Degraded:
                context.Response.Headers["X-Tenant-Warning"] = "payment_overdue";
                context.Items["Tenant"] = tenant;
                await PopulateCaches(context, tenant);
                await _next(context);
                return;

            default: // Active or any unknown
                context.Items["Tenant"] = tenant;
                await PopulateCaches(context, tenant);
                await _next(context);
                return;
        }
    }

    private async Task PopulateCaches(HttpContext context, Tenant tenant)
    {
        // Rate limit tier cache
        var tierCache = context.RequestServices.GetService<TenantTierCache>();
        var plan = tenant.GetPlan();
        var effectiveTier = tenant.GetRateLimitTier() != RateLimitTier.Standard
            ? tenant.GetRateLimitTier()  // manual override exists
            : PlanDefinition.GetDefaultTier(plan);

        // If metadata has explicit RateLimitTier, use it; otherwise derive from plan
        var hasExplicitTier = tenant.Metadata?.ContainsKey("RateLimitTier") == true;
        var tier = hasExplicitTier ? tenant.GetRateLimitTier() : PlanDefinition.GetDefaultTier(plan);
        tierCache?.SetTier(tenant.TenantId, tier);

        // Feature gate cache
        var featureGateCache = context.RequestServices.GetService<FeatureGateCache>();
        if (featureGateCache is not null)
        {
            var effectivePlan = tenant.Status == TenantStatus.Degraded ? TenantPlan.Starter : plan;
            var features = new HashSet<PlanFeature>(PlanDefinition.GetFeatures(effectivePlan));

            // Add-ons (only when not degraded)
            if (tenant.Status != TenantStatus.Degraded)
            {
                var addOnStore = context.RequestServices.GetService<ITenantAddOnStore>();
                if (addOnStore is not null)
                {
                    var addOns = await addOnStore.GetAsync(tenant.TenantId, context.RequestAborted);
                    foreach (var addOn in addOns)
                        features.Add(addOn.Feature);
                }
            }

            // Hierarchy ceiling: intersect with parent plan if parent exists
            if (tenant.ParentTenantId is not null)
            {
                var parentTenant = await context.RequestServices.GetRequiredService<ITenantStore>()
                    .GetAsync(tenant.ParentTenantId, context.RequestAborted);
                if (parentTenant is not null)
                {
                    var parentFeatures = PlanDefinition.GetFeatures(parentTenant.GetPlan());
                    features.IntersectWith(parentFeatures);
                }
            }

            var resolved = new ResolvedFeatures(
                effectivePlan,
                features.AsReadOnly(),
                PlanDefinition.GetMaxChannels(effectivePlan),
                PlanDefinition.GetAuditRetentionDays(effectivePlan),
                PlanDefinition.GetMaxWebhookSubscriptions(effectivePlan),
                PlanDefinition.GetMaxScheduledReports(effectivePlan));

            featureGateCache.Set(tenant.TenantId, resolved);
        }
    }

    private static async Task WriteError(HttpContext context, int statusCode, string type, string title, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
            new ErrorResponse(detail),
            ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
    }
}
```

- [ ] **Step 3: Run middleware tests**

```sh
dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantStatusMiddlewareTests"
```

Expected: all tests pass (6 existing + 4 new = 10).

- [ ] **Step 4: Commit**

```sh
git add src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs
git commit -m "feat: expand TenantStatusMiddleware for Warning, Degraded, PendingDeletion + FeatureGateCache"
```

---

## Task 9: Platform.Api — TenantSettings Facade Expansion

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs`
- Modify: `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs`

- [ ] **Step 1: Expand TenantSettingsDto with Plan, Features, AddOns, Dunning**

In `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs`:

Add new DTO at the top (after existing DTOs):

```csharp
internal sealed record DunningStatusDto(
    string InvoiceId,
    string CurrentStage,
    DateTimeOffset StartedAt,
    DateTimeOffset? EscalatedAt,
    bool IsPaused);
```

Modify `TenantSettingsDto` to add new fields:

```csharp
internal sealed record TenantSettingsDto(
    string TenantId,
    string Name,
    string Type,
    string Status,
    OperationalSettingsDto Operational,
    AuthSettingsDto Auth,
    QuotaSettingsDto Quotas,
    RetentionSettingsDto Retention,
    RateLimitTier RateLimitTier,
    string Plan,
    IReadOnlyList<string> EnabledFeatures,
    IReadOnlyList<string> AddOns,
    DunningStatusDto? Dunning);
```

Modify `UpdateTenantSettingsRequest` to add Plan and AddOns:

```csharp
internal sealed record UpdateTenantSettingsRequest(
    UpdateOperationalSettingsDto? Operational = null,
    UpdateAuthSettingsDto? Auth = null,
    UpdateQuotaSettingsDto? Quotas = null,
    UpdateRetentionSettingsDto? Retention = null,
    RateLimitTier? RateLimitTier = null,
    TenantPlan? Plan = null,
    IReadOnlyList<PlanFeature>? AddOns = null);
```

- [ ] **Step 2: Update AdminOnly PUT to strip Plan and AddOns**

In the `UpdateSettings` handler (AdminOnly), change the sanitize line:

```csharp
var sanitized = body with { Quotas = null, RateLimitTier = null, Plan = null, AddOns = null };
```

- [ ] **Step 3: Update BuildSettingsDto to include new sections**

Modify `BuildSettingsDto` signature to accept additional stores:

```csharp
internal static async Task<TenantSettingsDto?> BuildSettingsDto(
    string tenantId,
    ITenantStore tenantStore,
    ITenantAuthConfigStore authConfigStore,
    ITenantQuotaStore quotaStore,
    ITenantRetentionPolicyStore retentionStore,
    ITenantAddOnStore? addOnStore,
    IDunningStore? dunningStore,
    IFeatureGateService? featureGateService,
    CancellationToken ct)
```

After loading the tenant, auth, quota, retention (existing code), add:

```csharp
var addOns = addOnStore is not null
    ? await addOnStore.GetAsync(tenantId, ct)
    : Array.Empty<TenantAddOn>();

var dunning = dunningStore is not null
    ? await dunningStore.GetActiveAsync(tenantId, ct)
    : null;

var plan = tenant.GetPlan();
var enabledFeatures = featureGateService?.GetEnabledFeatures(tenantId)
    ?? (IReadOnlySet<PlanFeature>)PlanDefinition.GetFeatures(plan);
```

Update the return statement to include the new fields:

```csharp
return new TenantSettingsDto(
    // ...existing fields...
    RateLimitTier: tenant.GetRateLimitTier(),
    Plan: plan.ToString(),
    EnabledFeatures: enabledFeatures.Select(f => f.ToString()).ToList(),
    AddOns: addOns.Select(a => a.Feature.ToString()).ToList(),
    Dunning: dunning is null ? null : new DunningStatusDto(
        InvoiceId: dunning.InvoiceId,
        CurrentStage: dunning.CurrentStage.ToString(),
        StartedAt: dunning.StartedAt,
        EscalatedAt: dunning.EscalatedAt,
        IsPaused: dunning.IsPaused));
```

- [ ] **Step 4: Update ApplyUpdates for Plan and AddOns**

Modify `ApplyUpdates` signature to accept additional stores:

```csharp
internal static async Task ApplyUpdates(
    string tenantId,
    UpdateTenantSettingsRequest body,
    ITenantStore tenantStore,
    ITenantAuthConfigStore authConfigStore,
    ITenantQuotaStore quotaStore,
    ITenantRetentionPolicyStore retentionStore,
    TenantTierCache? tierCache,
    FeatureGateCache? featureGateCache,
    ITenantAddOnStore? addOnStore,
    CancellationToken ct)
```

In the `Operational` section where the tenant is already loaded and updated, add Plan handling:

```csharp
if (body.Plan is { } newPlan)
{
    // Hierarchy ceiling: child cannot exceed parent's plan
    if (tenant.ParentTenantId is not null)
    {
        var parent = await tenantStore.GetAsync(tenant.ParentTenantId, ct);
        if (parent is not null && parent.GetPlan() < newPlan)
            throw new InvalidOperationException($"Cannot assign {newPlan} to tenant under a {parent.GetPlan()} partner");
    }

    newMetadata["Plan"] = newPlan.ToString();

    // Derive tier from plan if no manual override
    if (!newMetadata.ContainsKey("RateLimitTier"))
    {
        var derivedTier = PlanDefinition.GetDefaultTier(newPlan);
        tierCache?.SetTier(tenantId, derivedTier);
    }
}
```

Note: this needs to be inside the existing `if (body.Operational is not null || body.RateLimitTier is not null)` block, but the condition also needs to include `body.Plan is not null`. Update the condition:

```csharp
if (body.Operational is not null || body.RateLimitTier is not null || body.Plan is not null)
```

After the tenant upsert, add add-on management and cache invalidation:

```csharp
if (body.AddOns is not null && addOnStore is not null)
{
    var existingAddOns = await addOnStore.GetAsync(tenantId, ct);
    var requestedSet = body.AddOns.ToHashSet();
    var existingSet = existingAddOns.Select(a => a.Feature).ToHashSet();

    // Add new
    foreach (var feature in requestedSet.Except(existingSet))
    {
        await addOnStore.UpsertAsync(new TenantAddOn
        {
            TenantId = tenantId,
            Feature = feature,
            EnabledAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    // Remove old
    foreach (var feature in existingSet.Except(requestedSet))
        await addOnStore.DeleteAsync(tenantId, feature, ct);
}

// Invalidate feature cache after plan/add-on changes
if (body.Plan is not null || body.AddOns is not null)
    featureGateCache?.Remove(tenantId);
```

- [ ] **Step 5: Update GetSettings and UpdateSettings handlers in TenantSettingsEndpoints**

Update the `GetSettings` handler parameters:

```csharp
private static async Task<IResult> GetSettings(
    HttpContext context,
    [FromServices] ITenantStore tenantStore,
    [FromServices] ITenantAuthConfigStore authConfigStore,
    [FromServices] ITenantQuotaStore quotaStore,
    [FromServices] ITenantRetentionPolicyStore retentionStore,
    [FromServices] ITenantAddOnStore addOnStore,
    [FromServices] IDunningStore dunningStore,
    [FromServices] IFeatureGateService featureGateService,
    CancellationToken ct)
```

Pass the new stores to `BuildSettingsDto`. Do the same for `UpdateSettings` (add `FeatureGateCache` and `ITenantAddOnStore` params, pass to `ApplyUpdates`).

- [ ] **Step 6: Update ManagementTenantSettingsEndpoints handlers**

Same parameter additions for `GetSettings` and `UpdateSettings` in `ManagementTenantSettingsEndpoints.cs`. These handlers delegate to the shared `BuildSettingsDto` and `ApplyUpdates`, so just add the new parameter passing.

- [ ] **Step 7: Write tests for expanded facade**

Add to `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs`:

```csharp
[Fact]
public async Task GetSettings_ShouldIncludePlanAndFeatures()
{
    // Setup tenant with Plan=Pro in metadata
    // Assert response includes Plan="Pro", EnabledFeatures contains "Dialer"
}

[Fact]
public async Task UpdateSettings_ShouldStripPlanAndAddOns_WhenAdminOnly()
{
    // Send PUT with Plan=Enterprise to AdminOnly endpoint
    // Assert Plan is NOT changed (stripped)
}

[Fact]
public async Task UpdateSettings_ShouldRejectHigherPlanThanParent()
{
    // Create child tenant under Pro parent
    // Try to set child plan to Enterprise via management endpoint
    // Assert 400 or appropriate error
}

[Fact]
public async Task UpdateSettings_ShouldUpdateAddOns_WhenPlatformAdmin()
{
    // Send PUT with AddOns=["Dialer"] to management endpoint
    // Assert add-on is stored and returned in subsequent GET
}
```

These tests need full implementations with mocks — follow the existing test patterns in the file. Set up mock stores, create test tenants with metadata, and verify the results.

- [ ] **Step 8: Run tests**

```sh
dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantSettingsEndpoint"
```

- [ ] **Step 9: Commit**

```sh
git add src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs
git commit -m "feat: expand TenantSettings facade with Plan, AddOns, EnabledFeatures, Dunning"
```

---

## Task 10: Platform.Api — Dunning Endpoints, ApiJsonContext, Program.cs Wiring

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Add dunning endpoints to ManagementBillingEndpoints**

Read `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` and add a dunning group inside `MapManagementBillingEndpoints`:

```csharp
// Add inside the endpoint mapping method:
var dunning = app.MapGroup("/management/tenants/{id}")
    .RequireAuthorization("PlatformAdminOnly");

dunning.MapGet("/dunning", GetDunning);
dunning.MapPost("/dunning/pause", PauseDunning);
```

Add invoice payment resolution — modify the existing `IssueInvoice` handler (or add a new `PayInvoice` endpoint):

```csharp
invoices.MapPost("/{invoiceId}/pay", PayInvoice);
```

Add the handler methods:

```csharp
private static async Task<IResult> GetDunning(
    string id,
    [FromServices] IDunningStore dunningStore,
    CancellationToken ct)
{
    var record = await dunningStore.GetActiveAsync(id, ct);
    return record is null
        ? Results.NotFound()
        : Results.Ok(new DunningRecordDto(
            record.DunningId,
            record.TenantId,
            record.InvoiceId,
            record.CurrentStage.ToString(),
            record.StartedAt,
            record.EscalatedAt,
            record.ResolvedAt,
            record.IsPaused,
            record.IsActive));
}

private static async Task<IResult> PauseDunning(
    string id,
    [FromServices] IDunningStore dunningStore,
    CancellationToken ct)
{
    var record = await dunningStore.GetActiveAsync(id, ct);
    if (record is null)
        return Results.NotFound();

    record.IsPaused = !record.IsPaused;
    await dunningStore.UpsertAsync(record, ct);
    return Results.Ok(new DunningRecordDto(
        record.DunningId,
        record.TenantId,
        record.InvoiceId,
        record.CurrentStage.ToString(),
        record.StartedAt,
        record.EscalatedAt,
        record.ResolvedAt,
        record.IsPaused,
        record.IsActive));
}

private static async Task<IResult> PayInvoice(
    string invoiceId,
    [FromServices] IInvoiceStore invoiceStore,
    [FromServices] IDunningStore dunningStore,
    [FromServices] ITenantStore tenantStore,
    [FromServices] TenantTierCache tierCache,
    [FromServices] FeatureGateCache featureGateCache,
    CancellationToken ct)
{
    var invoice = await invoiceStore.GetByIdAsync(new EntityId(invoiceId), ct);
    if (invoice is null)
        return Results.NotFound();

    invoice.Status = InvoiceStatus.Paid;
    invoice.PaymentStatus = PaymentStatus.Current;
    invoice.PaidAt = DateTimeOffset.UtcNow;
    await invoiceStore.SaveAsync(invoice, ct);

    // Resolve dunning
    var dunning = await dunningStore.GetByInvoiceAsync(invoiceId, ct);
    if (dunning is not null)
    {
        dunning.IsActive = false;
        dunning.ResolvedAt = DateTimeOffset.UtcNow;
        await dunningStore.UpsertAsync(dunning, ct);

        // Restore tenant to Active
        await tenantStore.UpdateStatusAsync(dunning.TenantId, TenantStatus.Active, ct);

        // Invalidate caches (force re-resolution on next request)
        tierCache.Remove(dunning.TenantId);
        featureGateCache.Remove(dunning.TenantId);
    }

    return Results.Ok(new MessageResponse("Invoice marked as paid"));
}
```

Add the DTO at the bottom of the file:

```csharp
internal sealed record DunningRecordDto(
    string DunningId,
    string TenantId,
    string InvoiceId,
    string CurrentStage,
    DateTimeOffset StartedAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? ResolvedAt,
    bool IsPaused,
    bool IsActive);
```

Also update existing `InvoiceDto` to include `PaymentStatus` and `DueDate`:

```csharp
internal sealed record InvoiceDto(
    string InvoiceId, string TenantId, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    string Currency, IReadOnlyList<InvoiceLineItemDto> LineItems,
    decimal Subtotal, decimal Tax, decimal Total,
    string Status, string PaymentStatus, DateTimeOffset? DueDate,
    DateTimeOffset GeneratedAt, DateTimeOffset? IssuedAt, DateTimeOffset? PaidAt);
```

Update the DTO mapping helper for invoices to include the new fields.

- [ ] **Step 2: Register new types in ApiJsonContext**

In `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`, add:

```csharp
[JsonSerializable(typeof(DunningStatusDto))]
[JsonSerializable(typeof(DunningRecordDto))]
[JsonSerializable(typeof(TenantPlan))]
[JsonSerializable(typeof(PlanFeature))]
[JsonSerializable(typeof(IReadOnlyList<PlanFeature>))]
[JsonSerializable(typeof(PaymentStatus))]
```

- [ ] **Step 3: Wire everything in Program.cs**

Add these registrations and changes to `src/Asterisk.Platform.Api/Program.cs`:

**DI registrations** (add near existing TenantTierCache registration):

```csharp
builder.Services.AddSingleton<FeatureGateCache>();
builder.Services.AddSingleton<IFeatureGateService, DefaultFeatureGateService>();
builder.Services.Configure<DunningConfig>(builder.Configuration.GetSection("Dunning"));
builder.Services.AddHostedService<DunningService>();
```

Add `using` statements for new types:

```csharp
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
```

**RequirePlanFeature on endpoint groups** — after each endpoint group mapping call, add the filter. In the endpoint mapping section:

```csharp
// Find the existing endpoint group mappings and add RequirePlanFeature.
// Example: where campaign endpoints are mapped:
// v1.MapCampaignEndpoints();  // Already exists
// Add after it (or modify to chain):
```

The simplest approach: create a helper method in Program.cs or a separate file that applies feature gates to the existing endpoint groups. Since endpoint groups are mapped as extension methods that return `void`, we need to modify each `Map*Endpoints()` method to apply the filter internally, OR apply it in Program.cs.

The cleanest approach is to add `RequirePlanFeature` inside each endpoint group's `Map*Endpoints()` method. But that changes many files. Instead, create a new extension method:

Create an inline helper in Program.cs or a new file `src/Asterisk.Platform.Api/Endpoints/PlanFeatureGateExtensions.cs`:

```csharp
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Endpoints;

internal static class PlanFeatureGateExtensions
{
    public static IEndpointRouteBuilder ApplyPlanFeatureGates(this IEndpointRouteBuilder app)
    {
        // Feature gates are checked as endpoint filters on route groups.
        // Since groups are already mapped, we use endpoint metadata + a middleware approach.
        // The RequirePlanFeature is applied per endpoint group via AddEndpointFilter.
        return app;
    }

    public static RouteGroupBuilder RequirePlanFeature(this RouteGroupBuilder group, PlanFeature feature)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var tenantId = httpContext.Items.TryGetValue("TenantId", out var tid) ? (tid as TenantId)?.Value : null;

            if (tenantId is null)
                return await next(context);

            // Platform tenant bypasses feature gates
            var tenant = httpContext.Items["Tenant"] as Tenant;
            if (tenant?.Type == TenantType.Platform)
                return await next(context);

            var featureGate = httpContext.RequestServices.GetService<IFeatureGateService>();
            if (featureGate is null || featureGate.IsFeatureEnabled(tenantId, feature))
                return await next(context);

            var plan = httpContext.RequestServices.GetService<FeatureGateCache>()?.Get(tenantId)?.EffectivePlan ?? TenantPlan.Starter;

            return Results.Json(new
            {
                type = "feature_not_available",
                title = "Feature Not Available",
                detail = $"This feature is not available on your current plan ({plan}). Upgrade to access this feature.",
            }, statusCode: 403);
        });

        return group;
    }
}
```

Then modify each `Map*Endpoints()` to add the feature gate. The endpoint groups that need gating (per spec §1.9):

| Method | Feature |
|--------|---------|
| `MapCampaignEndpoints` | Dialer |
| `MapCallAttemptEndpoints` | Dialer |
| `MapDncListEndpoints` | Dialer |
| `MapCallerIdPoolEndpoints` | Dialer |
| `MapHolidayCalendarEndpoints` | Dialer |
| `MapDialerSettingsEndpoints` | Dialer |
| `MapBotEndpoints` | BotBasic |
| `MapAgentAssistEndpoints` | AgentAssist |
| `MapFlowEndpoints` | Flows |
| `MapWebhookSubscriptionEndpoints` | Webhooks |
| `MapOidcEndpoints` | OidcSso |
| `MapScheduledReportEndpoints` | ScheduledReports |
| `MapKnowledgeBaseEndpoints` | KnowledgeBase |
| `MapRecordingEndpoints` | Recordings |

For each, read the endpoint file, find the `MapGroup()` call, and chain `.RequirePlanFeature(PlanFeature.X)` after any existing `RequireAuthorization()`:

```csharp
// Example in CampaignEndpoints.cs:
var group = app.MapGroup("/admin/campaigns")
    .RequireAuthorization("AdminOnly")
    .RequirePlanFeature(PlanFeature.Dialer);
```

If the `Map*Endpoints` uses `IEndpointRouteBuilder` instead of returning a `RouteGroupBuilder`, you may need to capture the group variable and chain the filter. Read each file to determine the exact pattern.

- [ ] **Step 4: Build full solution**

```sh
dotnet build Asterisk.Platform.slnx
```

Fix any compilation errors (missing usings, parameter mismatches).

- [ ] **Step 5: Run all tests**

```sh
dotnet test Asterisk.Platform.slnx
```

All tests should pass. If existing tests break due to the `BuildSettingsDto` or `ApplyUpdates` signature changes, update them to pass the new required parameters (use `null` for optional stores in test setups where not needed).

- [ ] **Step 6: Verify test count**

The test output should show the total. Expected: ~1440+ tests (1410 existing + ~30 new).

- [ ] **Step 7: Commit**

```sh
git add src/Asterisk.Platform.Api/ tests/Asterisk.Platform.Api.Tests/
git commit -m "feat: add dunning endpoints, RequirePlanFeature filter, wire Sprint 2 in Program.cs"
```

- [ ] **Step 8: Update CLAUDE.md with Sprint 2 section**

Add Sprint 2 documentation to `CLAUDE.md` after the Sprint 1 section:

```markdown
## Sprint 2: Feature Flags Per-Tenant + Billing-Lifecycle Dunning -- COMPLETE

**Spec:** `docs/superpowers/specs/2026-04-07-sprint2-features-dunning-design.md`
**Plan:** `docs/superpowers/plans/2026-04-07-sprint2-features-dunning.md`

Three deliverables:
1. **Tenant Plans + Feature Flags** -- TenantPlan (Starter/Pro/Enterprise) stored in Metadata, PlanFeature (13 flags), PlanDefinition (static mapping), TenantAddOn (on/off), IFeatureGateService, FeatureGateCache, RequirePlanFeature endpoint filter on 14 endpoint groups. Hierarchical inheritance: child cannot exceed parent plan.
2. **Billing-Lifecycle Dunning** -- DunningService (IHostedService, 6h interval) monitors overdue invoices. Progressive escalation: Warning (day 0) → Degraded (day 7, forced Starter features) → Suspended (day 14) → PendingDeletion (day 30). PaymentStatus on Invoice. Pause/resume via management API.
3. **TenantSettings Facade Expansion** -- Plan, EnabledFeatures, AddOns, Dunning sections added to GET/PUT settings. PlatformAdminOnly can set plan and add-ons. Hierarchy validation on plan assignment.
```

Update test count and endpoint group count as appropriate.

- [ ] **Step 9: Final commit**

```sh
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with Sprint 2 deliverables"
```

---

## Self-Review Checklist

**Spec coverage:**
- §1.1 TenantPlan enum → Task 2 ✓
- §1.2 PlanFeature enum → Task 2 ✓
- §1.3 PlanDefinition → Task 2 ✓
- §1.4 TenantExtensions GetPlan/SetPlan → Task 3 ✓
- §1.5 TenantAddOn → Task 3 ✓
- §1.6 ITenantAddOnStore → Task 3 ✓
- §1.7 IFeatureGateService → Task 3 ✓
- §1.8 FeatureGateCache + DefaultFeatureGateService → Task 7 ✓
- §1.9 RequirePlanFeature filter → Task 10 ✓
- §1.10 Hierarchical enforcement → Task 9 (ApplyUpdates) ✓
- §2.1 TenantStatus new values → Task 1 ✓
- §2.2 PaymentStatus + DueDate → Task 4 ✓
- §2.3 DunningConfig → Task 4 ✓
- §2.4 DunningRecord → Task 4 ✓
- §2.5 IDunningStore → Task 4 ✓
- §2.6 DunningService → Task 5 ✓
- §2.7 Dunning resolution → Task 10 (PayInvoice) ✓
- §2.8 TenantStatusMiddleware expansion → Task 8 ✓
- §2.9 Management dunning endpoints → Task 10 ✓
- §3.1-3.4 TenantSettings expansion → Task 9 ✓

**Placeholder scan:** No TBD/TODO found.

**Type consistency:** All types defined in earlier tasks match usage in later tasks. `ResolvedFeatures` defined in Task 7, used in Tasks 8-10. `DunningRecordDto` defined and used in Task 10. `UpdateTenantSettingsRequest` expanded in Task 9 with Plan/AddOns fields.
