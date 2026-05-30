# Getting Started — Verbara.Platform (10 minutes)

This guide takes you from a blank machine to a running Platform tenant you
can log into. It is the shortest supported path. Once you have completed
it, follow [first-deploy.md](operations/first-deploy.md) to make your first
real call (30 minutes), and [first-realistic-demo.md](operations/first-realistic-demo.md)
for an end-to-end multi-tenant tour (~1 hour).

> **Reading time:** ~10 minutes.
> **What you get:** A locally running stack — Asterisk 22 LTS + Platform
> API + Web UI + Postgres — plus the `platform` tenant, an admin user, and
> a Management API key in your terminal.

---

## Prerequisites

- **Linux / macOS / WSL2** with at least 4 GB free RAM and 8 GB disk.
- **Docker Engine 24+** with `docker compose` v2 (run `docker compose version`
  to confirm).
- **`git`**, **`curl`**, and **`python3`** (for parsing JSON responses below;
  any tool works — `jq` is fine too).
- **Open ports on `localhost`:** 80 (Web UI), 5000 (API), 5038 (AMI),
  5060/udp (SIP), 8088/8089 (Asterisk HTTP / HTTPS for WebRTC),
  20000–20200/udp (RTP). If any are bound, edit `docker/docker-compose.full.yml`
  before starting.

> No external API keys required for this 10-minute path. AI providers
> (Deepgram / OpenAI / Whisper) are wired in [first-realistic-demo.md](operations/first-realistic-demo.md).

---

## Step 1 — Clone the repo

```bash
git clone https://github.com/verbara/Verbara.Platform.git
cd Verbara.Platform
```

---

## Step 2 — Start the full stack

The bundled `docker-compose.full.yml` brings up Asterisk, Platform API,
Renderer (`:5010`, internal), Mail (`:5020`, internal), Web UI, and Postgres.
The Web UI is built from the sibling `Verbara.Platform.Web` repo, so this
expects it cloned next to `Verbara.Platform/` (`../Verbara.Platform.Web`).

```bash
git clone https://github.com/verbara/Verbara.Platform.Web.git ../Verbara.Platform.Web

docker compose -f docker/docker-compose.full.yml up -d
```

First boot pulls + builds images (~3-5 min). Subsequent starts are seconds.

> **Optional profiles.** Add `--profile cluster` (Redis backplane for
> multi-node), `--profile identity-redis` (Redis-backed JTI revocation +
> MFA caches), or `--profile s3` (MinIO at `:9000` / console `:9001`).
> Default profile is single-node, in-memory caches, local-disk recordings.

---

## Step 3 — Verify the Platform API is ready

```bash
until curl -sf http://localhost:5000/health > /dev/null; do
    echo "Waiting for API..." && sleep 3
done
curl -s http://localhost:5000/health
```

Expected output: `Healthy`. If after ~60s it is still not ready, see
**Troubleshooting** below.

---

## Step 4 — Initialize the platform (one-shot setup wizard)

A fresh database has no users. Call the setup endpoint **once** to create
the host tenant + the first admin user + a Management API key. The endpoint
will refuse subsequent calls (idempotent).

```bash
SETUP=$(curl -sf -X POST http://localhost:5000/api/v1/setup \
    -H "Content-Type: application/json" \
    -d '{
        "email": "platform@admin.local",
        "password": "PlatformAdmin2026!",
        "displayName": "Platform Admin",
        "platformName": "Verbara Platform",
        "customerTenantId": "my-company",
        "customerName": "My Company",
        "customerAdminEmail": "admin@my-company.local",
        "customerAdminPassword": "CustomerAdmin2026!"
    }')

echo "$SETUP" | python3 -m json.tool
MGMT_KEY=$(echo "$SETUP" | python3 -c "import sys,json; print(json.load(sys.stdin)['managementApiKey'])")
echo "Save this Management API key (cannot be re-displayed): $MGMT_KEY"
```

> **Save the management key in your password manager now.** It is the
> only way to call `/api/v1/management/*` endpoints (create child tenants,
> set plans, manage rate cards). The setup wizard only returns it once.

Now log in to confirm the admin user works:

```bash
curl -sf -X POST http://localhost:5000/api/v1/auth/login \
    -H "Content-Type: application/json" \
    -d '{
        "tenantId": "platform",
        "email": "platform@admin.local",
        "password": "PlatformAdmin2026!"
    }' | python3 -m json.tool
```

You should see `accessToken` + `refreshToken`. If MFA is enforced you would
see `mfaRequired: true` instead — fresh installs do not enforce MFA by
default.

---

## Step 5 — Open the Web UI

Browse to **http://localhost** — the Web container serves the React app
on port 80.

Sign in with:

- **Tenant ID:** `platform`
- **Email:** `platform@admin.local`
- **Password:** `PlatformAdmin2026!`

You should land on the dashboard. Explore the left nav: **Conversations**,
**Queues**, **Agents**, **Analytics**, **Admin → Audit Log / Tenant
Settings / Retention / API Keys / License**. The platform-admin views
(Tenants, Servers, Cluster, Impersonation) appear because `platform` is
the host tenant and your user has the `Platform Admin` role.

---

## Next steps

- **[first-deploy.md](operations/first-deploy.md)** — register a softphone
  to extension `2001`, place your first inbound call, and verify it lands
  in the audit log (~30 min).
- **[first-realistic-demo.md](operations/first-realistic-demo.md)** —
  multi-tenant seed, real-time analytics wallboard, AgentAssist runtime
  toggle, audit log viewer, retention admin (~55 min).
- **[operations/cluster-management.md](operations/cluster-management.md)** —
  multi-node setup with the Redis cluster backplane.
- **[operations/agentassist-setup.md](operations/agentassist-setup.md)** —
  wire Deepgram / Whisper / Azure / Google STT for live AI assist.
- **[operations/api-keys-management.md](operations/api-keys-management.md)** —
  scoped tenant + management API keys (reveal-once UX).

---

## Troubleshooting

### `docker compose up` fails with "port is already allocated"

Some other process owns one of the bound ports. Check with
`ss -tlnp | grep -E '(:80|:5000|:5038|:8088|:8089)'` (or `lsof -i`) and
either stop the conflicting process or remap the port in
`docker/docker-compose.full.yml` (left side of the `"host:container"` pair).

### `/health` returns 500 or never goes Healthy

The API is most likely waiting on Postgres. Run
`docker compose -f docker/docker-compose.full.yml logs platform-api --tail=80`
and look for `Npgsql` errors. If Postgres itself is unhealthy, run
`docker compose -f docker/docker-compose.full.yml logs postgres --tail=40`.
A common cause is leftover state in the `pgdata` volume from a previous
incompatible Postgres major version — wipe with
`docker compose -f docker/docker-compose.full.yml down -v` and start over.

### Setup wizard returns 409 Conflict

The platform was already initialized (this is expected after the first
run). If you have lost the admin password, reset by tearing the volume
down (`docker compose -f docker/docker-compose.full.yml down -v`) and
re-running Step 2 + Step 4. The Management API key is single-shot — you
can mint a new one via `/api/v1/management/api-keys` once logged in as
Platform Admin.

### Web UI shows "Network error" on login

The browser is hitting the API directly (not via the Web container's
reverse proxy) and CORS is blocking it. The bundled `docker-compose.full.yml`
sets `CORS_ORIGINS=http://localhost`. If you are accessing the UI under a
different origin (e.g. `http://192.168.x.x`), add it to that env var and
restart `platform-api`.
