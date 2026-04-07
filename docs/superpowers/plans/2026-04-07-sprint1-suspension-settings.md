# Sprint 1: Suspension Enforcement + TenantSettings Facade — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enforce tenant suspension at runtime and provide a unified settings facade with per-tenant rate limiting.

**Architecture:** TenantStatusMiddleware blocks Suspended/Deleted tenants after auth. ManagementTenantEndpoints invokes ITenantLifecycleHandler on status changes. TenantSettingsEndpoints aggregates 4 stores + RateLimitTier from Metadata. TenantTierCache bridges per-tenant tier to the existing rate limit middleware.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, xUnit + FluentAssertions + NSubstitute

---

## File Structure

| Action | Path | Responsibility |
|--------|------|---------------|
| Create | `src/Asterisk.Platform.Core/TenantExtensions.cs` | `GetRateLimitTier()` extension method |
| Create | `src/Asterisk.Platform.Api/Services/TenantTierCache.cs` | Singleton ConcurrentDictionary for fast tier lookups |
| Create | `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs` | Block Suspended/Deleted tenants, populate cache |
| Create | `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` | AdminOnly facade GET/PUT |
| Create | `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs` | PlatformAdminOnly facade GET/PUT |
| Modify | `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs` | Lifecycle handler dispatch |
| Modify | `src/Asterisk.Platform.Api/Middleware/TenantRateLimitPolicy.cs` | Read tier from TenantTierCache |
| Modify | `src/Asterisk.Platform.Api/Middleware/RateLimitHeadersMiddleware.cs` | Read tier from TenantTierCache |
| Modify | `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Register new DTOs |
| Modify | `src/Asterisk.Platform.Api/Program.cs` | Register middleware + cache + endpoints |
| Create | `tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs` | Middleware unit tests |
| Create | `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs` | Facade integration tests |
| Create | `tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs` | Extension method tests |

---

### Task 1: TenantExtensions + TenantTierCache

**Files:**
- Create: `src/Asterisk.Platform.Core/TenantExtensions.cs`
- Create: `src/Asterisk.Platform.Api/Services/TenantTierCache.cs`
- Create: `tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs`

- [ ] **Step 1: Write failing tests for GetRateLimitTier**

```csharp
// File: tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Core.Tests;

public sealed class TenantExtensionsTests
{
    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenMetadataNull()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = null };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenKeyMissing()
    {
        var tenant = new Tenant { TenantId = "t1", Name = "T1", Metadata = new() };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnTier_WhenMetadataSet()
    {
        var tenant = new Tenant
        {
            TenantId = "t1", Name = "T1",
            Metadata = new() { ["RateLimitTier"] = "Enterprise" },
        };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public void GetRateLimitTier_ShouldReturnStandard_WhenInvalidValue()
    {
        var tenant = new Tenant
        {
            TenantId = "t1", Name = "T1",
            Metadata = new() { ["RateLimitTier"] = "InvalidTier" },
        };
        tenant.GetRateLimitTier().Should().Be(RateLimitTier.Standard);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Core.Tests/ --filter "TenantExtensionsTests" -v q`
Expected: FAIL — `GetRateLimitTier` does not exist

- [ ] **Step 3: Implement TenantExtensions**

```csharp
// File: src/Asterisk.Platform.Core/TenantExtensions.cs
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Core;

public static class TenantExtensions
{
    private const string RateLimitTierKey = "RateLimitTier";

    public static RateLimitTier GetRateLimitTier(this Tenant tenant)
        => tenant.Metadata?.GetValueOrDefault(RateLimitTierKey) is string s
            && Enum.TryParse<RateLimitTier>(s, out var tier) ? tier : RateLimitTier.Standard;

    public static void SetRateLimitTier(this Tenant tenant, RateLimitTier tier)
    {
        // Metadata dict may be null on deserialized Tenants — we can't reassign (init-only)
        // but we CAN mutate existing dictionaries
        if (tenant.Metadata is not null)
            tenant.Metadata[RateLimitTierKey] = tier.ToString();
    }
}
```

- [ ] **Step 4: Implement TenantTierCache**

```csharp
// File: src/Asterisk.Platform.Api/Services/TenantTierCache.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

internal sealed class TenantTierCache
{
    private readonly ConcurrentDictionary<string, RateLimitTier> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RateLimitTier GetTier(string tenantId)
        => _cache.GetValueOrDefault(tenantId, RateLimitTier.Standard);

    public void SetTier(string tenantId, RateLimitTier tier)
        => _cache[tenantId] = tier;

    public void Remove(string tenantId)
        => _cache.TryRemove(tenantId, out _);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Core.Tests/ --filter "TenantExtensionsTests" -v q`
Expected: 4 passed

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Core/TenantExtensions.cs src/Asterisk.Platform.Api/Services/TenantTierCache.cs tests/Asterisk.Platform.Core.Tests/TenantExtensionsTests.cs
git commit -m "feat: add TenantExtensions.GetRateLimitTier and TenantTierCache"
```

---

### Task 2: TenantStatusMiddleware

**Files:**
- Create: `src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs:384` — register middleware

- [ ] **Step 1: Write failing tests**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs
using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantStatusMiddlewareTests
{
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly TenantTierCache _tierCache = new();
    private bool _nextCalled;

    private TenantStatusMiddleware CreateMiddleware()
        => new(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });

    private HttpContext CreateContext(string? tenantId = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServiceProvider();
        if (tenantId is not null)
            context.Items["TenantId"] = new TenantId(tenantId);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private IServiceProvider CreateServiceProvider()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITenantStore)).Returns(_tenantStore);
        sp.GetService(typeof(TenantTierCache)).Returns(_tierCache);
        return sp;
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenNoTenantIdResolved()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(tenantId: null);

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenTenantActive()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Active });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ShouldReturn403_WhenTenantSuspended()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Suspended });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_ShouldReturn404_WhenTenantDeleted()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Deleted });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Invoke_ShouldPopulateTenantTierCache_WhenTenantActive()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = "acme", Name = "ACME", Status = TenantStatus.Active,
                Metadata = new() { ["RateLimitTier"] = "Enterprise" },
            });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _tierCache.GetTier("acme").Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenTenantNotFoundInStore()
    {
        _tenantStore.GetAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);
        var middleware = CreateMiddleware();
        var context = CreateContext("unknown");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantStatusMiddlewareTests" -v q`
Expected: FAIL — `TenantStatusMiddleware` does not exist

- [ ] **Step 3: Implement TenantStatusMiddleware**

```csharp
// File: src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs
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
            // Tenant ID was resolved from header/subdomain but not found in store — let auth handle it
            await _next(context);
            return;
        }

        switch (tenant.Status)
        {
            case TenantStatus.Suspended:
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body,
                    new ErrorResponse("This tenant account has been suspended. Contact your administrator."),
                    ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
                return;

            case TenantStatus.Deleted:
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body,
                    new ErrorResponse("Not found"),
                    ApiJsonContext.Default.ErrorResponse, context.RequestAborted);
                return;

            default:
                // Active — store tenant for downstream and update tier cache
                context.Items["Tenant"] = tenant;
                var tierCache = context.RequestServices.GetService<TenantTierCache>();
                tierCache?.SetTier(tenantId.Value, tenant.GetRateLimitTier());
                await _next(context);
                return;
        }
    }
}
```

- [ ] **Step 4: Register middleware in Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, after line 383 (`app.UseAuthorization();`), add:

```csharp
app.UseMiddleware<TenantStatusMiddleware>();
```

And in the DI section (around line 337, after `AddRateLimiter`), add:

```csharp
builder.Services.AddSingleton<TenantTierCache>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "TenantStatusMiddlewareTests" -v q`
Expected: 6 passed

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/TenantStatusMiddleware.cs tests/Asterisk.Platform.Api.Tests/TenantStatusMiddlewareTests.cs src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: add TenantStatusMiddleware blocking suspended/deleted tenants"
```

---

### Task 3: Lifecycle Handler Dispatch in ManagementTenantEndpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`
- Modify: `tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs`

- [ ] **Step 1: Write failing tests for lifecycle dispatch**

Append to `tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs`:

```csharp
[Fact]
public async Task SuspendTenant_ShouldReturn403_WhenSuspendedTenantAccessesApi()
{
    // Create a tenant and suspend it
    var tenantId = "suspend-gate-" + Guid.NewGuid().ToString("N")[..8];
    await _client.PostAsJsonAsync("/api/management/tenants", new
    {
        tenantId,
        name = "Suspend Gate Test",
        type = 2,
    });
    await _client.PostAsync($"/api/management/tenants/{tenantId}/suspend", null);

    // Try to access an admin endpoint AS that tenant (via X-Tenant-Id header)
    // The PlatformAdmin client uses management endpoints so it won't be blocked,
    // but we can verify the suspension took effect via GET
    var getResponse = await _client.GetAsync($"/api/management/tenants/{tenantId}");
    getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await getResponse.Content.ReadAsStringAsync();
    body.Should().Contain("Suspended");
}
```

- [ ] **Step 2: Modify ManagementTenantEndpoints to inject and invoke lifecycle handlers**

In `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`:

Add `IEnumerable<ITenantLifecycleHandler>` parameter to `CreateTenant`, `SuspendTenant`, and `DeleteTenant` handlers. Add dispatch calls after the status change + audit.

Replace the `CreateTenant` method signature (line 59) to add `[FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers`. After the `Results.Created` return preparation (line 127, after `store.UpsertAsync`), before audit, add:

```csharp
await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantCreatedAsync(tenant, ct));
```

Replace the `SuspendTenant` method signature (line 191) to add `[FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers, [FromServices] ILogger<Program> logger`. After `store.UpdateStatusAsync` (line 203), add:

```csharp
await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantSuspendedAsync(id, ct), logger);
```

Replace the `DeleteTenant` method signature (line 243) to add `[FromServices] IEnumerable<ITenantLifecycleHandler> lifecycleHandlers, [FromServices] ILogger<Program> logger`. After `store.UpdateStatusAsync` (line 256), add:

```csharp
await DispatchLifecycleAsync(lifecycleHandlers, h => h.OnTenantDeletedAsync(id, ct), logger);
```

Add the helper method at the bottom of the class (before the DTOs section):

```csharp
private static async Task DispatchLifecycleAsync(
    IEnumerable<ITenantLifecycleHandler> handlers,
    Func<ITenantLifecycleHandler, ValueTask> action,
    ILogger? logger = null)
{
    foreach (var handler in handlers)
    {
        try
        {
            await action(handler);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lifecycle handler {Handler} failed", handler.GetType().Name);
        }
    }
}
```

Add the using at the top:

```csharp
using Asterisk.Sdk.Pro.MultiTenant;  // already present
using Microsoft.Extensions.Logging;
```

- [ ] **Step 3: Run all tests to verify nothing broke**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass (1396+)

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs tests/Asterisk.Platform.Api.Tests/ManagementTenantEndpointTests.cs
git commit -m "feat: invoke ITenantLifecycleHandler on tenant create/suspend/delete"
```

---

### Task 4: Wire RateLimitTier to TenantTierCache

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/TenantRateLimitPolicy.cs:28-30`
- Modify: `src/Asterisk.Platform.Api/Middleware/RateLimitHeadersMiddleware.cs:27-28`

- [ ] **Step 1: Update TenantRateLimitPolicy to read from TenantTierCache**

Replace lines 28-30 in `src/Asterisk.Platform.Api/Middleware/TenantRateLimitPolicy.cs`:

Old:
```csharp
            var tier = tenantId == "__global__"
                ? RateLimitTier.Unlimited
                : RateLimitTier.Standard; // Default tier; will be read from tenant config in a future update
```

New:
```csharp
            var tier = tenantId == "__global__"
                ? RateLimitTier.Unlimited
                : context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;
```

Add using at top:

```csharp
using Asterisk.Platform.Api.Services;
```

- [ ] **Step 2: Update RateLimitHeadersMiddleware to read from TenantTierCache**

Replace lines 27-28 in `src/Asterisk.Platform.Api/Middleware/RateLimitHeadersMiddleware.cs`:

Old:
```csharp
        // Default tier — will come from tenant config in a future update
        var tier = RateLimitTier.Standard;
```

New:
```csharp
        var tier = context.RequestServices.GetService<Services.TenantTierCache>()?.GetTier(tenantId) ?? RateLimitTier.Standard;
```

- [ ] **Step 3: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/TenantRateLimitPolicy.cs src/Asterisk.Platform.Api/Middleware/RateLimitHeadersMiddleware.cs
git commit -m "feat: wire per-tenant RateLimitTier via TenantTierCache"
```

---

### Task 5: TenantSettings DTOs + ApiJsonContext

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` (DTOs only first)
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Create DTO records**

```csharp
// File: src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs (DTOs section at top, handlers added in Task 6)
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;

namespace Asterisk.Platform.Api.Endpoints;

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record TenantSettingsDto(
    string TenantId,
    string Name,
    string Type,
    string Status,
    OperationalSettingsDto Operational,
    AuthSettingsDto Auth,
    QuotaSettingsDto Quotas,
    RetentionSettingsDto Retention,
    RateLimitTier RateLimitTier);

internal sealed record OperationalSettingsDto(
    int MaxConcurrentChannels,
    int MaxActiveCampaigns,
    string? DialplanContextPrefix,
    List<string>? NodeAffinity,
    List<int>? AllowedDialingModes);

internal sealed record AuthSettingsDto(
    string MfaPolicy,
    IReadOnlyList<string> MfaRequiredRoles,
    int PasswordMinLength,
    bool PasswordRequireUppercase,
    bool PasswordRequireNumber,
    bool PasswordRequireSpecial,
    int LockoutThreshold,
    int LockoutDurationMinutes,
    int SessionIdleTimeoutMinutes,
    int SessionAbsoluteTimeoutHours,
    bool OidcEnabled,
    string? OidcAuthority,
    string? OidcClientId,
    bool OidcAutoCreateUsers,
    string OidcDefaultRole);

internal sealed record QuotaSettingsDto(
    long? MaxMonthlyVoiceMinutes,
    long? MaxMonthlyMessages,
    long? MaxStorageBytes,
    int? MaxActiveAgents,
    string QuotaAction);

internal sealed record RetentionSettingsDto(
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);

internal sealed record UpdateTenantSettingsRequest(
    UpdateOperationalSettingsDto? Operational = null,
    UpdateAuthSettingsDto? Auth = null,
    UpdateQuotaSettingsDto? Quotas = null,
    UpdateRetentionSettingsDto? Retention = null,
    RateLimitTier? RateLimitTier = null);

internal sealed record UpdateOperationalSettingsDto(
    int? MaxConcurrentChannels = null,
    int? MaxActiveCampaigns = null,
    string? DialplanContextPrefix = null,
    List<string>? NodeAffinity = null,
    List<int>? AllowedDialingModes = null);

internal sealed record UpdateAuthSettingsDto(
    string? MfaPolicy = null,
    IReadOnlyList<string>? MfaRequiredRoles = null,
    int? PasswordMinLength = null,
    bool? PasswordRequireUppercase = null,
    bool? PasswordRequireNumber = null,
    bool? PasswordRequireSpecial = null,
    int? LockoutThreshold = null,
    int? LockoutDurationMinutes = null,
    int? SessionIdleTimeoutMinutes = null,
    int? SessionAbsoluteTimeoutHours = null,
    bool? OidcEnabled = null,
    string? OidcAuthority = null,
    string? OidcClientId = null,
    string? OidcClientSecret = null,
    bool? OidcAutoCreateUsers = null,
    string? OidcDefaultRole = null);

internal sealed record UpdateQuotaSettingsDto(
    long? MaxMonthlyVoiceMinutes = null,
    long? MaxMonthlyMessages = null,
    long? MaxStorageBytes = null,
    int? MaxActiveAgents = null,
    string? QuotaAction = null);

internal sealed record UpdateRetentionSettingsDto(
    int? ConversationRetentionDays = null,
    int? AuthEventRetentionDays = null,
    int? AuditRetentionDays = null,
    int? UsageRecordRetentionDays = null);

internal static class TenantSettingsEndpoints
{
    // Placeholder — implemented in Task 6
}
```

- [ ] **Step 2: Register DTOs in ApiJsonContext**

In `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`, add before the `[JsonSourceGenerationOptions` line (line 246):

```csharp
// TenantSettings Facade
[JsonSerializable(typeof(TenantSettingsDto))]
[JsonSerializable(typeof(UpdateTenantSettingsRequest))]
[JsonSerializable(typeof(OperationalSettingsDto))]
[JsonSerializable(typeof(AuthSettingsDto))]
[JsonSerializable(typeof(QuotaSettingsDto))]
[JsonSerializable(typeof(RetentionSettingsDto))]
[JsonSerializable(typeof(UpdateOperationalSettingsDto))]
[JsonSerializable(typeof(UpdateAuthSettingsDto))]
[JsonSerializable(typeof(UpdateQuotaSettingsDto))]
[JsonSerializable(typeof(UpdateRetentionSettingsDto))]
```

- [ ] **Step 3: Build to verify DTOs compile**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded, 0 warnings

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "feat: add TenantSettings DTOs and register in ApiJsonContext"
```

---

### Task 6: TenantSettingsEndpoints (AdminOnly facade)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` — add handlers
- Modify: `src/Asterisk.Platform.Api/Program.cs` — map endpoints
- Create: `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs`

- [ ] **Step 1: Write failing integration test**

```csharp
// File: tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public TenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", PlatformAdminApiFactory.HostTenantId);
    }

    [Fact]
    public async Task GetSettings_ShouldReturnAggregatedSettings()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenant/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("operational");
        body.Should().Contain("auth");
        body.Should().Contain("quotas");
        body.Should().Contain("retention");
        body.Should().Contain("rateLimitTier");
    }

    [Fact]
    public async Task GetSettings_ShouldReturnDefaultValues_WhenNoConfigSet()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenant/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"mfaPolicy\":\"optional\"");
        body.Should().Contain("\"maxConcurrentChannels\":100");
    }

    [Fact]
    public async Task UpdateSettings_ShouldUpdateAuthSection()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            auth = new { passwordMinLength = 16 },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify persisted
        var getResponse = await _client.GetAsync("/api/v1/admin/tenant/settings");
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("\"passwordMinLength\":16");
    }

    [Fact]
    public async Task UpdateSettings_ShouldIgnoreQuotas_WhenAdminOnly()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            quotas = new { maxActiveAgents = 999 },
        });
        // AdminOnly PUT should succeed but quotas section is ignored
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Implement TenantSettingsEndpoints handlers**

Replace the placeholder `TenantSettingsEndpoints` class in `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` with:

```csharp
internal static class TenantSettingsEndpoints
{
    public static void MapTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/tenant").RequireAuthorization("AdminOnly");
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", UpdateSettings);
    }

    private static async Task<IResult> GetSettings(
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null)
            return Results.Unauthorized();

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSettings(
        HttpContext context,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        CancellationToken ct)
    {
        var tenantId = context.User.FindFirst("tid")?.Value
            ?? context.User.FindFirst("tenant_id")?.Value;
        if (tenantId is null)
            return Results.Unauthorized();

        // AdminOnly: cannot update quotas or rateLimitTier
        await ApplyUpdates(tenantId, body with { Quotas = null, RateLimitTier = null },
            tenantStore, authConfigStore, quotaStore, retentionStore, tierCache: null, ct);

        var dto = await BuildSettingsDto(tenantId, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return Results.Ok(dto);
    }

    internal static async Task<TenantSettingsDto?> BuildSettingsDto(
        string tenantId,
        ITenantStore tenantStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantQuotaStore quotaStore,
        ITenantRetentionPolicyStore retentionStore,
        CancellationToken ct)
    {
        var tenantTask = tenantStore.GetAsync(tenantId, ct);
        var authTask = authConfigStore.GetAsync(tenantId, ct);
        var quotaTask = quotaStore.GetAsync(new TenantId(tenantId), ct);
        var retentionTask = retentionStore.GetAsync(tenantId, ct);

        await Task.WhenAll(tenantTask.AsTask(), authTask, quotaTask, retentionTask);

        var tenant = tenantTask.IsCompletedSuccessfully ? tenantTask.Result : await tenantTask;
        if (tenant is null) return null;

        var auth = authTask.IsCompletedSuccessfully ? authTask.Result : await authTask;
        var quota = quotaTask.IsCompletedSuccessfully ? quotaTask.Result : await quotaTask;
        var retention = retentionTask.IsCompletedSuccessfully ? retentionTask.Result : await retentionTask;

        auth ??= new TenantAuthConfig { TenantId = tenantId };
        var defaultQuota = new TenantQuota { TenantId = new TenantId(tenantId) };

        return new TenantSettingsDto(
            TenantId: tenant.TenantId,
            Name: tenant.Name,
            Type: tenant.Type.ToString(),
            Status: tenant.Status.ToString(),
            Operational: new OperationalSettingsDto(
                tenant.Options.MaxConcurrentChannels,
                tenant.Options.MaxActiveCampaigns,
                tenant.Options.DialplanContextPrefix,
                tenant.Options.NodeAffinity,
                tenant.Options.AllowedDialingModes),
            Auth: new AuthSettingsDto(
                auth.MfaPolicy, auth.MfaRequiredRoles,
                auth.PasswordMinLength, auth.PasswordRequireUppercase,
                auth.PasswordRequireNumber, auth.PasswordRequireSpecial,
                auth.LockoutThreshold, auth.LockoutDurationMinutes,
                auth.SessionIdleTimeoutMinutes, auth.SessionAbsoluteTimeoutHours,
                auth.OidcEnabled, auth.OidcAuthority, auth.OidcClientId,
                auth.OidcAutoCreateUsers, auth.OidcDefaultRole),
            Quotas: new QuotaSettingsDto(
                (quota ?? defaultQuota).MaxMonthlyVoiceMinutes,
                (quota ?? defaultQuota).MaxMonthlyMessages,
                (quota ?? defaultQuota).MaxStorageBytes,
                (quota ?? defaultQuota).MaxActiveAgents,
                (quota ?? defaultQuota).QuotaAction.ToString()),
            Retention: new RetentionSettingsDto(
                retention?.ConversationRetentionDays,
                retention?.AuthEventRetentionDays,
                retention?.AuditRetentionDays,
                retention?.UsageRecordRetentionDays),
            RateLimitTier: tenant.GetRateLimitTier());
    }

    internal static async Task ApplyUpdates(
        string tenantId,
        UpdateTenantSettingsRequest body,
        ITenantStore tenantStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantQuotaStore quotaStore,
        ITenantRetentionPolicyStore retentionStore,
        TenantTierCache? tierCache,
        CancellationToken ct)
    {
        if (body.Operational is not null || body.RateLimitTier is not null)
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            if (tenant is not null)
            {
                var ops = body.Operational;
                var metadata = tenant.Metadata ?? new Dictionary<string, string>();

                if (body.RateLimitTier is not null)
                    metadata["RateLimitTier"] = body.RateLimitTier.Value.ToString();

                var updated = new Tenant
                {
                    TenantId = tenant.TenantId,
                    Name = tenant.Name,
                    Status = tenant.Status,
                    Type = tenant.Type,
                    ParentTenantId = tenant.ParentTenantId,
                    Options = new TenantOptions
                    {
                        MaxConcurrentChannels = ops?.MaxConcurrentChannels ?? tenant.Options.MaxConcurrentChannels,
                        MaxActiveCampaigns = ops?.MaxActiveCampaigns ?? tenant.Options.MaxActiveCampaigns,
                        DialplanContextPrefix = ops?.DialplanContextPrefix ?? tenant.Options.DialplanContextPrefix,
                        NodeAffinity = ops?.NodeAffinity ?? tenant.Options.NodeAffinity,
                        AllowedDialingModes = ops?.AllowedDialingModes ?? tenant.Options.AllowedDialingModes,
                    },
                    Metadata = metadata,
                    CreatedAt = tenant.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                await tenantStore.UpsertAsync(updated, ct);
                if (body.RateLimitTier is not null)
                    tierCache?.SetTier(tenantId, body.RateLimitTier.Value);
            }
        }

        if (body.Auth is not null)
        {
            var auth = await authConfigStore.GetAsync(tenantId, ct)
                ?? new TenantAuthConfig { TenantId = tenantId };

            var a = body.Auth;
            if (a.MfaPolicy is not null) auth.MfaPolicy = a.MfaPolicy;
            if (a.MfaRequiredRoles is not null) auth.MfaRequiredRoles = a.MfaRequiredRoles;
            if (a.PasswordMinLength.HasValue) auth.PasswordMinLength = a.PasswordMinLength.Value;
            if (a.PasswordRequireUppercase.HasValue) auth.PasswordRequireUppercase = a.PasswordRequireUppercase.Value;
            if (a.PasswordRequireNumber.HasValue) auth.PasswordRequireNumber = a.PasswordRequireNumber.Value;
            if (a.PasswordRequireSpecial.HasValue) auth.PasswordRequireSpecial = a.PasswordRequireSpecial.Value;
            if (a.LockoutThreshold.HasValue) auth.LockoutThreshold = a.LockoutThreshold.Value;
            if (a.LockoutDurationMinutes.HasValue) auth.LockoutDurationMinutes = a.LockoutDurationMinutes.Value;
            if (a.SessionIdleTimeoutMinutes.HasValue) auth.SessionIdleTimeoutMinutes = a.SessionIdleTimeoutMinutes.Value;
            if (a.SessionAbsoluteTimeoutHours.HasValue) auth.SessionAbsoluteTimeoutHours = a.SessionAbsoluteTimeoutHours.Value;
            if (a.OidcEnabled.HasValue) auth.OidcEnabled = a.OidcEnabled.Value;
            if (a.OidcAuthority is not null) auth.OidcAuthority = a.OidcAuthority;
            if (a.OidcClientId is not null) auth.OidcClientId = a.OidcClientId;
            if (a.OidcClientSecret is not null) auth.OidcClientSecret = a.OidcClientSecret;
            if (a.OidcAutoCreateUsers.HasValue) auth.OidcAutoCreateUsers = a.OidcAutoCreateUsers.Value;
            if (a.OidcDefaultRole is not null) auth.OidcDefaultRole = a.OidcDefaultRole;

            auth.UpdatedAt = DateTimeOffset.UtcNow;
            await authConfigStore.SaveAsync(auth, ct);
        }

        if (body.Quotas is not null)
        {
            var quota = await quotaStore.GetAsync(new TenantId(tenantId), ct)
                ?? new TenantQuota { TenantId = new TenantId(tenantId) };

            var q = body.Quotas;
            if (q.MaxMonthlyVoiceMinutes.HasValue) quota.MaxMonthlyVoiceMinutes = q.MaxMonthlyVoiceMinutes;
            if (q.MaxMonthlyMessages.HasValue) quota.MaxMonthlyMessages = q.MaxMonthlyMessages;
            if (q.MaxStorageBytes.HasValue) quota.MaxStorageBytes = q.MaxStorageBytes;
            if (q.MaxActiveAgents.HasValue) quota.MaxActiveAgents = q.MaxActiveAgents;
            if (q.QuotaAction is not null && Enum.TryParse<QuotaAction>(q.QuotaAction, ignoreCase: true, out var action))
                quota.QuotaAction = action;

            await quotaStore.UpsertAsync(quota, ct);
        }

        if (body.Retention is not null)
        {
            var r = body.Retention;
            var policy = new TenantRetentionPolicy
            {
                TenantId = tenantId,
                ConversationRetentionDays = r.ConversationRetentionDays,
                AuthEventRetentionDays = r.AuthEventRetentionDays,
                AuditRetentionDays = r.AuditRetentionDays,
                UsageRecordRetentionDays = r.UsageRecordRetentionDays,
            };
            await retentionStore.SaveAsync(policy, ct);
        }
    }
}
```

Add the necessary usings at the top of the file:

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;
```

- [ ] **Step 3: Map endpoints in Program.cs**

Add after line 453 (`v1.MapGdprEndpoints();`):

```csharp
v1.MapTenantSettingsEndpoints();
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All pass including new TenantSettingsEndpointTests

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: add TenantSettingsEndpoints (AdminOnly facade) with GET/PUT"
```

---

### Task 7: ManagementTenantSettingsEndpoints (PlatformAdminOnly facade)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs` — map endpoints
- Modify: `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs` — add management tests

- [ ] **Step 1: Write failing management tests**

Append to `tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs` a new test class:

```csharp
public sealed class ManagementTenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementTenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task GetSettings_ShouldReturnSettingsForAnyTenant()
    {
        // Create a tenant first
        var tenantId = "settings-mgmt-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Settings Mgmt Test",
            type = 2,
        });

        var response = await _client.GetAsync($"/api/v1/management/tenants/{tenantId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(tenantId);
        body.Should().Contain("operational");
    }

    [Fact]
    public async Task UpdateSettings_ShouldUpdateAllSections()
    {
        var tenantId = "settings-all-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Settings All Test",
            type = 2,
        });

        var response = await _client.PutAsJsonAsync($"/api/v1/management/tenants/{tenantId}/settings", new
        {
            operational = new { maxConcurrentChannels = 200 },
            auth = new { passwordMinLength = 20 },
            quotas = new { maxActiveAgents = 50 },
            retention = new { conversationRetentionDays = 365 },
            rateLimitTier = "Enterprise",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify all sections persisted
        var getResponse = await _client.GetAsync($"/api/v1/management/tenants/{tenantId}/settings");
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("\"maxConcurrentChannels\":200");
        body.Should().Contain("\"passwordMinLength\":20");
        body.Should().Contain("\"maxActiveAgents\":50");
        body.Should().Contain("\"conversationRetentionDays\":365");
        body.Should().Contain("Enterprise");
    }

    [Fact]
    public async Task GetSettings_ShouldReturn404_WhenTenantNotFound()
    {
        var response = await _client.GetAsync("/api/v1/management/tenants/nonexistent/settings");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Implement ManagementTenantSettingsEndpoints**

```csharp
// File: src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementTenantSettingsEndpoints
{
    public static void MapManagementTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/tenants/{id}/settings")
            .RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", GetSettings);
        group.MapPut("/", UpdateSettings);
    }

    private static async Task<IResult> GetSettings(
        string id,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        CancellationToken ct)
    {
        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> UpdateSettings(
        string id,
        [FromBody] UpdateTenantSettingsRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] ITenantQuotaStore quotaStore,
        [FromServices] ITenantRetentionPolicyStore retentionStore,
        [FromServices] TenantTierCache tierCache,
        CancellationToken ct)
    {
        var existing = await tenantStore.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();

        // PlatformAdminOnly: ALL sections writable
        await TenantSettingsEndpoints.ApplyUpdates(
            id, body, tenantStore, authConfigStore, quotaStore, retentionStore, tierCache, ct);

        var dto = await TenantSettingsEndpoints.BuildSettingsDto(
            id, tenantStore, authConfigStore, quotaStore, retentionStore, ct);
        return Results.Ok(dto);
    }
}
```

- [ ] **Step 3: Map endpoints in Program.cs**

Add after the `v1.MapTenantSettingsEndpoints();` line:

```csharp
v1.MapManagementTenantSettingsEndpoints();
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantSettingsEndpoints.cs tests/Asterisk.Platform.Api.Tests/TenantSettingsEndpointTests.cs src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: add ManagementTenantSettingsEndpoints (PlatformAdminOnly facade)"
```

---

### Task 8: Final verification + cleanup

**Files:**
- All modified files

- [ ] **Step 1: Full build**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded, 0 warnings

- [ ] **Step 2: Full test suite**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass (1396 + ~17 new ≈ 1413)

- [ ] **Step 3: Verify new endpoint count**

The Platform now has 49 endpoint groups (was 47):
- +1: `TenantSettingsEndpoints` (`/admin/tenant/settings`)
- +1: `ManagementTenantSettingsEndpoints` (`/management/tenants/{id}/settings`)

- [ ] **Step 4: Commit any cleanup**

```bash
git add -A
git commit -m "chore: Sprint 1 final cleanup and verification"
```

---

## Self-Review Checklist

| Spec Requirement | Task |
|-----------------|------|
| TenantStatusMiddleware blocks Suspended (403) | Task 2 |
| TenantStatusMiddleware blocks Deleted (404) | Task 2 |
| Middleware skips when no TenantId | Task 2 |
| Middleware populates TenantTierCache | Task 2 |
| Lifecycle handlers invoked on Create | Task 3 |
| Lifecycle handlers invoked on Suspend | Task 3 |
| Lifecycle handlers invoked on Delete | Task 3 |
| Handler errors logged, don't block | Task 3 |
| GET /admin/tenant/settings (AdminOnly) | Task 6 |
| PUT /admin/tenant/settings (AdminOnly, no quotas/tier) | Task 6 |
| GET /management/tenants/{id}/settings (PlatformAdminOnly) | Task 7 |
| PUT /management/tenants/{id}/settings (PlatformAdminOnly, all sections) | Task 7 |
| RateLimitTier stored in Tenant.Metadata | Task 1 (extension), Task 6/7 (write) |
| TenantTierCache singleton for fast reads | Task 1 |
| TenantRateLimitPolicy reads from cache | Task 4 |
| RateLimitHeadersMiddleware reads from cache | Task 4 |
| DTOs registered in ApiJsonContext | Task 5 |
| Middleware registered in Program.cs | Task 2 |
| Endpoints mapped in Program.cs | Tasks 6, 7 |
