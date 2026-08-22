---
tier: PEQUEÑO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: SMB self-host operators reaching the box by IP address; anyone running the host locally
decision_ref: Platform/ADR-0038
---

## Why

**A Platform host reached over a bare IPv4 address resolves a phantom tenant. Customer callers then get
403 on everything; Platform and Partner callers get something worse — they are let through, still scoped
to the phantom tenant.**

`TenantResolutionMiddleware.ResolveFromSubdomainAsync` splits the request host on its first `.` and
treats the leading label as a subdomain. For `127.0.0.1` that label is **`127`**, which is not in
the `www` / `api` / `localhost` exclusion list, so resolution returns `TenantId("127")`. Crucially
this runs **before** the `X-Tenant-Id` header is consulted, so the header cannot correct it.

What happens next depends on who is calling, and only one of the two outcomes was originally reported:

- **Customer principals — 403.** `TenantBoundaryValidationMiddleware` compares the JWT's `tid` against
  `127`, finds a mismatch, and returns `403 "Tenant header does not match authenticated principal."`
  Noisy, but the door closes.
- **Platform / Partner principals — silent mis-scoping.** `TenantBoundaryValidationMiddleware:91-97`
  lets those two tenant types through on a mismatch, because operating on another tenant is legitimate
  for them — but it does **not** correct `Items["TenantId"]`. Every endpoint downstream then reads
  `TenantId("127")` and reads *and writes* data scoped to a tenant that does not exist, **even when the
  caller sent a correct `X-Tenant-Id`**. This is a cross-tenant data-scoping defect in the present tense,
  not a hypothetical one, and it is the more serious of the two outcomes.

Observed live while verifying `encrypt-mfa-secrets-at-rest`: every authenticated call to a host
bound on `127.0.0.1` returned 403, **with and without** an explicit and correct `X-Tenant-Id`
header. Switching the client to `localhost` fixed it instantly, because a host with no `.` returns
early from the subdomain resolver and the header is then honoured.

The same logic applies to any dotted IPv4 address, so it is not a loopback quirk: an operator who
reaches their self-host box at `192.168.1.50` resolves tenant `192`.

**Why it matters.** The primary product track is SMB self-host, where reaching the box by LAN IP
before DNS exists is the normal first step. For a Customer the symptom — everything authenticated 403s
while login itself succeeds — is maximally confusing, and the workaround (use a hostname) is
undiscoverable from the error message. For a Platform admin there is no symptom at all until the data
turns up under the wrong tenant. It also silently costs developer time locally; it is already recorded
in this project's operational notes (`.project-memory/reference_local_infra_gotchas.md:50`), mechanism
and all, which is why the diagnosis was quick this time — but a documented trap is still a trap.

## What Changes

- **Never treat an IP-address literal as a subdomain.** `ResolveFromSubdomainAsync` must return
  `null` when the request host parses as an `IPAddress` (v4 or v6), before any label splitting. The
  request then falls through to the `X-Tenant-Id` header, which is the correct source in exactly
  this deployment shape.
- **Stop matching the reserved labels case-sensitively.** `www` / `api` / `localhost` are compared
  ordinally today, so `WWW.platform.example` resolves tenant `WWW` — the same defect class, on the
  same three lines.
- **Document the resolution order rather than inverting it.** The header currently loses to the
  subdomain, which is what makes the IP case unrecoverable by the caller. Keeping that order is
  defensible (a white-label subdomain should pin the tenant) but it is nowhere stated, and an
  undocumented precedence is indistinguishable from an accident. A new ADR-0038 becomes its home.
- **Make the 403 explain itself.** The response says the header does not match the principal
  without saying what was resolved or from where. Naming the resolved tenant and its source
  (subdomain / header / path) turns a dead end into a one-line diagnosis. The caller at that point is
  authenticated, so the information is about their own request.
- **Regression coverage:** a test per resolution source, including a bare IPv4 host with a valid
  `X-Tenant-Id` asserting the header wins, and a Platform-principal case asserting the resolved tenant
  is the real one rather than the phantom.

## Capabilities

### New Capabilities
- `ip-host-tenant-resolution`: a request whose host is an IP-address literal resolves its tenant
  from the header (or path), never from a numeric label mistaken for a subdomain.

### Modified Capabilities
<!-- None. No existing living spec covers tenant resolution; ADR-0002 governs tenant stamping but
     is an ADR, not an openspec capability. -->

## Impact

- **Source:** `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
  (`ResolveFromSubdomainAsync`, and `ResolveTenantIdAsync` to carry the resolution source);
  `TenantBoundaryValidationMiddleware`'s 403 body; the two auth-time writers of `Items["TenantId"]`
  so the source they imply is not stale.
- **Tests:** resolution-source coverage in `Verbara.Platform.Api.Tests`.
- **Docs:** ADR-0038 plus the seven live documents that state or imply the precedence today. The
  operational note at `.project-memory/reference_local_infra_gotchas.md:50` already states the full
  mechanism correctly; once the fix lands it becomes **redundant**, so it is retitled or deleted — not
  "completed".
- **No schema change. No cross-repo impact.**
- **Behavioural note:** any deployment relying on a *numeric* leading label resolving as a tenant id
  changes behaviour. The naming audit found none: no seeder, migration, fixture, test, compose file,
  demo script or doc creates or expects one. State that as **"nothing in the product uses numeric tenant
  ids"**, *not* "the system prevents them" — `SetupEndpoints.IsValidSlug:280-291` permits digits and
  `ManagementTenantEndpoints.CreateTenant:76-137` applies no format validation at all, so an admin who
  tries can create `"999"`.

### Out of Scope (explicit)

- **The `DateTimeOffset` `Local`-kind crash** found in the same session — separate defect, shipped as
  `fix-local-kind-datetimeoffset`.
- **Reworking white-label subdomain resolution.** The branding-store lookup stays as it is; this
  change only stops an IP literal from entering that path at all.
- **The backward-compat fallback for dotted non-IP hostnames** (`TenantResolutionMiddleware.cs:126`,
  "unknown subdomain becomes the tenant id"). A box reached at `verbara.acme-corp.lan`,
  `platform-api.internal`, or a Kubernetes service name still resolves a phantom tenant the same way,
  and there `localhost` is not an available workaround. Removing the fallback is a live behavioural
  change for real deployments — `SubdomainResolutionTests.cs:69` pins it deliberately — so it needs an
  operator escape hatch and its own decision. Recorded here as **accepted residual risk**, carried into
  ADR-0038, with a follow-up change to be opened.
- **The rate-limiting defects** surfaced while scoping this change: the `"per-tenant"` and
  `"global-safety"` policies are defined but attached to no endpoint, and the `Items["TenantId"]`
  string type-test is wrong at three sites. Independent, larger, and security-relevant — its own change.
