# Verbara Platform — Docker assets

This directory ships docker-compose files for various deployment shapes
(`docker-compose.full.yml`, `docker-compose.production.yml`,
`docker-compose.smb.yml`, `docker-compose.scale.yml`, …) plus the
Asterisk container build (`Dockerfile.asterisk` + `entrypoint-asterisk.sh`)
and observability sidecars (`docker-compose.observability.yml`).

## Verifying image signature (Docker Compose)

Pro v2.3.x ships image-binding (Pro/ADR-0011)
that detects unauthorised Verbara Platform images at runtime via Layer C
(in-process digest check). The docker-compose tooling here adds Layer B
(pre-flight cosign signature verification) so customers catch a tampered
image at pull time, before any container code runs.

### Quick path: `verbara-quickstart.sh`

One command, end-to-end: look up the digest, verify the signature,
generate a digest-pinned compose file, pull, and bring the stack up.

```sh
cd docker/
./verbara-quickstart.sh v2.0.1
docker compose -f docker-compose.verified.v2.0.1.yml pull
docker compose -f docker-compose.verified.v2.0.1.yml up -d
```

Requires: `cosign`, `docker`, `curl`, `jq`.

### Power-user path: manual

If you prefer to run each step by hand, or want to verify a specific
image without touching the registry:

```sh
# 1. Verify the cosign signature
./verbara-verify-image.sh ghcr.io/verbara/platform/api:v2.0.1
# (prints the resolved manifest-list digest on success)

# 2. Edit docker-compose.verified.yml — replace
#    sha256:REPLACE_WITH_MANIFEST_LIST_DIGEST with the digest from step 1.

# 3. Bring up the stack
docker compose -f docker-compose.verified.yml pull
docker compose -f docker-compose.verified.yml up -d
```

### Why this matters

Without running `verbara-verify-image.sh` (or the `quickstart` wrapper),
docker-compose customers get only the **in-process Layer-C check** —
which is still a real defense (Pro rejects unauthorised images at
startup) but does NOT detect a tampered image being **pulled** in the
first place. Pre-flight verification closes that gap.

The Kyverno admission policy that K8s customers get via the Helm chart
(`infra/k8s/helm/platform/templates/cosign-admission-policy.yaml`)
enforces the equivalent guarantee — a tampered image is rejected at Pod
admission, before kubelet pulls the layers. This script provides the
docker-compose equivalent.

### Defense-in-depth (Pro/ADR-0011)

| Layer | Where it runs | What it catches |
|-------|---------------|-----------------|
| F (ECDSA license) | Pro v2.2.0-pro `LicenseTrustAnchor` | Forged license payloads |
| **B (cosign signature)** | `verbara-verify-image.sh` (or Kyverno on K8s) | Unsigned/tampered images at pull/admission time |
| **C (in-process digest check)** | `Verbara.Sdk.Pro.Licensing.ContainerImageDigest` | Authorised-image mismatch at Pro startup |

### Files

| File | Purpose |
|------|---------|
| `verbara-verify-image.sh` | Pre-flight cosign verify for a single image ref. |
| `verbara-smoke-released.sh` | Post-release FUNCTIONAL smoke: composes the released (digest-pinned) images and runs one end-to-end journey (`/health/ready` binary polling + setup→login). `--local` builds the demo images instead — dev-only, does NOT verify the actually-released artifact. |
| `verbara-quickstart.sh` | End-to-end wrapper: lookup -> verify -> generate -> pull -> up. |
| `docker-compose.verified.yml` | Template — operators substitute their resolved digest. |
| `docker-compose.full.yml` | Existing dev/demo full-stack compose (Asterisk + API + Web + storage). |
| `docker-compose.production.yml` | Existing production compose (no dev seeds, external storage assumed). |

### Operator runbook

After every Verbara Platform tagged release, the maintainer must update
the digest registry that powers `verbara-quickstart.sh`. See
[`docs/operations/2026-05-10-update-authorized-digests-after-release.md`](../docs/operations/2026-05-10-update-authorized-digests-after-release.md).

## Request logs and query strings

A browser `EventSource` or `WebSocket` cannot send an `Authorization` header, so the
SSE event stream (`/api/v1/events/stream?token=…`) and the SignalR hub
(`/hubs/platform?…&access_token=…`) carry the user's access token in the query
string. The OIDC callback (`?code=…`) and the password-reset link
(`/reset-password?token=…`, which a browser may later send on as a `Referer`) do the
same. The shipped services therefore keep query strings out of their logs:

- **The .NET services write no per-request lines.** Platform.Api, Platform.Realtime
  and Platform.Mail set the `Microsoft.AspNetCore.Hosting.Diagnostics` category
  (the "Request starting …" / "Request finished …" lines) to `Warning` in code, so a
  `Logging__LogLevel__*` variable cannot turn those lines back on. A provider-specific
  key such as `Logging__Console__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics`
  can, and would write every request's full URL, token included: do not set one
  in production.
- **The gateway logs paths, not queries.** `nginx-gateway.conf` writes its access log
  in the `verbara_noquery` format: client address, time, method, path, protocol,
  status, size, the path part of the Referer, and the User-Agent. That is where
  per-request records come from, failed requests included: a 502 or 504 there names
  the path, and the path names the service (`/api/` and `/health` platform-api,
  `/hubs/` realtime, anything else web).
- **The gateway's error log records no requests.** nginx adds the request line, the
  upstream URL and the Referer, query strings included, to every message it logs
  while it handles a request, at every level (a failed proxied request, a response
  buffered to a temporary file, a temporary file it cannot write), and no setting
  changes that text. The gateway's server block therefore sends its error log to
  `/dev/null`. `docker logs` still shows the messages of nginx's master process
  (start-up, configuration errors, reloads, a worker that exits), but none about
  connections or requests, `worker_connections are not enough` included. To find out
  why requests fail, read the failing service's own log. If you turn the error log
  back on to troubleshoot (`error_log /var/log/nginx/error.log;` in the server
  block), it writes the token of every failing request: turn it off again afterwards
  and recreate the container (last point below), which also discards its log.
  `nginx-loadbalancer.conf`, the benchmark balancer of `docker-compose.scale.yml`,
  discards its error log the same way.
- **The web image gets no query.** The gateway passes `/api/` and `/hubs/` requests on
  with their query (the event stream and the hub authenticate with it), but sends
  requests for the console to the web image without the query and with only the
  scheme, host and path of the Referer: the web image's nginx logs the raw request
  line and Referer, and the console reads the reset link's token in the browser.
  Where the web image is reached without this gateway (the Helm chart routes `/`
  straight to it), its log receives each reset link's token: restrict access to it.
- **Your own proxy must do the same.** If another reverse proxy, a Kubernetes
  ingress, a load balancer, an APM agent or a log shipper records these requests,
  configure it to drop query strings (and the query part of the Referer) from what
  it keeps. A server block you add to `nginx-gateway.conf` (for TLS on port 443, say)
  needs the same `access_log … verbara_noquery;` and `error_log /dev/null;` lines.
- **Changes to `nginx-gateway.conf` need a recreated container.** A running container
  keeps reading the file it started with, and `docker compose up -d` does not
  recreate the gateway when only that file changed: run
  `docker compose -f <compose file> up -d --force-recreate nginx-gateway`.

## Public address of the console (`Platform__PublicBaseUrl`)

Platform.Api builds the links that bring a user back to the console from configuration, never
from the request: the password-reset link it mails (`<address>/reset-password?token=…`) and the
OIDC `redirect_uri` (the callback at the address's origin, `<scheme>://<host>[:<port>]/api/auth/oidc/callback`).
A sign-in's `return_url` is honoured only on that origin, or as a path; anything else sends the
browser to `/`. A request's `Host` header, and `X-Forwarded-Host`, are whatever the client sends.
The address is, in order:

1. `Platform__PublicBaseUrl` (`Platform:PublicBaseUrl`): the absolute `http` or `https` address
   users open the console at, such as `https://contact.example.com`, or
   `https://example.com/console` when the console is served under a path (the reset link keeps the
   path; the OIDC callback stays at the host's root, where `/api/` is routed). A trailing `/` is
   ignored; a query, a fragment or user information makes the value unusable. When each tenant opens
   the console at its own host, write it with `{tenant}`, as in `https://{tenant}.example.com`: each
   link fills it with the label its tenant is reached at, the tenant's branding subdomain or else its
   id, which must be a lowercase DNS label. The tenant is the user's for a reset and the sign-in's for
   OIDC, never the request's `Host`.
2. Otherwise, the origin `CORS_ORIGINS` names, when it names exactly one.

With neither, or with an unusable `Platform__PublicBaseUrl` (which is never replaced by
`CORS_ORIGINS`), the API sends no reset email — `POST /api/v1/auth/forgot-password` still answers
200 — and answers OIDC sign-in with 500. It logs a warning naming the setting once at startup, with
the reason (event id 7522), and on each refused request (7520 and 7521).

| Reference | Where to set it |
|---|---|
| `docker-compose.full.yml`, `docker-compose.scale.yml` | `PUBLIC_BASE_URL`; unset, `CORS_ORIGINS=http://localhost` is used |
| `docker-compose.reference-smb.yml` | `PUBLIC_BASE_URL` in `.env.reference-smb`; needed unless `CORS_ORIGINS` names one origin (its default names two) |
| `docker-compose.production.yml` | `Platform__PublicBaseUrl` in `.env.production`; unset, the single `CORS_ORIGINS` origin is used |
| `docker-compose.verified.yml` | `PUBLIC_BASE_URL` |
| Helm chart | `api.publicBaseUrl`; empty, it is derived from `ingress.hostnameWeb` and `ingress.tlsEnabled` while the ingress is enabled |

For OIDC single sign-on, the redirect URI registered with each identity provider must be exactly
the callback at the address's origin (with `{tenant}`, at the tenant's host), and users must start
sign-in from the console at that address: the sign-in state cookie belongs to the host the sign-in
started on.

### Optional: refuse unknown Host headers

Platform.Api honours ASP.NET Core's `AllowedHosts` setting (host names separated by `;`, without
ports). No shipped reference sets it, so every `Host` is admitted. Setting it makes the API answer
any other `Host` with 400; the links above do not depend on it. If you set it, list every name the
API is reached by, not only the public one:

- the public host name(s);
- `localhost`: the compose health checks (`curl http://localhost:5000/health`) and the examples in
  [`docs/operations/first-deploy.md`](../docs/operations/first-deploy.md);
- `platform-api` (on Kubernetes, `platform-api.<namespace>.svc.cluster.local`): Platform.Realtime's
  calls to `Services__PlatformApi__BaseUrl`;
- on Kubernetes, the `httpGet` probes send the pod IP as `Host`, which cannot be listed: give them
  `httpHeaders: [{ name: Host, value: localhost }]` first.

The reference gateway (`nginx-gateway.conf`) passes the `Host` header through unchanged: the API
resolves a tenant from the first label of a subdomain host.
