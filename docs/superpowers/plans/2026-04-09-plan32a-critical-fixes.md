# v1.5.0 Sub-project A: Critical Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix broken bot handoff, add hold/unhold endpoints, add outbound conversation creation, and expand error handling.

**Architecture:** Four independent fixes in Platform.Api and Platform.Switchboard. Bot handoff adds two response handlers in WebhookEndpoints. Hold/Unhold adds two switchboard methods + two API endpoints. Outbound conversation creation adds one endpoint using existing `GetOrCreateForContactAsync`. Error handling expands the exception-to-status mapping.

**Tech Stack:** .NET 10, xUnit, NSubstitute, FluentAssertions

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs` | Modify | Add TransferToQueue + EndConversation handlers |
| `src/Asterisk.Platform.Switchboard/IConversationSwitchboard.cs` | Modify | Add HoldAsync, UnholdAsync |
| `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs` | Modify | Implement HoldAsync, UnholdAsync |
| `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs` | Modify | Add hold, unhold, create endpoints |
| `src/Asterisk.Platform.Api/Middleware/ErrorHandlingMiddleware.cs` | Modify | Expand exception mapping, add traceId |
| `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` | Modify | Add new DTO types |
| `tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs` | Modify | Add hold/unhold tests |
| `tests/Asterisk.Platform.Api.Tests/WebhookEndpointTests.cs` | Modify | Add bot handoff tests |
| `tests/Asterisk.Platform.Api.Tests/ConversationEndpointTests.cs` | Modify or Create | Add hold/unhold/create tests |
| `tests/Asterisk.Platform.Api.Tests/ErrorHandlingMiddlewareTests.cs` | Modify or Create | Add expanded exception tests |

---

### Task 1: Bot Handoff — TransferToQueue + EndConversation

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs:116-128`
- Test: `tests/Asterisk.Platform.Api.Tests/WebhookEndpointTests.cs`

- [ ] **Step 1: Write failing tests for bot handoff**

Add to `WebhookEndpointTests.cs` (or create if it doesn't exist). These tests verify the bot handoff handling at the switchboard/lifecycle level since WebhookEndpoints is an integration endpoint:

```csharp
[Fact]
public async Task BotHandoff_ShouldAssignToQueue_WhenTransferToQueueAction()
{
    // This test verifies that when a bot returns TransferToQueue,
    // the conversation is assigned to the target queue
    var store = Substitute.For<IConversationStore>();
    var switchboard = Substitute.For<IConversationSwitchboard>();
    var lifecycleService = Substitute.For<IConversationLifecycleService>();

    var tid = new TenantId("t1");
    var convId = EntityId.From("conv-1");
    var queueId = EntityId.From("queue-sales");

    var botResponse = new BotResponse(
        BotResponseAction.TransferToQueue,
        Messages: null,
        TargetQueueId: queueId,
        Priority: MessagePriority.High,
        HandoffReason: "Customer wants human");

    // When TransferToQueue, we call AssignToQueueAsync
    switchboard.AssignToQueueAsync(convId, tid, queueId, Arg.Any<CancellationToken>())
        .Returns(new OwnershipResult(true, ConversationOwner.ForQueue(queueId), ConversationState.Queued, null));

    // Act: simulate the handoff logic
    if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
    {
        var result = await switchboard.AssignToQueueAsync(convId, tid, botResponse.TargetQueueId.Value, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.NewState.Should().Be(ConversationState.Queued);
    }

    await switchboard.Received(1).AssignToQueueAsync(convId, tid, queueId, Arg.Any<CancellationToken>());
}

[Fact]
public async Task BotHandoff_ShouldCloseConversation_WhenEndConversationAction()
{
    var lifecycleService = Substitute.For<IConversationLifecycleService>();
    var tid = new TenantId("t1");
    var convId = EntityId.From("conv-1");

    var botResponse = new BotResponse(
        BotResponseAction.EndConversation,
        Messages: null,
        TargetQueueId: null,
        Priority: null,
        HandoffReason: "Issue resolved by bot");

    // Act: simulate the end logic
    if (botResponse.Action == BotResponseAction.EndConversation)
    {
        await lifecycleService.CloseAsync(tid, convId, wrapUp: null, CancellationToken.None);
    }

    await lifecycleService.Received(1).CloseAsync(tid, convId, null, Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run tests to verify they pass (these are unit-level verifications)**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "BotHandoff" -v q`

- [ ] **Step 3: Implement bot handoff in WebhookEndpoints**

In `src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs`, add `IConversationLifecycleService` parameter to `HandleWebhook` and add handling after the existing `Reply` block (after line 128):

```csharp
// Add to HandleWebhook parameters:
[FromServices] IConversationLifecycleService lifecycleService,

// Replace the block at lines 116-128 with:
if (botResponse.Action == BotResponseAction.Reply && botResponse.Messages is not null)
{
    foreach (var reply in botResponse.Messages)
    {
        await conversationService.SendMessageAsync(
            updated.ConversationId,
            tid,
            reply,
            updated.Owner.OwnerId.Value,
            ConversationOwnerKind.Bot,
            ct);
    }
}
else if (botResponse.Action == BotResponseAction.TransferToQueue && botResponse.TargetQueueId is not null)
{
    await switchboard.AssignToQueueAsync(
        updated.ConversationId,
        tid,
        botResponse.TargetQueueId.Value,
        ct);

    eventBus.Publish(new ConversationStateChangedEvent(
        tid.Value, updated.ConversationId.Value, "Bot", "Queued"));
}
else if (botResponse.Action == BotResponseAction.EndConversation)
{
    await lifecycleService.CloseAsync(tid, updated.ConversationId, wrapUp: null, ct);
}
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/WebhookEndpoints.cs tests/Asterisk.Platform.Api.Tests/WebhookEndpointTests.cs
git commit -m "fix: execute bot handoff TransferToQueue and EndConversation actions

WebhookEndpoints only handled BotResponseAction.Reply, ignoring
TransferToQueue and EndConversation. Conversations remained stuck
with bot ownership forever after handoff decision."
```

---

### Task 2: Hold/Unhold — Switchboard Methods

**Files:**
- Modify: `src/Asterisk.Platform.Switchboard/IConversationSwitchboard.cs`
- Modify: `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs`
- Test: `tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs`

- [ ] **Step 1: Write failing tests for HoldAsync and UnholdAsync**

Add to `tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs`:

```csharp
// ─── Hold ────────────────────────────────────────────────────────────────

[Fact]
public async Task Hold_ShouldTransitionToOnHold_WhenActive()
{
    var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
    _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
          .Returns(conversation);

    var sut = CreateSut();
    var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

    result.Success.Should().BeTrue();
    result.NewState.Should().Be(ConversationState.OnHold);
}

[Fact]
public async Task Hold_ShouldFail_WhenNotOwnedByAgent()
{
    var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForQueue(_queueId));
    _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
          .Returns(conversation);

    var sut = CreateSut();
    var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

    result.Success.Should().BeFalse();
}

[Fact]
public async Task Hold_ShouldFail_WhenNotActive()
{
    var conversation = BuildConversation(ConversationState.Queued, ConversationOwner.ForAgent(_agentId));
    _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
          .Returns(conversation);

    var sut = CreateSut();
    var result = await sut.HoldAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

    result.Success.Should().BeFalse();
}

// ─── Unhold ──────────────────────────────────────────────────────────────

[Fact]
public async Task Unhold_ShouldTransitionToActive_WhenOnHold()
{
    var conversation = BuildConversation(ConversationState.OnHold, ConversationOwner.ForAgent(_agentId));
    _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
          .Returns(conversation);

    var sut = CreateSut();
    var result = await sut.UnholdAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

    result.Success.Should().BeTrue();
    result.NewState.Should().Be(ConversationState.Active);
}

[Fact]
public async Task Unhold_ShouldFail_WhenNotOnHold()
{
    var conversation = BuildConversation(ConversationState.Active, ConversationOwner.ForAgent(_agentId));
    _store.GetByIdAsync(_tenantId, _conversationId, Arg.Any<CancellationToken>())
          .Returns(conversation);

    var sut = CreateSut();
    var result = await sut.UnholdAsync(_conversationId, _tenantId, _agentId, CancellationToken.None);

    result.Success.Should().BeFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail (methods don't exist yet)**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ -v q`
Expected: FAIL — `HoldAsync` and `UnholdAsync` not found

- [ ] **Step 3: Add methods to IConversationSwitchboard**

Add to `src/Asterisk.Platform.Switchboard/IConversationSwitchboard.cs` after `ReturnToBotAsync`:

```csharp
Task<OwnershipResult> HoldAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
Task<OwnershipResult> UnholdAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct);
```

- [ ] **Step 4: Implement HoldAsync and UnholdAsync in ConversationSwitchboard**

Add to `src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs` after `ReturnToBotAsync`:

```csharp
public async Task<OwnershipResult> HoldAsync(
    EntityId conversationId,
    TenantId tenantId,
    EntityId agentId,
    CancellationToken ct)
{
    var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
    if (conversation is null)
        return Fail(ConversationState.Active, "Conversation not found.");

    // Verify the requesting agent owns this conversation
    if (conversation.Owner?.Kind != ConversationOwnerKind.Agent ||
        conversation.Owner.OwnerId != agentId)
        return Fail(conversation.State, "Only the assigned agent can hold the conversation.");

    if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.OnHold))
        return Fail(conversation.State, $"Cannot transition from {conversation.State} to OnHold.");

    var oldState = conversation.State;
    conversation.TransitionTo(ConversationState.OnHold, _clock.UtcNow);
    conversation.UpdatedAt = _clock.UtcNow;

    await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
    _eventBus.Publish(new ConversationStateChangedEvent(
        tenantId.Value, conversationId.Value, oldState.ToString(), "OnHold"));
    return new OwnershipResult(true, conversation.Owner, conversation.State, null);
}

public async Task<OwnershipResult> UnholdAsync(
    EntityId conversationId,
    TenantId tenantId,
    EntityId agentId,
    CancellationToken ct)
{
    var conversation = await _store.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
    if (conversation is null)
        return Fail(ConversationState.OnHold, "Conversation not found.");

    // Verify the requesting agent owns this conversation
    if (conversation.Owner?.Kind != ConversationOwnerKind.Agent ||
        conversation.Owner.OwnerId != agentId)
        return Fail(conversation.State, "Only the assigned agent can unhold the conversation.");

    if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active))
        return Fail(conversation.State, $"Cannot transition from {conversation.State} to Active.");

    var oldState = conversation.State;
    conversation.TransitionTo(ConversationState.Active, _clock.UtcNow);
    conversation.UpdatedAt = _clock.UtcNow;

    await _store.SaveAsync(conversation, ct).ConfigureAwait(false);
    _eventBus.Publish(new ConversationStateChangedEvent(
        tenantId.Value, conversationId.Value, oldState.ToString(), "Active"));
    return new OwnershipResult(true, conversation.Owner, conversation.State, null);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Asterisk.Platform.Switchboard.Tests/ -v q`
Expected: All tests pass including 5 new hold/unhold tests

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Switchboard/IConversationSwitchboard.cs src/Asterisk.Platform.Switchboard/ConversationSwitchboard.cs tests/Asterisk.Platform.Switchboard.Tests/ConversationSwitchboardTests.cs
git commit -m "feat: add Hold/Unhold methods to ConversationSwitchboard

State machine already supported Active <-> OnHold transitions but
no API or switchboard methods existed to trigger them. Validates
agent ownership before allowing hold/unhold."
```

---

### Task 3: Hold/Unhold — API Endpoints

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ConversationEndpointTests.cs`

- [ ] **Step 1: Add hold and unhold routes to ConversationEndpoints**

In `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs`, add to the `MapConversationEndpoints` method after the `wrapup` line (line 27):

```csharp
group.MapPost("/{id}/hold", HoldConversation);
group.MapPost("/{id}/unhold", UnholdConversation);
```

Add the handler methods after `WrapUpConversation`:

```csharp
private static async Task<IResult> HoldConversation(
    string id,
    HttpContext context,
    IConversationSwitchboard switchboard,
    CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var agentId = GetCurrentAgentId(context);
    var result = await switchboard.HoldAsync(EntityId.From(id), tenantId, agentId, ct);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(new ErrorResponse(result.Reason ?? "Cannot hold conversation"));
}

private static async Task<IResult> UnholdConversation(
    string id,
    HttpContext context,
    IConversationSwitchboard switchboard,
    CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var agentId = GetCurrentAgentId(context);
    var result = await switchboard.UnholdAsync(EntityId.From(id), tenantId, agentId, ct);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(new ErrorResponse(result.Reason ?? "Cannot unhold conversation"));
}
```

- [ ] **Step 2: Build and run tests**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All pass, 0 warnings

- [ ] **Step 3: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs
git commit -m "feat: add POST /conversations/{id}/hold and /unhold endpoints

Exposes ConversationSwitchboard.HoldAsync and UnholdAsync via API.
Validates agent ownership, returns 400 if transition not allowed."
```

---

### Task 4: Outbound Conversation Creation

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ConversationEndpointTests.cs`

- [ ] **Step 1: Add CreateConversation request DTO**

Add at the bottom of `src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs` after the existing DTOs:

```csharp
internal sealed record CreateConversationRequest(
    string ContactId,
    string Channel,
    string? InitialMessage = null);
```

- [ ] **Step 2: Register DTO in ApiJsonContext**

Add to `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`:

```csharp
[JsonSerializable(typeof(CreateConversationRequest))]
```

- [ ] **Step 3: Add the route and handler**

In `ConversationEndpoints.MapConversationEndpoints`, add after `group.MapGet("/", ListConversations)`:

```csharp
group.MapPost("/", CreateConversation);
```

Add the handler:

```csharp
private static async Task<IResult> CreateConversation(
    HttpContext context,
    [FromBody] CreateConversationRequest body,
    IConversationService conversationService,
    [FromServices] IContactStore contactStore,
    CancellationToken ct)
{
    var tenantId = GetTenantId(context);
    var agentId = GetCurrentAgentId(context);

    if (!Enum.TryParse<ChannelType>(body.Channel, ignoreCase: true, out var channelType))
        return Results.BadRequest(new ErrorResponse($"Unknown channel: {body.Channel}"));

    var contactId = EntityId.From(body.ContactId);

    // Verify contact exists
    var contact = await contactStore.GetByIdAsync(tenantId, contactId, ct);
    if (contact is null)
        return Results.NotFound(new ErrorResponse("Contact not found"));

    var conversation = await conversationService.GetOrCreateForContactAsync(
        tenantId, contactId, channelType, ct);

    // Send initial message if provided
    if (body.InitialMessage is not null)
    {
        var envelope = new MessageEnvelope([new TextBlock(body.InitialMessage)]);
        await conversationService.SendMessageAsync(
            conversation.ConversationId, tenantId, envelope,
            agentId, ConversationOwnerKind.Agent, ct);
    }

    return Results.Created($"/conversations/{conversation.ConversationId.Value}", conversation);
}
```

- [ ] **Step 4: Build and run tests**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ -v q`
Expected: All pass, 0 warnings

- [ ] **Step 5: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ConversationEndpoints.cs src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs
git commit -m "feat: add POST /conversations endpoint for outbound conversations

Agents can now initiate conversations with contacts. Uses existing
GetOrCreateForContactAsync and optionally sends initial message."
```

---

### Task 5: Error Handling Expansion

**Files:**
- Modify: `src/Asterisk.Platform.Api/Middleware/ErrorHandlingMiddleware.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/ErrorHandlingMiddlewareTests.cs`

- [ ] **Step 1: Write failing tests**

Create or update `tests/Asterisk.Platform.Api.Tests/ErrorHandlingMiddlewareTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Platform.Api.Tests;

public class ErrorHandlingMiddlewareTests
{
    private static (ErrorHandlingMiddleware middleware, DefaultHttpContext context) CreateSut(
        Func<HttpContext, Task> next)
    {
        var middleware = new ErrorHandlingMiddleware(
            new RequestDelegate(next),
            NullLogger<ErrorHandlingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return (middleware, context);
    }

    [Fact]
    public async Task ShouldReturn400_WhenPlatformExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new PlatformException("INVALID_STATE", "Bad state"));
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ShouldReturn400_WhenArgumentExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new ArgumentException("Bad arg"));
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ShouldReturn499_WhenOperationCancelledThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new OperationCanceledException());
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(499);
    }

    [Fact]
    public async Task ShouldIncludeTraceId_InProblemDetails()
    {
        var (sut, ctx) = CreateSut(_ => throw new KeyNotFoundException("not found"));
        ctx.TraceIdentifier = "test-trace-123";

        await sut.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        body.Should().Contain("test-trace-123");
    }

    [Fact]
    public async Task ShouldReturn500_WhenUnknownExceptionThrown()
    {
        var (sut, ctx) = CreateSut(_ => throw new Exception("Unexpected"));
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(500);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ErrorHandling" -v q`
Expected: Some fail (PlatformException → 400 instead of current 500, no traceId)

- [ ] **Step 3: Update ErrorHandlingMiddleware**

Replace the entire `HandleExceptionAsync` method in `src/Asterisk.Platform.Api/Middleware/ErrorHandlingMiddleware.cs`:

```csharp
private async Task HandleExceptionAsync(HttpContext context, Exception exception)
{
    var (status, title) = exception switch
    {
        PlatformException px => (StatusCodes.Status400BadRequest, px.Code),
        ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
        InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
        OperationCanceledException => (499, "Client Closed Request"),
        _ => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
    };

    LogException(status, exception, title);

    var problem = new ProblemDetails
    {
        Status = status,
        Title = title,
        Detail = exception.Message,
        Instance = context.Request.Path,
    };

    problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";

    await context.Response.WriteAsync(
        JsonSerializer.Serialize(problem, ApiJsonContext.Default.ProblemDetails));
}

private void LogException(int status, Exception exception, string title)
{
    if (status == StatusCodes.Status500InternalServerError)
        LogUnhandledException(_logger, exception);
    else
        LogRequestError(_logger, title, exception);
}

[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
private static partial void LogUnhandledException(ILogger logger, Exception exception);

[LoggerMessage(Level = LogLevel.Warning, Message = "Request error: {Title}")]
private static partial void LogRequestError(ILogger logger, string title, Exception exception);
```

Also update the class declaration to be `partial`:

```csharp
internal sealed partial class ErrorHandlingMiddleware
```

Add the `using` for `PlatformException`:

```csharp
using Asterisk.Platform.Core;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build src/Asterisk.Platform.Api/ && dotnet test tests/Asterisk.Platform.Api.Tests/ --filter "ErrorHandling" -v q`
Expected: All 5 tests pass

- [ ] **Step 5: Run full test suite**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass, 0 warnings

- [ ] **Step 6: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/ErrorHandlingMiddleware.cs tests/Asterisk.Platform.Api.Tests/ErrorHandlingMiddlewareTests.cs
git commit -m "feat: expand error handling with PlatformException, traceId, LoggerMessage

PlatformException and ArgumentException now map to 400,
OperationCanceledException to 499. ProblemDetails includes traceId.
Replaced #pragma CA1848 with proper [LoggerMessage] delegates."
```

---

### Task 6: Final Build + Full Test Run

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build Asterisk.Platform.slnx`
Expected: 0 warnings, 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Asterisk.Platform.slnx -v q`
Expected: All tests pass (previous count + ~15 new tests)

- [ ] **Step 3: Verify new endpoints are mapped**

Check that `ConversationEndpoints` now has: `hold`, `unhold`, `POST /` (create). Check that `WebhookEndpoints` handles all three `BotResponseAction` values.
