# Plan 24: Bug Fixes & Warnings

**Date:** 2026-03-30
**Scope:** Verbara.Platform + Verbara.Sdk (two repos)
**Goal:** Fix 3 runtime bugs and eliminate all compiler warnings

## Context

After completing v1.1.0 (Plans 1-23), four pre-existing issues were identified. Investigation on 2026-03-30 confirmed root causes for three bugs and reclassified one as not-a-bug. Additionally, 10 compiler warnings exist that should be resolved.

## Bug #2: IUserRoleStore Not Registered in DI

### Root Cause

`AddInMemoryStorage()` in `Platform.Storage.InMemory/ServiceCollectionExtensions.cs` registers 27 stores but omits the 4 RBAC stores added during v1.1.0:

- `IPermissionStore`
- `IRoleTemplateStore`
- `ITenantRoleStore`
- `IUserRoleStore`

The Postgres storage registers all four (lines 86-90 in `Storage.Postgres/ServiceCollectionExtensions.cs`). When the API runs with in-memory storage (default for dev/demo), `PermissionResolver` cannot be activated because `IUserRoleStore` is missing.

### Fix

Move the 4 in-memory implementations from `tests/Verbara.Platform.Api.Tests/InMemoryRbacStores.cs` to individual files in `src/Verbara.Platform.Storage.InMemory/`:

| New File | Interface | Source (test lines) |
|----------|-----------|-------------------|
| `InMemoryPermissionStore.cs` | `IPermissionStore` | Lines 6-16 |
| `InMemoryRoleTemplateStore.cs` | `IRoleTemplateStore` | Lines 18-32 |
| `InMemoryTenantRoleStore.cs` | `ITenantRoleStore` | Lines 34-97 |
| `InMemoryUserRoleStore.cs` | `IUserRoleStore` | Lines 99-148 |

Register all four as singletons in `AddInMemoryStorage()`.

Update tests to import from the storage package instead of maintaining local duplicates (delete `InMemoryRbacStores.cs` from tests, add project reference if needed).

### Verification

- `PermissionResolver` resolves without error
- RBAC endpoints return proper responses with in-memory storage
- All 1063 tests pass

## Bug #1: AGI/ARI Health Returns 503

### Root Cause (in Verbara.Sdk)

In `Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs`:

1. **AGI**: `AgiHostedService` exists (`Verbara.Sdk.Agi/Hosting/AgiHostedService.cs`) but is never registered. The AGI server stays in `Stopped` state.
2. **ARI**: No hosted service exists. `AriClient` starts in `Initial` state and `ConnectAsync()` is never called. The health check reports `Unhealthy` for any state other than `Connected`.
3. **AMI works** because `AmiConnectionHostedService` is registered (line 73) and calls `ConnectAsync()` on startup.

### Fix (Verbara.Sdk repo)

**AGI** — Register the existing hosted service in `AddAsterisk()`:
```csharp
// After line 66 (AGI health check registration)
services.AddSingleton<IHostedService, Verbara.Sdk.Agi.Hosting.AgiHostedService>();
```

**ARI** — Create `AriConnectionHostedService` following the `AmiConnectionHostedService` pattern:
```csharp
// File: src/Verbara.Sdk.Hosting/AriConnectionHostedService.cs
public sealed class AriConnectionHostedService(IAriClient client) : IHostedService
{
    public async Task StartAsync(CancellationToken ct) =>
        await client.ConnectAsync(ct);

    public async Task StopAsync(CancellationToken ct) =>
        await client.DisconnectAsync(ct);
}
```

Register in the `if (options.Ari is not null)` block after the health check:
```csharp
// After line 121 (ARI health check registration)
services.AddSingleton<IHostedService, AriConnectionHostedService>();
```

**SDK version bump:** 1.5.1 → 1.5.2

### Verification

- `GET /health` returns 200 when Asterisk is reachable
- AGI state transitions to `Listening`
- ARI state transitions to `Connected`
- Health degrades gracefully when Asterisk is down (503 with meaningful state)

## Bug #3: Tenant Login — Not a Bug

### Investigation Result

The `POST /api/auth/login` endpoint accepts `tenantId` as a **body parameter** in `LoginRequest(string TenantId, string Email, string Password)`. The `TenantResolutionMiddleware` does not block — it only sets `context.Items["TenantId"]` from the `X-Tenant-Id` header if present. The login endpoint reads tenant from the request body independently.

The 400 error occurs when clients omit `tenantId` from the body. This is correct behavior — no code change needed.

### Action

No code changes. Document in demo scripts that login requires `tenantId` in body:
```json
{ "tenantId": "demo", "email": "admin@demo.com", "password": "Admin123!" }
```

## Warnings: CA1822 & CA2012

### CA1822 — Instance Methods That Can Be Static (9 warnings)

All in `src/Verbara.Platform.Api/Services/`:

| Service | Methods | Action |
|---------|---------|--------|
| `PasswordService.cs` | `HashPassword`, `VerifyPassword`, `ValidatePolicy` | Mark `static` |
| `MfaService.cs` | `GenerateSetup`, `VerifyCode`, `GenerateRecoveryCodes`, `HashRecoveryCodes`, `ValidateRecoveryCode` | Mark `static` |
| `PermissionResolver.cs` | `HasPermission` | Mark `static` (class retains instance state for other members) |

These services are registered as singletons via DI. Making methods static does not break DI — callers access them through the injected instance (e.g., `passwordService.HashPassword(...)` works on static methods via instance syntax). No call-site changes needed.

### CA2012 — ValueTask Not Awaited (1 warning)

In `tests/Verbara.Platform.Api.Tests/RealtimeStateBridgeTests.cs` line 113: false positive from NSubstitute's `.Returns()` mock setup creating a `ValueTask`. The ValueTask is consumed when the mocked method is called in production code.

**Action:** Suppress with `#pragma warning disable CA2012` around the mock setup.

### TreatWarningsAsErrors

Both `Verbara.Platform.Api.csproj` and `Verbara.Platform.Api.Tests.csproj` override `Directory.Build.props` with `TreatWarningsAsErrors=false`. After resolving all warnings, remove this override so they inherit `true` from the global config.

### Verification

- `dotnet build` produces 0 warnings
- All 1063 tests pass

## Scope Summary

| Item | Repo | Files Changed | Risk |
|------|------|--------------|------|
| RBAC stores in InMemory | Platform | ~6 files | Low — moving tested code |
| AGI hosted service registration | SDK | 1 file | Low — registering existing class |
| ARI hosted service + registration | SDK | 2 files | Low — follows AMI pattern |
| CA1822 fixes | Platform | 3 files | Low — adding `static` keyword |
| CA2012 suppression | Platform | 1 file | None — pragma on false positive |
| TreatWarningsAsErrors cleanup | Platform | 2 files | Low — removing override |

## Out of Scope

- Tenant login changes (not a bug)
- SDK test additions for new hosted service (can be a follow-up)
- API test changes beyond removing duplicate RBAC stores
