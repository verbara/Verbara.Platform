# Pro.Realtime Platform Integration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire Pro.Realtime into Asterisk.Platform — replace hardcoded AsteriskRealtimeSyncService with SDK's IRealtimeSyncService, add state bridge (DB+AMI), trunk decorator, 3 new endpoints, and reconciler desired state provider.

**Architecture:** Platform calls IRealtimeSyncService after admin CRUD operations. RealtimeStateBridge subscribes to PlatformEventBus for agent state→QueuePause. TrunkStoreBase decorator auto-syncs trunk CRUD. PlatformDesiredStateProvider feeds the SDK reconciler.

**Tech Stack:** .NET 10, ASP.NET Minimal API, Pro.Realtime SDK (v1.0.0-pro), Dapper, Npgsql, xUnit + FluentAssertions + NSubstitute

**Spec:** `/media/Data/Source/IPcom/Asterisk.Sdk.Pro/docs/specs/2026-03-25-platform-realtime-integration-design.md`

**Target repo:** `/media/Data/Source/IPcom/Asterisk.Platform/`

---

## Phase A: Foundation (Package refs, DI, seed replacement)

### Task 1: Add Pro.Realtime package references + replace DI registration

**Files:**
- Modify: `src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj`
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1:** Add package references to `Asterisk.Platform.Api.csproj`:
```xml
<PackageReference Include="Asterisk.Sdk.Pro.Realtime" />
<PackageReference Include="Asterisk.Sdk.Pro.Realtime.Storage.Postgres" />
```

- [ ] **Step 2:** Add using statements to `Program.cs`:
```csharp
using Asterisk.Sdk.Pro.Realtime;
using Asterisk.Sdk.Pro.Realtime.DependencyInjection;
using Asterisk.Sdk.Pro.Realtime.Storage.Postgres.DependencyInjection;
using Asterisk.Sdk.Pro.Realtime.Models;
using Asterisk.Sdk.Pro.Realtime.Decorators;
using Asterisk.Sdk.Pro.Dialer.Storage.Postgres;
```

- [ ] **Step 3:** Replace `builder.Services.AddSingleton<AsteriskRealtimeSyncService>();` (line ~41) with:
```csharp
// ─── Pro.Realtime (replaces AsteriskRealtimeSyncService) ─────
builder.Services.AddAsteriskRealtime(o =>
{
    o.ReconcilerIntervalSeconds = 60;
    o.EnableAgentPresenceTracking = false;
});
var realtimeConn = dialerConnectionString ?? analyticsConnectionString;
if (!string.IsNullOrEmpty(realtimeConn))
    builder.Services.UsePostgresRealtimeStorage(realtimeConn);
```
Note: Must come AFTER `UsePostgresDialerStorage` and `AddProDialer`.

- [ ] **Step 4:** Add trunk decorator wiring (AFTER the Realtime registration):
```csharp
// Trunk decorator — wraps PostgresTrunkStore with Realtime sync
builder.Services.AddSingleton<TrunkStoreBase>(sp =>
    new RealtimeSyncingTrunkStore(
        new PostgresTrunkStore(sp.GetRequiredService<DialerDbContext>()),
        sp.GetRequiredService<IRealtimeSyncService>()));
```

- [ ] **Step 5:** Build: `dotnet build src/Asterisk.Platform.Api/`

- [ ] **Step 6:** Commit: `feat(api): add Pro.Realtime package references and DI registration`

---

### Task 2: Update demo seed to use IRealtimeSyncService

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs` (demo seed section ~lines 319-334)

- [ ] **Step 1:** Replace the demo seed code. Change `AsteriskRealtimeSyncService` to `IRealtimeSyncService`:
```csharp
var syncService = app.Services.GetService<IRealtimeSyncService>();
if (syncService is not null)
{
    try
    {
        await syncService.ProvisionTenantAsync("demo");
        await syncService.SyncAgentAsync("demo", "demo-agent", "Demo Agent", "2001", "2001");
        await syncService.SyncQueueAsync("demo", "support", new RealtimeQueueOptions
        {
            Timeout = 30, Wrapuptime = 15, Servicelevel = 20
        });
        await syncService.AddQueueMemberAsync("demo", "support", "demo-agent", "Demo Agent");
        Console.WriteLine("Asterisk Realtime: demo tenant provisioned.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Asterisk Realtime sync skipped: {ex.Message}");
    }
}
```

- [ ] **Step 2:** Remove the `using Asterisk.Platform.Api.Services;` import for `AsteriskRealtimeSyncService` if it's only used for that.

- [ ] **Step 3:** Build to verify.

- [ ] **Step 4:** Commit: `feat(api): update demo seed to use IRealtimeSyncService`

---

### Task 3: Delete AsteriskRealtimeSyncService

**Files:**
- Delete: `src/Asterisk.Platform.Api/Services/AsteriskRealtimeSyncService.cs`

- [ ] **Step 1:** Delete the file.

- [ ] **Step 2:** Build entire solution to verify no remaining references: `dotnet build`

- [ ] **Step 3:** If build fails, fix any remaining `AsteriskRealtimeSyncService` references (search for the type name across the solution).

- [ ] **Step 4:** Commit: `refactor(api): remove AsteriskRealtimeSyncService (replaced by Pro.Realtime)`

---

## Phase B: Endpoint Wiring (Agent, Queue, Trunk sync)

### Task 4: Extend AdminEndpoints — agent CRUD sync + DeleteAgent

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AdminEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/AdminEndpointRealtimeTests.cs`

- [ ] **Step 1:** Write tests: CreateAgent with Extension+SipPassword calls SyncAgentAsync. UpdateAgent with changed Extension calls SyncAgentAsync. DeleteAgent calls RemoveAgentAsync then store delete.

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** Add `Extension` and `SipPassword` to `CreateAgentRequest` and `UpdateAgentRequest` records (inside AdminEndpoints.cs or wherever they're defined).

- [ ] **Step 4:** In `CreateAgent` handler: after `store.SaveAsync(agent, ct)`, add sync call:
```csharp
if (!string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword))
{
    var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        try { await syncService.SyncAgentAsync(tenantId, agent.AgentId.Value, agent.DisplayName, agent.Extension, agent.SipPassword, ct: ct); }
        catch (Exception ex) { /* log best-effort */ }
    }
}
```

- [ ] **Step 5:** In `UpdateAgent` handler: same pattern — sync if Extension or SipPassword changed.

- [ ] **Step 6:** Add `DeleteAgent` endpoint:
```csharp
group.MapDelete("/agents/{id}", DeleteAgent);

private static async Task<IResult> DeleteAgent(
    string id, HttpContext context, IAgentStore store, CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var agent = await store.GetByIdAsync(new TenantId(tenantId), EntityId.From(id), ct);
    if (agent is null) return Results.NotFound();

    var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        try { await syncService.RemoveAgentAsync(tenantId, agent.AgentId.Value, ct); }
        catch { }
    }

    await store.DeleteAsync(new TenantId(tenantId), EntityId.From(id), ct);
    return Results.NoContent();
}
```

- [ ] **Step 7:** Run tests — verify pass.

- [ ] **Step 8:** Commit: `feat(api): wire agent CRUD to Realtime sync + add DeleteAgent endpoint`

---

### Task 5: Extend AdminEndpoints — queue CRUD sync

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AdminEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/AdminEndpointQueueSyncTests.cs`

- [ ] **Step 1:** Write tests: CreateQueue calls SyncQueueAsync with correct RealtimeQueueOptions mapping. UpdateQueue re-syncs. DeleteQueue calls RemoveQueueAsync.

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** In `CreateQueue` handler, after `store.SaveAsync()`:
```csharp
var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
if (syncService is not null)
{
    try
    {
        var opts = new RealtimeQueueOptions
        {
            Timeout = 30,
            Wrapuptime = queue.WrapUp?.DefaultWrapUpSeconds ?? 15,
            Servicelevel = queue.SlaTargets?.AnswerWithinSeconds ?? 20,
            Maxlen = queue.MaxWaiting ?? 0,
        };
        await syncService.SyncQueueAsync(tenantId, queue.Name, opts, ct);
    }
    catch { }
}
```

- [ ] **Step 4:** Same for `UpdateQueue`. In `DeleteQueue`, call `RemoveQueueAsync` before store delete.

- [ ] **Step 5:** Run tests — verify pass.

- [ ] **Step 6:** Commit: `feat(api): wire queue CRUD to Realtime sync`

---

### Task 6: Add QueueMember endpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/AdminEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/QueueMemberEndpointTests.cs`

- [ ] **Step 1:** Write tests: AddQueueMember calls AddQueueMemberAsync. RemoveQueueMember calls RemoveQueueMemberAsync.

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** Add DTO and endpoints:
```csharp
internal sealed record AddQueueMemberRequest(string QueueId, string AgentId, int? Penalty = null);

group.MapPost("/queue-members", AddQueueMember);
group.MapDelete("/queue-members/{queueId}/{agentId}", RemoveQueueMember);

private static async Task<IResult> AddQueueMember(
    HttpContext context, AddQueueMemberRequest body,
    IQueueStore queueStore, IAgentStore agentStore, CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var queue = await queueStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(body.QueueId), ct);
    var agent = await agentStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(body.AgentId), ct);
    if (queue is null || agent is null) return Results.NotFound();

    var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        await syncService.AddQueueMemberAsync(tenantId, queue.Name, agent.AgentId.Value, agent.DisplayName, body.Penalty ?? 0, ct);
    }
    return Results.Created($"/api/admin/queue-members/{body.QueueId}/{body.AgentId}", null);
}

private static async Task<IResult> RemoveQueueMember(
    string queueId, string agentId, HttpContext context,
    IQueueStore queueStore, IAgentStore agentStore, CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var queue = await queueStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(queueId), ct);
    var agent = await agentStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(agentId), ct);
    if (queue is null || agent is null) return Results.NotFound();

    var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
    if (syncService is not null)
    {
        await syncService.RemoveQueueMemberAsync(tenantId, queue.Name, agent.AgentId.Value, ct);
    }
    return Results.NoContent();
}
```

- [ ] **Step 4:** Run tests — verify pass.

- [ ] **Step 5:** Add `[JsonSerializable(typeof(AddQueueMemberRequest))]` to `ApiJsonContext.cs`.

- [ ] **Step 6:** Commit: `feat(api): add queue member management endpoints`

---

### Task 7: Extend TrunkEndpoints with PJSIP fields

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/TrunkEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/TrunkEndpointPjsipTests.cs`

- [ ] **Step 1:** Write tests: CreateTrunk with PJSIP fields (Transport, Codecs, etc.) persists correctly. TrunkDto response includes PJSIP fields.

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** Extend DTOs in TrunkEndpoints.cs:
```csharp
// Extend TrunkDto
internal sealed record TrunkDto(
    long Id, string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels,
    string? Transport, string? Codecs, string? AuthUsername, string? RegistrationUri,
    string? ClientUri, string? Context);

// Extend CreateTrunkRequest
internal sealed record CreateTrunkRequest(
    string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels,
    string? Transport, string? Codecs, string? AuthUsername, string? AuthPassword,
    string? RegistrationUri, string? ClientUri, string? Context);

// Update MapToDto to include PJSIP fields
private static TrunkDto MapToDto(Trunk t) => new(
    t.Id, t.Name, t.DisplayName, t.Type.ToString(), t.IsActive, t.MaxChannels,
    t.Transport, t.Codecs, t.AuthUsername, t.RegistrationUri, t.ClientUri, t.Context);
```

- [ ] **Step 4:** Update `CreateTrunk` handler to map PJSIP fields from request to Trunk model. Update `UpdateTrunk` similarly.

- [ ] **Step 5:** Note: Trunk sync is automatic via `RealtimeSyncingTrunkStore` decorator. No explicit sync calls needed in endpoints.

- [ ] **Step 6:** Update `ApiJsonContext.cs` with `[JsonSerializable(typeof(TrunkDto))]` if not already present.

- [ ] **Step 7:** Run tests — verify pass.

- [ ] **Step 8:** Commit: `feat(api): extend trunk endpoints with PJSIP provisioning fields`

---

## Phase C: State Bridge + Reconciler

### Task 8: RealtimeStateBridge — agent state → DB + AMI

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/RealtimeStateBridge.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/RealtimeStateBridgeTests.cs`

- [ ] **Step 1:** Write tests:
  - Available → shouldPause=false, calls SyncAgentPausedAsync(false) + QueuePauseAction(Paused=false)
  - Break → shouldPause=true, calls SyncAgentPausedAsync(true) + QueuePauseAction(Paused=true)
  - All 8 states mapped correctly (Available, Busy = unpause; Break, Lunch, Training, ACW, DND, Offline = pause)
  - DB failure: SyncAgentPausedAsync throws, AMI still attempted
  - AMI failure: SendActionAsync throws, no exception propagates
  - Non-AgentStateChangedEvent ignored
  - Per-agent serialization: concurrent events for same agent processed in order

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** Implement `RealtimeStateBridge` per spec Section 4.1. Use `ConcurrentDictionary<string, SemaphoreSlim>` for per-agent serialization. Subscribe to `PlatformEventBus.Events`. Filter `AgentStateChangedEvent`. Call `SyncAgentPausedAsync` (DB) then `QueuePauseAction` (AMI), both best-effort.

- [ ] **Step 4:** Register in Program.cs: `builder.Services.AddHostedService<RealtimeStateBridge>();`

- [ ] **Step 5:** Run tests — verify pass.

- [ ] **Step 6:** Commit: `feat(api): implement RealtimeStateBridge for agent state → DB + AMI QueuePause`

---

### Task 9: PlatformDesiredStateProvider — reconciler data source

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/PlatformDesiredStateProvider.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/PlatformDesiredStateProviderTests.cs`

- [ ] **Step 1:** Write tests:
  - GetActiveTenantIdsAsync returns configured tenant IDs (default: ["demo"])
  - GetExpectedAgentsAsync returns agents with Extension+SipPassword mapped to AgentSyncRequest
  - GetExpectedAgentsAsync filters out agents without Extension
  - GetExpectedQueuesAsync returns active queues mapped to QueueSyncRequest with correct RealtimeQueueOptions

- [ ] **Step 2:** Run tests — verify fail.

- [ ] **Step 3:** Implement `PlatformDesiredStateProvider : IDesiredStateProvider`. Inject `IAgentStore`, `IQueueStore`, `IConfiguration`. Read tenant IDs from `configuration.GetSection("Realtime:TenantIds")` with fallback to `["demo"]`.

- [ ] **Step 4:** Register in Program.cs: `builder.Services.AddSingleton<IDesiredStateProvider, PlatformDesiredStateProvider>();`

- [ ] **Step 5:** Run tests — verify pass.

- [ ] **Step 6:** Commit: `feat(api): implement PlatformDesiredStateProvider for Realtime reconciler`

---

## Phase D: Final Verification

### Task 10: Full integration test + verification

**Files:**
- Test: `tests/Asterisk.Platform.Api.Tests/RealtimeIntegrationTests.cs`

- [ ] **Step 1:** Write integration test using `WebApplicationFactory` or DI container:
  - Verify IRealtimeSyncService is resolved from DI
  - Verify TrunkStoreBase resolves to RealtimeSyncingTrunkStore (decorator)
  - Verify RealtimeStateBridge is registered as hosted service
  - Verify PlatformDesiredStateProvider is registered

- [ ] **Step 2:** Run the integration test.

- [ ] **Step 3:** Run ALL Platform tests: `dotnet test` — verify 0 failures.

- [ ] **Step 4:** Report total test count (existing ~123 + ~39 new ≈ ~162).

- [ ] **Step 5:** Commit: `feat(api): add Realtime integration tests and verify full solution`

- [ ] **Step 6:** Update plan file: mark all tasks complete.
