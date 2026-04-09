# v1.4.1 "Core Operations" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the contact center operationally functional — ACD distribution, real-time events, conversation timeouts, persistent capacity, and missing Postgres stores.

**Architecture:** Dual ACD model: Asterisk `app_queue` for voice (native), Platform `QueueDistributionWorker` for digital channels (push with offer/accept/timeout). Unified `AgentCapacityTracker` in Postgres coordinates both via AMI sync. SSE events wired into all core flows for real-time UI.

**Tech Stack:** .NET 10, NativeAOT, xUnit 2.9.3, FluentAssertions 7.1.0, NSubstitute 5.3.0, Dapper 2.1.66, Npgsql 9.0.3, PostgreSQL 18

**Spec:** `docs/superpowers/specs/2026-04-08-v141-core-operations-design.md`

---

## Phase 1: Foundation (SSE Event Wiring + Agent State UI)

### Task 1: Add ConversationOfferedEvent to PlatformEventBus

**Files:**
- Modify: `src/Asterisk.Platform.Core/PlatformEventBus.cs`

- [ ] **Step 1: Add ConversationOfferedEvent record**

After line ~66 (after `AgentStateChangedEvent`), add:

```csharp
public sealed record ConversationOfferedEvent(
    string TenantId, string ConversationId, string AgentId, string QueueId)
    : PlatformEvent(TenantId, "conversation.offered", DateTimeOffset.UtcNow);

public sealed record ConversationOfferExpiredEvent(
    string TenantId, string ConversationId, string AgentId)
    : PlatformEvent(TenantId, "conversation.offer_expired", DateTimeOffset.UtcNow);

public sealed record ConversationAbandonedEvent(
    string TenantId, string ConversationId, string QueueId)
    : PlatformEvent(TenantId, "conversation.abandoned", DateTimeOffset.UtcNow);

public sealed record AgentCapacityChangedEvent(
    string TenantId, string AgentId, string Channel,
    int CurrentLoad, int MaxLoad, bool CanAcceptVoice)
    : PlatformEvent(TenantId, "agent.capacity_changed", DateTimeOffset.UtcNow);
```

- [ ] **Step 2: Build to verify no errors**

Run: `dotnet build src/Asterisk.Platform.Core/`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Core/PlatformEventBus.cs
git commit -m "feat: add ACD event records to PlatformEventBus"
```

---

### Task 2: Wire PlatformEventBus into ConversationSwitchboard

**Files:**
- Modify: `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs`
- Modify: `tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs`

- [ ] **Step 1: Write failing tests for event publishing**

Add to `ConversationSwitchboardTests.cs` after existing tests:

```csharp
[Fact]
public async Task AssignToQueueAsync_ShouldPublishStateChangedEvent_WhenSuccessful()
{
    var conversation = CreateQueuedConversation();
    _conversationStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
        .Returns(conversation);

    var queueId = EntityId.New();
    await _sut.AssignToQueueAsync(conversation.ConversationId, conversation.TenantId, queueId, CancellationToken.None);

    _eventBus.PublishedEvents.Should().ContainSingle(e =>
        e is ConversationStateChangedEvent evt &&
        evt.TenantId == conversation.TenantId.Value &&
        evt.NewState == "Queued");
}

[Fact]
public async Task OfferToAgentAsync_ShouldPublishOfferedEvent_WhenSuccessful()
{
    var conversation = CreateQueuedConversation();
    _conversationStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
        .Returns(conversation);

    var agentId = EntityId.New();
    await _sut.OfferToAgentAsync(conversation.ConversationId, conversation.TenantId, agentId, CancellationToken.None);

    _eventBus.PublishedEvents.Should().ContainSingle(e =>
        e is ConversationOfferedEvent evt &&
        evt.AgentId == agentId.Value);
}

[Fact]
public async Task AcceptAsync_ShouldPublishAssignedAndStateChangedEvents_WhenSuccessful()
{
    var conversation = CreateOfferedConversation();
    _conversationStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
        .Returns(conversation);
    _capacity.HasCapacityAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
        .Returns(true);

    var agentId = EntityId.New();
    await _sut.AcceptAsync(conversation.ConversationId, conversation.TenantId, agentId, CancellationToken.None);

    _eventBus.PublishedEvents.Should().Contain(e => e is ConversationAssignedEvent);
    _eventBus.PublishedEvents.Should().Contain(e =>
        e is ConversationStateChangedEvent evt && evt.NewState == "Active");
}

[Fact]
public async Task RejectAsync_ShouldPublishStateChangedEvent_WhenSuccessful()
{
    var conversation = CreateOfferedConversation();
    _conversationStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
        .Returns(conversation);

    var agentId = EntityId.New();
    await _sut.RejectAsync(conversation.ConversationId, conversation.TenantId, agentId, CancellationToken.None);

    _eventBus.PublishedEvents.Should().ContainSingle(e =>
        e is ConversationStateChangedEvent evt && evt.NewState == "Queued");
}
```

Update the test class setup to include a `TestPlatformEventBus`:

```csharp
private readonly TestPlatformEventBus _eventBus = new();

// In constructor, replace `new ConversationSwitchboard(_conversationStore, _capacity, _clock)` with:
_sut = new ConversationSwitchboard(_conversationStore, _capacity, _clock, _eventBus);

// Add helper class at bottom of file:
private sealed class TestPlatformEventBus : PlatformEventBus
{
    public List<PlatformEvent> PublishedEvents { get; } = [];
    public new void Publish(PlatformEvent evt)
    {
        PublishedEvents.Add(evt);
        base.Publish(evt);
    }
}
```

Add helper methods `CreateQueuedConversation()` and `CreateOfferedConversation()` if not already present.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ --filter "ShouldPublish" -v q`
Expected: FAIL — constructor mismatch (missing PlatformEventBus parameter)

- [ ] **Step 3: Update ConversationSwitchboard to accept and use PlatformEventBus**

In `ConversationSwitchboard.cs`, update constructor and add event publishing:

```csharp
public ConversationSwitchboard(
    IConversationStore store,
    IAgentCapacityService capacity,
    IClock clock,
    PlatformEventBus eventBus)
{
    _store = store;
    _capacity = capacity;
    _clock = clock;
    _eventBus = eventBus;
}

private readonly PlatformEventBus _eventBus;
```

Add publish calls after each successful state transition:

In `AssignToQueueAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationStateChangedEvent(
    tenantId.Value, conversationId.Value, oldState.ToString(), "Queued"));
```

In `OfferToAgentAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationOfferedEvent(
    tenantId.Value, conversationId.Value, agentId.Value,
    conversation.Owner?.OwnerId?.Value ?? ""));
_eventBus.Publish(new ConversationStateChangedEvent(
    tenantId.Value, conversationId.Value, "Queued", "Offered"));
```

In `AcceptAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationAssignedEvent(
    tenantId.Value, conversationId.Value, agentId.Value, "", ""));
_eventBus.Publish(new ConversationStateChangedEvent(
    tenantId.Value, conversationId.Value, "Offered", "Active"));
```

In `RejectAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationStateChangedEvent(
    tenantId.Value, conversationId.Value, "Offered", "Queued"));
```

In `TransferToQueueAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationStateChangedEvent(
    tenantId.Value, conversationId.Value, "Active", "Queued"));
```

In `TransferToAgentAsync`, after `_store.SaveAsync(conversation, ct)`:
```csharp
_eventBus.Publish(new ConversationAssignedEvent(
    tenantId.Value, conversationId.Value, targetAgentId.Value, "", ""));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ -v q`
Expected: All tests pass (existing + 4 new)

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs
git commit -m "feat: wire PlatformEventBus into ConversationSwitchboard"
```

---

### Task 3: Wire Events into WebhookEndpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs`

- [ ] **Step 1: Add PlatformEventBus parameter and publish events**

In `HandleWebhook` static method, add `[FromServices] PlatformEventBus eventBus` parameter.

After the pipeline result (after `var pipelineResult = await pipeline.ProcessAsync(...)` around line 83), add:

```csharp
if (pipelineResult.IsNewConversation)
{
    eventBus.Publish(new ConversationStateChangedEvent(
        tid.Value, pipelineResult.ConversationId.Value, "", "Queued"));
}

eventBus.Publish(new ConversationMessageEvent(
    tid.Value, pipelineResult.ConversationId.Value,
    pipelineResult.MessageId.Value, "Inbound", channelType.ToString()));
```

- [ ] **Step 2: Build to verify no errors**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs
git commit -m "feat: publish SSE events in WebhookEndpoints for inbound messages"
```

---

### Task 4: Wire Events into DefaultConversationService

**Files:**
- Modify: `src/Asterisk.Platform.Switchboard/DefaultConversationService.cs`
- Modify: `tests/Asterisk.Platform.Switchboard.Tests/DefaultConversationServiceTests.cs`

- [ ] **Step 1: Write failing test**

Add to `DefaultConversationServiceTests.cs`:

```csharp
[Fact]
public async Task SendMessageAsync_ShouldPublishMessageEvent_WhenSendSucceeds()
{
    // Arrange: set up conversation with Active state, owned by agent, etc.
    // (follow existing test pattern in the file)
    SetupActiveConversation();

    await _sut.SendMessageAsync(_tenantId, _conversationId, _agentId, _envelope, CancellationToken.None);

    _eventBus.PublishedEvents.Should().ContainSingle(e =>
        e is ConversationMessageEvent evt &&
        evt.Direction == "Outbound");
}
```

Update test class constructor to inject `TestPlatformEventBus` (same pattern as Task 2).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ --filter "ShouldPublishMessageEvent" -v q`
Expected: FAIL — constructor mismatch

- [ ] **Step 3: Update DefaultConversationService constructor and add publish**

Add `PlatformEventBus eventBus` parameter to constructor. After successful message send (after delivery status update), add:

```csharp
_eventBus.Publish(new ConversationMessageEvent(
    tenantId.Value, conversationId.Value, message.MessageId.Value,
    "Outbound", conversation.Channel.ToString()));
```

- [ ] **Step 4: Run all switchboard tests**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Switchboard/DefaultConversationService.cs tests/Asterisk.Platform.Switchboard.Tests/DefaultConversationServiceTests.cs
git commit -m "feat: publish SSE events in DefaultConversationService for outbound messages"
```

---

### Task 5: Update DI Registration for EventBus Injection

**Files:**
- Modify: `src/Asterisk.Platform.Switchboard/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Verify PlatformEventBus is already registered**

Check `src/Asterisk.Platform.Core/ServiceCollectionExtensions.cs` — `AddPlatformCore()` should register `PlatformEventBus` as singleton. If not, add it.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Run ALL tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass. The DI change may break tests that construct `ConversationSwitchboard` or `DefaultConversationService` manually — fix any failures by adding the `PlatformEventBus` parameter.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "fix: update DI and tests for PlatformEventBus injection in switchboard"
```

---

### Task 6: Agent State Selector in Platform.Web

**Files:**
- Create: `/media/Data/Source/IPcom/Asterisk.Platform.Web/src/pages/agent/components/agent-status-selector.tsx`
- Modify: `/media/Data/Source/IPcom/Asterisk.Platform.Web/src/core/api/hooks/use-agents.ts`

- [ ] **Step 1: Add useUpdateAgentState mutation hook**

In `use-agents.ts`, add:

```typescript
export function useUpdateAgentState() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (state: string) => {
      const response = await apiClient.put('/api/v1/agents/me/state', { state });
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agent', 'me'] });
    },
  });
}
```

- [ ] **Step 2: Create AgentStatusSelector component**

```tsx
import { useState } from 'react';
import { useAgent, useUpdateAgentState } from '@/core/api/hooks/use-agents';
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem } from '@/components/ui/select';
import { Circle } from 'lucide-react';

const stateConfig: Record<string, { label: string; color: string }> = {
  Available: { label: 'Available', color: 'text-green-500' },
  Busy: { label: 'Busy', color: 'text-red-500' },
  Break: { label: 'Break', color: 'text-yellow-500' },
  Lunch: { label: 'Lunch', color: 'text-yellow-500' },
  Training: { label: 'Training', color: 'text-yellow-500' },
  DND: { label: 'Do Not Disturb', color: 'text-red-500' },
  ACW: { label: 'After Call Work', color: 'text-orange-500' },
  Offline: { label: 'Offline', color: 'text-gray-400' },
};

export function AgentStatusSelector() {
  const { data: agent } = useAgent();
  const updateState = useUpdateAgentState();
  const currentState = agent?.state ?? 'Offline';
  const config = stateConfig[currentState] ?? stateConfig.Offline;

  return (
    <Select
      value={currentState}
      onValueChange={(value) => updateState.mutate(value)}
      disabled={updateState.isPending}
    >
      <SelectTrigger className="w-[180px] h-8" data-testid="agent-status-selector">
        <Circle className={`h-3 w-3 fill-current ${config.color}`} />
        <SelectValue>{config.label}</SelectValue>
      </SelectTrigger>
      <SelectContent>
        {Object.entries(stateConfig).map(([state, { label, color }]) => (
          <SelectItem key={state} value={state} data-testid={`agent-status-${state.toLowerCase()}`}>
            <Circle className={`h-3 w-3 fill-current ${color}`} />
            {label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
```

- [ ] **Step 3: Add AgentStatusSelector to the agent workspace header**

Find the agent workspace layout component and add `<AgentStatusSelector />` to the header bar.

- [ ] **Step 4: Build frontend**

Run: `cd /media/Data/Source/IPcom/Asterisk.Platform.Web && npm run build`
Expected: Build succeeds

- [ ] **Step 5: Commit**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
git add -A
git commit -m "feat: add agent status selector to workspace header"
```

---

## Phase 2: ACD (Queue Distribution Worker + Timeouts)

### Task 7: Extend IConversationStore with Queue Query Methods

**Files:**
- Modify: `src/Asterisk.Platform.Conversations/IConversationStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/InMemoryConversationStore.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresConversationStore.cs`

- [ ] **Step 1: Add methods to IConversationStore interface**

```csharp
/// <summary>Returns conversations in Queued state ordered by priority DESC, CreatedAt ASC.</summary>
Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct);

/// <summary>Returns conversations in a specific state ordered by CreatedAt ASC.</summary>
Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct);
```

- [ ] **Step 2: Implement in InMemoryConversationStore**

```csharp
public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
{
    IReadOnlyList<Conversation> result = _items.Values
        .Where(c => c.TenantId == tenantId && c.State == ConversationState.Queued)
        .OrderByDescending(c => c.Metadata.GetValueOrDefault("_priority", "0"))
        .ThenBy(c => c.CreatedAt)
        .Take(limit)
        .ToList();
    return Task.FromResult(result);
}

public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
{
    IReadOnlyList<Conversation> result = _items.Values
        .Where(c => c.TenantId == tenantId && c.State == state)
        .OrderBy(c => c.CreatedAt)
        .Take(limit)
        .ToList();
    return Task.FromResult(result);
}
```

- [ ] **Step 3: Implement in PostgresConversationStore**

```csharp
public async Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    var rows = await conn.QueryAsync<ConversationRow>(
        "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
        "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
        "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
        "ORDER BY created_at ASC LIMIT @Limit",
        new { TenantId = tenantId.Value, State = (int)ConversationState.Queued, Limit = limit });
    return rows.Select(r => r.ToConversation()).ToList();
}

public async Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    var rows = await conn.QueryAsync<ConversationRow>(
        "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
        "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
        "FROM conversations WHERE tenant_id = @TenantId AND state = @State " +
        "ORDER BY created_at ASC LIMIT @Limit",
        new { TenantId = tenantId.Value, State = (int)state, Limit = limit });
    return rows.Select(r => r.ToConversation()).ToList();
}
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Conversations/IConversationStore.cs src/Asterisk.Platform.Storage.InMemory/InMemoryConversationStore.cs src/Asterisk.Platform.Storage.Postgres/Stores/PostgresConversationStore.cs
git commit -m "feat: add ListQueuedAsync and ListByStateAsync to IConversationStore"
```

---

### Task 8: Create DistributionOptions Configuration

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/DistributionOptions.cs`

- [ ] **Step 1: Create configuration class**

```csharp
namespace Asterisk.Platform.Api.Services;

public sealed class DistributionOptions
{
    public int PollIntervalMs { get; set; } = 2000;
    public int OfferTimeoutSeconds { get; set; } = 30;
    public int DefaultQueueTimeoutSeconds { get; set; } = 300;
    public int DefaultWrapUpTimeoutSeconds { get; set; } = 120;
    public int MaxConversationsPerCycle { get; set; } = 50;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Asterisk.Platform.Api/`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/DistributionOptions.cs
git commit -m "feat: add DistributionOptions configuration for ACD workers"
```

---

### Task 9: Implement QueueDistributionWorker

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/QueueDistributionWorkerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Asterisk.Platform.Api.Tests/QueueDistributionWorkerTests.cs` with tests:

1. `DistributeAsync_ShouldOfferConversationToAgent_WhenAgentAvailable`
2. `DistributeAsync_ShouldSkipConversation_WhenNoAgentsAvailable`
3. `DistributeAsync_ShouldPublishOfferedEvent_WhenOfferSucceeds`
4. `DistributeAsync_ShouldSetOfferMetadata_WhenOfferSucceeds`
5. `DistributeAsync_ShouldRespectMaxConversationsPerCycle`

Each test should mock `IConversationStore`, `IQueueStore`, `IAgentSelector`, `IConversationSwitchboard`, `ITenantStore`, `PlatformEventBus`, and `IClock`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "QueueDistributionWorker" -v q`
Expected: FAIL — class not found

- [ ] **Step 3: Implement QueueDistributionWorker**

```csharp
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Routing.Inbound;
using Asterisk.Platform.Switchboard;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class QueueDistributionWorker : BackgroundService
{
    private readonly IConversationStore _conversationStore;
    private readonly IQueueStore _queueStore;
    private readonly ITenantStore _tenantStore;
    private readonly IAgentSelector _agentSelector;
    private readonly IConversationSwitchboard _switchboard;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly DistributionOptions _options;
    private readonly ILogger<QueueDistributionWorker> _logger;

    public QueueDistributionWorker(
        IConversationStore conversationStore,
        IQueueStore queueStore,
        ITenantStore tenantStore,
        IAgentSelector agentSelector,
        IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        IClock clock,
        IOptions<DistributionOptions> options,
        ILogger<QueueDistributionWorker> logger)
    {
        _conversationStore = conversationStore;
        _queueStore = queueStore;
        _tenantStore = tenantStore;
        _agentSelector = agentSelector;
        _switchboard = switchboard;
        _eventBus = eventBus;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PollIntervalMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DistributeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogDistributionError(ex);
            }
        }
    }

    internal async Task DistributeAsync(CancellationToken ct)
    {
        var tenants = await _tenantStore.ListAsync(new PagedQuery { PageSize = 500 }, ct);

        foreach (var tenant in tenants.Items)
        {
            if (tenant.Status != TenantStatus.Active) continue;

            var tid = new TenantId(tenant.TenantId);
            var queued = await _conversationStore.ListQueuedAsync(tid, _options.MaxConversationsPerCycle, ct);

            foreach (var conversation in queued)
            {
                if (conversation.Owner?.OwnerId is null) continue;

                var queueId = conversation.Owner.OwnerId.Value;
                var agentId = await _agentSelector.SelectAgentAsync(
                    tid, queueId, conversation.Channel, preferredAgentId: null, ct);

                if (!agentId.HasValue) continue;

                var result = await _switchboard.OfferToAgentAsync(
                    conversation.ConversationId, tid, agentId.Value, ct);

                if (result.Success)
                {
                    // Store offer metadata for timeout tracking
                    conversation.SetMetadata("_offeredAt", _clock.UtcNow.ToString("O"));
                    conversation.SetMetadata("_offeredTo", agentId.Value.Value);
                    await _conversationStore.SaveAsync(conversation, ct);

                    LogOfferSucceeded(conversation.ConversationId.Value, agentId.Value.Value);
                }
                else
                {
                    LogOfferFailed(conversation.ConversationId.Value, result.FailureReason ?? "unknown");
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offered conversation {ConversationId} to agent {AgentId}")]
    private partial void LogOfferSucceeded(string conversationId, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to offer conversation {ConversationId}: {Reason}")]
    private partial void LogOfferFailed(string conversationId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Distribution cycle failed")]
    private partial void LogDistributionError(Exception ex);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "QueueDistributionWorker" -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/QueueDistributionWorker.cs tests/Asterisk.Platform.Api.Tests/QueueDistributionWorkerTests.cs
git commit -m "feat: implement QueueDistributionWorker for digital ACD"
```

---

### Task 10: Implement ConversationTimeoutWorker

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/ConversationTimeoutWorkerTests.cs`

- [ ] **Step 1: Write failing tests**

Create tests:
1. `ProcessTimeoutsAsync_ShouldRejectExpiredOffers_WhenOfferTimedOut`
2. `ProcessTimeoutsAsync_ShouldAbandonQueuedConversations_WhenQueueTimedOut`
3. `ProcessTimeoutsAsync_ShouldCloseWrapUpConversations_WhenWrapUpTimedOut`
4. `ProcessTimeoutsAsync_ShouldSkipConversation_WhenNotTimedOut`
5. `ProcessTimeoutsAsync_ShouldPublishEvents_WhenTransitioning`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ConversationTimeout" -v q`
Expected: FAIL

- [ ] **Step 3: Implement ConversationTimeoutWorker**

```csharp
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Switchboard;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class ConversationTimeoutWorker : BackgroundService
{
    private readonly IConversationStore _conversationStore;
    private readonly IConversationSwitchboard _switchboard;
    private readonly ITenantStore _tenantStore;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly DistributionOptions _options;
    private readonly ILogger<ConversationTimeoutWorker> _logger;

    public ConversationTimeoutWorker(
        IConversationStore conversationStore,
        IConversationSwitchboard switchboard,
        ITenantStore tenantStore,
        PlatformEventBus eventBus,
        IClock clock,
        IOptions<DistributionOptions> options,
        ILogger<ConversationTimeoutWorker> logger)
    {
        _conversationStore = conversationStore;
        _switchboard = switchboard;
        _tenantStore = tenantStore;
        _eventBus = eventBus;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessTimeoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogTimeoutError(ex);
            }
        }
    }

    internal async Task ProcessTimeoutsAsync(CancellationToken ct)
    {
        var tenants = await _tenantStore.ListAsync(new PagedQuery { PageSize = 500 }, ct);
        var now = _clock.UtcNow;

        foreach (var tenant in tenants.Items)
        {
            if (tenant.Status != TenantStatus.Active) continue;
            var tid = new TenantId(tenant.TenantId);

            // Phase 1: Offer timeouts
            var offered = await _conversationStore.ListByStateAsync(tid, ConversationState.Offered, 100, ct);
            foreach (var conv in offered)
            {
                if (!conv.Metadata.TryGetValue("_offeredAt", out var offeredAtStr)) continue;
                if (!DateTimeOffset.TryParse(offeredAtStr, out var offeredAt)) continue;

                if ((now - offeredAt).TotalSeconds > _options.OfferTimeoutSeconds)
                {
                    var agentId = conv.Metadata.GetValueOrDefault("_offeredTo", "");
                    await _switchboard.RejectAsync(conv.ConversationId, tid, EntityId.From(agentId), ct);

                    _eventBus.Publish(new ConversationOfferExpiredEvent(
                        tid.Value, conv.ConversationId.Value, agentId));
                    LogOfferExpired(conv.ConversationId.Value);
                }
            }

            // Phase 2: Queue abandonment
            var queued = await _conversationStore.ListByStateAsync(tid, ConversationState.Queued, 100, ct);
            foreach (var conv in queued)
            {
                var elapsed = (now - conv.CreatedAt).TotalSeconds;
                if (elapsed > _options.DefaultQueueTimeoutSeconds)
                {
                    conv.TransitionTo(ConversationState.Abandoned, now);
                    conv.UpdatedAt = now;
                    await _conversationStore.SaveAsync(conv, ct);

                    _eventBus.Publish(new ConversationAbandonedEvent(
                        tid.Value, conv.ConversationId.Value,
                        conv.Owner?.OwnerId?.Value ?? ""));
                    LogQueueAbandoned(conv.ConversationId.Value);
                }
            }

            // Phase 3: WrapUp enforcement
            var wrapUp = await _conversationStore.ListByStateAsync(tid, ConversationState.WrapUp, 100, ct);
            foreach (var conv in wrapUp)
            {
                var elapsed = (now - (conv.UpdatedAt ?? conv.CreatedAt)).TotalSeconds;
                if (elapsed > _options.DefaultWrapUpTimeoutSeconds)
                {
                    conv.TransitionTo(ConversationState.Closed, now);
                    conv.ClosedAt = now;
                    conv.UpdatedAt = now;
                    await _conversationStore.SaveAsync(conv, ct);

                    _eventBus.Publish(new ConversationStateChangedEvent(
                        tid.Value, conv.ConversationId.Value, "WrapUp", "Closed"));
                    LogWrapUpClosed(conv.ConversationId.Value);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offer expired for conversation {ConversationId}")]
    private partial void LogOfferExpired(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queue timeout — conversation {ConversationId} abandoned")]
    private partial void LogQueueAbandoned(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WrapUp timeout — conversation {ConversationId} closed")]
    private partial void LogWrapUpClosed(string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timeout processing cycle failed")]
    private partial void LogTimeoutError(Exception ex);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ConversationTimeout" -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/ConversationTimeoutWorker.cs tests/Asterisk.Platform.Api.Tests/ConversationTimeoutWorkerTests.cs
git commit -m "feat: implement ConversationTimeoutWorker for offer/queue/wrapup timeouts"
```

---

### Task 11: Register ACD Workers in Program.cs

**Files:**
- Modify: `src/Asterisk.Platform.Api/Program.cs`

- [ ] **Step 1: Add worker registrations**

After the `AddHostedService<RetentionPurgeService>()` line (~line 77), add:

```csharp
// ─── ACD Distribution ───────────────────────────────────────────────────────
builder.Services.Configure<DistributionOptions>(builder.Configuration.GetSection("Distribution"));
builder.Services.AddHostedService<QueueDistributionWorker>();
builder.Services.AddHostedService<ConversationTimeoutWorker>();
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build Asterisk.Platform.slnx && dotnet test Asterisk.Platform.slnx -v q`
Expected: All pass

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Program.cs
git commit -m "feat: register QueueDistributionWorker and ConversationTimeoutWorker in DI"
```

---

## Phase 3: Persistence (Postgres Stores + Capacity)

### Task 12: Create Migration 013

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/013_DunningAddOnsCapacity.sql`

- [ ] **Step 1: Write migration**

```sql
-- Migration 013: Dunning records, tenant add-ons, agent capacity persistence

CREATE TABLE IF NOT EXISTS dunning_records (
    dunning_id      VARCHAR(64) PRIMARY KEY,
    tenant_id       VARCHAR(64) NOT NULL,
    invoice_id      VARCHAR(64) NOT NULL,
    current_stage   VARCHAR(32) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    escalated_at    TIMESTAMPTZ,
    resolved_at     TIMESTAMPTZ,
    is_paused       BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_dunning_records_tenant ON dunning_records(tenant_id) WHERE is_active = TRUE;
CREATE INDEX idx_dunning_records_invoice ON dunning_records(invoice_id) WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS tenant_add_ons (
    tenant_id       VARCHAR(64) NOT NULL,
    feature         VARCHAR(64) NOT NULL,
    enabled_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, feature)
);

CREATE INDEX idx_tenant_add_ons_tenant ON tenant_add_ons(tenant_id);

CREATE TABLE IF NOT EXISTS agent_capacity (
    tenant_id   VARCHAR(64) NOT NULL,
    agent_id    VARCHAR(64) NOT NULL,
    voice_load  INTEGER NOT NULL DEFAULT 0,
    chat_load   INTEGER NOT NULL DEFAULT 0,
    email_load  INTEGER NOT NULL DEFAULT 0,
    sms_load    INTEGER NOT NULL DEFAULT 0,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, agent_id)
);
```

- [ ] **Step 2: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Migrations/013_DunningAddOnsCapacity.sql
git commit -m "feat: add migration 013 for dunning, add-ons, and capacity tables"
```

---

### Task 13: Implement PostgresDunningStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresDunningStore.cs`
- Create: `tests/Asterisk.Platform.Storage.Postgres.Tests/PostgresDunningStoreTests.cs`

- [ ] **Step 1: Write tests**

Tests: `GetActiveAsync_ShouldReturnActiveRecord`, `ListActiveAsync_ShouldReturnAllActive`, `UpsertAsync_ShouldInsertNew`, `UpsertAsync_ShouldUpdateExisting`, `GetByInvoiceAsync_ShouldReturnRecord`

- [ ] **Step 2: Implement PostgresDunningStore**

Use Dapper with class-based `DunningRecordRow {get; init;}` row type. Pattern matches existing Postgres stores. UPSERT via `ON CONFLICT (dunning_id) DO UPDATE SET`.

- [ ] **Step 3: Register in PostgresStorageExtensions**

Add `services.AddSingleton<IDunningStore>(sp => new PostgresDunningStore(sp.GetRequiredService<NpgsqlDataSource>()));`

- [ ] **Step 4: Run tests, commit**

```bash
git commit -m "feat: implement PostgresDunningStore with Dapper"
```

---

### Task 14: Implement PostgresTenantAddOnStore

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAddOnStore.cs`

- [ ] **Step 1: Write tests**

Tests: `GetAsync_ShouldReturnAddOns`, `UpsertAsync_ShouldInsert`, `DeleteAsync_ShouldRemove`

- [ ] **Step 2: Implement with Dapper**

Pattern: `TenantAddOnRow {get; init;}`, UPSERT via `ON CONFLICT (tenant_id, feature)`.

- [ ] **Step 3: Register in PostgresStorageExtensions, run tests, commit**

```bash
git commit -m "feat: implement PostgresTenantAddOnStore with Dapper"
```

---

### Task 15: Implement IAgentCapacityStore and PersistentAgentCapacityService

**Files:**
- Create: `src/Asterisk.Platform.Queues/Services/IAgentCapacityStore.cs`
- Create: `src/Asterisk.Platform.Queues/Services/AgentCapacityRecord.cs`
- Create: `src/Asterisk.Platform.Queues/Services/PersistentAgentCapacityService.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryAgentCapacityStore.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAgentCapacityStore.cs`
- Create: `tests/Asterisk.Platform.Queues.Tests/PersistentAgentCapacityServiceTests.cs`

- [ ] **Step 1: Create IAgentCapacityStore interface and AgentCapacityRecord model**

As specified in the spec document.

- [ ] **Step 2: Write tests for PersistentAgentCapacityService**

Tests:
1. `ReserveAsync_ShouldPersistToStore`
2. `ReleaseAsync_ShouldPersistToStore`
3. `ReconcileAsync_ShouldRebuildFromActiveConversations`
4. `HasCapacityAsync_ShouldDelegateToInMemory`

- [ ] **Step 3: Implement PersistentAgentCapacityService**

Wraps `InMemoryAgentCapacityService`, writes through to `IAgentCapacityStore`. On startup, reconciles from active conversations in `IConversationStore`.

- [ ] **Step 4: Implement InMemory and Postgres stores**

- [ ] **Step 5: Update DI to use PersistentAgentCapacityService when Postgres configured**

In `Program.cs`, after storage registration:
```csharp
if (!string.IsNullOrEmpty(coreConnectionString))
    builder.Services.AddSingleton<IAgentCapacityService, PersistentAgentCapacityService>();
```

- [ ] **Step 6: Run all tests, commit**

```bash
git commit -m "feat: implement PersistentAgentCapacityService with Postgres backing"
```

---

## Phase 4: Integration (Asterisk Sync + Email Fix)

### Task 16: Implement AsteriskCapacitySyncService

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/AsteriskCapacitySyncService.cs`
- Modify: `src/Asterisk.Platform.Queues/IAgentStore.cs` (add GetByExtensionAsync)
- Modify: both storage implementations
- Create: `tests/Asterisk.Platform.Api.Tests/AsteriskCapacitySyncServiceTests.cs`

- [ ] **Step 1: Add GetByExtensionAsync to IAgentStore**

```csharp
Task<Agent?> GetByExtensionAsync(TenantId tenantId, string extension, CancellationToken ct);
```

- [ ] **Step 2: Implement in InMemory and Postgres stores**

InMemory: `.FirstOrDefault(a => a.Extension == extension)`
Postgres: `WHERE tenant_id = @tid AND extension = @ext LIMIT 1`

- [ ] **Step 3: Write tests for AsteriskCapacitySyncService**

Tests:
1. `HandleAgentConnect_ShouldReserveVoiceCapacity`
2. `HandleAgentComplete_ShouldReleaseVoiceCapacity`
3. `DigitalCapacityFull_ShouldPauseAgentInAsteriskQueues`
4. `DigitalCapacityFreed_ShouldUnpauseAgent`

Mock `IAmiClient` to simulate AMI events.

- [ ] **Step 4: Implement AsteriskCapacitySyncService**

Subscribe to AMI events (`QueueMemberStatus`, `AgentConnect`, `AgentComplete`). Extract tenant/extension from interface name `PJSIP/{tenantId}_ext{extension}`. Update capacity. Send `QueuePause`/`QueueAdd` via AMI when capacity changes.

Subscribe to `PlatformEventBus` for `AgentCapacityChangedEvent` to sync digital→voice.

- [ ] **Step 5: Conditional registration in Program.cs**

```csharp
// Only register when Asterisk AMI is configured
var amiConfig = builder.Configuration.GetSection("Asterisk:Ami");
if (!string.IsNullOrEmpty(amiConfig["Host"]))
    builder.Services.AddHostedService<AsteriskCapacitySyncService>();
```

- [ ] **Step 6: Run tests, commit**

```bash
git commit -m "feat: implement AsteriskCapacitySyncService for voice/digital capacity bridge"
```

---

### Task 17: Fix Email Attachments

**Files:**
- Modify: `src/Asterisk.Platform.Channels.Email/EmailConnector.cs`
- Create: `tests/Asterisk.Platform.Channels.Email.Tests/EmailAttachmentTests.cs`

- [ ] **Step 1: Write failing tests**

Tests:
1. `AddUrlAttachmentAsync_ShouldDownloadAndAttachBinary_WhenUrlValid`
2. `AddUrlAttachmentAsync_ShouldSkipAttachment_WhenDownloadFails`
3. `AddUrlAttachmentAsync_ShouldSkipAttachment_WhenOversized`

Use `MockHttpMessageHandler` pattern to mock HTTP responses.

- [ ] **Step 2: Run tests to verify they fail**

- [ ] **Step 3: Update EmailConnector**

Replace `AddUrlAttachment` (static, synchronous) with `AddUrlAttachmentAsync` (instance, async, uses IHttpClientFactory):

```csharp
private async Task AddUrlAttachmentAsync(
    MailMessage mail, string url, string fileName, string mimeType, CancellationToken ct)
{
    try
    {
        var client = _httpClientFactory.CreateClient("EmailAttachments");
        var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > 25 * 1024 * 1024)
        {
            _logger.LogWarning("Attachment {Url} exceeds 25MB limit ({Size} bytes), skipping", url, bytes.Length);
            return;
        }

        mail.Attachments.Add(new Attachment(new MemoryStream(bytes), fileName, mimeType));
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to download attachment {Url}, skipping", url);
    }
}
```

Update constructor to accept `IHttpClientFactory`. Update all `AddUrlAttachment` call sites to `await AddUrlAttachmentAsync(...)`.

- [ ] **Step 4: Register HttpClient in Program.cs**

```csharp
builder.Services.AddHttpClient("EmailAttachments", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 25 * 1024 * 1024;
});
```

- [ ] **Step 5: Run tests, commit**

```bash
git commit -m "fix: download email attachments instead of storing URL as content"
```

---

### Task 18: Final Integration Test + Version Bump

**Files:**
- Modify: `Directory.Build.props` (version bump)

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 2: Build AOT check**

Run: `dotnet build src/Asterisk.Platform.Api/ -v q`
Expected: Build succeeded. 0 Warning(s). 0 Error(s).

- [ ] **Step 3: Bump version**

In `Directory.Build.props`, update `<PackageVersion>1.4.1</PackageVersion>`.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props
git commit -m "chore: bump PackageVersion to 1.4.1"
```
