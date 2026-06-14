# AOT event-serialization guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the W4-C1 bug class — a `PlatformEvent` not registered in `ApiJsonContext` (SSE) / `PlatformPushJsonContext` (backplane) / `RemoteEventDispatcher` (decode) → runtime AOT crash or silent cross-pod loss — impossible to merge undetected.

**Architecture:** Layer 2 = an `ICrossPodEvent` marker making the cross-pod subset machine-checkable. Layer 1 = reflection-based completeness tests (tests are not AOT, so reflection is fine) that enumerate the `PlatformEvent` hierarchy and assert tri-presence. A small data-driven refactor of `RemoteEventDispatcher` exposes its handled set as the single source of truth. No reflection in shipped code.

**Tech Stack:** .NET 10, C# 14, System.Text.Json source-gen, xUnit + FluentAssertions, `System.Collections.Frozen`.

**Branch:** `feat/aot-event-serialization-guard` (spec already committed there). **Spec:** `docs/specs/2026-06-13-aot-event-serialization-guard.md`. **Conventions:** Conventional Commits, no Co-Authored-By. Build clean: `dotnet build Verbara.Platform.slnx -c Release` (warnings-as-errors).

---

## File structure

- **Create** `src/Verbara.Platform.Core/ICrossPodEvent.cs` — the marker (Layer 2).
- **Modify** `src/Verbara.Platform.Core/PlatformEventBus.cs` — 4 records implement `ICrossPodEvent`.
- **Modify** `src/Verbara.Platform.Realtime/Services/RemoteEventDispatcher.cs` — switch → data-driven `FrozenDictionary` + expose `HandledEventTypes`.
- **Modify** `tests/Verbara.Platform.Api.Tests/Endpoints/SseEndpointsTests.cs` — replace the broken hardcoded-array test with a reflection guard (Layer 1a).
- **Create** `tests/Verbara.Platform.Realtime.Tests/Services/CrossPodEventGuardTests.cs` — cross-pod sync guard (Layer 1b/1c).

---

## Task 1: `ICrossPodEvent` marker (Layer 2)

**Files:** Create `src/Verbara.Platform.Core/ICrossPodEvent.cs`; Modify `src/Verbara.Platform.Core/PlatformEventBus.cs:109,117,132,217`.

- [ ] **Step 1: Create the marker interface**

`src/Verbara.Platform.Core/ICrossPodEvent.cs`:
```csharp
namespace Verbara.Platform.Core;

/// <summary>
/// Marks a <see cref="PlatformEvent"/> that is distributed CROSS-POD via the Redis push
/// backplane (not only SSE). A cross-pod event MUST also be registered in
/// <c>Verbara.Platform.Core.Push.PlatformPushJsonContext</c> (backplane payload) and handled by
/// <c>Verbara.Platform.Realtime.Services.RemoteEventDispatcher</c> (cross-node decode/republish).
/// The guard tests enforce this by enumerating <see cref="ICrossPodEvent"/> implementers — a
/// compile-time contract; no runtime reflection ships.
/// </summary>
public interface ICrossPodEvent;
```

- [ ] **Step 2: Mark the 4 cross-pod records**

In `PlatformEventBus.cs`, append `, ICrossPodEvent` to the base-list of exactly these four records (leave their fields/discriminators unchanged):

`ConversationStateChangedEvent` (line ~114):
```csharp
    : PlatformEvent(TenantId, "conversation.state_changed", DateTimeOffset.UtcNow), ICrossPodEvent;
```
`AgentStateChangedEvent` (line ~123):
```csharp
    : PlatformEvent(TenantId, "agent.state_changed", DateTimeOffset.UtcNow), ICrossPodEvent;
```
`AgentPendingStateChangedEvent` (line ~137):
```csharp
    : PlatformEvent(TenantId, "agent.pending_state_changed", DateTimeOffset.UtcNow), ICrossPodEvent;
```
`TypificationSubmittedEvent` (line ~224):
```csharp
    : PlatformEvent(TenantId, "typification.submitted", DateTimeOffset.UtcNow), ICrossPodEvent;
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Verbara.Platform.Core/ -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Verbara.Platform.Core/ICrossPodEvent.cs src/Verbara.Platform.Core/PlatformEventBus.cs
git commit -m "feat(events): add ICrossPodEvent marker on the 4 cross-pod events"
```

---

## Task 2: data-driven `RemoteEventDispatcher` + `HandledEventTypes` (enables Layer 1c)

**Files:** Modify `src/Verbara.Platform.Realtime/Services/RemoteEventDispatcher.cs`.

- [ ] **Step 1: Write the failing test (the handled-set accessor must exist & be correct)**

Add to a new file `tests/Verbara.Platform.Realtime.Tests/Services/CrossPodEventGuardTests.cs` (the rest of this class is filled in Task 4 — for now just this one fact):
```csharp
using Verbara.Platform.Core;
using Verbara.Platform.Realtime.Services;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class CrossPodEventGuardTests
{
    private static IReadOnlySet<Type> CrossPodEventTypes() =>
        typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(ICrossPodEvent)))
            .ToHashSet();

    [Fact]
    public void Dispatcher_ShouldHandleExactlyTheCrossPodEventSet()
    {
        RemoteEventDispatcher.HandledEventTypes.Should().BeEquivalentTo(
            CrossPodEventTypes(),
            "the dispatcher must decode exactly the ICrossPodEvent set — no orphan handler, no missing case");
    }
}
```

- [ ] **Step 2: Run it to verify it FAILS to compile**

Run: `dotnet test tests/Verbara.Platform.Realtime.Tests/ --filter "FullyQualifiedName~CrossPodEventGuardTests" -c Release`
Expected: COMPILE ERROR — `RemoteEventDispatcher` has no `HandledEventTypes`.

- [ ] **Step 3: Refactor the dispatcher to data-driven dispatch**

In `RemoteEventDispatcher.cs`: add `using System.Collections.Frozen;` and `using System.Text.Json.Serialization.Metadata;` at the top. Replace the `switch` block (the `Dispatch` method body after the empty-payload check, lines 111-135) and the generic `DecodeAndPublish` with:

```csharp
        // SINGLE SOURCE OF TRUTH for cross-pod decode. Adding a cross-pod event = add one entry
        // here AND a [JsonSerializable] line in PlatformPushJsonContext + the ICrossPodEvent marker.
        // CrossPodEventGuardTests asserts these three stay in lock-step (no silent message loss).
        if (Handlers.TryGetValue(envelope.OriginalEventType, out var typeInfo))
        {
            DecodeAndPublish(envelope, typeInfo);
        }
        else
        {
            // Not a Platform.Core event — let other consumers (Pro internal mergers, custom
            // subscribers, the Pro.Push SignalR Presence path) handle the RemotePushEvent directly.
            RemoteEventDispatcherLog.UnknownEventType(_logger, envelope.OriginalEventType);
        }
    }

    private static readonly FrozenDictionary<string, JsonTypeInfo> Handlers =
        new Dictionary<string, JsonTypeInfo>
        {
            ["agent.state_changed"] = PlatformPushJsonContext.Default.AgentStateChangedEvent,
            ["agent.pending_state_changed"] = PlatformPushJsonContext.Default.AgentPendingStateChangedEvent,
            ["conversation.state_changed"] = PlatformPushJsonContext.Default.ConversationStateChangedEvent,
            ["typification.submitted"] = PlatformPushJsonContext.Default.TypificationSubmittedEvent,
        }.ToFrozenDictionary();

    /// <summary>The CLR event types this dispatcher decodes cross-pod — derived from
    /// <see cref="Handlers"/> (single source of truth). The cross-pod guard test asserts this
    /// equals the set of <c>ICrossPodEvent</c> implementers and the PlatformPushJsonContext set.</summary>
    internal static readonly IReadOnlySet<Type> HandledEventTypes =
        Handlers.Values.Select(ti => ti.Type).ToFrozenSet();

    private void DecodeAndPublish(RemotePushEvent envelope, JsonTypeInfo typeInfo)
    {
        var json = System.Text.Encoding.UTF8.GetString(envelope.RawPayload);
        if (JsonSerializer.Deserialize(json, typeInfo) is not PushEvent decoded)
        {
            RemoteEventDispatcherLog.DeserialiseFailed(_logger, envelope.OriginalEventType, "JsonSerializer returned null");
            return;
        }

        // Carry the envelope metadata through so tenant/user routing keeps working.
        if (envelope.Metadata is not null)
        {
            decoded = decoded with { Metadata = envelope.Metadata };
        }

        RemoteEventDispatcherLog.Decoded(_logger, envelope.OriginalEventType, envelope.SourceNodeId, envelope.Metadata?.TenantId ?? "(none)");

        var pending = _bus.PublishAsync(decoded, CancellationToken.None);
        if (!pending.IsCompletedSuccessfully)
        {
            pending.AsTask().GetAwaiter().GetResult();
        }
    }
```
(Remove the old `switch (envelope.OriginalEventType) { ... }` and the old generic `DecodeAndPublish<TEvent>(...)` — they are replaced above. Keep everything else in the file unchanged.)

- [ ] **Step 4: Run the new test + the EXISTING dispatcher tests (behavior preserved)**

Run: `dotnet test tests/Verbara.Platform.Realtime.Tests/ --filter "FullyQualifiedName~RemoteEventDispatcher|FullyQualifiedName~CrossPodEventGuard" -c Release`
Expected: PASS — `Dispatcher_ShouldHandleExactlyTheCrossPodEventSet` green AND the 3 existing `RemoteEventDispatcherTests` (round-trip decode of agent/conversation/typification events) still green, proving the data-driven refactor preserved decode behavior.

- [ ] **Step 5: Commit**

```bash
git add src/Verbara.Platform.Realtime/Services/RemoteEventDispatcher.cs tests/Verbara.Platform.Realtime.Tests/Services/CrossPodEventGuardTests.cs
git commit -m "refactor(realtime): data-driven RemoteEventDispatcher + expose HandledEventTypes"
```

---

## Task 3: ApiJsonContext completeness guard (Layer 1a) — replace the broken array test

**Files:** Modify `tests/Verbara.Platform.Api.Tests/Endpoints/SseEndpointsTests.cs`.

- [ ] **Step 1: Replace the broken hardcoded-array test with a reflection guard**

Delete the entire `WriteEventAsync_ShouldSerializeAllEventTypes_WhenUsingAotContext` method (lines 57-89, the 18-element hardcoded `PlatformEvent[]`). Replace it with:
```csharp
    [Fact]
    public void AllPlatformEvents_ShouldResolveInApiJsonContext()
    {
        // SSE serializes events by RUNTIME type (SseEndpoints.cs:195:
        // JsonSerializer.Serialize(data, data.GetType(), ApiJsonContext.Default)), so a missing
        // [JsonSerializable] registration is a RUNTIME crash the AOT analyzer cannot see. Enumerate
        // the closed PlatformEvent hierarchy (reflection is fine — tests are not AOT) and assert
        // each resolves. This replaces a hardcoded array that had itself gone stale (W4-C1 class).
        var eventTypes = typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(PlatformEvent)))
            .ToList();

        eventTypes.Should().HaveCountGreaterThan(15, "the PlatformEvent hierarchy should be discovered");

        var unregistered = eventTypes
            .Where(t => ApiJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        unregistered.Should().BeEmpty(
            "every PlatformEvent must be in ApiJsonContext for AOT SSE serialization");
    }
```
(`ApiJsonContext` is internal to the Api assembly; `Verbara.Platform.Api.Tests` already has `InternalsVisibleTo`, and the test file already has `using Verbara.Platform.Api.Serialization;` + `using Verbara.Platform.Core;`. Keep the other tests in the file unchanged.)

- [ ] **Step 2: Run it — verify PASS on current (consistent) state**

Run: `dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~AllPlatformEvents_ShouldResolveInApiJsonContext" -c Release`
Expected: PASS (all 19 events are currently registered).

- [ ] **Step 3: Prove the guard actually catches the bug — temporary negative check**

Comment out one event registration (e.g. the `[JsonSerializable(typeof(TypificationSubmittedEvent))]` line in `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs`), rebuild, rerun the test.
Run: `dotnet build Verbara.Platform.slnx -c Release && dotnet test tests/Verbara.Platform.Api.Tests/ --filter "FullyQualifiedName~AllPlatformEvents_ShouldResolveInApiJsonContext" -c Release`
Expected: **FAIL** listing `TypificationSubmittedEvent`. Then **restore** the commented line, rebuild, rerun → PASS. (This proves the guard would have caught W4-C1.)

- [ ] **Step 4: Commit**

```bash
git add tests/Verbara.Platform.Api.Tests/Endpoints/SseEndpointsTests.cs
git commit -m "test(sse): reflection-based ApiJsonContext completeness guard (replaces stale array)"
```

---

## Task 4: cross-pod sync guard (Layer 1b + 1c)

**Files:** Modify `tests/Verbara.Platform.Realtime.Tests/Services/CrossPodEventGuardTests.cs` (started in Task 2).

- [ ] **Step 1: Add the two remaining cross-pod assertions**

Add these facts to the `CrossPodEventGuardTests` class (it already has `CrossPodEventTypes()` + the dispatcher fact from Task 2). Add `using Verbara.Platform.Core.Push;` to the file's usings:
```csharp
    [Fact]
    public void EveryCrossPodEvent_ShouldBeRegisteredInPlatformPushJsonContext()
    {
        var missing = CrossPodEventTypes()
            .Where(t => PlatformPushJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name).OrderBy(n => n).ToList();

        missing.Should().BeEmpty(
            "every ICrossPodEvent must be in PlatformPushJsonContext for the Redis backplane");
    }

    [Fact]
    public void PlatformPushJsonContext_ShouldRegisterExactlyTheCrossPodEvents()
    {
        // The push context also registers the RemotePushEvent envelope (not a PlatformEvent);
        // restrict the comparison to PlatformEvent subtypes.
        var pushRegisteredEvents = typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract
                && t.IsAssignableTo(typeof(PlatformEvent))
                && PlatformPushJsonContext.Default.GetTypeInfo(t) is not null)
            .ToHashSet();

        pushRegisteredEvents.Should().BeEquivalentTo(CrossPodEventTypes(),
            "PlatformPushJsonContext should register exactly the ICrossPodEvent PlatformEvents — " +
            "no unmarked event in the push context, no marked event missing from it");
    }
```

- [ ] **Step 2: Run the full guard class**

Run: `dotnet test tests/Verbara.Platform.Realtime.Tests/ --filter "FullyQualifiedName~CrossPodEventGuardTests" -c Release`
Expected: PASS — all four facts green (the 3-way set {ICrossPodEvent} == {PushContext PlatformEvents} == {dispatcher HandledEventTypes} agrees).

- [ ] **Step 3: Prove it catches a sync break — temporary negative check**

Remove `, ICrossPodEvent` from `TypificationSubmittedEvent` in `PlatformEventBus.cs` (so it's now in the push context + dispatcher but NOT marked), rebuild, rerun.
Run: `dotnet build Verbara.Platform.slnx -c Release && dotnet test tests/Verbara.Platform.Realtime.Tests/ --filter "FullyQualifiedName~CrossPodEventGuardTests" -c Release`
Expected: **FAIL** — `PlatformPushJsonContext_ShouldRegisterExactlyTheCrossPodEvents` and `Dispatcher_ShouldHandleExactlyTheCrossPodEventSet` report the mismatch. **Restore** the marker, rebuild → PASS.

- [ ] **Step 4: Full build + the whole touched test surface**

Run: `dotnet build Verbara.Platform.slnx -c Release` (expect 0 warnings) then
`dotnet test Verbara.Platform.slnx --no-build -c Release --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"`
Expected: PASS — no unit regressions; the new guards green.

- [ ] **Step 5: Commit**

```bash
git add tests/Verbara.Platform.Realtime.Tests/Services/CrossPodEventGuardTests.cs
git commit -m "test(realtime): cross-pod 3-way sync guard (marker == push context == dispatcher)"
```

---

## Finalize

- [ ] **Push + PR**

```bash
git push -u origin feat/aot-event-serialization-guard
gh pr create -R verbara/Verbara.Platform --base main --head feat/aot-event-serialization-guard \
  --title "feat: AOT event-serialization guard (W4-C1 class)" \
  --body "Reflection-based completeness guards + ICrossPodEvent marker make a forgotten event registration (ApiJsonContext / PlatformPushJsonContext / RemoteEventDispatcher) a red CI build instead of a runtime AOT crash or silent cross-pod loss. Replaces the stale hardcoded SSE array test. Spec + plan under docs/. P2 of the methodology audit."
```

- [ ] **`git mv` the plan to completed on ship.**

---

## Self-Review

- **Spec coverage:** Layer 2 marker = Task 1 ✓; Layer 1a (ApiJsonContext superset) = Task 3 ✓; Layer 1b (PushContext) = Task 4 ✓; Layer 1c (dispatcher sync) = Tasks 2+4 ✓; broken array test replaced = Task 3 ✓; Layer 3 dropped (per spec) — no task, correct. Layer 4 (release.yml AOT smoke) pre-exists — no task.
- **Placeholders:** none — every step has exact code/paths/commands + a negative-check proving the guard bites.
- **Type consistency:** `ICrossPodEvent` (Core), `RemoteEventDispatcher.HandledEventTypes` (`IReadOnlySet<Type>`, internal), `Handlers` (`FrozenDictionary<string, JsonTypeInfo>`), `CrossPodEventTypes()` helper — names match across Tasks 2 & 4. `GetTypeInfo(Type)` returns `JsonTypeInfo?` on both contexts. `DecodeAndPublish(RemotePushEvent, JsonTypeInfo)` non-generic; `Deserialize(json, JsonTypeInfo)` → `object?` pattern-matched to `PushEvent` (preserves the existing `with { Metadata }` carry).
- **Risk note:** if `decoded with { Metadata = }` does not compile on the `PushEvent` static type (it did under the generic `TEvent : PushEvent`, so it should), the existing 3 `RemoteEventDispatcherTests` will catch any decode regression at Task 2 Step 4.
