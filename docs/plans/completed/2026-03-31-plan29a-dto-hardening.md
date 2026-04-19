# Plan 29A: Anonymous DTO Hardening

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace all 61 anonymous `new { }` patterns in API endpoints with typed sealed records registered in `ApiJsonContext`, ensuring full Native AOT compatibility.

**Architecture:** Introduce a shared `ErrorResponse` record for the ~43 error responses, plus ~9 specific DTOs for structured responses. All registered in the existing `ApiJsonContext` source generator. Mechanical refactor — no behavior changes.

**Tech Stack:** .NET 10, System.Text.Json source generation, sealed records.

**Spec:** `docs/superpowers/specs/2026-03-31-v121-operations-design.md` — Sub-project E.

---

### Task 1: Create shared ErrorResponse and MessageResponse DTOs

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/Shared/ErrorResponse.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

- [ ] **Step 1: Create the shared DTOs file**

```csharp
// src/Asterisk.Platform.Api/Endpoints/Shared/ErrorResponse.cs
namespace Asterisk.Platform.Api.Endpoints.Shared;

internal sealed record ErrorResponse(string Error);

internal sealed record ErrorDetailResponse(string Error, IReadOnlyList<string> Details);

internal sealed record MessageResponse(string Message);

internal sealed record StatusUpdateResponse(string Id, string Status);
```

- [ ] **Step 2: Register in ApiJsonContext**

Add these lines to `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`, inside the `[JsonSerializable]` attribute block (before the closing `]` of the class):

```csharp
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ErrorDetailResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(StatusUpdateResponse))]
```

Add the using at the top:

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/Shared/ErrorResponse.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: add shared ErrorResponse, MessageResponse, StatusUpdateResponse DTOs for AOT"
```

---

### Task 2: Refactor AuthEndpoints.cs (22 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

**Context:** AuthEndpoints.cs has 22 `new {` instances. 11 are error responses, 5 are message responses, 2 are auth event logging details, and 4 are structured responses needing specific DTOs.

- [ ] **Step 1: Add MfaChallengeResponse DTO to AuthEndpoints.cs**

Add at the bottom of `AuthEndpoints.cs` (after the last closing brace of the class, but inside the namespace):

```csharp
internal sealed record MfaChallengeResponse(bool MfaRequired, string ChallengeToken);
```

Register in `ApiJsonContext.cs`:

```csharp
[JsonSerializable(typeof(MfaChallengeResponse))]
```

- [ ] **Step 2: Replace all error responses**

Replace every `new { error = "..." }` with `new ErrorResponse("...")`.
Replace every `new { error = "...", details = ... }` with `new ErrorDetailResponse("...", ...)`.

Add using at top of file:

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Specific replacements (line numbers are approximate — match the exact string):

| Original | Replacement |
|----------|-------------|
| `new { error = "Tenant identification required..." }` | `new ErrorResponse("Tenant identification required...")` |
| `new { error = "Account is locked" }` | `new ErrorResponse("Account is locked")` |
| `new { error = "Invalid or expired challenge token" }` | `new ErrorResponse("Invalid or expired challenge token")` |
| `new { error = "Challenge token expired" }` | `new ErrorResponse("Challenge token expired")` |
| `new { error = "Current password is incorrect" }` | `new ErrorResponse("Current password is incorrect")` |
| `new { error = "Password does not meet policy", details = validation.Errors }` | `new ErrorDetailResponse("Password does not meet policy", validation.Errors)` |
| `new { error = "Invalid or expired reset token" }` | `new ErrorResponse("Invalid or expired reset token")` |
| `new { error = "Reset token expired" }` | `new ErrorResponse("Reset token expired")` |
| `new { error = "Invalid reset token" }` | `new ErrorResponse("Invalid reset token")` |
| `new { error = "MFA setup not initiated" }` | `new ErrorResponse("MFA setup not initiated")` |
| `new { error = "Invalid verification code" }` | `new ErrorResponse("Invalid verification code")` |
| `new { error = "Invalid password" }` | `new ErrorResponse("Invalid password")` |

- [ ] **Step 3: Replace message responses**

| Original | Replacement |
|----------|-------------|
| `new { message = "Logged out" }` | `new MessageResponse("Logged out")` |
| `new { message = "Password changed" }` | `new MessageResponse("Password changed")` |
| `new { message = "If the email exists, a reset link has been sent" }` | `new MessageResponse("If the email exists, a reset link has been sent")` |
| `new { message = "Password reset successful" }` | `new MessageResponse("Password reset successful")` |
| `new { message = "MFA enabled" }` | `new MessageResponse("MFA enabled")` |
| `new { message = "MFA disabled" }` | `new MessageResponse("MFA disabled")` |

- [ ] **Step 4: Replace MFA challenge response**

| Original | Replacement |
|----------|-------------|
| `new { mfaRequired = true, challengeToken }` | `new MfaChallengeResponse(true, challengeToken)` |

- [ ] **Step 5: Replace auth event logging details**

These are passed to `AuthEventService.LogAsync(..., details: ...)`. They need typed DTOs too for AOT serialization.

Add at bottom of AuthEndpoints.cs:

```csharp
internal sealed record AuthEventDetail(string? Email = null, string? Reason = null);
```

Register in `ApiJsonContext.cs`:

```csharp
[JsonSerializable(typeof(AuthEventDetail))]
```

Replacements:

| Original | Replacement |
|----------|-------------|
| `new { email = body.Email, reason = "invalid_credentials" }` | `new AuthEventDetail(Email: body.Email, Reason: "invalid_credentials")` |
| `new { reason = "invalid_password" }` | `new AuthEventDetail(Reason: "invalid_password")` |

- [ ] **Step 6: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 7: Run tests**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/AuthEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: replace 22 anonymous types in AuthEndpoints with typed DTOs"
```

---

### Task 3: Refactor ManagementSystemEndpoints.cs (5 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

**Context:** All 5 instances are structured responses with multiple fields. Need 3 new DTOs.

- [ ] **Step 1: Add DTOs at bottom of ManagementSystemEndpoints.cs**

```csharp
internal sealed record SystemInfoDto(
    string Version,
    string? HostTenantId,
    string? PlatformName,
    IReadOnlyDictionary<string, bool> Features);

internal sealed record LicenseInfoDto(
    string Tier,
    IReadOnlyList<string> Features,
    int MaxAgents,
    string? Message = null);

internal sealed record SystemSettingsDto(
    string? PlatformName,
    string? DefaultTimezone,
    string? DefaultLanguage);
```

- [ ] **Step 2: Register in ApiJsonContext**

```csharp
[JsonSerializable(typeof(SystemInfoDto))]
[JsonSerializable(typeof(LicenseInfoDto))]
[JsonSerializable(typeof(SystemSettingsDto))]
```

- [ ] **Step 3: Replace anonymous types**

Replace the 5 `new { ... }` patterns with the typed DTOs. Match field names to the constructor parameters.

For GET system info:
```csharp
// Before: new { version = "1.1.0", hostTenantId = ..., platformName = ..., features = ... }
// After:
new SystemInfoDto("1.1.0", hostTenantId?.Value, platformName, features)
```

For GET license:
```csharp
// Before: new { tier = "community", features = Array.Empty<string>(), maxAgents = 10 }
// After:
new LicenseInfoDto("community", Array.Empty<string>(), 10)
```

For POST license/activate:
```csharp
// Before: new { tier = "community", features = ..., message = "License activation not yet implemented." }
// After:
new LicenseInfoDto("community", Array.Empty<string>(), 0, "License activation not yet implemented.")
```

For GET/PUT settings:
```csharp
// Before: new { platformName = ..., defaultTimezone = ..., defaultLanguage = ... }
// After:
new SystemSettingsDto(platformName, defaultTimezone, defaultLanguage)
```

- [ ] **Step 4: Add using**

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

- [ ] **Step 5: Verify build and tests**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: Build succeeds, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementSystemEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: replace 5 anonymous types in ManagementSystemEndpoints with typed DTOs"
```

---

### Task 4: Refactor OidcEndpoints.cs (8 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs`

**Context:** 7 error responses + 1 message response. All use shared DTOs already created.

- [ ] **Step 1: Add using and replace all instances**

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Replace 7 error patterns:
| Original | Replacement |
|----------|-------------|
| `new { error = "tenant_id query parameter is required" }` | `new ErrorResponse("tenant_id query parameter is required")` |
| `new { error = "OIDC is not enabled for this tenant" }` | `new ErrorResponse("OIDC is not enabled for this tenant")` |
| `new { error = "Missing tenant_id (state parameter)" }` | `new ErrorResponse("Missing tenant_id (state parameter)")` |
| `new { error = $"OIDC provider error: {error}" }` | `new ErrorResponse($"OIDC provider error: {error}")` |
| `new { error = "Missing authorization code" }` | `new ErrorResponse("Missing authorization code")` |
| `new { error = "OIDC is not enabled for this tenant" }` | `new ErrorResponse("OIDC is not enabled for this tenant")` |
| `new { error = "OIDC token exchange not yet implemented" }` | `new ErrorResponse("OIDC token exchange not yet implemented")` |

Replace 1 message pattern:
| Original | Replacement |
|----------|-------------|
| `new { message = "Logged out" }` | `new MessageResponse("Logged out")` |

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/OidcEndpoints.cs
git commit -m "refactor: replace 8 anonymous types in OidcEndpoints with typed DTOs"
```

---

### Task 5: Refactor ManagementTenantEndpoints.cs (8 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs`

**Context:** 6 error responses + 2 status update responses.

- [ ] **Step 1: Add using and replace all instances**

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Replace 6 error patterns with `new ErrorResponse("...")`.

Replace 2 status patterns:
| Original | Replacement |
|----------|-------------|
| `new { tenantId = id, status = "Suspended" }` | `new StatusUpdateResponse(id, "Suspended")` |
| `new { tenantId = id, status = "Active" }` | `new StatusUpdateResponse(id, "Active")` |

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantEndpoints.cs
git commit -m "refactor: replace 8 anonymous types in ManagementTenantEndpoints with typed DTOs"
```

---

### Task 6: Refactor RbacEndpoints.cs (5 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/RbacEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

**Context:** 2 error responses + 2 permission grouping responses + 1 user permissions response.

- [ ] **Step 1: Add DTOs at bottom of RbacEndpoints.cs**

```csharp
internal sealed record PermissionGroupDto(string Category, IReadOnlyList<string> Permissions);

internal sealed record UserPermissionsDto(string UserId, IReadOnlyList<string> Permissions);
```

- [ ] **Step 2: Register in ApiJsonContext**

```csharp
[JsonSerializable(typeof(PermissionGroupDto))]
[JsonSerializable(typeof(List<PermissionGroupDto>))]
[JsonSerializable(typeof(UserPermissionsDto))]
```

- [ ] **Step 3: Add using and replace all instances**

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Replace 2 error patterns with `new ErrorResponse("...")`.

Replace permission grouping (appears in a `.Select()` LINQ):
```csharp
// Before: .Select(g => new { Category = g.Key, Permissions = g.ToList() })
// After:
.Select(g => new PermissionGroupDto(g.Key, g.ToList()))
```

Replace user permissions:
```csharp
// Before: new { UserId = id, Permissions = permissions.Order().ToList() }
// After:
new UserPermissionsDto(id, permissions.Order().ToList())
```

- [ ] **Step 4: Verify build and tests**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: Build succeeds, all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/RbacEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: replace 5 anonymous types in RbacEndpoints with typed DTOs"
```

---

### Task 7: Refactor ManagementBillingEndpoints.cs (4 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs`

**Context:** 3 error responses + 1 status update response.

- [ ] **Step 1: Add using and replace all instances**

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Replace 3 error patterns with `new ErrorResponse("...")`.

Replace status update:
```csharp
// Before: new { invoiceId = id, status = "Issued" }
// After:
new StatusUpdateResponse(id, "Issued")
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementBillingEndpoints.cs
git commit -m "refactor: replace 4 anonymous types in ManagementBillingEndpoints with typed DTOs"
```

---

### Task 8: Refactor ChannelConfigEndpoints.cs (3 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ChannelConfigEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

**Context:** 2 channel status responses + 1 success message.

- [ ] **Step 1: Add DTO at bottom of ChannelConfigEndpoints.cs**

```csharp
internal sealed record ChannelStatusDto(string Channel, bool IsActive);
```

- [ ] **Step 2: Register in ApiJsonContext**

```csharp
[JsonSerializable(typeof(ChannelStatusDto))]
[JsonSerializable(typeof(List<ChannelStatusDto>))]
```

- [ ] **Step 3: Add using and replace all instances**

Add using:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

Replace 2 channel status patterns:
```csharp
// Before: new { channel = channelType.ToString(), isActive = false }
// After:
new ChannelStatusDto(channelType.ToString(), false)
```

Replace success message:
```csharp
// Before: new { success = true, message = "Connection test passed" }
// After:
new MessageResponse("Connection test passed")
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ChannelConfigEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: replace 3 anonymous types in ChannelConfigEndpoints with typed DTOs"
```

---

### Task 9: Refactor AnalyticsEndpoints.cs (2 instances)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AnalyticsEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`

**Context:** 2 paged list responses with identical structure.

- [ ] **Step 1: Add DTO at bottom of AnalyticsEndpoints.cs**

```csharp
internal sealed record PagedListResponse<T>(IReadOnlyList<T> Data, bool HasMore, int Page, int PageSize);
```

Note: Since generic types can't be registered in JsonSerializable, use a concrete type instead:

```csharp
internal sealed record PagedCdrResponse(IReadOnlyList<CdrDto> Data, bool HasMore, int Page, int PageSize);
internal sealed record PagedQaResponse(IReadOnlyList<QaDto> Data, bool HasMore, int Page, int PageSize);
```

Check the actual DTO types used in the file (likely `CdrSummaryDto` or similar) and match them.

- [ ] **Step 2: Register in ApiJsonContext**

Register the concrete paged response types used.

- [ ] **Step 3: Replace all instances**

Replace both paged responses:
```csharp
// Before: new { Data = dtos, HasMore = hasMore, Page = page, PageSize = pageSize }
// After:
new PagedCdrResponse(dtos, hasMore, page, pageSize)
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeds, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/AnalyticsEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "refactor: replace 2 anonymous types in AnalyticsEndpoints with typed DTOs"
```

---

### Task 10: Refactor remaining 6 files (11 instances total)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/MediaEndpoints.cs` (3 errors)
- Modify: `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs` (2 errors)
- Modify: `src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs` (2 errors)
- Modify: `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs` (2 errors)
- Modify: `src/Asterisk.Platform.Api/Endpoints/AuthAdminEndpoints.cs` (1 error)
- Modify: `src/Asterisk.Platform.Api/Endpoints/SupervisorEndpoints.cs` (1 error)

**Context:** All 11 instances are simple error responses using `new { error = "..." }`. All use the shared `ErrorResponse` DTO.

- [ ] **Step 1: Add using to all 6 files**

Add to each file:
```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
```

- [ ] **Step 2: Replace all `new { error = "..." }` with `new ErrorResponse("...")`**

**MediaEndpoints.cs:**
- `new { error = "Request must be multipart/form-data" }` → `new ErrorResponse("Request must be multipart/form-data")`
- `new { error = "No file provided or file is empty" }` → `new ErrorResponse("No file provided or file is empty")`
- `new { error = ex.Message }` → `new ErrorResponse(ex.Message)`

**ConversationEndpoints.cs:**
- `new { error = "Either targetQueueId or targetAgentId must be specified" }` → `new ErrorResponse("Either targetQueueId or targetAgentId must be specified")`
- `new { error = ex.Message }` → `new ErrorResponse(ex.Message)`

**SetupEndpoints.cs:**
- `new { error = "Platform already initialized." }` → `new ErrorResponse("Platform already initialized.")`
- `new { error = "Email and password are required." }` → `new ErrorResponse("Email and password are required.")`

**WebhookEndpoints.cs:**
- `new { error = $"Unknown channel: {channel}" }` → `new ErrorResponse($"Unknown channel: {channel}")`
- `new { error = ex.Message }` → `new ErrorResponse(ex.Message)`

**AuthAdminEndpoints.cs:**
- `new { error = "userId query parameter is required" }` → `new ErrorResponse("userId query parameter is required")`

**SupervisorEndpoints.cs:**
- `new { error = "Whisper delivery is not enabled for this session." }` → `new ErrorResponse("Whisper delivery is not enabled for this session.")`

- [ ] **Step 3: Verify build and run full test suite**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: Build succeeds, all 1162 tests pass, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/MediaEndpoints.cs src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs src/Asterisk.Platform.Api/Endpoints/SetupEndpoints.cs src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs src/Asterisk.Platform.Api/Endpoints/AuthAdminEndpoints.cs src/Asterisk.Platform.Api/Endpoints/SupervisorEndpoints.cs
git commit -m "refactor: replace 11 anonymous types in 6 remaining endpoint files with typed DTOs"
```

---

### Task 11: Verification — zero anonymous types remaining

**Files:**
- None modified

- [ ] **Step 1: Grep for remaining anonymous types**

Run: `grep -rn "new {" src/Asterisk.Platform.Api/Endpoints/ --include="*.cs" | grep -v "new Dictionary" | grep -v "new List" | grep -v "new HashSet" | grep -v "//" | grep -v "new {}" | head -20`

Expected: **0 results** (no anonymous object creation in endpoint files).

- [ ] **Step 2: Full solution build + test**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: Build succeeds, all tests pass, 0 warnings.

- [ ] **Step 3: Document completion**

Update `docs/superpowers/plans/2026-03-31-plan29a-dto-hardening.md` — mark all tasks complete.
