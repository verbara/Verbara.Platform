# Upgrading to 2.25.0

What an operator or an API integrator meets when moving a Platform installation from 2.24.x to
2.25.0, and what to check after the first start.

## Role changes move RBAC roles

Server-side permission checks read a user's RBAC roles (`user_roles`), not its role. As of 2.25.0 the
two stay in step:

- Changing a user's role through `PUT /api/v1/admin/users/{id}` (the role field of the console's user
  form) moves the user's RBAC roles in the same request. The roles that come with the former role are
  removed: the tenant's copy of its role template in every shape it comes in (the template id,
  `role_{template}_{tenant}`, the role named like the template, and the `admin-{tenant}` and
  `platform-admin-{tenant}` roles setup creates), and for an Admin the copies of every administrator
  template (Admin, System Admin, Platform Admin, Partner Admin). The new role's role is granted. Roles
  created under other names are kept. The change is audited as `user.role_changed`, with the roles
  removed and granted.
- Creating a user (`POST /api/v1/admin/users`, OIDC auto-provisioning) grants the role's RBAC role at
  once instead of at the next start.
- The role migration that runs at every start removes the grants it made itself, or that user
  creation made, for a role the user no longer has. The first start of 2.25.0 therefore removes the
  grants earlier role changes left behind, when the migration had made them.

### After the first start: review administrator roles held by non-administrators

Grants made by setup for the first administrators, and grants an administrator made, are not the
migration's to remove: it cannot tell a leftover from an intended grant. List them once the first
start of 2.25.0 has completed:

```sql
SELECT u.tenant_id, u.user_id, u.email, u.role, ur.role_id, tr.name AS role_name,
       ur.assigned_by, ur.assigned_at
FROM users u
JOIN user_roles ur ON ur.tenant_id = u.tenant_id AND ur.user_id = u.user_id
JOIN tenant_roles tr ON tr.tenant_id = ur.tenant_id AND tr.role_id = ur.role_id
WHERE u.role <> 2  -- 2 = Admin
  AND (tr.source_template_id IN ('admin', 'system_admin', 'platform_admin', 'partner_admin')
       OR lower(tr.name) IN ('admin', 'system admin', 'platform admin', 'partner admin'))
ORDER BY u.tenant_id, u.email, ur.role_id;
```

Each row is a user whose role is not Admin and who holds an administrator RBAC role, or a role made
from an administrator template. `assigned_by` is empty for the roles setup grants and for grants made
from the console, and a user id for a grant made with that user's API key. A role an administrator
created under its own name (an auditors role, say) may be intended; a template copy such as
`admin-{tenant}` or `platform-admin-{tenant}` on a user who is no longer an administrator is a leftover
of an earlier demotion. Remove it with `DELETE /api/v1/admin/users/{userId}/roles/{roleId}`, which is
audited and drops the user's cached permissions on every node. A row deleted in SQL instead takes
effect within five minutes (the lifetime of the permission cache) or at the next start.

## A lower role ends the user's sessions

A change to a lower role (Admin > Supervisor > Agent > Api) revokes the user's refresh tokens. The user
signs in again once its current access token expires, at most 15 minutes later; until then that token
keeps the role it was issued with.

## `PUT /api/v1/admin/users/{id}` honours `If-Match`

`GET /api/v1/admin/users/{id}` and `PUT /api/v1/admin/users/{id}` return an `ETag` for the user's
display name, role and status. A `PUT` that sends it back in `If-Match` is refused with
`412 Precondition Failed` (`application/problem+json`) when one of those values changed since it was
read, and nothing is written. `If-Match` is optional: without it an edit is applied as before. API
clients that edit users should send it.

## Partner credit attribution needs `partner:billing:view`

`GET /api/v1/partner/credit-ledger/attribution` requires `partner:billing:view`, like
`/api/v1/partner/revenue`. The Admin, Partner Admin, Partner Billing and Partner Viewer templates carry
it; other users of a Partner tenant get 403.

## Password-reset links and the OIDC redirect URI use the configured public address

Platform.Api no longer builds the password-reset link or the OIDC `redirect_uri` from the request's
`Host` header. It uses `Platform:PublicBaseUrl` (environment variable `Platform__PublicBaseUrl`),
the absolute `http` or `https` address users open the console at, and while that is unset, the
origin `CORS_ORIGINS` names when it names exactly one:

- An installation whose `CORS_ORIGINS` names only the console's origin, as
  `docker/.env.production.example` shows, needs no change.
- Any other installation should set `Platform__PublicBaseUrl` when it upgrades. Until it does, the
  API sends no password-reset email (`POST /api/v1/auth/forgot-password` still answers 200),
  answers OIDC sign-in with 500, and logs a warning naming the setting each time (event ids 7520
  and 7521). This covers a `CORS_ORIGINS` that lists several origins, such as the
  `docker-compose.reference-smb.yml` default (`http://localhost,https://localhost`); set
  `PUBLIC_BASE_URL` in `.env.reference-smb`. The Helm chart derives the address from
  `ingress.hostnameWeb` and `ingress.tlsEnabled` unless `api.publicBaseUrl` is set.
- A value that is not an absolute `http` or `https` URL, or that carries a query, a fragment or user
  information, counts as unset, and is not replaced by `CORS_ORIGINS`.

### OIDC single sign-on: check the registered redirect URI

The `redirect_uri` is now `<address>/api/auth/oidc/callback`, scheme included, from the configured
address. Before, it took the scheme and host of the sign-in request, which behind a proxy that
terminates TLS without `ForwardedHeaders:TrustedProxies` came out as `http://`. Check that the
redirect URI registered with each tenant's identity provider matches the new value exactly, and
register the new value where it does not. Users must start single sign-on from the console at the
configured address: the sign-in state cookie belongs to the host the sign-in started on.

### The reset link's token is percent-encoded

The token in the reset link is now percent-encoded. The console's reset page could not use a token
that contained `+` (about half of them), and now can; links mailed before the upgrade are unchanged.

### Host filtering stays off

No shipped configuration sets ASP.NET Core's `AllowedHosts`, so the API still answers on any
`Host`. If you set it, `docker/README.md` lists the names it must include so that health checks,
Kubernetes probes and Platform.Realtime keep reaching the API.
