---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Platform.Web frontend team (auth-config admin consumer), Platform API maintainers
decision_ref: Verbara.Platform.Web/ADR-0009
---

# Proposal: surface-agent-presence-admin-controls (Platform host — expose the pending-pause timeout on the tenant auth-config HTTP surface)

## Why

`TenantAuthConfig.PendingPauseTimeoutMinutes` (`src/Verbara.Platform.Identity/TenantAuthConfig.cs:51`,
default 30) already governs how long a deferred "pause-when-free" request may stay pending before the
`PendingPauseDrainWorker` force-applies it (W4). The value is persisted by both stores and read by the
drain sweep — but it is **invisible over HTTP**: neither the write DTO (`UpdateTenantAuthConfigRequest`)
nor the read DTO (`TenantAuthConfigResponse`) carries it, so an admin cannot see or change the tenant's
pending-pause timeout without a direct DB write. Platform.Web wants an admin control for it
(Verbara.Platform.Web/ADR-0009), which is impossible until the field appears on the tenant auth-config
contract. This change surfaces the already-persisted field on that contract — no behavior change, just
making an existing knob reachable.

## What Changes

- **`UpdateTenantAuthConfigRequest` (write) gains `pendingPauseTimeoutMinutes`** — a nullable `int?`
  partial-update field, mirroring the existing `SessionIdleTimeoutMinutes` at
  `src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs` (DTO member at L190, handler line at L65).
  `UpdateConfig` gains exactly one handler line: `if (body.PendingPauseTimeoutMinutes.HasValue)
  config.PendingPauseTimeoutMinutes = body.PendingPauseTimeoutMinutes.Value;`. Omitting the field leaves
  the persisted value untouched (partial-update semantics, identical to every sibling field).
- **`TenantAuthConfigResponse` (read) gains `pendingPauseTimeoutMinutes`** plus its `FromConfig`
  projection line (`src/Verbara.Platform.Api/Endpoints/TenantAuthConfigResponse.cs`), so `GET` and the
  `PUT`-after-write both echo the current value.
- **No new endpoint, no store change, no migration, no `ApiJsonContext` addition** — both DTOs are
  already registered (`Serialization/ApiJsonContext.cs:424`, `:655`); they only gain a property. The
  model field, its persistence, and the drain-sweep consumer are all already shipped.
- **Force-offline is NOT Platform work here.** `POST /api/v1/admin/agents/{id}/force-offline`
  (`src/Verbara.Platform.Api/Endpoints/AdminEndpoints.cs`, `ForceAgentOffline`, `AdminOnly` +
  `RequireOperationalTenant`; `ForceAgentOfflineRequest` registered at `ApiJsonContext.cs:602`) is
  **already fully shipped**. It is listed here only as the already-shipped capability the Web child
  change consumes (a `useForceOffline` hook + button) — this host change makes zero edits to it.
- **No runtime behavior change on the pending-pause sweep** — the drain worker already reads
  `PendingPauseTimeoutMinutes`; this change only exposes read/write of the same value over HTTP.

## Capabilities

### New Capabilities

- `tenant-auth-config-surface`: the HTTP read/write contract for the per-tenant auth configuration
  (`GET`/`PUT /api/v1/admin/auth/config`) exposes the pending-pause timeout as
  `pendingPauseTimeoutMinutes` — a partial-update integer on the request and an echoed integer on the
  response — so an admin can read and change the already-persisted `TenantAuthConfig.PendingPauseTimeoutMinutes`.

### Modified Capabilities

<!-- none — no existing openspec/specs/ capability covers the tenant auth-config HTTP surface -->

## Impact

- **Cross-repo — see `impact.yaml`** (`openspec/changes/surface-agent-presence-admin-controls/impact.yaml`).
  Scope confirmed by `/xr:change` scouts: **producer** = Verbara.Platform (this host — surfaces
  `pendingPauseTimeoutMinutes` on the two auth-config DTOs); **consumer** = Verbara.Platform.Web (adds a
  `pendingPauseTimeoutMinutes` editor mirroring the shipped `sessionIdleTimeoutMinutes` control, plus a
  `useForceOffline` hook + button over the already-shipped force-offline endpoint — the Web child change,
  authored later by `/xr:propagate`, NOT here).
- **Affected code (host):** `src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs`
  (`UpdateTenantAuthConfigRequest` + one `UpdateConfig` handler line) and
  `src/Verbara.Platform.Api/Endpoints/TenantAuthConfigResponse.cs` (record property + `FromConfig`
  projection). No change to `TenantAuthConfig`, the stores, `ApiJsonContext`, `Program.cs`, or any
  endpoint routing.
- **buildOrder:** Platform (1) surfaces the field before Web (2) binds a control to it — a hard contract
  barrier (Web is decoupled from Platform's NuGet feed but depends on the emitted contract).
- **AOT / constraints:** both DTOs stay typed sealed records already in `ApiJsonContext`; adding an
  `int?` / `int` property is reflection-free and source-gen-clean. Zero warnings
  (`TreatWarningsAsErrors=true`).
- **decision_ref:** Verbara.Platform.Web/ADR-0009 (the admin-controls decision this surfacing serves;
  the field is a knob that decision needs reachable over HTTP). No Platform ADR is required — this is a
  pure surfacing of an existing, decided-and-shipped model field.
