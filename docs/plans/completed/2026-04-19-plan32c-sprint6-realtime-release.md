# Plan 32C Sprint 6 — Real-Time Presence coordinated release

**Status:** Completed (local — not yet pushed) · **Date:** 2026-04-19
**Closes:** Plan 32C "Real-Time Presence" (v1.4.0 Phase 2B)
**Prior sprints:** Sprint 0–4 + Sprint 5 E1 in Pro 1.6.0-pro (committed in Pro repo, pre-consolidation). Sprint 5 E2 in Pro 1.7.1-pro + Platform.Web `1699781` (pre-consolidation).

> This is the first plan completion tracked in this repo under the new consolidation rule (see `docs/decisions/` and `feedback_platform_web_consolidation.md` in the project memory). Sprint 6 touches three repos but is planned and audited from here.

## Scope delivered

| Task | Repo | Commit | Summary |
|------|------|--------|---------|
| T27 | Pro | `ee3234f` | `PresenceFanoutService : BackgroundService` subscribes to `PresenceTracker.Deltas` and fans transitions to the `presence:agent:{agentId}` SignalR group via `IHubContext<PlatformHub, IPlatformHubClient>`. Pro bumped to **1.7.2-pro**. |
| T39 | Platform | `eabb965` | `DefaultFeatureRegistry` adds `realtimePushSignalR=true`. SDK pinned to 1.11.1; Pro pinned to 1.7.2-pro; transitive deps (Npgsql 10 / Rx 6.1.0 / ME.* 10.0.6) aligned. |
| T39 | Web | `7f76417` | `useRealtimeBootstrap` gates `startPlatformHub()` on `useAuthStore.features.realtimePushSignalR`. Feature propagates through existing `/auth` login response pipeline. |
| T37 | Web | `0065c92` | `tests/e2e/tests/operations/realtime-presence.spec.ts` — dual-browser (supervisor + agent) verifying 3s-or-faster propagation of state transitions and offline detection on agent disconnect. |
| T38 | Web | `0065c92` | `tests/e2e/tests/operations/supervisor-coach.spec.ts` — start supervision → agent `<SupervisionBanner />` visible → whisper delivered → stop → banner clears. |
| T40–T43 | Pro / Platform / Web | _this commit set_ | CLAUDE.md + docs/packages.md Pro refresh; this plan completion doc in Platform. |
| T44 | all | _pending user approval_ | Coordinated version bumps: Pro remains 1.7.2-pro, Platform 1.7.0 → 1.8.0, Platform.Web 1.7.0 → 1.8.0; tag + GitHub release in all three repos. |

## Realtime loop — end-to-end path now wired

```
Agent page (Platform.Web) invokes PlatformHub.UpdatePresenceAsync
  → Hub mutates PresenceTracker (local) + IPushEventBus.PublishAsync(PresenceSnapshotEvent)
     → Pro.Push backplane (Redis/Postgres) relays to every other node as RemotePushEvent
     → Each remote node's PresenceMergeConsumer deserializes + ApplyRemote on its tracker
  → Every tracker (local + remote) emits Deltas
  → PresenceFanoutService (1.7.2-pro) subscribes Deltas and calls
    IHubContext<PlatformHub, IPlatformHubClient>.Clients.Group("presence:agent:{id}").OnPresenceUpdated(snapshot)
     → Browser hub proxy receives OnPresenceUpdated → useRealtimeStore.upsertPresence
     → useRealtimePresence(agentId) / agent-states-page re-render
```

Gate: `realtimePushSignalR` feature flag (Platform-side) can disable the loop centrally without rebuilding the client.

## Test posture

- **Pro unit:** 1,034 (+4 for `PresenceFanoutServiceTests`)
- **Pro SignalR.Tests:** 50 (+4 fanout)
- **Pro integration:** 260 (Postgres IT + Push IT, unchanged)
- **Platform:** build green, feature-gate test unchanged
- **Web typecheck:** tsc -b --noEmit green
- **Web vitest:** 45 baseline
- **Web E2E:** 2 new specs (gated on `E2E_FULL_STACK=true`); discovered by Playwright, skipped in CI

## Pending (T44 — awaits user push confirm)

1. `Asterisk.Platform/Directory.Build.props` bump `1.7.0` → `1.8.0`.
2. `Asterisk.Platform.Web/package.json` bump `1.7.0` → `1.8.0`.
3. Commit + tag `v1.8.0` in Platform and Platform.Web, tag `v1.7.2-pro` in Pro.
4. Push three repos + GitHub release notes referencing this plan file.

## Artefacts

- Pro head: `ee3234f` on `main` (7 commits ahead of origin since last push)
- Platform head: `eabb965` on `main` (N commits ahead of origin)
- Platform.Web head: `0065c92` on `main` (3 commits ahead of origin)
- Local NuGet feed: 24 Pro `1.7.2-pro` + SDK `1.11.1`, all legacy Pro versions purged.
