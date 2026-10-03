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
