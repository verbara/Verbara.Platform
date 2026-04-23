# Management API Keys

**Audience:** Platform administrators provisioning programmatic access
to the Platform Management API. Shipped in Platform v1.10.0 (R5.1
Phase 2) with Web v1.9.0 UI.

## Concept

A Management API key is a long-lived bearer token an operator presents
as `Authorization: Bearer <raw-key>` to authenticate to
`/api/v1/admin/*` endpoints without going through the password /
OIDC login flow. Scopes are **hardcoded in the backend** to the full
Management API surface — there is no per-key scope UI.

Each key has:

- A human-readable **name** (audit-visible).
- An **owner** (the admin user who created it).
- A **creation timestamp**.
- A **status** (`active`, `revoked`).
- A server-side **SHA-256 hash** of the raw key (the plaintext is never
  stored after creation).

## Creation flow (reveal-once)

The raw key is shown **exactly once** at creation time, in the Web UI.
Operators must copy it immediately — neither the UI nor the API expose
the plaintext afterward. If lost, the key must be rotated or revoked
and a new one created.

```sh
# Equivalent to the Web "Create Key" dialog:
curl -sS -X POST https://platform.example.com/api/v1/admin/api-keys \
  -H "Authorization: Bearer $ADMIN_JWT" \
  -H "Content-Type: application/json" \
  -d '{"name": "ci-runner-prod"}'
```

Response:

```json
{
  "id": "3c7fbb7e-7f04-4a64-9a59-7b02df5dfc6c",
  "name": "ci-runner-prod",
  "createdAtUtc": "2026-04-22T13:02:41Z",
  "createdBy": "alice@example.com",
  "rawKey": "apk_live_4d2c…9e7a"
}
```

The `rawKey` field is the one-and-only opportunity to capture the
plaintext. Subsequent `GET /api/v1/admin/api-keys/{id}` responses omit
it and return only metadata.

## Rotation flow

Rotation creates a new plaintext for the same key id, invalidating the
old one. Same one-time reveal semantics. Prefer rotating over deleting
when the consumer just needs to re-key without changing identity in
audit logs.

```sh
curl -sS -X POST https://platform.example.com/api/v1/admin/api-keys/{id}/rotate \
  -H "Authorization: Bearer $ADMIN_JWT"
```

Response mirrors the create shape (`rawKey` present exactly once).

## Revocation

Revokes the key; the hash is retained for audit traceability but no
further requests authenticate.

```sh
curl -sS -X POST https://platform.example.com/api/v1/admin/api-keys/{id}/revoke \
  -H "Authorization: Bearer $ADMIN_JWT"
```

## UI walkthrough (Web v1.9.0)

1. Navigate to **Admin → API Keys**. Table shows existing keys with
   status, creation date, owner, and a row-actions menu.
2. Click **Create Key**; enter a meaningful name (e.g.,
   `ci-runner-prod`, `terraform-ops`).
3. A modal shows the raw key with a **Copy** button (uses the
   `CopyButton` primitive shipped in R5.1 Phase 0). The modal blocks
   until the operator confirms they copied the key.
4. The key now appears in the table as **active**.
5. For rotate / revoke, use the row-action menu; both use the
   `ConfirmDeleteDialog.confirmationWord` primitive requiring the
   operator to type the key name to confirm (prevents fat-finger
   mistakes on production keys).

## Header format

```
Authorization: Bearer apk_live_<opaque>
```

The `ApiKeyAuthenticationHandler` validates and rehydrates the caller
principal with the `api-key` authentication scheme. `/auth/token` and
`/auth/refresh` flows are bypassed for API-key-authenticated requests.

## Known limitations

- **No "last used" timestamp in the UI.** The Management API key store
  does not currently track `lastUsedAt`, so the Web table shows only
  `createdAt`. Tracked as an **R5.2 backend gap** — adding the column
  + write path requires a migration.
- **No per-key scopes UI.** Scopes are hardcoded to full Management
  API access. Intentional — the use case was provisioned ops access,
  not user-facing API tokens. If scoped tokens become a requirement,
  the shape would move to OIDC client-credentials flow rather than this
  opaque-key path.
- **Query-string token fallback is gone.** Prior to v1.9.2, some clients
  passed `?token=<raw-key>` as a query parameter. Platform v1.9.2 removed
  that fallback to eliminate key leakage via access logs, referer
  headers, and browser history. Clients must use `Authorization: Bearer`
  headers only.

## Audit trail

Every create / rotate / revoke action writes an entry to the audit log
under the `api-keys` subsystem:

- `api-key.created { keyId, name, createdBy }`
- `api-key.rotated { keyId, rotatedBy }`
- `api-key.revoked { keyId, revokedBy }`

The Web UI's `AuditTrailMini` primitive (shipped in R5.1 Phase 2)
surfaces these inline per-key on the detail drawer.
