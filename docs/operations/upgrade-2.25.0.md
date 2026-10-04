# Upgrading to 2.25.0

What an operator or an API integrator meets when moving a Platform installation from 2.24.x to
2.25.0: what to check before, what to do during, what changes, and how to roll back. 2.25.0 adds no
database migration. The only data its first start changes is RBAC grants, which the role migration
removes for roles the user no longer has (see [Role changes move RBAC roles](#role-changes-move-rbac-roles)).

## Before you upgrade

1. **Public address.** Set `Platform__PublicBaseUrl` to the address users open the console at
   (compose `PUBLIC_BASE_URL`, Helm `api.publicBaseUrl`). You can skip it only when `CORS_ORIGINS`
   names exactly that one origin. Check with
   `docker compose exec platform-api printenv CORS_ORIGINS Platform__PublicBaseUrl`. You must set it
   when:
   - `CORS_ORIGINS` lists several origins (the `docker-compose.reference-smb.yml` default, the Helm
     default `corsOrigins`);
   - you deploy with `docker-compose.verified.yml`, which sets no `CORS_ORIGINS`;
   - you use the Helm chart with `ingress.enabled: false` (it derives the address only while the
     ingress is enabled).

   Without it, no reset email is sent and OIDC sign-in answers 500. The API logs warning 7522 once
   at startup, naming the reason, and warnings 7520 and 7521 on each refused request.
2. **Tenants on their own host names** (`acme.example.com`, or a branding subdomain): set the
   address with `{tenant}` in it. See [Tenants on their own host names](#tenants-on-their-own-host-names).
3. **Single sign-on.** Each tenant below must have exactly the redirect URI described in
   [OIDC single sign-on: check the registered redirect URI](#oidc-single-sign-on-check-the-registered-redirect-uri)
   registered with its identity provider:

   ```sql
   SELECT tenant_id, oidc_authority, oidc_client_id
   FROM tenant_auth_config WHERE oidc_enabled ORDER BY tenant_id;
   ```

4. **Supervisors and Admins who take over conversations need an Agent profile**
   (`POST /api/v1/admin/agents`). Without one they can still transfer, close and type any
   conversation, but not take one over, send, hold or resume.

   ```sql
   SELECT u.tenant_id, u.user_id, u.email,
          CASE u.role WHEN 1 THEN 'Supervisor' WHEN 2 THEN 'Admin' END AS role
   FROM users u
   WHERE u.role IN (1, 2) AND u.status = 0
     AND NOT EXISTS (SELECT 1 FROM agents a WHERE a.tenant_id = u.tenant_id AND a.user_id = u.user_id)
   ORDER BY u.tenant_id, u.email;
   ```

5. **Users with two Agent profiles.** Ownership checks match the user to one of them, not always the
   same one, and refuse the other. Keep one profile per user.

   ```sql
   SELECT a.tenant_id, a.user_id, count(*) AS profiles,
          string_agg(a.agent_id, ', ' ORDER BY a.agent_id) AS agent_ids
   FROM agents a GROUP BY a.tenant_id, a.user_id HAVING count(*) > 1 ORDER BY 1, 2;
   ```

6. **API-key integrations that act on conversations** (see
   [Conversation actions act as the caller's Agent profile](#conversation-actions-act-as-the-callers-agent-profile)):

   ```sql
   -- standard keys bound to no user: refused on transfer, close and typify
   SELECT tenant_id, key_id, name, last_used_at FROM api_keys
   WHERE key_type = 0 AND user_id IS NULL AND NOT is_revoked
     AND (expires_at IS NULL OR expires_at > now())
   ORDER BY tenant_id, name;
   -- keys whose owner has the Api role: refused on transfer
   SELECT k.tenant_id, k.key_id, k.name, u.email
   FROM api_keys k JOIN users u ON u.tenant_id = k.tenant_id AND u.user_id = k.user_id
   WHERE u.role = 3 AND NOT k.is_revoked ORDER BY k.tenant_id, k.name;
   ```

7. **Several API replicas, or Platform.Realtime:** set `ConnectionStrings__IdentityRedis` on
   platform-api and on realtime, pointing at the same Redis, and set `Identity__Redis__KeyPrefix` to
   the same value on both. The defaults differ (`asterisk:identity:` on the API,
   `verbara:platform:identity:` on Realtime), so without the prefix the two read different keys; the
   Helm chart sets Realtime's to the API's. `docker-compose.full.yml` and
   `docker-compose.reference-smb.yml` ship the platform-api line commented out and set no prefix.
   Without both, a revoked impersonation token is refused only by the process that revoked it, and
   the revocation is lost when that process restarts.
8. **Logs:** the per-request lines are gone, the gateway access-log format changes, and the
   gateway's error log no longer records requests (see [Request logging](#request-logging)). Update
   dashboards, alerts and parsers.
9. **Stop, then start.** Do not run 2.24.x and 2.25.0 side by side. Until the last 2.24.x process
   stops, it still accepts what 2.25.0 refuses, and its user writes can recreate an account a 2.25.0
   node deleted. With Helm, scale platform-api to 0 first, or use `strategy: Recreate` for this
   release.

## During the upgrade

1. Stop every 2.24.x platform-api and realtime process, then start 2.25.0.
2. With the compose files, recreate the gateway:
   `docker compose -f <compose file> up -d --force-recreate nginx-gateway`. `docker compose up -d`
   leaves it running, since neither its image nor its settings change, and a running container keeps
   reading the `nginx-gateway.conf` it started with, so it would go on logging as 2.24.x did.
3. Wait for `RBAC seeder: permissions, role templates, and per-tenant role migration complete.` If
   you see `RBAC seeder FAILED`, stale grants were not removed: fix the cause and restart.
4. Repair conversation owners (below) before agents resume work.
5. Impersonation sessions open at the upgrade end (401). Start new ones.

### Conversations owned by a user id

2.24.x could make a user id the owner of a conversation, through a takeover or a transfer to a user
id. 2.25.0 decides ownership on Agent profiles, so no one can send on, hold or resume such a
conversation, and only a supervisor can transfer, close or type it. List the open ones
(`owner_kind` 2 = Agent; states 40 and 50 to 53 are finished):

```sql
SELECT c.tenant_id, c.conversation_id, c.state, c.owner_id,
       EXISTS (SELECT 1 FROM users u WHERE u.tenant_id = c.tenant_id AND u.user_id = c.owner_id) AS owner_is_a_user,
       (SELECT count(*) FROM agents a WHERE a.tenant_id = c.tenant_id AND a.user_id = c.owner_id) AS profiles_of_that_user
FROM conversations c
WHERE c.owner_kind = 2 AND c.state NOT IN (40, 50, 51, 52, 53)
  AND NOT EXISTS (SELECT 1 FROM agents a WHERE a.tenant_id = c.tenant_id AND a.agent_id = c.owner_id)
ORDER BY c.tenant_id, c.updated_at;
```

Where the owner is a user with exactly one Agent profile, give the conversation to that profile:

```sql
UPDATE conversations c
SET owner_id = a.agent_id, updated_at = now(), updated_by = 'upgrade-2.25.0'
FROM agents a
WHERE c.owner_kind = 2 AND c.state NOT IN (40, 50, 51, 52, 53)
  AND a.tenant_id = c.tenant_id AND a.user_id = c.owner_id
  AND NOT EXISTS (SELECT 1 FROM agents x WHERE x.tenant_id = c.tenant_id AND x.agent_id = c.owner_id)
  AND (SELECT count(*) FROM agents y WHERE y.tenant_id = c.tenant_id AND y.user_id = c.owner_id) = 1;
```

A supervisor moves the rest with `POST /api/v1/supervisor/conversations/{id}/reassign`: the rows
whose `profiles_of_that_user` is not 1, or whose `owner_is_a_user` is false. Finished conversations
keep their owner. Remapped conversations may not count against the agent's capacity until they
close. Run the update only once you are committed to 2.25.0: 2.24.x looks owners up by user id, and
rolling back does not undo it.

## After the first start

1. Review administrator roles held by non-administrators (see
   [Role changes move RBAC roles](#role-changes-move-rbac-roles)).
2. Open offers that record no agent can neither be accepted nor time out. Reassign them:

   ```sql
   SELECT tenant_id, conversation_id, updated_at FROM conversations
   WHERE state = 1 AND NOT (metadata ? '_offeredTo') ORDER BY tenant_id, updated_at;
   ```

3. Look for these warnings:
   - 7522 at startup, 7520 and 7521 on requests: no usable public address;
   - 7500 and 7501: a new user holds no RBAC role yet;
   - 7502 and 7503: a role change whose RBAC move failed, or found no role to grant (also
     `rbac_roles_moved=false` in `user.role_changed`);
   - 7510: a conversation audit entry was not written;
   - 9102: the impersonation sweep could not revoke a token.
4. Purge request logs kept from 2.24.x and earlier, the gateway's error log and the web container's
   log included. They hold bearer tokens, OIDC codes and reset tokens in URLs and Referers, and the
   access tokens of single sign-on users. All have expired:
   access tokens after 15 minutes, impersonation tokens after 30, reset tokens and codes after an
   hour. No key rotation is needed.

## Conversation actions act as the caller's Agent profile

The owner of a conversation is an Agent profile (`agents.agent_id`), never a user id. Error bodies are
`{"error": "<code>"}`.

- `POST /api/v1/conversations/{id}/messages`, `/hold`, `/unhold`: only the owner. Otherwise 403
  `not-an-agent` or `not-owner`; 404 for an unknown conversation. Before: 400.
- `/accept`, `/reject`: only the agent the offer was made to. Otherwise 403 `not-an-agent` or
  `not-offered-to-you`; 409 with the reason when the switchboard refuses (no capacity, wrong state).
  Before: 200 with `success: false`. Clients must read the status code.
- `/transfer`: needs `contacts:conversation:transfer` (the Agent, Supervisor, Manager and Admin
  templates hold it; API and Quality Analyst do not). Then the owner, or a Supervisor or Admin. The
  target must exist: 400 `target-agent-not-found` or `target-queue-not-found`; 409 when the
  conversation cannot move. Audited as `conversation.transferred`, with `by_supervisor` when a
  supervisor acts.
- `/close`, `/typify`: the owner or a Supervisor or Admin, else 403 `not-owner`. A supervisor acting
  on another agent's conversation is audited (`conversation.closed`, `conversation.typified`).
- `POST /api/v1/supervisor/conversations/{id}/takeover`: the caller's own Agent profile becomes the
  owner, else 403 `not-an-agent`; 409 when the conversation cannot move. Audited as
  `conversation.taken_over`.
- `/reassign`: the target must exist (400 `target-agent-not-found` or `target-queue-not-found`).
- `POST /api/v1/conversations` with `initialMessage` needs an Agent profile (403 `not-an-agent`).

API keys:

- A management key acts with the Admin role. It can transfer, close and type as a supervisor
  (audited under the key id). It has no Agent profile, so it cannot send, accept, reject, hold or
  resume.
- A standard key bound to no user carries no role. It is now refused on transfer, close and typify,
  which it could call before.
- A key bound to a user acts as that user. To keep an integration working, bind its key to a user
  with the role it needs, or to one whose Agent profile owns the conversations it works.

## Impersonation sessions

- Ending a session (`DELETE /api/v1/management/impersonate`), revoking it
  (`POST /api/v1/management/impersonation/sessions/{id}/revoke`, on the replica that holds it), or its
  timeout now revokes its token. The next request gets 401, and Realtime refuses it at hub connect.
  Connections already open last until the token expires (at most 30 minutes). Sessions on Customer
  tenants can be ended again (they got 403).
- When the shared store (`ConnectionStrings__IdentityRedis`) is unreachable, impersonation requests
  fail with a server error. Other tokens are not affected.
- Tokens minted by 2.24.x are refused (they record no impersonator role).
- Each request checks that the impersonator is still active and still holds the role the session
  started with, so demoting or suspending the impersonator ends its sessions. Starting a session
  requires the impersonator's stored role to be Admin.
- Read-only sessions get 403 on everything but GET, HEAD, OPTIONS and ending the session; 2.24.x
  enforced none of this. Reads gated by a manage permission are refused too: agent-assist settings,
  Partner customer settings, Partner rate cards.
- No session may start another, change the signed-in account's password, MFA, recovery codes or
  sessions, revoke other users' sessions, delete tenants, or change installation settings. These
  blocks are now enforced.
- Permission gates admit an impersonation token only on the permissions minted into it at start: the
  impersonator's own permissions without the `platform:*` ones, or their read subset in a read-only
  session. Its Admin role no longer passes them. Routes gated by role (`AdminOnly`, `SupervisorPlus`)
  still accept it: a full session can, for example, give its impersonator an Agent profile in the
  tenant (`POST /api/v1/admin/agents`) and then take over and send.
- Audit entries written during impersonation carry `impersonator_id`, `impersonator_tenant` and
  `impersonation_session_id`.
- Web 3.20.0: when a session ends on the server, the console fetches the operator's own token with
  the refresh cookie and keeps the target tenant selected. A platform or Partner operator then
  carries on in that tenant with full rights while the banner still shows the impersonation. End
  impersonation from the banner. A later Web release fixes this.

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
- Demoting an Admin removes the Admin, System Admin, Platform Admin and Partner Admin copies;
  promoting the user back grants only the Admin copy, which lacks `billing:credits:grant`,
  `system:cluster:manage` and `system:auth:configure`. Grant the others again with
  `POST /api/v1/admin/users/{id}/roles/{roleId}` (for example `platform-admin-{tenant}` on the host
  tenant).

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

## Deleting or erasing a user ends its sessions

`DELETE /api/v1/admin/users/{id}` and `POST /api/v1/admin/gdpr/purge-user` revoke the user's refresh
tokens, close its live connections and write `user.deleted`. A user deleted while one of its sign-ins
was in flight is no longer recreated by it.

## Legacy MFA enrollment refuses an enrolled account

`POST /api/v1/auth/mfa/setup` and `POST /api/v1/auth/mfa/confirm` answer 400
`MFA already enrolled. Disable first to re-enroll.` while MFA is on. The console offers setup only
while MFA is off.

## Partner credit attribution needs `partner:billing:view`

`GET /api/v1/partner/credit-ledger/attribution` requires `partner:billing:view`, like
`/api/v1/partner/revenue`. The Admin, Partner Admin, Partner Billing and Partner Viewer templates carry
it; other users of a Partner tenant get 403.

## Partner tenants are supported from 2.25.0

Partner tenants (resellers who run their own Customers) are recommended from 2.25.0 on. Before it, a
Partner's administrator could impersonate its Customers with write access and keep administrator
permissions after a demotion (the impersonation, revocation and role-change advisories above). Do not
give a third party Partner administrator credentials on 2.24.x or earlier.

Known limitations:

- The host issues the invoice. A Partner generates its Customers' invoices
  (`POST /api/v1/partner/customers/{customerId}/invoices/generate`), but only the host's
  `POST /api/v1/management/invoices/{id}/issue` issues them.
- The Partner's rate card and the host's base rate card must use the same currency. The Partner's
  margin is the difference of the two totals, and no step compares their currencies.
- The console does not yet show the tenant's own logo, favicon or colours.

## Password-reset links and the OIDC redirect URI use the configured public address

Platform.Api no longer builds the password-reset link or the OIDC `redirect_uri` from the request's
`Host` header. It uses `Platform:PublicBaseUrl` (environment variable `Platform__PublicBaseUrl`),
the absolute `http` or `https` address users open the console at, and while that is unset, the
origin `CORS_ORIGINS` names when it names exactly one:

- An installation whose `CORS_ORIGINS` names only the console's origin, as
  `docker/.env.production.example` shows, needs no change.
- Any other installation should set `Platform__PublicBaseUrl` when it upgrades. Until it does, the
  API sends no password-reset email (`POST /api/v1/auth/forgot-password` still answers 200),
  answers OIDC sign-in with 500, and logs a warning naming the setting: once at startup (event id
  7522, with the reason) and on each refused request (7520 and 7521). This covers a `CORS_ORIGINS`
  that lists several origins, such as the `docker-compose.reference-smb.yml` default
  (`http://localhost,https://localhost`), for which you set `PUBLIC_BASE_URL` in
  `.env.reference-smb`; `docker-compose.verified.yml`, whose `PUBLIC_BASE_URL` has no default; and
  the Helm chart, which derives the address from `ingress.hostnameWeb` and `ingress.tlsEnabled`
  while `ingress.enabled` is true, unless `api.publicBaseUrl` is set.
- A value that is not an absolute `http` or `https` URL, or that carries a query, a fragment or user
  information, counts as unset, and is not replaced by `CORS_ORIGINS`.

### Tenants on their own host names

Where each tenant opens the console at its own host (`acme.example.com`, which the API resolves to
tenant `acme`, or a branding subdomain), write that host with `{tenant}`:
`Platform__PublicBaseUrl=https://{tenant}.example.com`. For each link the API fills `{tenant}` with
the label the tenant is reached at: its branding subdomain when it has one, otherwise its id. The
tenant is the one the user belongs to (a reset) or the one the sign-in is for (OIDC), never the
request's `Host`. The label must be a lowercase DNS label (letters, digits and inner hyphens, up to
63 characters); for a tenant without one, no reset email is sent and sign-in is refused, with
warnings 7520 and 7521 naming the tenant. A single address without `{tenant}` sends every tenant's
links to that address instead: each tenant's OIDC redirect URI becomes that address's, and sign-on
started on a tenant's own host fails at the callback, because the sign-in state cookie belongs to
the host the sign-in started on.

### Console served under a path

When the console is served under a path (`https://example.com/console`), the reset link keeps it
(`https://example.com/console/reset-password?token=…`). The OIDC callback is at the host's root
(`https://example.com/api/auth/oidc/callback`), where the API answers, as before.

### OIDC single sign-on: check the registered redirect URI

The `redirect_uri` is now the callback at the configured address's origin,
`<scheme>://<host>[:<port>]/api/auth/oidc/callback`, scheme included, and with `{tenant}` on each
tenant's own host. Before, it took the scheme and host of the sign-in request, which behind a proxy
that terminates TLS without `ForwardedHeaders:TrustedProxies` came out as `http://`. Check that the
redirect URI registered with each tenant's identity provider matches the new value exactly, and
register the new value where it does not. Users must start single sign-on from the console at the
configured address (with `{tenant}`, at their tenant's host): the sign-in state cookie belongs to the
host the sign-in started on.

### A sign-in returns only to the console

`GET /api/v1/auth/oidc/login` takes a `return_url`, where the browser lands when the sign-in
completes. It is honoured only on the console: a path (`/login`), or an absolute URL whose scheme,
host and port are those of the configured address (with `{tenant}`, of the tenant's host). Any other
value is replaced by `/`. The console sends its own login page, so it is unaffected; an integration
that started sign-in with a `return_url` on another origin now lands on the console's root.

### The reset link's token is percent-encoded

The token in the reset link is now percent-encoded; links mailed before the upgrade are unchanged.
Resetting a password from the console needs the Platform.Web release after 3.20.0: the 3.20.0 reset
page sends the token in a field the API does not read, so every reset from it fails, whatever the
token.

### Host filtering stays off

No shipped configuration sets ASP.NET Core's `AllowedHosts`, so the API still answers on any
`Host`. If you set it, `docker/README.md` lists the names it must include so that health checks,
Kubernetes probes and Platform.Realtime keep reaching the API.

## Request logging

- Platform.Api, Platform.Realtime and Platform.Mail no longer write `Request starting` and
  `Request finished` (`Microsoft.AspNetCore.Hosting.Diagnostics`), and Platform.Api no longer writes
  `Executing RedirectResult` (`Microsoft.AspNetCore.Http.Result.RedirectResult`), whose destination
  carried a single sign-on user's access token. `Logging__LogLevel__*` settings cannot bring them
  back. For request rates, statuses and latencies, use the OpenTelemetry HTTP metrics or traces, or
  the gateway log.
- `docker/nginx-gateway.conf` logs in the `verbara_noquery` format:
  `$remote_addr - $remote_user [$time_local] "$request_method $uri $server_protocol" $status $body_bytes_sent "<referer path>" "$http_user_agent"`.
  Parsers written for `combined` (fail2ban, GoAccess, Promtail) need updating. If you mount your own
  gateway configuration, apply the same format.
- The gateway's error log no longer records requests: its server block sends it to `/dev/null`.
  nginx writes the request line, the upstream URL and the Referer, query strings included, with every
  message about a request, at any level, so this was the only way to keep tokens out of it. Messages
  such as `connect() failed`, `upstream timed out` or `upstream prematurely closed connection` no
  longer appear in the gateway's `docker logs`; the request still appears in the access log with its
  status (502, 504) and path, which names the service. Alerts that read the error log for those
  messages must read the access log's status instead. The messages of nginx's master process
  (start-up, configuration errors, reloads, a worker that exits) are still written; those about a
  connection, such as `worker_connections are not enough`, are not. If you mount your own gateway
  configuration, add `error_log /dev/null;` to each server block. `docker/nginx-loadbalancer.conf`
  (the `docker-compose.scale.yml` benchmark balancer) does the same.
- Requests for the console reach the web image without their query string and with only the scheme,
  host and path of the Referer, because the web image's nginx logs both raw. `/api/` and `/hubs/`
  requests keep their query.
- Your own ingress, load balancer, APM agent or log shipper must drop query strings, including the
  query part of the Referer. On Kubernetes the Helm chart routes `/` to the web image directly, so its
  log receives each password-reset link's token; restrict access to it.

## Platform.Web 3.20.0 against 2.25.0

It works: it sends no `If-Match` (so no 412), and its transfer and reassign targets are Agent profile
ids. What changes for its users:

- Refusals show the raw codes (`not-an-agent`, `not-owner`, `not-offered-to-you`,
  `target-agent-not-found`, `target-queue-not-found`). A refused accept now shows an error instead of
  a success toast.
- A takeover now reaches the supervisor's console: the assignment names the supervisor's Agent
  profile, which is what the console matches.
- Password reset and the end of an impersonation behave as described above.

## Rolling back

2.24.x starts on the same database: there is no migration. What stays changed:

- removed RBAC grants (2.24.x adds back only those of each user's current role);
- revoked refresh tokens;
- remapped conversation owners (2.24.x matches owners by user id);
- `user_roles` rows with `assigned_by = 'default-role'` (2.24.x ignores the value).

Rolling back reopens every issue 2.25.0 fixes.

## Custom code built on Platform's libraries

- `IUserStore.SaveAsync` is gone: use `CreateAsync` and the targeted writers.
- `ConversationSwitchboard` takes an `IAgentStore`.
- `JwtTokenService.ValidateTokenAsync` is gone, and `GenerateImpersonationToken` returns
  `(Token, ExpiresAt, TokenId)`.
- `IUserRoleStore.MoveAsync` is new.
