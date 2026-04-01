# Plan 28A: Metering Engine + Quota Enforcement — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the `Asterisk.Platform.Billing` package with metering engine (usage recording + summaries) and quota enforcement (tenant limits checked at runtime), plus InMemory and Postgres storage implementations.

**Architecture:** New feature package following Audit pattern — domain models + interfaces + services in `Platform.Billing`, InMemory stores in `Storage.InMemory`, Postgres stores in `Storage.Postgres`. Services registered via `AddPlatformBilling()`. TenantQuota is a standalone model (not on TenantOptions) stored per-tenant. QuotaEnforcementService reads current-period summaries from IUsageRecordStore + TenantQuota limits from ITenantQuotaStore.

**Tech Stack:** .NET 10, AOT-compatible, xUnit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0, Dapper 2.1.66, Npgsql 9.0.3

---

## File Map

### New Files (Platform.Billing package)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj` | Package definition, refs Core only |
| `src/Asterisk.Platform.Billing/UsageType.cs` | 16-value enum of billable consumption types |
| `src/Asterisk.Platform.Billing/UsageUnit.cs` | 6-value enum of measurement units |
| `src/Asterisk.Platform.Billing/UsageRecord.cs` | Individual consumption event model |
| `src/Asterisk.Platform.Billing/UsageSummary.cs` | Aggregated per tenant/period/type |
| `src/Asterisk.Platform.Billing/TenantQuota.cs` | Enforced limits per tenant + QuotaAction enum + QuotaCheckResult |
| `src/Asterisk.Platform.Billing/IUsageRecordStore.cs` | Persistence interface for usage records + summaries |
| `src/Asterisk.Platform.Billing/ITenantQuotaStore.cs` | Persistence interface for tenant quotas |
| `src/Asterisk.Platform.Billing/IMeteringService.cs` | High-level metering interface |
| `src/Asterisk.Platform.Billing/IQuotaEnforcementService.cs` | Quota check interface + TenantQuotaStatus |
| `src/Asterisk.Platform.Billing/DefaultMeteringService.cs` | Implementation: record usage via store + clock |
| `src/Asterisk.Platform.Billing/DefaultQuotaEnforcementService.cs` | Implementation: check limits against current-period summaries |
| `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs` | `AddPlatformBilling()` DI registration |

### New Files (Storage)

| File | Responsibility |
|------|----------------|
| `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs` | ConcurrentDictionary-backed usage records |
| `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantQuotaStore.cs` | ConcurrentDictionary-backed quotas |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs` | Dapper/Npgsql usage records |
| `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs` | Dapper/Npgsql quotas |
| `src/Asterisk.Platform.Storage.Postgres/Migrations/002_BillingSchema.sql` | DDL for usage_records + tenant_quotas tables |

### New Files (Tests)

| File | Responsibility |
|------|----------------|
| `tests/Asterisk.Platform.Billing.Tests/Asterisk.Platform.Billing.Tests.csproj` | Test project |
| `tests/Asterisk.Platform.Billing.Tests/GlobalUsings.cs` | Global usings |
| `tests/Asterisk.Platform.Billing.Tests/UsageRecordTests.cs` | Model property tests |
| `tests/Asterisk.Platform.Billing.Tests/TenantQuotaTests.cs` | Model + defaults + QuotaCheckResult tests |
| `tests/Asterisk.Platform.Billing.Tests/DefaultMeteringServiceTests.cs` | Service logic tests |
| `tests/Asterisk.Platform.Billing.Tests/DefaultQuotaEnforcementServiceTests.cs` | Quota check logic tests |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs` | Store behavior tests |
| `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryTenantQuotaStoreTests.cs` | Store behavior tests |

### Modified Files

| File | Change |
|------|--------|
| `Asterisk.Platform.slnx` | Add Billing + Billing.Tests projects |
| `src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj` | Add Billing ProjectReference |
| `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` | Register billing stores |
| `src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj` | Add Billing ProjectReference |
| `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` | Register billing stores |
| `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs` | Add `[JsonSerializable]` for `Dictionary<string, string>?` (metadata) |
| `src/Asterisk.Platform.Api/Program.cs` | Add `using`, call `AddPlatformBilling()` |

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj`
- Create: `tests/Asterisk.Platform.Billing.Tests/Asterisk.Platform.Billing.Tests.csproj`
- Create: `tests/Asterisk.Platform.Billing.Tests/GlobalUsings.cs`
- Modify: `Asterisk.Platform.slnx`

- [ ] **Step 1: Create the Billing package csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <Description>Metering engine and quota enforcement for Asterisk.Platform — usage recording, tenant quotas, and billing abstractions</Description>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Asterisk.Platform.Billing.Tests" />
    <InternalsVisibleTo Include="DynamicProxyGenAssembly2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Asterisk.Platform.Core\Asterisk.Platform.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  </ItemGroup>

</Project>
```

Save to `src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj`.

- [ ] **Step 2: Create the Billing test project csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\..\src\Asterisk.Platform.Billing\Asterisk.Platform.Billing.csproj" />
    <ProjectReference Include="..\..\src\Asterisk.Platform.Storage.InMemory\Asterisk.Platform.Storage.InMemory.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

</Project>
```

Save to `tests/Asterisk.Platform.Billing.Tests/Asterisk.Platform.Billing.Tests.csproj`.

- [ ] **Step 3: Create GlobalUsings.cs**

```csharp
global using FluentAssertions;
global using NSubstitute;
global using Xunit;
```

Save to `tests/Asterisk.Platform.Billing.Tests/GlobalUsings.cs`.

- [ ] **Step 4: Add projects to solution**

```xml
<!-- In /src/ folder, after Audit line -->
<Project Path="src/Asterisk.Platform.Billing/Asterisk.Platform.Billing.csproj" />

<!-- In /tests/ folder, after Audit.Tests line -->
<Project Path="tests/Asterisk.Platform.Billing.Tests/Asterisk.Platform.Billing.Tests.csproj" />
```

Add these two lines to `Asterisk.Platform.slnx` in the appropriate `<Folder>` sections.

- [ ] **Step 5: Add Billing ProjectReference to Storage.InMemory.csproj**

Add after the existing Audit reference in `src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj`:

```xml
<ProjectReference Include="..\Asterisk.Platform.Billing\Asterisk.Platform.Billing.csproj" />
```

- [ ] **Step 6: Add Billing ProjectReference to Storage.Postgres.csproj**

Add after the existing Audit reference in `src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj`:

```xml
<ProjectReference Include="..\Asterisk.Platform.Billing\Asterisk.Platform.Billing.csproj" />
```

- [ ] **Step 7: Verify build**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 8: Commit**

```
feat(billing): scaffold Platform.Billing package and test project
```

---

## Task 2: Domain Models — Enums

**Files:**
- Create: `src/Asterisk.Platform.Billing/UsageType.cs`
- Create: `src/Asterisk.Platform.Billing/UsageUnit.cs`

- [ ] **Step 1: Create UsageType enum**

```csharp
namespace Asterisk.Platform.Billing;

/// <summary>
/// Classifies the type of billable consumption event.
/// </summary>
public enum UsageType
{
    VoiceInbound,
    VoiceOutbound,
    SmsInbound,
    SmsOutbound,
    WhatsAppInbound,
    WhatsAppOutbound,
    EmailInbound,
    EmailOutbound,
    WebChatSession,
    TelegramInbound,
    TelegramOutbound,
    RecordingStorage,
    MediaStorage,
    DialerAttempt,
    DialerConnected,
    AgentLoginHour,
    AiAnalysis,
}
```

Save to `src/Asterisk.Platform.Billing/UsageType.cs`.

- [ ] **Step 2: Create UsageUnit enum**

```csharp
namespace Asterisk.Platform.Billing;

/// <summary>
/// Measurement unit for a usage record.
/// </summary>
public enum UsageUnit
{
    Minutes,
    Segments,
    Conversations,
    Bytes,
    Count,
    Hours,
}
```

Save to `src/Asterisk.Platform.Billing/UsageUnit.cs`.

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Asterisk.Platform.Billing/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 4: Commit**

```
feat(billing): add UsageType and UsageUnit enums
```

---

## Task 3: Domain Models — UsageRecord + UsageSummary

**Files:**
- Create: `src/Asterisk.Platform.Billing/UsageRecord.cs`
- Create: `src/Asterisk.Platform.Billing/UsageSummary.cs`
- Create: `tests/Asterisk.Platform.Billing.Tests/UsageRecordTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class UsageRecordTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void UsageRecord_ShouldHoldAllProperties()
    {
        var id = EntityId.New();
        var now = DateTimeOffset.UtcNow;
        var meta = new Dictionary<string, string> { ["campaign"] = "c1" };

        var record = new UsageRecord
        {
            RecordId = id,
            TenantId = Tenant1,
            UsageType = UsageType.VoiceInbound,
            Quantity = 5.5m,
            Unit = UsageUnit.Minutes,
            Channel = "voice",
            ReferenceId = "call-123",
            RecordedAt = now,
            Metadata = meta,
        };

        record.RecordId.Should().Be(id);
        record.TenantId.Should().Be(Tenant1);
        record.UsageType.Should().Be(UsageType.VoiceInbound);
        record.Quantity.Should().Be(5.5m);
        record.Unit.Should().Be(UsageUnit.Minutes);
        record.Channel.Should().Be("voice");
        record.ReferenceId.Should().Be("call-123");
        record.RecordedAt.Should().Be(now);
        record.Metadata.Should().ContainKey("campaign").WhoseValue.Should().Be("c1");
    }

    [Fact]
    public void UsageRecord_ShouldAllowNullOptionalFields()
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = Tenant1,
            UsageType = UsageType.SmsOutbound,
            Quantity = 1m,
            Unit = UsageUnit.Segments,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        record.Channel.Should().BeNull();
        record.ReferenceId.Should().BeNull();
        record.Metadata.Should().BeNull();
    }

    [Fact]
    public void UsageRecord_ShouldImplementITenantScoped()
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = Tenant1,
            UsageType = UsageType.WebChatSession,
            Quantity = 1m,
            Unit = UsageUnit.Conversations,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        ITenantScoped scoped = record;
        scoped.TenantId.Should().Be(Tenant1);
    }
}

public class UsageSummaryTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void UsageSummary_ShouldHoldAllProperties()
    {
        var start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var updated = DateTimeOffset.UtcNow;

        var summary = new UsageSummary
        {
            TenantId = Tenant1,
            PeriodStart = start,
            PeriodEnd = end,
            UsageType = UsageType.VoiceInbound,
            TotalQuantity = 1234.5m,
            RecordCount = 42,
            LastUpdatedAt = updated,
        };

        summary.TenantId.Should().Be(Tenant1);
        summary.PeriodStart.Should().Be(start);
        summary.PeriodEnd.Should().Be(end);
        summary.UsageType.Should().Be(UsageType.VoiceInbound);
        summary.TotalQuantity.Should().Be(1234.5m);
        summary.RecordCount.Should().Be(42);
        summary.LastUpdatedAt.Should().Be(updated);
    }
}
```

Save to `tests/Asterisk.Platform.Billing.Tests/UsageRecordTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "UsageRecordTests|UsageSummaryTests" -v q`
Expected: FAIL — `UsageRecord` and `UsageSummary` types not found.

- [ ] **Step 3: Create UsageRecord model**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// An individual consumption event recorded for billing purposes.
/// </summary>
public sealed class UsageRecord : ITenantScoped
{
    public required EntityId RecordId { get; init; }
    public required TenantId TenantId { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal Quantity { get; init; }
    public required UsageUnit Unit { get; init; }
    public string? Channel { get; init; }
    public string? ReferenceId { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
```

Save to `src/Asterisk.Platform.Billing/UsageRecord.cs`.

- [ ] **Step 4: Create UsageSummary model**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Aggregated usage for a tenant within a specific time period and usage type.
/// </summary>
public sealed class UsageSummary : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required UsageType UsageType { get; init; }
    public required decimal TotalQuantity { get; set; }
    public required int RecordCount { get; set; }
    public required DateTimeOffset LastUpdatedAt { get; set; }
}
```

Save to `src/Asterisk.Platform.Billing/UsageSummary.cs`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "UsageRecordTests|UsageSummaryTests" -v q`
Expected: Passed! 4 tests.

- [ ] **Step 6: Commit**

```
feat(billing): add UsageRecord and UsageSummary domain models
```

---

## Task 4: Domain Models — TenantQuota + QuotaCheckResult

**Files:**
- Create: `src/Asterisk.Platform.Billing/TenantQuota.cs`
- Create: `tests/Asterisk.Platform.Billing.Tests/TenantQuotaTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class TenantQuotaTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void TenantQuota_ShouldHaveDefaults()
    {
        var quota = new TenantQuota { TenantId = Tenant1 };

        quota.MaxConcurrentChannels.Should().Be(100);
        quota.MaxActiveCampaigns.Should().Be(10);
        quota.MaxMonthlyVoiceMinutes.Should().BeNull();
        quota.MaxMonthlyMessages.Should().BeNull();
        quota.MaxStorageBytes.Should().BeNull();
        quota.MaxActiveAgents.Should().BeNull();
        quota.QuotaAction.Should().Be(QuotaAction.Warn);
    }

    [Fact]
    public void TenantQuota_ShouldAllowCustomLimits()
    {
        var quota = new TenantQuota
        {
            TenantId = Tenant1,
            MaxConcurrentChannels = 200,
            MaxActiveCampaigns = 50,
            MaxMonthlyVoiceMinutes = 10_000,
            MaxMonthlyMessages = 50_000,
            MaxStorageBytes = 10L * 1024 * 1024 * 1024,
            MaxActiveAgents = 100,
            QuotaAction = QuotaAction.HardBlock,
        };

        quota.MaxConcurrentChannels.Should().Be(200);
        quota.MaxActiveCampaigns.Should().Be(50);
        quota.MaxMonthlyVoiceMinutes.Should().Be(10_000);
        quota.MaxMonthlyMessages.Should().Be(50_000);
        quota.MaxStorageBytes.Should().Be(10L * 1024 * 1024 * 1024);
        quota.MaxActiveAgents.Should().Be(100);
        quota.QuotaAction.Should().Be(QuotaAction.HardBlock);
    }

    [Fact]
    public void TenantQuota_ShouldImplementITenantScoped()
    {
        ITenantScoped scoped = new TenantQuota { TenantId = Tenant1 };
        scoped.TenantId.Should().Be(Tenant1);
    }
}

public class QuotaCheckResultTests
{
    [Fact]
    public void QuotaCheckResult_ShouldHoldAllProperties()
    {
        var result = new QuotaCheckResult(true, null, 45.0);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.UsagePercent.Should().Be(45.0);
    }

    [Fact]
    public void QuotaCheckResult_ShouldRepresentDenied()
    {
        var result = new QuotaCheckResult(false, "Monthly voice minutes exceeded", 100.5);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be("Monthly voice minutes exceeded");
        result.UsagePercent.Should().Be(100.5);
    }
}
```

Save to `tests/Asterisk.Platform.Billing.Tests/TenantQuotaTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "TenantQuotaTests|QuotaCheckResultTests" -v q`
Expected: FAIL — types not found.

- [ ] **Step 3: Create TenantQuota model**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Enforced resource limits for a tenant.
/// </summary>
public sealed class TenantQuota : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public int MaxConcurrentChannels { get; set; } = 100;
    public int MaxActiveCampaigns { get; set; } = 10;
    public long? MaxMonthlyVoiceMinutes { get; set; }
    public long? MaxMonthlyMessages { get; set; }
    public long? MaxStorageBytes { get; set; }
    public int? MaxActiveAgents { get; set; }
    public QuotaAction QuotaAction { get; set; } = QuotaAction.Warn;
}

/// <summary>
/// What happens when a quota limit is reached.
/// </summary>
public enum QuotaAction
{
    Warn,
    SoftBlock,
    HardBlock,
}

/// <summary>
/// Result of a quota check for a specific usage type.
/// </summary>
public sealed record QuotaCheckResult(bool Allowed, string? Reason, double UsagePercent);
```

Save to `src/Asterisk.Platform.Billing/TenantQuota.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "TenantQuotaTests|QuotaCheckResultTests" -v q`
Expected: Passed! 5 tests.

- [ ] **Step 5: Commit**

```
feat(billing): add TenantQuota, QuotaAction, and QuotaCheckResult models
```

---

## Task 5: Store Interfaces

**Files:**
- Create: `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`
- Create: `src/Asterisk.Platform.Billing/ITenantQuotaStore.cs`

- [ ] **Step 1: Create IUsageRecordStore**

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
    Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Returns the aggregated summary for a specific usage type within a date range.</summary>
    Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
```

Save to `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`.

- [ ] **Step 2: Create ITenantQuotaStore**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for tenant quota configurations.
/// </summary>
public interface ITenantQuotaStore
{
    /// <summary>Gets the quota for a tenant, or null if not configured.</summary>
    Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Creates or updates the quota for a tenant.</summary>
    Task UpsertAsync(TenantQuota quota, CancellationToken ct);

    /// <summary>Deletes the quota for a tenant.</summary>
    Task DeleteAsync(TenantId tenantId, CancellationToken ct);
}
```

Save to `src/Asterisk.Platform.Billing/ITenantQuotaStore.cs`.

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Asterisk.Platform.Billing/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 4: Commit**

```
feat(billing): add IUsageRecordStore and ITenantQuotaStore interfaces
```

---

## Task 6: Service Interfaces

**Files:**
- Create: `src/Asterisk.Platform.Billing/IMeteringService.cs`
- Create: `src/Asterisk.Platform.Billing/IQuotaEnforcementService.cs`

- [ ] **Step 1: Create IMeteringService**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Records consumption events for billing and metering purposes.
/// </summary>
public interface IMeteringService
{
    /// <summary>Records a single usage event.</summary>
    Task RecordUsageAsync(TenantId tenantId, UsageType type, decimal quantity, UsageUnit unit, string? channel, string? referenceId, CancellationToken ct);

    /// <summary>Records a batch of pre-built usage records.</summary>
    Task RecordBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);

    /// <summary>Returns aggregated summaries for the current billing period (calendar month).</summary>
    Task<IReadOnlyList<UsageSummary>> GetCurrentPeriodSummaryAsync(TenantId tenantId, CancellationToken ct);
}
```

Save to `src/Asterisk.Platform.Billing/IMeteringService.cs`.

- [ ] **Step 2: Create IQuotaEnforcementService + TenantQuotaStatus**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Checks tenant resource consumption against configured quota limits.
/// </summary>
public interface IQuotaEnforcementService
{
    /// <summary>Checks whether the tenant can consume additional units of the specified type.</summary>
    Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct);

    /// <summary>Returns an overview of the tenant's quota usage across all metered types.</summary>
    Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct);
}

/// <summary>
/// Overall quota status for a tenant, with per-type breakdown.
/// </summary>
public sealed record TenantQuotaStatus(
    TenantId TenantId,
    TenantQuota? Quota,
    IReadOnlyList<UsageSummary> CurrentUsage);
```

Save to `src/Asterisk.Platform.Billing/IQuotaEnforcementService.cs`.

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Asterisk.Platform.Billing/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 4: Commit**

```
feat(billing): add IMeteringService and IQuotaEnforcementService interfaces
```

---

## Task 7: DefaultMeteringService

**Files:**
- Create: `src/Asterisk.Platform.Billing/DefaultMeteringService.cs`
- Create: `tests/Asterisk.Platform.Billing.Tests/DefaultMeteringServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class DefaultMeteringServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

    private static (DefaultMeteringService Service, IUsageRecordStore Store, IClock Clock) Build()
    {
        var store = Substitute.For<IUsageRecordStore>();
        store.SaveAsync(Arg.Any<UsageRecord>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);
        store.SaveBatchAsync(Arg.Any<IReadOnlyList<UsageRecord>>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultMeteringService(store, clock);
        return (service, store, clock);
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldSaveRecord_WithCorrectFields()
    {
        var (service, store, _) = Build();

        await service.RecordUsageAsync(Tenant1, UsageType.VoiceInbound, 3.5m, UsageUnit.Minutes, "voice", "call-1", CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<UsageRecord>(r =>
                r.TenantId == Tenant1 &&
                r.UsageType == UsageType.VoiceInbound &&
                r.Quantity == 3.5m &&
                r.Unit == UsageUnit.Minutes &&
                r.Channel == "voice" &&
                r.ReferenceId == "call-1" &&
                r.RecordedAt == FixedNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldGenerateUniqueRecordId()
    {
        var capturedIds = new List<string>();
        var store = Substitute.For<IUsageRecordStore>();
        store.SaveAsync(Arg.Do<UsageRecord>(r => capturedIds.Add(r.RecordId.Value)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);
        var service = new DefaultMeteringService(store, clock);

        await service.RecordUsageAsync(Tenant1, UsageType.SmsOutbound, 1m, UsageUnit.Segments, null, null, CancellationToken.None);
        await service.RecordUsageAsync(Tenant1, UsageType.SmsOutbound, 1m, UsageUnit.Segments, null, null, CancellationToken.None);

        capturedIds.Should().HaveCount(2);
        capturedIds[0].Should().NotBe(capturedIds[1]);
    }

    [Fact]
    public async Task RecordBatchAsync_ShouldDelegateToStore()
    {
        var (service, store, _) = Build();
        var records = new List<UsageRecord>
        {
            new()
            {
                RecordId = EntityId.New(),
                TenantId = Tenant1,
                UsageType = UsageType.SmsInbound,
                Quantity = 1m,
                Unit = UsageUnit.Segments,
                RecordedAt = FixedNow,
            },
        };

        await service.RecordBatchAsync(records, CancellationToken.None);

        await store.Received(1).SaveBatchAsync(records, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentPeriodSummaryAsync_ShouldQueryCurrentMonth()
    {
        var (service, store, _) = Build();
        var expectedStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var expectedEnd = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var summaries = new List<UsageSummary>();
        store.GetSummaryAsync(Tenant1, expectedStart, expectedEnd, Arg.Any<CancellationToken>())
             .Returns(summaries);

        var result = await service.GetCurrentPeriodSummaryAsync(Tenant1, CancellationToken.None);

        result.Should().BeSameAs(summaries);
        await store.Received(1).GetSummaryAsync(Tenant1, expectedStart, expectedEnd, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordUsageAsync_ShouldAllowNullOptionalFields()
    {
        var (service, store, _) = Build();

        await service.RecordUsageAsync(Tenant1, UsageType.WebChatSession, 1m, UsageUnit.Conversations, null, null, CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<UsageRecord>(r => r.Channel == null && r.ReferenceId == null),
            Arg.Any<CancellationToken>());
    }
}
```

Save to `tests/Asterisk.Platform.Billing.Tests/DefaultMeteringServiceTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultMeteringServiceTests" -v q`
Expected: FAIL — `DefaultMeteringService` not found.

- [ ] **Step 3: Implement DefaultMeteringService**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Default implementation of <see cref="IMeteringService"/>.
/// Records usage events via <see cref="IUsageRecordStore"/>.
/// </summary>
public sealed class DefaultMeteringService : IMeteringService
{
    private readonly IUsageRecordStore _store;
    private readonly IClock _clock;

    public DefaultMeteringService(IUsageRecordStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public Task RecordUsageAsync(TenantId tenantId, UsageType type, decimal quantity, UsageUnit unit, string? channel, string? referenceId, CancellationToken ct)
    {
        var record = new UsageRecord
        {
            RecordId = EntityId.New(),
            TenantId = tenantId,
            UsageType = type,
            Quantity = quantity,
            Unit = unit,
            Channel = channel,
            ReferenceId = referenceId,
            RecordedAt = _clock.UtcNow,
        };

        return _store.SaveAsync(record, ct);
    }

    public Task RecordBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        return _store.SaveBatchAsync(records, ct);
    }

    public Task<IReadOnlyList<UsageSummary>> GetCurrentPeriodSummaryAsync(TenantId tenantId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var periodStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1);

        return _store.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);
    }
}
```

Save to `src/Asterisk.Platform.Billing/DefaultMeteringService.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultMeteringServiceTests" -v q`
Expected: Passed! 5 tests.

- [ ] **Step 5: Commit**

```
feat(billing): implement DefaultMeteringService with usage recording
```

---

## Task 8: DefaultQuotaEnforcementService

**Files:**
- Create: `src/Asterisk.Platform.Billing/DefaultQuotaEnforcementService.cs`
- Create: `tests/Asterisk.Platform.Billing.Tests/DefaultQuotaEnforcementServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing.Tests;

public class DefaultQuotaEnforcementServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static (DefaultQuotaEnforcementService Service, ITenantQuotaStore QuotaStore, IUsageRecordStore UsageStore) Build()
    {
        var quotaStore = Substitute.For<ITenantQuotaStore>();
        var usageStore = Substitute.For<IUsageRecordStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultQuotaEnforcementService(quotaStore, usageStore, clock);
        return (service, quotaStore, usageStore);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenNoQuotaConfigured()
    {
        var (service, quotaStore, _) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>()).Returns((TenantQuota?)null);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().Be(0);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenNoLimitForType()
    {
        var (service, quotaStore, _) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1 }); // no MaxMonthlyVoiceMinutes set

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().Be(0);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenUnderLimit()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000 });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 500m,
                RecordCount = 100,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().BeApproximately(51.0, 0.1); // (500+10)/1000 * 100
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDeny_WhenOverLimit_AndHardBlock()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.HardBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 995m,
                RecordCount = 200,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("VoiceInbound");
        result.UsagePercent.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldAllow_WhenOverLimit_AndWarnAction()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.Warn });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 995m,
                RecordCount = 200,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 10m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.Reason.Should().NotBeNull(); // warning reason still provided
        result.UsagePercent.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldDeny_WhenOverLimit_AndSoftBlock()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyMessages = 5000, QuotaAction = QuotaAction.SoftBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.SmsOutbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(new UsageSummary
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.SmsOutbound,
                TotalQuantity = 5000m,
                RecordCount = 5000,
                LastUpdatedAt = FixedNow,
            });

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.SmsOutbound, 1m, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("SmsOutbound");
    }

    [Fact]
    public async Task CheckQuotaAsync_ShouldHandleNoExistingUsage()
    {
        var (service, quotaStore, usageStore) = Build();
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>())
            .Returns(new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000, QuotaAction = QuotaAction.HardBlock });

        usageStore.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns((UsageSummary?)null);

        var result = await service.CheckQuotaAsync(Tenant1, UsageType.VoiceInbound, 5m, CancellationToken.None);

        result.Allowed.Should().BeTrue();
        result.UsagePercent.Should().BeApproximately(0.5, 0.01); // 5/1000 * 100
    }

    [Fact]
    public async Task GetQuotaStatusAsync_ShouldReturnQuotaAndUsage()
    {
        var (service, quotaStore, usageStore) = Build();
        var quota = new TenantQuota { TenantId = Tenant1, MaxMonthlyVoiceMinutes = 1000 };
        quotaStore.GetAsync(Tenant1, Arg.Any<CancellationToken>()).Returns(quota);

        var summaries = new List<UsageSummary>
        {
            new()
            {
                TenantId = Tenant1,
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                UsageType = UsageType.VoiceInbound,
                TotalQuantity = 500m,
                RecordCount = 100,
                LastUpdatedAt = FixedNow,
            },
        };
        usageStore.GetSummaryAsync(Tenant1, PeriodStart, PeriodEnd, Arg.Any<CancellationToken>())
            .Returns(summaries);

        var status = await service.GetQuotaStatusAsync(Tenant1, CancellationToken.None);

        status.TenantId.Should().Be(Tenant1);
        status.Quota.Should().BeSameAs(quota);
        status.CurrentUsage.Should().BeSameAs(summaries);
    }
}
```

Save to `tests/Asterisk.Platform.Billing.Tests/DefaultQuotaEnforcementServiceTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultQuotaEnforcementServiceTests" -v q`
Expected: FAIL — `DefaultQuotaEnforcementService` not found.

- [ ] **Step 3: Implement DefaultQuotaEnforcementService**

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Default implementation of <see cref="IQuotaEnforcementService"/>.
/// Checks tenant usage against configured quota limits.
/// </summary>
public sealed class DefaultQuotaEnforcementService : IQuotaEnforcementService
{
    private readonly ITenantQuotaStore _quotaStore;
    private readonly IUsageRecordStore _usageStore;
    private readonly IClock _clock;

    public DefaultQuotaEnforcementService(ITenantQuotaStore quotaStore, IUsageRecordStore usageStore, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(quotaStore);
        ArgumentNullException.ThrowIfNull(usageStore);
        ArgumentNullException.ThrowIfNull(clock);
        _quotaStore = quotaStore;
        _usageStore = usageStore;
        _clock = clock;
    }

    public async Task<QuotaCheckResult> CheckQuotaAsync(TenantId tenantId, UsageType type, decimal additionalQuantity, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        if (quota is null)
            return new QuotaCheckResult(true, null, 0);

        var limit = GetLimitForType(quota, type);
        if (limit is null)
            return new QuotaCheckResult(true, null, 0);

        var (periodStart, periodEnd) = GetCurrentPeriod();
        var summary = await _usageStore.GetSummaryByTypeAsync(tenantId, type, periodStart, periodEnd, ct);
        var currentUsage = summary?.TotalQuantity ?? 0m;
        var projectedUsage = currentUsage + additionalQuantity;
        var usagePercent = (double)(projectedUsage / limit.Value * 100m);

        if (projectedUsage <= limit.Value)
            return new QuotaCheckResult(true, null, usagePercent);

        var reason = $"{type} quota exceeded: {projectedUsage:F1}/{limit.Value} ({usagePercent:F1}%)";

        return quota.QuotaAction switch
        {
            QuotaAction.Warn => new QuotaCheckResult(true, reason, usagePercent),
            QuotaAction.SoftBlock => new QuotaCheckResult(false, reason, usagePercent),
            QuotaAction.HardBlock => new QuotaCheckResult(false, reason, usagePercent),
            _ => new QuotaCheckResult(true, reason, usagePercent),
        };
    }

    public async Task<TenantQuotaStatus> GetQuotaStatusAsync(TenantId tenantId, CancellationToken ct)
    {
        var quota = await _quotaStore.GetAsync(tenantId, ct);
        var (periodStart, periodEnd) = GetCurrentPeriod();
        var summaries = await _usageStore.GetSummaryAsync(tenantId, periodStart, periodEnd, ct);

        return new TenantQuotaStatus(tenantId, quota, summaries);
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetCurrentPeriod()
    {
        var now = _clock.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }

    private static long? GetLimitForType(TenantQuota quota, UsageType type) => type switch
    {
        UsageType.VoiceInbound or UsageType.VoiceOutbound => quota.MaxMonthlyVoiceMinutes,
        UsageType.SmsInbound or UsageType.SmsOutbound or
        UsageType.WhatsAppInbound or UsageType.WhatsAppOutbound or
        UsageType.EmailInbound or UsageType.EmailOutbound or
        UsageType.TelegramInbound or UsageType.TelegramOutbound => quota.MaxMonthlyMessages,
        UsageType.RecordingStorage or UsageType.MediaStorage => quota.MaxStorageBytes,
        _ => null,
    };
}
```

Save to `src/Asterisk.Platform.Billing/DefaultQuotaEnforcementService.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Billing.Tests/ --filter "DefaultQuotaEnforcementServiceTests" -v q`
Expected: Passed! 8 tests.

- [ ] **Step 5: Commit**

```
feat(billing): implement DefaultQuotaEnforcementService with limit checks
```

---

## Task 9: DI Registration — AddPlatformBilling

**Files:**
- Create: `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create AddPlatformBilling extension**

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Billing;

/// <summary>
/// DI registration extensions for Platform.Billing services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMeteringService"/> and <see cref="IQuotaEnforcementService"/>.
    /// Store implementations (<see cref="IUsageRecordStore"/>, <see cref="ITenantQuotaStore"/>) must be registered separately.
    /// </summary>
    public static IServiceCollection AddPlatformBilling(this IServiceCollection services)
    {
        services.AddSingleton<IMeteringService, DefaultMeteringService>();
        services.AddSingleton<IQuotaEnforcementService, DefaultQuotaEnforcementService>();
        return services;
    }
}
```

Save to `src/Asterisk.Platform.Billing/ServiceCollectionExtensions.cs`.

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Billing/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 3: Commit**

```
feat(billing): add AddPlatformBilling DI registration extension
```

---

## Task 10: InMemory Storage — InMemoryUsageRecordStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs`
- Create: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryUsageRecordStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");
    private static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static UsageRecord MakeRecord(
        TenantId? tenantId = null,
        UsageType type = UsageType.VoiceInbound,
        decimal quantity = 5m,
        UsageUnit unit = UsageUnit.Minutes,
        DateTimeOffset? recordedAt = null) => new()
    {
        RecordId = EntityId.New(),
        TenantId = tenantId ?? Tenant1,
        UsageType = type,
        Quantity = quantity,
        Unit = unit,
        RecordedAt = recordedAt ?? BaseTime,
    };

    [Fact]
    public async Task SaveAsync_ShouldPersistRecord()
    {
        var store = new InMemoryUsageRecordStore();
        var record = MakeRecord();

        await store.SaveAsync(record, CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.TotalQuantity.Should().Be(5m);
        summary.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldPersistAllRecords()
    {
        var store = new InMemoryUsageRecordStore();
        var records = new List<UsageRecord>
        {
            MakeRecord(quantity: 3m),
            MakeRecord(quantity: 7m),
        };

        await store.SaveBatchAsync(records, CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        summary.Should().NotBeNull();
        summary!.TotalQuantity.Should().Be(10m);
        summary.RecordCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldGroupByUsageType()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.VoiceInbound, quantity: 5m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, quantity: 3m, unit: UsageUnit.Segments), CancellationToken.None);

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().HaveCount(2);
        var voice = summaries.First(s => s.UsageType == UsageType.VoiceInbound);
        voice.TotalQuantity.Should().Be(15m);
        voice.RecordCount.Should().Be(2);
        var sms = summaries.First(s => s.UsageType == UsageType.SmsOutbound);
        sms.TotalQuantity.Should().Be(3m);
        sms.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(-1)), CancellationToken.None); // before range
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddDays(5), quantity: 8m), CancellationToken.None); // in range
        await store.SaveAsync(MakeRecord(recordedAt: BaseTime.AddMonths(2)), CancellationToken.None); // after range

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().HaveCount(1);
        summaries[0].TotalQuantity.Should().Be(8m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnEmpty_WhenNoRecords()
    {
        var store = new InMemoryUsageRecordStore();

        var summaries = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummaryByTypeAsync_ShouldReturnNull_WhenNoRecordsForType()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(type: UsageType.SmsOutbound, unit: UsageUnit.Segments), CancellationToken.None);

        var summary = await store.GetSummaryByTypeAsync(Tenant1, UsageType.VoiceInbound, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        summary.Should().BeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(tenantId: Tenant1, quantity: 10m), CancellationToken.None);
        await store.SaveAsync(MakeRecord(tenantId: Tenant2, quantity: 20m), CancellationToken.None);

        var s1 = await store.GetSummaryAsync(Tenant1, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);
        var s2 = await store.GetSummaryAsync(Tenant2, BaseTime, BaseTime.AddMonths(1), CancellationToken.None);

        s1.Should().HaveCount(1);
        s1[0].TotalQuantity.Should().Be(10m);
        s2.Should().HaveCount(1);
        s2[0].TotalQuantity.Should().Be(20m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldSetPeriodBounds()
    {
        var store = new InMemoryUsageRecordStore();
        await store.SaveAsync(MakeRecord(), CancellationToken.None);
        var from = BaseTime;
        var to = BaseTime.AddMonths(1);

        var summaries = await store.GetSummaryAsync(Tenant1, from, to, CancellationToken.None);

        summaries[0].PeriodStart.Should().Be(from);
        summaries[0].PeriodEnd.Should().Be(to);
    }
}
```

Save to `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryUsageRecordStoreTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryUsageRecordStoreTests" -v q`
Expected: FAIL — `InMemoryUsageRecordStore` not found.

- [ ] **Step 3: Implement InMemoryUsageRecordStore**

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryUsageRecordStore : IUsageRecordStore
{
    private readonly ConcurrentDictionary<TenantId, List<UsageRecord>> _records = new();

    public Task SaveAsync(UsageRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var list = _records.GetOrAdd(record.TenantId, _ => []);
        lock (list)
        {
            list.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            var list = _records.GetOrAdd(record.TenantId, _ => []);
            lock (list)
            {
                list.Add(record);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.RecordedAt >= from && r.RecordedAt < to);

        IReadOnlyList<UsageSummary> summaries = filtered
            .GroupBy(r => r.UsageType)
            .Select(g => new UsageSummary
            {
                TenantId = tenantId,
                PeriodStart = from,
                PeriodEnd = to,
                UsageType = g.Key,
                TotalQuantity = g.Sum(r => r.Quantity),
                RecordCount = g.Count(),
                LastUpdatedAt = g.Max(r => r.RecordedAt),
            })
            .ToList();

        return Task.FromResult(summaries);
    }

    public Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var filtered = GetTenantRecords(tenantId)
            .Where(r => r.UsageType == type && r.RecordedAt >= from && r.RecordedAt < to)
            .ToList();

        if (filtered.Count == 0)
            return Task.FromResult<UsageSummary?>(null);

        var summary = new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = to,
            UsageType = type,
            TotalQuantity = filtered.Sum(r => r.Quantity),
            RecordCount = filtered.Count,
            LastUpdatedAt = filtered.Max(r => r.RecordedAt),
        };

        return Task.FromResult<UsageSummary?>(summary);
    }

    private List<UsageRecord> GetTenantRecords(TenantId tenantId)
    {
        if (!_records.TryGetValue(tenantId, out var list))
            return [];

        lock (list)
        {
            return [.. list];
        }
    }
}
```

Save to `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryUsageRecordStoreTests" -v q`
Expected: Passed! 8 tests.

- [ ] **Step 5: Commit**

```
feat(billing): implement InMemoryUsageRecordStore with summary aggregation
```

---

## Task 11: InMemory Storage — InMemoryTenantQuotaStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantQuotaStore.cs`
- Create: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryTenantQuotaStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryTenantQuotaStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenNotConfigured()
    {
        var store = new InMemoryTenantQuotaStore();

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ShouldPersistQuota()
    {
        var store = new InMemoryTenantQuotaStore();
        var quota = new TenantQuota
        {
            TenantId = Tenant1,
            MaxConcurrentChannels = 200,
            MaxMonthlyVoiceMinutes = 5000,
        };

        await store.UpsertAsync(quota, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result.Should().NotBeNull();
        result!.MaxConcurrentChannels.Should().Be(200);
        result.MaxMonthlyVoiceMinutes.Should().Be(5000);
    }

    [Fact]
    public async Task UpsertAsync_ShouldOverwriteExisting()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 5 }, CancellationToken.None);
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 50 }, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result!.MaxActiveCampaigns.Should().Be(50);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveQuota()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1 }, CancellationToken.None);

        await store.DeleteAsync(Tenant1, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotThrow_WhenNotExists()
    {
        var store = new InMemoryTenantQuotaStore();

        var act = () => store.DeleteAsync(Tenant1, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Stores_ShouldIsolateTenants()
    {
        var store = new InMemoryTenantQuotaStore();
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant1, MaxActiveCampaigns = 10 }, CancellationToken.None);
        await store.UpsertAsync(new TenantQuota { TenantId = Tenant2, MaxActiveCampaigns = 20 }, CancellationToken.None);

        var r1 = await store.GetAsync(Tenant1, CancellationToken.None);
        var r2 = await store.GetAsync(Tenant2, CancellationToken.None);

        r1!.MaxActiveCampaigns.Should().Be(10);
        r2!.MaxActiveCampaigns.Should().Be(20);
    }
}
```

Save to `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryTenantQuotaStoreTests.cs`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryTenantQuotaStoreTests" -v q`
Expected: FAIL — `InMemoryTenantQuotaStore` not found.

- [ ] **Step 3: Implement InMemoryTenantQuotaStore**

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantQuotaStore : ITenantQuotaStore
{
    private readonly ConcurrentDictionary<TenantId, TenantQuota> _quotas = new();

    public Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        _quotas.TryGetValue(tenantId, out var quota);
        return Task.FromResult(quota);
    }

    public Task UpsertAsync(TenantQuota quota, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quota);
        _quotas[quota.TenantId] = quota;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, CancellationToken ct)
    {
        _quotas.TryRemove(tenantId, out _);
        return Task.CompletedTask;
    }
}
```

Save to `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantQuotaStore.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.InMemory.Tests/ --filter "InMemoryTenantQuotaStoreTests" -v q`
Expected: Passed! 6 tests.

- [ ] **Step 5: Commit**

```
feat(billing): implement InMemoryTenantQuotaStore
```

---

## Task 12: Postgres Storage — Schema Migration

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/002_BillingSchema.sql`

- [ ] **Step 1: Create billing migration SQL**

```sql
-- 002_BillingSchema.sql — Metering Engine + Quota Enforcement tables

CREATE TABLE IF NOT EXISTS usage_records (
    record_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    usage_type SMALLINT NOT NULL,
    quantity NUMERIC(18,6) NOT NULL,
    unit SMALLINT NOT NULL,
    channel TEXT,
    reference_id TEXT,
    recorded_at TIMESTAMPTZ NOT NULL,
    metadata JSONB
);

CREATE INDEX IF NOT EXISTS idx_usage_tenant_period ON usage_records (tenant_id, recorded_at DESC);
CREATE INDEX IF NOT EXISTS idx_usage_tenant_type ON usage_records (tenant_id, usage_type, recorded_at DESC);

CREATE TABLE IF NOT EXISTS tenant_quotas (
    tenant_id TEXT PRIMARY KEY,
    max_concurrent_channels INT NOT NULL DEFAULT 100,
    max_active_campaigns INT NOT NULL DEFAULT 10,
    max_monthly_voice_minutes BIGINT,
    max_monthly_messages BIGINT,
    max_storage_bytes BIGINT,
    max_active_agents INT,
    quota_action SMALLINT NOT NULL DEFAULT 0
);
```

Save to `src/Asterisk.Platform.Storage.Postgres/Migrations/002_BillingSchema.sql`.

- [ ] **Step 2: Commit**

```
feat(billing): add Postgres migration for usage_records and tenant_quotas
```

---

## Task 13: Postgres Storage — PostgresUsageRecordStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs`

- [ ] **Step 1: Implement PostgresUsageRecordStore**

```csharp
using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresUsageRecordStore : IUsageRecordStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUsageRecordStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(UsageRecord record, CancellationToken ct)
    {
        var metadataJson = record.Metadata != null
            ? JsonSerializer.Serialize(record.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO usage_records (record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata) " +
            "VALUES (@RecordId, @TenantId, @UsageType, @Quantity, @Unit, @Channel, @ReferenceId, @RecordedAt, @Metadata::jsonb)",
            new
            {
                RecordId = record.RecordId.Value,
                TenantId = record.TenantId.Value,
                UsageType = (short)record.UsageType,
                record.Quantity,
                Unit = (short)record.Unit,
                record.Channel,
                record.ReferenceId,
                record.RecordedAt,
                Metadata = metadataJson,
            });
    }

    public async Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var record in records)
        {
            var metadataJson = record.Metadata != null
                ? JsonSerializer.Serialize(record.Metadata, PostgresJson.Ctx.DictionaryStringString)
                : null;

            await conn.ExecuteAsync(
                "INSERT INTO usage_records (record_id, tenant_id, usage_type, quantity, unit, channel, reference_id, recorded_at, metadata) " +
                "VALUES (@RecordId, @TenantId, @UsageType, @Quantity, @Unit, @Channel, @ReferenceId, @RecordedAt, @Metadata::jsonb)",
                new
                {
                    RecordId = record.RecordId.Value,
                    TenantId = record.TenantId.Value,
                    UsageType = (short)record.UsageType,
                    record.Quantity,
                    Unit = (short)record.Unit,
                    record.Channel,
                    record.ReferenceId,
                    record.RecordedAt,
                    Metadata = metadataJson,
                },
                tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND recorded_at >= @From AND recorded_at < @To " +
            "GROUP BY usage_type",
            new { TenantId = tenantId.Value, From = from, To = to });

        return rows.Select(r => new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = to,
            UsageType = (UsageType)r.usage_type,
            TotalQuantity = r.total_quantity,
            RecordCount = r.record_count,
            LastUpdatedAt = r.last_updated_at,
        }).ToList();
    }

    public async Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SummaryRow?>(
            "SELECT usage_type, SUM(quantity) AS total_quantity, COUNT(*) AS record_count, MAX(recorded_at) AS last_updated_at " +
            "FROM usage_records WHERE tenant_id = @TenantId AND usage_type = @UsageType AND recorded_at >= @From AND recorded_at < @To " +
            "GROUP BY usage_type",
            new { TenantId = tenantId.Value, UsageType = (short)type, From = from, To = to });

        if (row is null) return null;

        return new UsageSummary
        {
            TenantId = tenantId,
            PeriodStart = from,
            PeriodEnd = to,
            UsageType = type,
            TotalQuantity = row.total_quantity,
            RecordCount = row.record_count,
            LastUpdatedAt = row.last_updated_at,
        };
    }

    private sealed record SummaryRow(short usage_type, decimal total_quantity, int record_count, DateTimeOffset last_updated_at);
}
```

Save to `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs`.

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 3: Commit**

```
feat(billing): implement PostgresUsageRecordStore with Dapper
```

---

## Task 14: Postgres Storage — PostgresTenantQuotaStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs`

- [ ] **Step 1: Implement PostgresTenantQuotaStore**

```csharp
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantQuotaStore : ITenantQuotaStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantQuotaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantQuota?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QuotaRow?>(
            "SELECT tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action " +
            "FROM tenant_quotas WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });

        return row?.ToQuota();
    }

    public async Task UpsertAsync(TenantQuota quota, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_quotas (tenant_id, max_concurrent_channels, max_active_campaigns, " +
            "max_monthly_voice_minutes, max_monthly_messages, max_storage_bytes, max_active_agents, quota_action) " +
            "VALUES (@TenantId, @MaxConcurrentChannels, @MaxActiveCampaigns, " +
            "@MaxMonthlyVoiceMinutes, @MaxMonthlyMessages, @MaxStorageBytes, @MaxActiveAgents, @QuotaAction) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "max_concurrent_channels = EXCLUDED.max_concurrent_channels, " +
            "max_active_campaigns = EXCLUDED.max_active_campaigns, " +
            "max_monthly_voice_minutes = EXCLUDED.max_monthly_voice_minutes, " +
            "max_monthly_messages = EXCLUDED.max_monthly_messages, " +
            "max_storage_bytes = EXCLUDED.max_storage_bytes, " +
            "max_active_agents = EXCLUDED.max_active_agents, " +
            "quota_action = EXCLUDED.quota_action",
            new
            {
                TenantId = quota.TenantId.Value,
                quota.MaxConcurrentChannels,
                quota.MaxActiveCampaigns,
                quota.MaxMonthlyVoiceMinutes,
                quota.MaxMonthlyMessages,
                quota.MaxStorageBytes,
                quota.MaxActiveAgents,
                QuotaAction = (short)quota.QuotaAction,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM tenant_quotas WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
    }

    private sealed record QuotaRow(
        string tenant_id,
        int max_concurrent_channels,
        int max_active_campaigns,
        long? max_monthly_voice_minutes,
        long? max_monthly_messages,
        long? max_storage_bytes,
        int? max_active_agents,
        short quota_action)
    {
        public TenantQuota ToQuota() => new()
        {
            TenantId = new TenantId(tenant_id),
            MaxConcurrentChannels = max_concurrent_channels,
            MaxActiveCampaigns = max_active_campaigns,
            MaxMonthlyVoiceMinutes = max_monthly_voice_minutes,
            MaxMonthlyMessages = max_monthly_messages,
            MaxStorageBytes = max_storage_bytes,
            MaxActiveAgents = max_active_agents,
            QuotaAction = (QuotaAction)quota_action,
        };
    }
}
```

Save to `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantQuotaStore.cs`.

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/`
Expected: Build succeeded. 0 Warning(s).

- [ ] **Step 3: Commit**

```
feat(billing): implement PostgresTenantQuotaStore with Dapper
```

---

## Task 15: Storage DI Registration

**Files:**
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Register billing stores in AddInMemoryStorage**

In `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`, add after the `// Audit` section (line 78) and before `// MultiTenant`:

```csharp
        // Billing
        services.AddSingleton<IUsageRecordStore, InMemoryUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, InMemoryTenantQuotaStore>();
```

Also add the using at the top of the file:

```csharp
using Asterisk.Platform.Billing;
```

- [ ] **Step 2: Register billing stores in AddPostgresStorage**

In `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`, add after the `// RBAC` section (at the end, before `return services;`):

```csharp
        // Billing
        services.AddSingleton<IUsageRecordStore, PostgresUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, PostgresTenantQuotaStore>();
```

Also add the using at the top of the file:

```csharp
using Asterisk.Platform.Billing;
```

- [ ] **Step 3: Verify full solution build**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 4: Commit**

```
feat(billing): register billing stores in InMemory and Postgres DI
```

---

## Task 16: Wire Billing into Program.cs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Add using statement**

Add to the top of `src/Asterisk.Platform.Api/Program.cs` after the existing usings:

```csharp
using Asterisk.Platform.Billing;
```

- [ ] **Step 2: Add AddPlatformBilling call**

In `Program.cs`, add after `builder.Services.AddPlatformSurveys();` (line 67):

```csharp
builder.Services.AddPlatformBilling();
```

- [ ] **Step 3: Verify full solution build**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 4: Commit**

```
feat(billing): wire AddPlatformBilling into Platform.Api composition root
```

---

## Task 17: Run All Tests — Final Verification

- [ ] **Step 1: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass. At least 1,100+ tests total (1,068 existing + ~36 new billing tests).

- [ ] **Step 2: Verify test counts**

New tests added:
- `UsageRecordTests` (3) + `UsageSummaryTests` (1) = 4
- `TenantQuotaTests` (3) + `QuotaCheckResultTests` (2) = 5
- `DefaultMeteringServiceTests` = 5
- `DefaultQuotaEnforcementServiceTests` = 8
- `InMemoryUsageRecordStoreTests` = 8
- `InMemoryTenantQuotaStoreTests` = 6
- **Total new: 36 tests**

- [ ] **Step 3: Commit (if any fix was needed)**

Only if fixes were required:

```
fix(billing): resolve test failures in billing package
```

---

## Summary

| Task | Description | New Tests |
|------|-------------|-----------|
| 1 | Project scaffolding | 0 |
| 2 | Enums (UsageType, UsageUnit) | 0 |
| 3 | UsageRecord + UsageSummary models | 4 |
| 4 | TenantQuota + QuotaCheckResult models | 5 |
| 5 | Store interfaces | 0 |
| 6 | Service interfaces | 0 |
| 7 | DefaultMeteringService | 5 |
| 8 | DefaultQuotaEnforcementService | 8 |
| 9 | DI: AddPlatformBilling | 0 |
| 10 | InMemoryUsageRecordStore | 8 |
| 11 | InMemoryTenantQuotaStore | 6 |
| 12 | Postgres migration SQL | 0 |
| 13 | PostgresUsageRecordStore | 0 |
| 14 | PostgresTenantQuotaStore | 0 |
| 15 | Storage DI registration | 0 |
| 16 | Program.cs wiring | 0 |
| 17 | Full test verification | 0 |
| **Total** | **17 tasks, 14 commits** | **36 tests** |
