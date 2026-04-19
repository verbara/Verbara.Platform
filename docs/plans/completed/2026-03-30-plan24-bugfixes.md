# Plan 24: Bug Fixes & Warnings — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix IUserRoleStore DI bug, register AGI/ARI hosted services in SDK, eliminate all compiler warnings.

**Architecture:** Two repos affected. SDK gets ARI hosted service + AGI registration (v1.5.2). Platform gets InMemory RBAC stores + static method fixes + warning cleanup. Changes are independent across repos.

**Tech Stack:** .NET 10, xunit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0

**Spec:** `docs/superpowers/specs/2026-03-30-plan24-bugfixes-design.md`

---

## Phase A: SDK — AGI/ARI Hosted Services (Asterisk.Sdk repo)

> **Working directory:** `/media/Data/Source/IPcom/Asterisk.Sdk/`

### Task 1: Create AriConnectionHostedService

**Files:**
- Create: `src/Asterisk.Sdk.Hosting/AriConnectionHostedService.cs`

- [ ] **Step 1: Create AriConnectionHostedService**

```csharp
using Asterisk.Sdk.Ari.Client;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Sdk.Hosting;

/// <summary>
/// Hosted service that connects the ARI client on application start and disconnects on stop.
/// </summary>
public sealed class AriConnectionHostedService(IAriClient client) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await client.ConnectAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await client.DisconnectAsync(cancellationToken);
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/Asterisk.Sdk.Hosting/`
Expected: Build succeeded, 0 warnings

### Task 2: Register AGI + ARI Hosted Services in AddAsterisk

**Files:**
- Modify: `src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs:57-122`

- [ ] **Step 1: Add AGI hosted service registration after AGI health check (line 66)**

In `ServiceCollectionExtensions.cs`, after line 66 (`AddCheck<AgiHealthCheck>("agi")`), add:

```csharp
        services.AddSingleton<IHostedService, Asterisk.Sdk.Agi.Hosting.AgiHostedService>();
```

- [ ] **Step 2: Add ARI hosted service registration after ARI health check (line 121)**

In `ServiceCollectionExtensions.cs`, inside the `if (options.Ari is not null)` block, after line 121 (`AddCheck<AriHealthCheck>("ari")`), add:

```csharp
            services.AddSingleton<IHostedService, AriConnectionHostedService>();
```

- [ ] **Step 3: Verify it builds**

Run: `dotnet build src/Asterisk.Sdk.Hosting/`
Expected: Build succeeded, 0 warnings

- [ ] **Step 4: Run all SDK tests**

Run: `dotnet test Asterisk.Sdk.slnx -v q`
Expected: All 1815 tests pass

- [ ] **Step 5: Bump version to 1.5.2**

In `Directory.Build.props`, change line 38:
```xml
<PackageVersion>1.5.2</PackageVersion>
```

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Sdk.Hosting/AriConnectionHostedService.cs src/Asterisk.Sdk.Hosting/ServiceCollectionExtensions.cs Directory.Build.props
git commit -m "fix: register AGI + ARI hosted services for automatic lifecycle management

AGI had AgiHostedService but it was never registered in DI.
ARI had no hosted service — client stayed in Initial state forever.
Both now auto-start/stop like AMI's AmiConnectionHostedService."
```

### Task 3: Pack SDK and Update Platform NuGet Reference

- [ ] **Step 1: Pack SDK NuGet packages**

```bash
cd /media/Data/Source/IPcom/Asterisk.Sdk
dotnet pack -c Release -o /tmp/nuget-local/
```

Expected: All packages pack at version 1.5.2

- [ ] **Step 2: Update Platform's Directory.Packages.props**

In `/media/Data/Source/IPcom/Asterisk.Platform/Directory.Packages.props`, find the `Asterisk.Sdk.Hosting` package version and update to `1.5.2`:

```xml
<PackageVersion Include="Asterisk.Sdk.Hosting" Version="1.5.2" />
```

- [ ] **Step 3: Restore and build Platform**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform
dotnet restore
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeded

---

## Phase B: Platform — InMemory RBAC Stores (Bug #2)

> **Working directory:** `/media/Data/Source/IPcom/Asterisk.Platform/`

### Task 4: Create InMemoryPermissionStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryPermissionStore.cs`

- [ ] **Step 1: Create the file**

```csharp
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryPermissionStore : IPermissionStore
{
    private readonly List<PermissionDefinition> _permissions = [];

    public Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(_permissions);

    public Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(
            _permissions.Where(p => p.Category == category).ToList());
}
```

### Task 5: Create InMemoryRoleTemplateStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryRoleTemplateStore.cs`

- [ ] **Step 1: Create the file**

```csharp
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryRoleTemplateStore : IRoleTemplateStore
{
    private readonly List<RoleTemplate> _templates = [];
    private readonly Dictionary<string, List<string>> _permissions = new();

    public Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleTemplate>>(_templates);

    public Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct)
        => Task.FromResult(_templates.FirstOrDefault(t => t.TemplateId == templateId));

    public Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(
            _permissions.GetValueOrDefault(templateId, []));
}
```

### Task 6: Create InMemoryTenantRoleStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantRoleStore.cs`

- [ ] **Step 1: Create the file**

```csharp
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantRoleStore : ITenantRoleStore
{
    private readonly List<TenantRole> _roles = [];
    private readonly Dictionary<string, List<string>> _permissions = new();

    public Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TenantRole>>(
            _roles.Where(r => r.TenantId == tenantId).ToList());

    public Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var role = _roles.FirstOrDefault(r => r.TenantId == tenantId && r.RoleId == roleId);
        if (role is not null)
        {
            var key = $"{tenantId.Value}:{roleId}";
            role.Permissions = _permissions.GetValueOrDefault(key, []);
        }
        return Task.FromResult(role);
    }

    public Task SaveAsync(TenantRole role, CancellationToken ct)
    {
        _roles.RemoveAll(r => r.TenantId == role.TenantId && r.RoleId == role.RoleId);
        _roles.Add(role);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        _roles.RemoveAll(r => r.TenantId == tenantId && r.RoleId == roleId);
        _permissions.Remove($"{tenantId.Value}:{roleId}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct)
    {
        var key = $"{tenantId.Value}:{roleId}";
        return Task.FromResult<IReadOnlyList<string>>(_permissions.GetValueOrDefault(key, []));
    }

    public Task SetPermissionsAsync(TenantId tenantId, string roleId, IReadOnlyList<string> permissionIds, CancellationToken ct)
    {
        var key = $"{tenantId.Value}:{roleId}";
        _permissions[key] = permissionIds.ToList();
        return Task.CompletedTask;
    }

    public Task CloneFromTemplateAsync(TenantId tenantId, string roleId, string templateId, string name, string? description, CancellationToken ct)
    {
        _roles.Add(new TenantRole
        {
            RoleId = roleId,
            TenantId = tenantId,
            Name = name,
            Description = description,
            SourceTemplateId = templateId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }

    public Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct)
        => Task.FromResult(0);
}
```

### Task 7: Create InMemoryUserRoleStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryUserRoleStore.cs`

- [ ] **Step 1: Create the file**

```csharp
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryUserRoleStore : IUserRoleStore
{
    private readonly List<UserRoleAssignment> _assignments = [];

    public Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UserRoleAssignment>>(
            _assignments.Where(a => a.TenantId == tenantId && a.UserId == userId).ToList());

    public Task AssignAsync(TenantId tenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct)
    {
        if (!_assignments.Any(a => a.TenantId == tenantId && a.UserId == userId && a.RoleId == roleId))
        {
            _assignments.Add(new UserRoleAssignment
            {
                TenantId = tenantId,
                UserId = userId,
                RoleId = roleId,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = assignedBy,
            });
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct)
    {
        _assignments.RemoveAll(a => a.TenantId == tenantId && a.UserId == userId && a.RoleId == roleId);
        return Task.CompletedTask;
    }

    public Task ReplaceAllAsync(TenantId tenantId, EntityId userId, IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct)
    {
        _assignments.RemoveAll(a => a.TenantId == tenantId && a.UserId == userId);
        foreach (var roleId in roleIds)
        {
            _assignments.Add(new UserRoleAssignment
            {
                TenantId = tenantId,
                UserId = userId,
                RoleId = roleId,
                AssignedAt = DateTimeOffset.UtcNow,
                AssignedBy = assignedBy,
            });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
```

### Task 8: Register RBAC Stores and Remove Test Duplicates

**Files:**
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs:36-42`
- Delete: `tests/Asterisk.Platform.Api.Tests/InMemoryRbacStores.cs`

- [ ] **Step 1: Add RBAC registrations to AddInMemoryStorage**

In `ServiceCollectionExtensions.cs`, after line 42 (`ITenantAuthConfigStore`), add:

```csharp
        services.AddSingleton<IPermissionStore, InMemoryPermissionStore>();
        services.AddSingleton<IRoleTemplateStore, InMemoryRoleTemplateStore>();
        services.AddSingleton<ITenantRoleStore, InMemoryTenantRoleStore>();
        services.AddSingleton<IUserRoleStore, InMemoryUserRoleStore>();
```

- [ ] **Step 2: Delete the test duplicate file**

```bash
rm tests/Asterisk.Platform.Api.Tests/InMemoryRbacStores.cs
```

- [ ] **Step 3: Build and run all tests**

```bash
dotnet build Asterisk.Platform.slnx
dotnet test Asterisk.Platform.slnx -v q
```

Expected: Build succeeded, 0 warnings in InMemory project, all 1063 tests pass.
The tests use `WebApplicationFactory` which calls `AddInMemoryStorage()`, so they will now get the production InMemory implementations instead of the local test duplicates.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Storage.InMemory/ tests/Asterisk.Platform.Api.Tests/
git commit -m "fix: register InMemory RBAC stores — IUserRoleStore, IPermissionStore, IRoleTemplateStore, ITenantRoleStore

AddInMemoryStorage() registered 27 stores but omitted the 4 RBAC stores
added during v1.1.0. PermissionResolver could not activate without
IUserRoleStore. Moved implementations from test duplicates to production
InMemory storage package."
```

---

## Phase C: Platform — Warnings Cleanup

### Task 9: Fix CA1822 in PasswordService

**Files:**
- Modify: `src/Asterisk.Platform.Api/Services/PasswordService.cs:9,12,15`

- [ ] **Step 1: Add `static` keyword to all three methods**

Line 9: change `public string HashPassword` to `public static string HashPassword`
Line 12: change `public bool VerifyPassword` to `public static bool VerifyPassword`
Line 15: change `public PasswordValidationResult ValidatePolicy` to `public static PasswordValidationResult ValidatePolicy`

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: No CA1822 warnings for PasswordService

### Task 10: Fix CA1822 in MfaService

**Files:**
- Modify: `src/Asterisk.Platform.Api/Services/MfaService.cs:11,17,22,30,31`

- [ ] **Step 1: Add `static` keyword to all five methods**

Line 11: change `public (string Secret, string QrUri) GenerateSetup` to `public static (string Secret, string QrUri) GenerateSetup`
Line 17: change `public bool VerifyCode` to `public static bool VerifyCode`
Line 22: change `public IReadOnlyList<string> GenerateRecoveryCodes` to `public static IReadOnlyList<string> GenerateRecoveryCodes`
Line 30: change `public IReadOnlyList<string> HashRecoveryCodes` to `public static IReadOnlyList<string> HashRecoveryCodes`
Line 31: change `public (bool IsValid, int Index) ValidateRecoveryCode` to `public static (bool IsValid, int Index) ValidateRecoveryCode`

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: No CA1822 warnings for MfaService

### Task 11: Fix CA1822 in PermissionResolver

**Files:**
- Modify: `src/Asterisk.Platform.Api/Services/PermissionResolver.cs:28`

- [ ] **Step 1: Add `static` keyword**

Line 28: change `public bool HasPermission` to `public static bool HasPermission`

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: No CA1822 warnings for PermissionResolver

### Task 12: Suppress CA2012 in RealtimeStateBridgeTests

**Files:**
- Modify: `tests/Asterisk.Platform.Api.Tests/RealtimeStateBridgeTests.cs:113`

- [ ] **Step 1: Add pragma around the mock setup**

Wrap lines 113-114 with:

```csharp
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        _syncService.SyncAgentPausedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => new ValueTask(Task.FromException(new InvalidOperationException("DB unavailable"))));
#pragma warning restore CA2012
```

- [ ] **Step 2: Verify build**

Run: `dotnet build tests/Asterisk.Platform.Api.Tests/`
Expected: No CA2012 warning

### Task 13: Enable TreatWarningsAsErrors and Final Verification

**Files:**
- Modify: `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj:9`
- Modify: `tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj:5`

- [ ] **Step 1: Remove TreatWarningsAsErrors override from Api.csproj**

In `Asterisk.Platform.Api.csproj`, delete line 9:
```xml
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

- [ ] **Step 2: Remove TreatWarningsAsErrors override from Api.Tests.csproj**

In `Asterisk.Platform.Api.Tests.csproj`, delete line 5:
```xml
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
```

- [ ] **Step 3: Full build — zero warnings**

```bash
dotnet build Asterisk.Platform.slnx
```

Expected: Build succeeded, 0 errors, 0 warnings across all projects

- [ ] **Step 4: Full test run**

```bash
dotnet test Asterisk.Platform.slnx -v q
```

Expected: All 1063 tests pass, 0 failures

- [ ] **Step 5: Commit warnings cleanup**

```bash
git add src/Asterisk.Platform.Api/Services/PasswordService.cs src/Asterisk.Platform.Api/Services/MfaService.cs src/Asterisk.Platform.Api/Services/PermissionResolver.cs tests/Asterisk.Platform.Api.Tests/RealtimeStateBridgeTests.cs src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj
git commit -m "fix: resolve all CA1822/CA2012 warnings, enable TreatWarningsAsErrors

Mark 9 stateless utility methods as static (PasswordService, MfaService,
PermissionResolver.HasPermission). Suppress CA2012 false positive in
NSubstitute mock setup. Remove TreatWarningsAsErrors=false overrides
so both projects inherit the global true setting."
```

---

## Phase D: Update NuGet Reference and Final Commit

### Task 14: Update SDK NuGet Version in Platform

**Files:**
- Modify: `Directory.Packages.props` (Asterisk.Sdk.Hosting version)

- [ ] **Step 1: Update Asterisk.Sdk.Hosting version to 1.5.2**

Find the line with `Asterisk.Sdk.Hosting` in `Directory.Packages.props` and change version to `1.5.2`.

- [ ] **Step 2: Restore, build, and test**

```bash
dotnet restore Asterisk.Platform.slnx
dotnet build Asterisk.Platform.slnx
dotnet test Asterisk.Platform.slnx -v q
```

Expected: All pass, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add Directory.Packages.props
git commit -m "chore: bump Asterisk.Sdk.Hosting to 1.5.2 — AGI/ARI auto-start"
```

### Task 15: Update Plan and CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update CLAUDE.md test count if changed**

Verify current test count:
```bash
dotnet test Asterisk.Platform.slnx -v q 2>&1 | grep -E "Passed|Failed"
```

Update the test count in CLAUDE.md header table if it differs from 1063.

- [ ] **Step 2: Mark this plan steps as complete**

Check off all completed steps in this plan file.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/superpowers/plans/2026-03-30-plan24-bugfixes.md
git commit -m "docs: update CLAUDE.md — Plan 24 complete, all warnings resolved"
```
