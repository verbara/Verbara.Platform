# tenant-auth-config-surface Specification

## Purpose
TBD - created by archiving change surface-agent-presence-admin-controls. Update Purpose after archive.
## Requirements
### Requirement: The tenant auth-config update request accepts pendingPauseTimeoutMinutes as a partial-update field

The `PUT /api/v1/admin/auth/config` request body (`UpdateTenantAuthConfigRequest`) SHALL accept an
optional integer field named `pendingPauseTimeoutMinutes` (JSON name verbatim), typed `int?`
(nullable), mirroring the existing `sessionIdleTimeoutMinutes` partial-update field. When the field is
present, the handler MUST set `TenantAuthConfig.PendingPauseTimeoutMinutes` to the supplied value and
persist it; when the field is absent (JSON omitted or `null`), the handler MUST leave the persisted
`PendingPauseTimeoutMinutes` untouched (partial-update semantics identical to every sibling field). The
DTO MUST remain a typed sealed record registered in `ApiJsonContext` — no reflection, no anonymous
object. The field name MUST match `fixtures/tenant-auth-config-update-request.json` verbatim:
`pendingPauseTimeoutMinutes`.

#### Scenario: A partial update sets pendingPauseTimeoutMinutes

- **GIVEN** an authenticated `AdminOnly` caller for a resolved tenant whose persisted
  `PendingPauseTimeoutMinutes` is 30
- **WHEN** the caller sends `PUT /api/v1/admin/auth/config` with body
  `{ "pendingPauseTimeoutMinutes": 20 }` (verbatim per `fixtures/tenant-auth-config-update-request.json`)
- **THEN** the persisted `TenantAuthConfig.PendingPauseTimeoutMinutes` for that tenant becomes 20
- **AND** no other auth-config field on that tenant is modified

#### Scenario: Omitting pendingPauseTimeoutMinutes preserves the persisted value

- **GIVEN** an authenticated `AdminOnly` caller for a tenant whose persisted
  `PendingPauseTimeoutMinutes` is 20
- **WHEN** the caller sends `PUT /api/v1/admin/auth/config` with a body that does not include
  `pendingPauseTimeoutMinutes`
- **THEN** the persisted `PendingPauseTimeoutMinutes` remains 20 (unchanged)

### Requirement: The tenant auth-config response echoes pendingPauseTimeoutMinutes

The `GET /api/v1/admin/auth/config` and the `PUT`-after-write responses (`TenantAuthConfigResponse`,
projected by `FromConfig`) SHALL include an integer field named `pendingPauseTimeoutMinutes` (JSON name
verbatim) carrying the current persisted `TenantAuthConfig.PendingPauseTimeoutMinutes` for the resolved
tenant. The field name MUST match `fixtures/tenant-auth-config-response.json` verbatim:
`pendingPauseTimeoutMinutes`. Adding this field MUST NOT alter any other field emitted by
`TenantAuthConfigResponse` (including the redacted OIDC-secret surface `oidcClientSecretSet` /
`oidcClientSecretFingerprint` — PREPUB-2026-05-09-ADMIN-001 stays intact), and the response MUST remain
a typed sealed record registered in `ApiJsonContext`.

#### Scenario: GET echoes the persisted pendingPauseTimeoutMinutes

- **GIVEN** an authenticated `AdminOnly` caller for a tenant whose persisted
  `PendingPauseTimeoutMinutes` is 30
- **WHEN** the caller sends `GET /api/v1/admin/auth/config`
- **THEN** the JSON response contains `"pendingPauseTimeoutMinutes": 30` (name verbatim per
  `fixtures/tenant-auth-config-response.json`)
- **AND** the OIDC client secret is never emitted (`oidcClientSecretSet` / `oidcClientSecretFingerprint`
  stand-ins remain the only OIDC-secret surface)

#### Scenario: PUT-after-write echoes the newly persisted value

- **GIVEN** an authenticated `AdminOnly` caller who successfully sets
  `pendingPauseTimeoutMinutes` to 20 via `PUT /api/v1/admin/auth/config`
- **WHEN** the same `PUT` returns its response body
- **THEN** the response contains `"pendingPauseTimeoutMinutes": 20`

### Requirement: Surfacing pendingPauseTimeoutMinutes does not change pending-pause sweep behavior

Exposing `pendingPauseTimeoutMinutes` on the HTTP surface SHALL be a pure read/write projection of the
already-persisted `TenantAuthConfig.PendingPauseTimeoutMinutes`. It MUST NOT introduce a new endpoint,
a store change, a migration, or any change to the `PendingPauseDrainWorker` sweep, which already reads
`PendingPauseTimeoutMinutes` (`<= 0` disables the timeout). The value set over HTTP is the same value
the sweep consumes.

#### Scenario: The drain sweep consumes the HTTP-updated value

- **GIVEN** the drain sweep reads `TenantAuthConfig.PendingPauseTimeoutMinutes` to bound how long a
  deferred pause may stay pending
- **WHEN** an admin changes `pendingPauseTimeoutMinutes` via `PUT /api/v1/admin/auth/config`
- **THEN** the sweep's timeout bound on the next cycle reflects the updated value, with no code change
  to the sweep itself
- **AND** setting `pendingPauseTimeoutMinutes` to `0` (or less) disables the timeout for that tenant,
  exactly as the model field already documents

