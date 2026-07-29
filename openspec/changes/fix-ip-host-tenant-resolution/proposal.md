---
tier: PEQUEÑO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: SMB self-host operators reaching the box by IP address; anyone running the host locally
decision_ref: Platform/ADR-0002
---

## Why

**A Platform host reached over a bare IPv4 address resolves a bogus tenant, and every authenticated
request then fails with 403.**

`TenantResolutionMiddleware.ResolveFromSubdomainAsync` splits the request host on its first `.` and
treats the leading label as a subdomain. For `127.0.0.1` that label is **`127`**, which is not in
the `www` / `api` / `localhost` exclusion list, so resolution returns `TenantId("127")`. Crucially
this runs **before** the `X-Tenant-Id` header is consulted, so the header cannot correct it.
`TenantBoundaryValidationMiddleware` then compares the JWT's `tid` against `127`, finds a mismatch,
and — because the caller's tenant is a `Customer`, not `Platform`/`Partner` — returns
`403 "Tenant header does not match authenticated principal."`

Observed live while verifying `encrypt-mfa-secrets-at-rest`: every authenticated call to a host
bound on `127.0.0.1` returned 403, **with and without** an explicit and correct `X-Tenant-Id`
header. Switching the client to `localhost` fixed it instantly, because a host with no `.` returns
early from the subdomain resolver and the header is then honoured.

The same logic applies to any dotted IPv4 address, so it is not a loopback quirk: an operator who
reaches their self-host box at `192.168.1.50` resolves tenant `192`.

**Why it matters.** The primary product track is SMB self-host, where reaching the box by LAN IP
before DNS exists is the normal first step. The symptom — everything authenticated 403s while login
itself succeeds — is maximally confusing, and the workaround (use a hostname) is undiscoverable
from the error message. It also silently costs developer time locally; it was already folded into
this project's operational notes as a "use localhost, not 127.0.0.1" rule of thumb, but without the
mechanism, so it kept being rediscovered.

## What Changes

- **Never treat an IP-address literal as a subdomain.** `ResolveFromSubdomainAsync` must return
  `null` when the request host parses as an `IPAddress` (v4 or v6), before any label splitting. The
  request then falls through to the `X-Tenant-Id` header, which is the correct source in exactly
  this deployment shape.
- **Reconsider the resolution order, or justify it.** The header currently loses to the subdomain.
  That may well be deliberate — a white-label subdomain arguably should pin the tenant — but it is
  undocumented, and it is what makes the IP case unrecoverable by the caller. Either document the
  precedence as intentional or invert it; do not leave it implicit.
- **Make the 403 explain itself.** The response says the header does not match the principal
  without saying what was resolved or from where. Naming the resolved tenant and its source
  (subdomain / header / path) turns a dead end into a one-line diagnosis. Weigh this against not
  leaking tenant existence to an unauthenticated caller — the caller here is authenticated, so the
  information is about their own request.
- **Regression coverage:** a test per resolution source, including a host that is a bare IPv4
  address with a valid `X-Tenant-Id` header, asserting the header wins.

## Capabilities

### New Capabilities
- `ip-host-tenant-resolution`: a request whose host is an IP-address literal resolves its tenant
  from the header (or path), never from a numeric label mistaken for a subdomain.

### Modified Capabilities
<!-- None. No existing living spec covers tenant resolution; ADR-0002 governs tenant stamping but
     is an ADR, not an openspec capability. -->

## Impact

- **Source:** `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
  (`ResolveFromSubdomainAsync` and possibly the resolution order in `ResolveTenantIdAsync`);
  optionally `TenantBoundaryValidationMiddleware`'s 403 body.
- **Tests:** resolution-source coverage in `Verbara.Platform.Api.Tests`.
- **Docs:** the operational note that currently says "use `localhost`, not `127.0.0.1`" can state
  the mechanism, or be deleted once the fix lands.
- **No schema change. No cross-repo impact.**
- **Behavioural note:** any deployment that today relies on a *numeric* leading label resolving as a
  tenant id would change behaviour. No such tenant naming exists in this product (tenant ids are
  slugs like `platform`, `acme`), so the risk is theoretical — but the change must confirm it rather
  than assume.

### Out of Scope (explicit)

- **The `DateTimeOffset` `Local`-kind crash** found in the same session — separate defect, tracked
  as `fix-local-kind-datetimeoffset`.
- **Reworking white-label subdomain resolution.** The branding-store lookup stays as it is; this
  change only stops an IP literal from entering that path at all.
