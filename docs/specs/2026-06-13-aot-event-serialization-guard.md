# AOT event-serialization guard — design

**Status:** Approved design — 2026-06-13
**Origin:** P2 of the methodology audit (`verbara-meta`). First P2 sub-project.

## Problem

The SSE endpoint serializes events by their **runtime type**:
`SseEndpoints.cs:195` → `JsonSerializer.Serialize(data, data.GetType(), ApiJsonContext.Default)`.
Because `data.GetType()` is an opaque `Type`, the .NET AOT/trim analyzer (already enabled)
is **structurally blind** to a missing `[JsonSerializable]` registration — it surfaces only
at **runtime** as `NotSupportedException`/`InvalidOperationException` (the W4-C1 incident:
a new event wasn't in `ApiJsonContext` → AOT SSE crash on every publish).

A new `PlatformEvent` must be registered on **three** manual sync surfaces, today guarded
only by a code comment:
1. `ApiJsonContext` (SSE) — **all** events.
2. `PlatformPushJsonContext` (Redis cross-pod backplane) — the **cross-pod subset** (4 today).
3. A `case` in `RemoteEventDispatcher.Dispatch()`'s `switch (OriginalEventType)` — same subset
   (a missing case = **silent message loss**, no exception).

The existing guard (`SseEndpointsTests.WriteEventAsync_ShouldSerializeAllEventTypes`) is
**provably broken**: it iterates a hardcoded 18-event array while the hierarchy has 19 —
`TypificationSubmittedEvent` is already absent from the array (green only by luck of correct
hand-registration). A forgotten event is forgotten in both the context and the array → green.

**Scope is narrow (events only):** of 22 `JsonSerializerContext` partials and ~75 serialize
sites, the SSE site is the **only** one that crashes on a missing registration; the other 20
contexts use compile-checked `Context.Default.X` and the audit store has a try/catch fallback.
`PlatformEvent` (19 `sealed record`s in `PlatformEventBus.cs`) is the only serialized
"must-register-all-subclasses" hierarchy.

## Goals / success criteria

- A new `PlatformEvent` subclass missing from `ApiJsonContext` **fails CI** (the new `ci.yml`
  gate) before merge — would have caught W4-C1.
- The cross-pod 3-way sync (marker ⟺ `PlatformPushJsonContext` ⟺ dispatcher case) is
  **machine-enforced**, not comment-enforced.
- A deploy-time backstop fails the pod fast on any registration gap the JIT test can't prove.

## Non-goals (analyzed, deliberately excluded)

- **`[JsonDerivedType]` polymorphism** — REJECTED: it injects a `$type` discriminator into every
  SSE frame, breaking the flat-camelCase wire contract the React client reads (`use-sse.ts`).
- **Custom incremental source generator** — DEFERRED: greenfield Roslyn infra, disproportionate
  for ~1–2 events/phase; revisit only if event churn grows. (Tracked as a future option.)
- **Broad all-22-contexts guard** — out of scope: the other contexts are compile-checked, so the
  systemic risk does not exist. (Layer 1 is designed to be *reusable* if that ever changes.)
- **Pre-commit format/lint hooks** — out of scope: `EnforceCodeStyleInBuild=true` + warnings-as-
  errors already enforce format at build.

## Design — defense in depth (4 layers)

### Layer 2 — `ICrossPodEvent` marker (keystone, build first)
A marker interface `ICrossPodEvent` on the cross-pod records (`AgentStateChangedEvent`,
`AgentPendingStateChangedEvent`, `ConversationStateChangedEvent`, `TypificationSubmittedEvent`)
in `PlatformEventBus.cs`. Makes the 4-vs-19 cross-pod split **declarative and machine-checkable**
(no runtime reflection in shipped code — it's a compile-time type contract).

### Layer 1 — reflection completeness test (primary, CI/merge-time)
Replace the broken hardcoded-array test with a reflection test (tests are not AOT → reflection
is fine) that enumerates `typeof(PlatformEvent).Assembly` for non-abstract `PlatformEvent`
subtypes and asserts:
- (a) **every** subtype resolves via `ApiJsonContext.Default.GetTypeInfo(t) is not null`;
- (b) **every** `ICrossPodEvent` subtype also resolves via `PlatformPushJsonContext.Default.GetTypeInfo(t)`;
- (c) the dispatcher handles **exactly** the `ICrossPodEvent` set — assert the dispatcher's known
  `OriginalEventType` discriminators == `{ each ICrossPodEvent.Type }`. (Expose the dispatcher's
  known-discriminator set for testability, or drive it functionally with a `RemotePushEvent` per
  event and assert it republishes rather than hitting the `UnknownEventType` default.)

Runs in the `ci.yml` unit-test gate (`dotnet test`, CoreCLR). High-fidelity because source-gen
emits the same `JsonTypeInfo` table in JIT and AOT, and `JsonSerializerIsReflectionEnabledByDefault=false`
makes the missing-registration throw identical on CoreCLR.

### Layer 3 — runtime startup self-check (deploy-time backstop)
An `IStartupFilter`/`IHostedService` in the Api host that, at boot, iterates the `PlatformEvent`
subtypes and calls the **actually configured** resolver's `GetTypeInfo(t)`; throws on the first
unresolvable type so the pod fail-fasts (crash-loop visible to readiness) instead of crashing on
the first SSE publish hours later. Runs **inside the real AOT image**, closing the JIT-proxy gap
the Layer-1 test cannot fully cover (AOT trim could prune a member the JIT test never exercises).

### Layer 4 — AOT-publish smoke (release-train net, already exists)
The full Native AOT publish in `release.yml` is the final end-to-end net. No new work; optionally
ensure the smoke path exercises one event serialize. Out of scope to build here.

## Honest caveat

A JIT `GetTypeInfo() != null` test catches **100% of "forgot the `[JsonSerializable]` line"**
(the actual W4-C1 / current-near-miss class) but is **not** a perfect proxy for all AOT-trim
serialization failures. Layer 3 (runs in the AOT image) is what makes this "no shortcuts."

## Key files

- `src/Verbara.Platform.Core/PlatformEventBus.cs` — `PlatformEvent` + 19 events; add `ICrossPodEvent` + mark 4.
- `src/Verbara.Platform.Api/Serialization/ApiJsonContext.cs` — SSE registrations (all 19).
- `src/Verbara.Platform.Core/Push/PlatformPushJsonContext.cs` — cross-pod (4).
- `src/Verbara.Platform.Realtime/Services/RemoteEventDispatcher.cs:114` — the 4-case switch (expose known set).
- `src/Verbara.Platform.Api/Endpoints/SseEndpoints.cs:195` — the only crashing site (context only; no change).
- `tests/Verbara.Platform.Api.Tests/Endpoints/SseEndpointsTests.cs` — replace the broken array test.
- `src/Verbara.Platform.Api/Program.cs` — wire the Layer-3 startup self-check.

## Verification

- Remove one event's `[JsonSerializable]` line locally → the Layer-1 test goes RED (and the
  Layer-3 check throws on boot). Restore → green.
- Remove a dispatcher `case` for a cross-pod event → Layer-1 (c) goes RED.
- The test enumerates 19/19 today and passes (confirms current state is consistent — the existing
  array's missing `TypificationSubmittedEvent` is no longer a blind spot).
