## Context

`TenantAuthConfig.PendingPauseTimeoutMinutes` (`src/Verbara.Platform.Identity/TenantAuthConfig.cs:51`,
default 30, W4) is already persisted by both stores and read by `PendingPauseDrainWorker` to bound how
long a deferred "pause-when-free" request may stay pending. It is invisible over HTTP: the two tenant
auth-config DTOs on `AuthAdminEndpoints.cs` (`UpdateTenantAuthConfigRequest`) and
`TenantAuthConfigResponse.cs` do not carry it. Verbara.Platform.Web/ADR-0009 needs an admin control for
this knob, which requires the field on the tenant auth-config contract. This design covers the host
(producer) surfacing only; the Web control is the child change authored later by `/xr:propagate`.

This artifact exists primarily to unblock `tasks` (the OpenSpec `tasks` schema lists `design` as a
dependency). The change is deliberately small; the decisions below are the few that matter.

## Goals / Non-Goals

**Goals:**
- Expose `pendingPauseTimeoutMinutes` on the `PUT` request DTO as a partial-update `int?` and on the
  read DTO as an `int`, using the exact wire name pinned in the golden fixtures.
- Mirror the shipped `sessionIdleTimeoutMinutes` handling verbatim so the surfacing is boring and
  reviewable.

**Non-Goals:**
- No new endpoint, no route change, no store change, no migration (the field is already persisted).
- No `ApiJsonContext` addition — both DTOs are already registered (`ApiJsonContext.cs:424`, `:655`).
- No change to force-offline (`POST /api/v1/admin/agents/{id}/force-offline`) — already fully shipped;
  it is the already-shipped capability the Web child consumes, not host work.
- No change to `PendingPauseDrainWorker` — it already reads the model field.
- No Web work (the `pendingPauseTimeoutMinutes` editor + `useForceOffline` hook are the child change).

## Decisions

**D1 — Mirror `sessionIdleTimeoutMinutes` exactly (partial-update `int?` on the write DTO).**
`UpdateTenantAuthConfigRequest` gains `public int? PendingPauseTimeoutMinutes { get; init; }` and
`UpdateConfig` gains one guarded line
`if (body.PendingPauseTimeoutMinutes.HasValue) config.PendingPauseTimeoutMinutes = body.PendingPauseTimeoutMinutes.Value;`,
placed next to the existing `SessionIdleTimeoutMinutes` line (`AuthAdminEndpoints.cs:65`). Rationale:
partial-update semantics are the established contract for every field on this DTO — a nullable field
that, when omitted, leaves the persisted value untouched. Alternative considered: a non-nullable field
with a sentinel default — rejected because it would silently overwrite the persisted value on any
partial update that omits the field, breaking the DTO's uniform partial-update contract.

**D2 — Read DTO is a projection via `FromConfig`, not a direct model echo.**
`TenantAuthConfigResponse` gains `int PendingPauseTimeoutMinutes` and `FromConfig` gains the
corresponding projection line. Rationale: `TenantAuthConfigResponse` is deliberately a redacted
projection of `TenantAuthConfig` (PREPUB-2026-05-09-ADMIN-001: the OIDC client secret must never leave
the process; it is replaced by `OidcClientSecretSet` / `OidcClientSecretFingerprint`). New fields MUST
be added through `FromConfig` so the redaction boundary stays the single projection seam. Alternative
considered: serializing `TenantAuthConfig` directly — rejected, it would leak the raw OIDC secret.

**D3 — Verbatim wire name and position pinned to fixtures.** The JSON name is
`pendingPauseTimeoutMinutes` exactly (per `fixtures/tenant-auth-config-update-request.json` and
`fixtures/tenant-auth-config-response.json`); in the response fixture it sits between
`sessionAbsoluteTimeoutHours` and `oidcEnabled`. Rationale: the Web child consumes the emitted contract
by name; the verbatim-fixture-citation rule is a blocking cross-repo boundary contract, not a style
choice.

**D4 — AOT posture unchanged.** Both DTOs stay typed sealed records already registered in
`ApiJsonContext` (source-gen serialization, no reflection) per Platform/ADR-0022. Adding an `int?` /
`int` property is source-gen-clean and needs no new `[JsonSerializable]` entry. No Dapper, no store
change, so no data-access surface is touched.

## Risks / Trade-offs

- [Overwriting the persisted value on omission] → mitigated by D1: the write field is `int?` and the
  handler line is `HasValue`-guarded, identical to every sibling partial-update field.
- [Leaking the OIDC secret when extending the read DTO] → mitigated by D2: the field is added through
  the `FromConfig` projection seam; the redacted OIDC surface is untouched.
- [Web binding a wrong field name] → mitigated by D3: the name is pinned verbatim to the golden
  fixtures and the child change lands only after the host surfaces the field (hard buildOrder barrier,
  `impact.yaml`).

## Migration Plan

None. The field is already persisted by both stores at its existing default (30); this change only
adds read/write projection over HTTP. Deploy is the normal Platform image release. Rollback is a plain
revert of the two DTO edits — no schema or data migration to unwind.

## Open Questions

None. No Platform ADR is required (pure surfacing of an already-decided, already-shipped model field);
the motivating decision is Verbara.Platform.Web/ADR-0009.
