# Tasks: surface-agent-presence-admin-controls (Platform host / producer)

> Host (producer) tasks for the cross-repo `surface-agent-presence-admin-controls` change. Scope is a
> pure surfacing of the already-persisted `TenantAuthConfig.PendingPauseTimeoutMinutes` on the two
> tenant auth-config DTOs (design.md D1–D4), mirroring the shipped `sessionIdleTimeoutMinutes` handling
> verbatim. Force-offline is already fully shipped — NOT touched here. The Web control (a
> `pendingPauseTimeoutMinutes` editor + `useForceOffline` hook/button) is the child change
> (`web/surface-agent-presence-admin-controls`, buildOrder 2 per `impact.yaml`), authored by
> `/xr:propagate` — NOT this host.

## 1. Write DTO surfacing (Phase A — foundation)

- [ ] 1.1 Add `public int? PendingPauseTimeoutMinutes { get; init; }` to `UpdateTenantAuthConfigRequest`
  (`src/Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs`, next to `SessionIdleTimeoutMinutes` at
  L190). Nullable partial-update field; wire name `pendingPauseTimeoutMinutes` (verbatim per
  `fixtures/tenant-auth-config-update-request.json`). No `ApiJsonContext` change — the DTO is already
  registered (`ApiJsonContext.cs:655`).
- [ ] 1.2 Add one guarded handler line to `UpdateConfig`
  (`AuthAdminEndpoints.cs`, next to the `SessionIdleTimeoutMinutes` line at L65):
  `if (body.PendingPauseTimeoutMinutes.HasValue) config.PendingPauseTimeoutMinutes = body.PendingPauseTimeoutMinutes.Value;`.
  Partial-update semantics — omission leaves the persisted value untouched (design.md D1).

## 2. Read DTO surfacing (Phase A — foundation)

- [ ] 2.1 Add `int PendingPauseTimeoutMinutes` to the `TenantAuthConfigResponse` record
  (`src/Verbara.Platform.Api/Endpoints/TenantAuthConfigResponse.cs`), positioned between
  `SessionAbsoluteTimeoutHours` and `OidcEnabled` to match
  `fixtures/tenant-auth-config-response.json`. Wire name `pendingPauseTimeoutMinutes` verbatim.
- [ ] 2.2 Add the corresponding projection line to `FromConfig`
  (`PendingPauseTimeoutMinutes: config.PendingPauseTimeoutMinutes`) — the redaction seam stays the
  single projection point; the OIDC-secret redaction (`OidcClientSecretSet` /
  `OidcClientSecretFingerprint`, PREPUB-2026-05-09-ADMIN-001) is untouched (design.md D2).

## 3. Tests (Phase B — critical component)

- [ ] 3.1 Extend the auth-admin config endpoint tests: a `PUT /api/v1/admin/auth/config` with
  `{ "pendingPauseTimeoutMinutes": 20 }` persists 20 and returns it in the response body; a `PUT` that
  omits the field leaves the persisted value untouched (partial-update). Test names follow
  `Method_ShouldExpected_WhenCondition`.
- [ ] 3.2 Extend the tests: a `GET /api/v1/admin/auth/config` echoes the persisted
  `pendingPauseTimeoutMinutes` and the OIDC secret is never emitted (redaction stand-ins unchanged).
- [ ] 3.3 Assert the wire field name is `pendingPauseTimeoutMinutes` verbatim (JSON round-trip),
  matching `fixtures/tenant-auth-config-update-request.json` and `fixtures/tenant-auth-config-response.json`.

## 4. Records (Phase C — integration)

- [ ] 4.1 Add the `[Unreleased]` CHANGELOG entry (surfaced `pendingPauseTimeoutMinutes` on the tenant
  auth-config read/write contract). No Platform ADR required (pure surfacing of an already-decided,
  already-shipped model field; motivated by Verbara.Platform.Web/ADR-0009 — design.md Open Questions).

## 5. Verification gate

- [ ] 5.1 `dotnet build Verbara.Platform.slnx -c Release` and `dotnet test` green — zero warnings
  (`TreatWarningsAsErrors=true`, `WarningLevel=9999`), no new AOT diagnostics
  (`IL2026`/`IL3050`/`IL207x`); `openspec validate surface-agent-presence-admin-controls --strict`
  green; CI green.

## 6. Cross-repo handoff (Web child change — NOT this host's edit)

- [ ] 6.1 After this host surfaces the field and CI is green, the Web child change
  (`web/surface-agent-presence-admin-controls`, buildOrder 2 per `impact.yaml`) regenerates its typed
  client, adds a `pendingPauseTimeoutMinutes` number `Input` on `admin/system/auth-config-page.tsx`
  (mirroring `sessionIdleTimeoutMinutes`), and adds a `useForceOffline` hook + button over the
  already-shipped force-offline endpoint. Web gate: `npm run build`, `npx vitest run`, `npx eslint .`,
  i18n parity green. Driven by `/xr:propagate` then `/xr:apply` — NOT this host.
