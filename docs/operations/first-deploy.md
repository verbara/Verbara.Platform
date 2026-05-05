# First deploy — make your first call (30 minutes)

Picks up where [getting-started.md](../getting-started.md) left off. By
the end of this guide you will have:

1. Created a child tenant (`acme`) with a real admin user.
2. Provisioned an agent + WebRTC extension for them.
3. Registered a softphone (Linphone) and placed an inbound call.
4. Verified the call appears in the audit log + analytics.

> **Reading + execution time:** ~30 minutes.
> **Prerequisites:** Step 1–5 of [getting-started.md](../getting-started.md)
> completed; you have a `MGMT_KEY` in your shell environment from the
> setup wizard.

---

## Step 1 — Create a child tenant

Tenants in Verbara.Platform are hierarchical. The host (`platform`) tenant
owns customer tenants. Use the Management API key to create one:

```bash
curl -sf -X POST http://localhost:5000/api/v1/management/tenants \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $MGMT_KEY" \
    -d '{
        "tenantId": "acme",
        "name": "Acme Contact Center",
        "type": 2
    }'
```

`type: 2` = customer tenant (vs. `0` host, `1` reseller). Set its plan so
the Pro feature gates open:

```bash
curl -sf -X PUT http://localhost:5000/api/v1/management/tenants/acme/settings \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $MGMT_KEY" \
    -d '{"plan": "Pro"}'
```

`Pro` enables: Dialer, Bot, Analytics export, Flows, Webhooks, Scheduled
reports, Knowledge base, Recordings. Use `Enterprise` to unlock everything
(Cluster, multi-region, etc.).

---

## Step 2 — Create the tenant admin user

We need a JWT for the platform admin to call the per-tenant `/admin/*`
endpoints. Re-run the login from getting-started.md and capture the token:

```bash
PLATFORM_JWT=$(curl -sf -X POST http://localhost:5000/api/v1/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"platform","email":"platform@admin.local","password":"PlatformAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

curl -sf -X POST http://localhost:5000/api/v1/admin/users \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $PLATFORM_JWT" \
    -H "X-Tenant-Id: acme" \
    -d '{
        "userId": "acme-user-admin",
        "email": "admin@acme.local",
        "displayName": "Acme Admin",
        "role": "Admin",
        "password": "AcmeAdmin2026!"
    }'
```

> **`X-Tenant-Id` header.** Per-tenant operations always need this header
> when the calling user belongs to a different tenant. Platform admins can
> address any descendant tenant; regular tenant users are scoped to their
> own.

---

## Step 3 — Provision an agent + WebRTC extension

Switch to the Acme admin and create an agent. Agents have a SIP extension
auto-provisioned in the Asterisk Realtime tables — Pro.Realtime handles
this so you do not edit `pjsip.conf`.

```bash
ACME_JWT=$(curl -sf -X POST http://localhost:5000/api/v1/auth/login \
    -H "Content-Type: application/json" \
    -d '{"tenantId":"acme","email":"admin@acme.local","password":"AcmeAdmin2026!"}' \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])")

curl -sf -X POST http://localhost:5000/api/v1/admin/users \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACME_JWT" \
    -H "X-Tenant-Id: acme" \
    -d '{
        "userId": "acme-user-alice",
        "email": "alice@acme.local",
        "displayName": "Alice (Agent)",
        "role": "Agent",
        "password": "AcmeAgent2026!"
    }'

AGENT=$(curl -sf -X POST http://localhost:5000/api/v1/admin/agents \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACME_JWT" \
    -H "X-Tenant-Id: acme" \
    -d '{
        "userId": "acme-user-alice",
        "displayName": "Alice",
        "extension": "4001",
        "sipPassword": "alice4001"
    }')
AGENT_ID=$(echo "$AGENT" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")

# Skills are managed via PUT /admin/agents/{id} after create
curl -sf -X PUT "http://localhost:5000/api/v1/admin/agents/$AGENT_ID" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACME_JWT" \
    -H "X-Tenant-Id: acme" \
    -d '{"skills": ["sales"]}'
```

Verify the extension landed in the Realtime tables:

```bash
docker compose -f docker/docker-compose.full.yml exec -T postgres \
    psql -U platform -d platform -c \
    "SELECT id, callerid, webrtc, transport FROM ps_endpoints WHERE id='4001';"
```

You should see one row with `webrtc=yes` and `transport=transport-wss`.

---

## Step 4 — Create a queue and add Alice to it

```bash
curl -sf -X POST http://localhost:5000/api/v1/admin/queues \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACME_JWT" \
    -H "X-Tenant-Id: acme" \
    -d '{"name": "Acme Sales", "isActive": true}'

# Re-fetch the queue id (server-assigned). ListQueues returns a PagedResult
# `{items, totalCount, page, pageSize}` and each row has an `id` field.
QUEUE_ID=$(curl -sf -H "Authorization: Bearer $ACME_JWT" -H "X-Tenant-Id: acme" \
    http://localhost:5000/api/v1/admin/queues \
    | python3 -c "import sys,json; print(next(q['id'] for q in json.load(sys.stdin)['items'] if q['name']=='Acme Sales'))")

# Queue members live at /queues/{queueId}/members (RESTful, R5.1) — not under /admin
curl -sf -X POST "http://localhost:5000/api/v1/queues/$QUEUE_ID/members" \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $ACME_JWT" \
    -H "X-Tenant-Id: acme" \
    -d "{\"agentId\": \"$AGENT_ID\", \"penalty\": 0}"
```

---

## Step 5 — Register a softphone

We will use **Linphone** (free, cross-platform, WebRTC-friendly). Any
SIP softphone supporting WSS works (Zoiper, Bria, MicroSIP, jssip in a
browser).

### Linphone account settings

| Field | Value |
|---|---|
| Username | `4001` |
| Password | `alice4001` |
| Domain | `localhost` (or your machine's IP) |
| Transport | **TLS** (WSS over 8089) — for plain SIP/UDP testing use `5060` |
| Outbound proxy | `localhost:8089` (TLS) or `localhost:5060` (UDP) |

> **Self-signed cert.** The bundled Asterisk uses a self-signed cert at
> `:8089`. Linphone will warn — accept the cert. For production, swap the
> cert at `docker/asterisk-config/keys/`.

> **Plain SIP fallback.** If TLS gives trouble for testing, register on
> `5060/udp` instead (Username `4001`, Password `alice4001`, Domain
> `localhost`, Transport UDP). Then `core show endpoints` from the
> Asterisk CLI to confirm the contact landed.

After registering, the Linphone status should show **Registered**. If not,
peek at the Asterisk CLI:

```bash
docker compose -f docker/docker-compose.full.yml exec -T asterisk \
    asterisk -rx "pjsip show endpoint 4001"
```

You should see `Contact: 4001/sip:...` with status `Avail`.

---

## Step 6 — Place an inbound call

The bundled dialplan (`docker/asterisk-config/extensions.conf`, context
`default`) routes any extension dialed to the matching endpoint. From a
**second** softphone (or the Asterisk CLI) dial `4001`. Alice's softphone
should ring.

To test from the Asterisk CLI:

```bash
docker compose -f docker/docker-compose.full.yml exec -T asterisk \
    asterisk -rx "channel originate PJSIP/4001 application Playback hello-world"
```

Pick up — you should hear `"Hello, world"`. The call is recorded (default
behavior) under the `recordings` Docker volume.

---

## Step 7 — Verify the audit log + CDR

In the Web UI (signed in as `admin@acme.local` on tenant `acme`):

1. **Admin → Audit Log.** Filter by today. You should see entries for the
   tenant create, plan change, user creates, agent create, queue create.
   Each row has `actorId` (sub claim of the JWT), `action`, `tenantId`,
   `timestamp`, `outcome`. Click a row for the diff payload.
2. **Analytics → Recent Calls.** Within ~5 seconds the originated call
   should appear with `direction=outbound`, `duration`, `disposition=ANSWERED`.
3. **Analytics → Live Wallboard.** Queue snapshot tile should show `Acme
   Sales` with `LoggedIn=1`, `Available` count reflecting Alice's state.

If the audit log is empty, check
`docker compose -f docker/docker-compose.full.yml logs platform-api --tail=80`
for `audit` errors. The audit sink is wired by default to the
`audit_entries` Postgres table.

---

## What you have now

- A two-tenant install (`platform` host + `acme` customer).
- One agent (Alice / extension 4001) registered + reachable.
- One queue (`Acme Sales`) with Alice as a member.
- Working CDR + audit log + recordings.

Continue to [first-realistic-demo.md](first-realistic-demo.md) for a
guided tour of the R4 + R5 features (multi-tenant analytics, AgentAssist
toggle, retention admin, drain demo).

---

## Common pitfalls

### Linphone shows "Registration failed"

Most often: TLS cert not trusted. Either accept the self-signed cert in
Linphone's preferences or fall back to UDP/5060.

### Originate succeeds but no audio

RTP ports `20000-20200/udp` may not be open in your firewall. On Linux
desktop: `sudo ufw allow 20000:20200/udp`. In Docker Desktop, ensure the
range is mapped (it is in the bundled compose file).

### Agent does not appear in queue

`Pro.Realtime`'s reconciler runs every 30s. The simplest remedy is to wait ~30s
and re-list with
`curl -sf -H "Authorization: Bearer $ACME_JWT" -H "X-Tenant-Id: acme" "http://localhost:5000/api/v1/queues/$QUEUE_ID/members"`.
If you need to force an immediate sync, restart the API container —
`docker compose -f docker/docker-compose.full.yml restart platform-api` — which
runs the reconciler at startup. (A first-class `POST /admin/realtime/reconcile`
admin endpoint is on the R5.4 backlog.)
